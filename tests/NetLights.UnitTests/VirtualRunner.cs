using Microsoft.Extensions.Time.Testing;
using NetLights.Core;

namespace NetLights.UnitTests;

internal sealed class ProbeScript
{
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(20);
    public ProbeOutcome Outcome { get; init; } = ProbeOutcome.Reachable;
    public int Status { get; init; } = 200;
    public bool Frozen { get; init; }
    public string? RetryAfter { get; init; }
    public StructuredFailureKind? Failure { get; init; }
}

internal sealed class VirtualRunner
{
    private readonly List<Pending> _pending = [];
    private readonly List<MonitorSnapshot> _snapshots = [];
    private readonly MonotonicClock _clock;

    public VirtualRunner(MonitorConfiguration? configuration = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _clock = new MonotonicClock(Time);
        Kernel = new MonitorKernel(configuration ?? new MonitorConfiguration
        {
            Endpoints = TestPools.Independent(),
            UsingBuiltinPool = false
        }, Time);
        Kernel.Tracing = true;
        Script = _ => new ProbeScript();
        _snapshots.Add(Kernel.Snapshot);
    }

    public FakeTimeProvider Time { get; }
    public MonitorKernel Kernel { get; }
    public Func<EndpointDefinition, ProbeScript> Script { get; set; }
    public IReadOnlyList<MonitorSnapshot> Snapshots => _snapshots;
    public int Started { get; private set; }
    public int MaxInflight { get; private set; }
    public List<string> StartLog { get; } = [];

    public void Run(TimeSpan duration, TimeSpan? step = null)
    {
        TimeSpan stride = step ?? TimeSpan.FromMilliseconds(50);
        long end = _clock.Add(_clock.Now, duration);
        Dispatch(Kernel.Tick());
        CompleteDue();
        while (_clock.Now < end)
        {
            TimeSpan remain = _clock.Elapsed(_clock.Now, end);
            Time.Advance(remain < stride ? remain : stride);
            CompleteDue();
            Dispatch(Kernel.Tick());
            MaxInflight = Math.Max(MaxInflight, Kernel.PhysicalInflight);
        }

        CompleteDue();
        Dispatch(Kernel.Tick());
    }

    public void BreakAll(ProbeOutcome outcome = ProbeOutcome.Unreachable)
    {
        Script = _ => new ProbeScript
        {
            Delay = MonitorConstants.ProbeDeadline,
            Outcome = outcome,
            Failure = StructuredFailureKind.TransportTimeout,
            Status = 0
        };
        FailPending(outcome);
    }

    public void FailPending(ProbeOutcome outcome = ProbeOutcome.Unreachable)
    {
        foreach (Pending pending in _pending)
        {
            if (pending.LogicalDone)
            {
                continue;
            }

            pending.OverrideOutcome = outcome;
            pending.CompleteAt = _clock.Now;
        }
    }

    public void RestoreAll()
    {
        Script = _ => new ProbeScript { Delay = TimeSpan.FromMilliseconds(20) };
    }

    public void RestoreOnly(params string[] ids)
    {
        HashSet<string> live = ids.ToHashSet(StringComparer.Ordinal);
        Script = endpoint => live.Contains(endpoint.Id)
            ? new ProbeScript { Delay = TimeSpan.FromMilliseconds(20) }
            : new ProbeScript
            {
                Delay = MonitorConstants.ProbeDeadline,
                Outcome = ProbeOutcome.Unreachable,
                Failure = StructuredFailureKind.TransportTimeout
            };
    }

    public void Kill(params string[] ids)
    {
        HashSet<string> dead = ids.ToHashSet(StringComparer.Ordinal);
        Func<EndpointDefinition, ProbeScript> previous = Script;
        Script = endpoint => dead.Contains(endpoint.Id)
            ? new ProbeScript
            {
                Delay = MonitorConstants.ProbeDeadline,
                Outcome = ProbeOutcome.Unreachable,
                Failure = StructuredFailureKind.TransportTimeout
            }
            : previous(endpoint);
    }

    private void Dispatch(IReadOnlyList<WorkItem> work)
    {
        foreach (WorkItem item in work)
        {
            switch (item.Kind)
            {
                case WorkKind.StartProbe:
                    Start(item);
                    break;
                case WorkKind.CancelAttempt:
                    Pending? pending = _pending.FirstOrDefault(p => p.Item.AttemptId == item.AttemptId);
                    if (pending is not null)
                    {
                        pending.Cancelled = true;
                    }

                    break;
                case WorkKind.PublishSnapshot:
                    _snapshots.Add(Kernel.Snapshot);
                    break;
                case WorkKind.CloseIdleConnections:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown work {item.Kind}.");
            }
        }
    }

    private void Start(WorkItem item)
    {
        EndpointDefinition endpoint = item.Endpoint!;
        ProbeScript script = Script(endpoint);
        long started = Time.GetTimestamp();
        var pending = new Pending
        {
            Item = item,
            Started = started,
            CompleteAt = script.Frozen ? long.MaxValue : _clock.Add(started, script.Delay),
            DeadlineAt = _clock.Add(started, item.Deadline),
            Script = script
        };
        _pending.Add(pending);
        Started++;
        StartLog.Add(endpoint.Id);
        MaxInflight = Math.Max(MaxInflight, Kernel.PhysicalInflight);
    }

    private void CompleteDue()
    {
        foreach (Pending pending in _pending.ToArray())
        {
            if (pending.LogicalDone)
            {
                if (!pending.PhysicalDone && !pending.Script.Frozen && pending.CompleteAt <= _clock.Now)
                {
                    Dispatch(Kernel.NotifyPhysicalFinished(pending.Item.AttemptId));
                    pending.PhysicalDone = true;
                    _pending.Remove(pending);
                }

                continue;
            }

            if (pending.Cancelled)
            {
                Finish(pending, ProbeOutcome.Cancelled, null, new StructuredFailure(StructuredFailureKind.Cancelled, "cancelled"));
                continue;
            }

            if (pending.CompleteAt <= _clock.Now && (!pending.Script.Frozen || pending.OverrideOutcome is not null))
            {
                ProbeOutcome outcome = pending.OverrideOutcome ?? pending.Script.Outcome;
                int? status = outcome == ProbeOutcome.Reachable ? pending.Script.Status : pending.Script.Status == 0 ? null : pending.Script.Status;
                StructuredFailure? failure = pending.Script.Failure is { } kind
                    ? new StructuredFailure(kind, kind.ToString())
                    : null;
                long next = 0;
                if (status is 403 or 429 or 503)
                {
                    next = RetryAfterParser.ResolvePauseUntil(status.Value, pending.Script.RetryAfter, _clock, Time.GetUtcNow());
                }

                Finish(pending, outcome, status, failure, next);
                continue;
            }

            if (pending.DeadlineAt <= _clock.Now)
            {
                Finish(pending, ProbeOutcome.Indeterminate, null, new StructuredFailure(StructuredFailureKind.DeadlineExceeded, "deadline"), physical: pending.Script.Frozen);
            }
        }
    }

    private void Finish(
        Pending pending,
        ProbeOutcome outcome,
        int? status,
        StructuredFailure? failure,
        long nextAllowed = 0,
        bool physical = false)
    {
        pending.LogicalDone = true;
        long completed = Time.GetTimestamp();
        var observation = ObservationFactory.Create(
            pending.Item.Endpoint!,
            pending.Item.NetworkEpoch,
            pending.Item.AttemptId,
            pending.Started,
            completed,
            outcome,
            Time.GetElapsedTime(pending.Started, completed),
            status,
            failure,
            nextAllowed);
        Dispatch(Kernel.ApplyObservation(observation));
        if (!physical)
        {
            Dispatch(Kernel.NotifyPhysicalFinished(pending.Item.AttemptId));
            pending.PhysicalDone = true;
            _pending.Remove(pending);
        }
    }

    private sealed class Pending
    {
        public required WorkItem Item { get; init; }
        public required long Started { get; init; }
        public required long DeadlineAt { get; init; }
        public required ProbeScript Script { get; init; }
        public long CompleteAt { get; set; }
        public ProbeOutcome? OverrideOutcome { get; set; }
        public bool LogicalDone { get; set; }
        public bool PhysicalDone { get; set; }
        public bool Cancelled { get; set; }
    }
}

internal sealed class SplitTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _inner = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public TimeSpan UtcShift { get; set; }
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + UtcShift;
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _inner.CreateTimer(callback, state, dueTime, period);
    public void Advance(TimeSpan delta) => _inner.Advance(delta);
}

internal static class TestPools
{    public static IReadOnlyList<EndpointDefinition> Independent()
    {
        var list = new List<EndpointDefinition>(14);
        for (int i = 0; i < 7; i++)
        {
            list.Add(new EndpointDefinition($"ru-{i}", EndpointGroup.Ru, new Uri($"https://ru{i}.example.test/"), $"ru-infra-{i}"));
            list.Add(new EndpointDefinition($"vpn-{i}", EndpointGroup.Vpn, new Uri($"https://vpn{i}.example.test/"), $"vpn-infra-{i}"));
        }

        return list;
    }

    public static IReadOnlyList<EndpointDefinition> SharedCdn()
    {
        var list = Independent().ToList();
        return list.Select(e => e.Id is "ru-0" or "ru-1"
            ? e with { InfrastructureId = "shared-cdn" }
            : e).ToList();
    }
}
