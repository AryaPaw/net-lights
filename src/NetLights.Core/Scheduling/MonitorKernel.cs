namespace NetLights.Core;

public sealed partial class MonitorKernel
{
    private readonly MonitorConfiguration _config;
    private readonly TimeProvider _time;
    private readonly MonotonicClock _clock;
    private readonly GroupRuntime _ru;
    private readonly GroupRuntime _vpn;
    private readonly StartLimiter _limiter = new();
    private readonly BoundedEventLog _log = new();
    private readonly List<KernelTrace> _trace = new();
    private readonly Dictionary<long, EndpointSlot> _inflightAttempts = new();

    private long _epoch = 1;
    private long _nextAttemptId;
    private bool _networkUnavailable;
    private bool _capacityExhausted;
    private string? _monitorError;
    private MonitorSnapshot _snapshot;
    private bool _tracing;

    public MonitorKernel(MonitorConfiguration config, TimeProvider time)
    {
        EndpointPoolValidation validation = EndpointPoolValidator.Validate(config.Endpoints);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Error, nameof(config));
        }

        _config = config;
        _time = time;
        _clock = new MonotonicClock(time);
        _ru = CreateGroup(EndpointGroup.Ru, _clock.Now);
        _vpn = CreateGroup(EndpointGroup.Vpn, _clock.Add(_clock.Now, config.GroupOffset));
        BeginConfirmation(_ru, _clock.Now, "startup");
        BeginConfirmation(_vpn, _clock.Now, "startup");
        _snapshot = BuildSnapshot();
    }

    public MonitorSnapshot Snapshot => _snapshot;
    public BoundedEventLog Log => _log;
    public StartLimiter Limiter => _limiter;
    public long Epoch => _epoch;
    public int PhysicalInflight => _ru.PhysicalCount + _vpn.PhysicalCount;
    public IReadOnlyList<KernelTrace> Trace => _trace;
    public bool Tracing { get => _tracing; set => _tracing = value; }
    public int ConfirmationEpisodeCount { get; private set; }
    public int MaxObservedPhysical { get; private set; }
    public int AppliedCompletions { get; private set; }

    public IReadOnlyList<WorkItem> Tick()
    {
        var work = new List<WorkItem>();
        _limiter.RemoveOlderThan(_clock, TimeSpan.FromSeconds(60));
        if (_networkUnavailable)
        {
            MaybePublish(work, force: false);
            return work;
        }

        ExpireEpisodes(work);
        AdvanceAndMaybeRotate(_ru, work);
        AdvanceAndMaybeRotate(_vpn, work);
        FillConfirmation(_ru, work);
        FillConfirmation(_vpn, work);
        MaybePublish(work, force: true);
        return work;
    }

    public IReadOnlyList<WorkItem> ApplyObservation(EndpointObservation observation)
    {
        var work = new List<WorkItem>();
        EndpointSlot? slot = FindSlot(observation.EndpointId);
        if (slot is null)
        {
            return work;
        }

        slot.ApplicationInFlight = false;
        MaxObservedPhysical = Math.Max(MaxObservedPhysical, PhysicalInflight);

        if (observation.NetworkEpoch != _epoch)
        {
            AddTrace("drop-epoch", slot, observation.AttemptId);
            MaybePublish(work, force: false);
            return work;
        }

        if (slot.Applied is { } existing && observation.AttemptId < existing.AttemptId)
        {
            AddTrace("drop-stale", slot, observation.AttemptId);
            return work;
        }

        if (slot.Applied is { } same && same.AttemptId == observation.AttemptId)
        {
            return work;
        }

        if (observation.Outcome == ProbeOutcome.Cancelled)
        {
            AddTrace("cancelled", slot, observation.AttemptId);
            _log.Add(_clock.UtcNow, "cancelled", slot.Definition.Id);
            MaybePublish(work, force: true);
            return work;
        }

        slot.Applied = new AppliedSample
        {
            AttemptId = observation.AttemptId,
            Epoch = observation.NetworkEpoch,
            Started = observation.MonotonicStarted,
            Completed = observation.MonotonicCompleted,
            Outcome = observation.Outcome,
            HttpStatus = observation.HttpStatus,
            Failure = observation.Failure,
            Elapsed = observation.Elapsed,
            CompletedUtc = _clock.UtcNow
        };
        slot.NextAllowed = Math.Max(
            observation.NextAllowedAt,
            _clock.Add(slot.LastStarted, _config.MinEndpointInterval));
        AppliedCompletions++;
        AddTrace("applied-" + observation.Outcome, slot, observation.AttemptId);

        GroupRuntime group = GroupOf(slot.Definition.Group);
        if (observation.Outcome == ProbeOutcome.Reachable)
        {
            group.LastReachableAt = observation.MonotonicCompleted;
            group.ConsecutiveFailures.Clear();
            if (group.Confirmation is not null)
            {
                MarkEpisodeCoverage(group, slot, observation);
                TryFinishConfirmation(group, work);
            }
        }
        else if (observation.Outcome == ProbeOutcome.Unreachable)
        {
            group.ConsecutiveFailures.Add(new FailureMark(slot.Definition.InfrastructureId, observation.MonotonicStarted));
            while (group.ConsecutiveFailures.Count > MonitorConstants.MaxConsecutiveFailureMarks)
            {
                group.ConsecutiveFailures.RemoveAt(0);
            }
            if (group.Confirmation is not null)
            {
                MarkEpisodeCoverage(group, slot, observation);
                TryFinishConfirmation(group, work);
            }
            else
            {
                TryStartFailureConfirmation(group, work);
            }
        }
        else
        {
            if (group.Confirmation is not null)
            {
                MarkEpisodeCoverage(group, slot, observation);
                TryFinishConfirmation(group, work);
            }
        }

        MaybePublish(work, force: true);
        return work;
    }

    public IReadOnlyList<WorkItem> NotifyPhysicalFinished(long attemptId)
    {
        if (_inflightAttempts.TryGetValue(attemptId, out EndpointSlot? slot))
        {
            slot.PhysicalInFlight = false;
            slot.ApplicationInFlight = false;
            _inflightAttempts.Remove(attemptId);
        }

        return [];
    }

    public IReadOnlyList<WorkItem> BeginNewEpoch(string reason)
    {
        var work = new List<WorkItem>();
        CancelInflight(work, reason);
        _epoch++;
        _networkUnavailable = false;
        _capacityExhausted = false;
        _monitorError = null;
        ResetGroupEvidence(_ru);
        ResetGroupEvidence(_vpn);
        BeginConfirmation(_ru, _clock.Now, reason);
        BeginConfirmation(_vpn, _clock.Now, reason);
        work.Add(new WorkItem(WorkKind.CloseIdleConnections, null, _epoch, 0, TimeSpan.Zero, reason));
        _log.Add(_clock.UtcNow, "epoch", reason);
        MaybePublish(work, force: true);
        return work;
    }

    public IReadOnlyList<WorkItem> SetNetworkUnavailable(bool unavailable)
    {
        if (unavailable && _networkUnavailable)
        {
            return [];
        }

        _networkUnavailable = unavailable;
        if (unavailable)
        {
            var work = new List<WorkItem>();
            CancelInflight(work, "network-unavailable");
            _log.Add(_clock.UtcNow, "net", "нет сетевого подключения");
            MaybePublish(work, force: true);
            return work;
        }

        return BeginNewEpoch("network-available");
    }

    public IReadOnlyList<WorkItem> RequestCheckNow()
    {
        var work = new List<WorkItem>();
        TryBeginCheckNow(_ru, work);
        TryBeginCheckNow(_vpn, work);
        MaybePublish(work, force: true);
        return work;
    }

    public IReadOnlyList<WorkItem> NotifyInternalError(string message)
    {
        _monitorError = BoundedEventLog.Sanitize(message);
        _log.Add(_clock.UtcNow, "monitor", _monitorError);
        var work = new List<WorkItem>();
        MaybePublish(work, force: true);
        return work;
    }

    public EndpointSlotSnapshot Inspect(string endpointId)
    {
        EndpointSlot slot = FindSlot(endpointId) ?? throw new ArgumentOutOfRangeException(nameof(endpointId));
        return new EndpointSlotSnapshot(
            slot.Definition,
            slot.LastAttemptId,
            slot.LastStarted,
            slot.NextAllowed,
            slot.PhysicalInFlight,
            slot.Applied?.Outcome,
            slot.Applied?.AttemptId ?? 0);
    }

    private void AdvanceAndMaybeRotate(GroupRuntime group, List<WorkItem> work)
    {
        if (!_clock.IsDue(group.NextSlotAt))
        {
            return;
        }

        long scheduled = group.NextSlotAt;
        group.NextSlotAt = _clock.Add(scheduled, _config.GroupInterval);
        if (group.NextSlotAt <= _clock.Now)
        {
            group.NextSlotAt = _clock.Add(_clock.Now, _config.GroupInterval);
        }

        if (group.Confirmation is not null)
        {
            return;
        }

        EndpointSlot target = SelectRoundRobin(group);
        TryLaunch(group, target, work);
    }

    private void FillConfirmation(GroupRuntime group, List<WorkItem> work)
    {
        if (group.Confirmation is null)
        {
            return;
        }

        while (true)
        {
            EndpointSlot? target = SelectConfirmationTarget(group);
            if (target is null || !TryLaunch(group, target, work))
            {
                return;
            }
        }
    }

    private bool TryLaunch(GroupRuntime group, EndpointSlot target, List<WorkItem> work)
    {
        if (!CanLaunch(group, target))
        {
            if (PhysicalInflight >= MonitorConstants.MaxPhysicalInflight)
            {
                _capacityExhausted = true;
            }

            return false;
        }

        Launch(group, target, work);
        return true;
    }

    private EndpointSlot SelectRoundRobin(GroupRuntime group)
    {
        EndpointSlot slot = group.Slots[group.RrIndex];
        group.RrIndex = (group.RrIndex + 1) % group.Slots.Length;
        return slot;
    }

    private EndpointSlot? SelectConfirmationTarget(GroupRuntime group)
    {
        ConfirmationState episode = group.Confirmation!;
        if (episode.NewProbesIssued >= MonitorConstants.MaxEpisodeNewProbes || episode.Succeeded)
        {
            return null;
        }

        EndpointSlot? best = null;
        foreach (EndpointSlot slot in group.Slots)
        {
            if (slot.EpisodeIssuedNewProbe || slot.EpisodeCovered || slot.PhysicalInFlight)
            {
                continue;
            }

            if (slot.LastStarted > 0 && _clock.Age(slot.LastStarted) < _config.MinEndpointInterval)
            {
                continue;
            }

            if (slot.NextAllowed > _clock.Now)
            {
                continue;
            }

            if (best is null || slot.LastStarted < best.LastStarted)
            {
                best = slot;
            }
        }

        return best;
    }

    private bool CanLaunch(GroupRuntime group, EndpointSlot slot)
    {
        if (slot.PhysicalInFlight || slot.ApplicationInFlight)
        {
            return false;
        }

        if (slot.NextAllowed > _clock.Now)
        {
            return false;
        }

        if (slot.LastStarted > 0 && _clock.Age(slot.LastStarted) < _config.MinEndpointInterval)
        {
            return false;
        }

        int groupPhysical = group.PhysicalCount;
        return _limiter.CanStart(_clock, PhysicalInflight, groupPhysical, slot.PhysicalInFlight);
    }

    private void Launch(GroupRuntime group, EndpointSlot slot, List<WorkItem> work)
    {
        slot.LastAttemptId = ++_nextAttemptId;
        slot.LastStarted = _clock.Now;
        slot.PhysicalInFlight = true;
        slot.ApplicationInFlight = true;
        slot.InFlightAttemptId = slot.LastAttemptId;
        _limiter.Record(slot.LastStarted);
        _inflightAttempts[slot.LastAttemptId] = slot;
        MaxObservedPhysical = Math.Max(MaxObservedPhysical, PhysicalInflight);
        if (group.Confirmation is not null)
        {
            slot.EpisodeIssuedNewProbe = true;
            group.Confirmation.NewProbesIssued++;
        }

        AddTrace("start", slot, slot.LastAttemptId);
        work.Add(new WorkItem(
            WorkKind.StartProbe,
            slot.Definition,
            _epoch,
            slot.LastAttemptId,
            _config.ProbeDeadline,
            group.Confirmation is null ? "rotate" : "confirm"));
    }

    private void TryStartFailureConfirmation(GroupRuntime group, List<WorkItem> work)
    {
        if (group.Confirmation is not null || CurrentAvailability(group) == GroupAvailability.Offline)
        {
            return;
        }

        GroupAvailability now = CurrentAvailability(group);
        bool leavingOnline = now == GroupAvailability.Online;
        if (!leavingOnline
            && group.LastConfirmationEnded > 0
            && _clock.Age(group.LastConfirmationEnded) < MonitorConstants.ConfirmationCooldown)
        {
            return;
        }

        HashSet<string> infras = group.ConsecutiveFailures
            .Select(f => f.InfrastructureId)
            .ToHashSet(StringComparer.Ordinal);
        int needed = leavingOnline ? 2 : 1;
        if (infras.Count < needed)
        {
            return;
        }

        long episodeStart = group.ConsecutiveFailures[0].Started;
        BeginConfirmation(group, episodeStart, "failures");
        AbsorbRelevantInflight(group);
        AddTrace("confirm-begin", group.Slots[0], 0);
    }

    private void TryBeginCheckNow(GroupRuntime group, List<WorkItem> work)
    {
        if (group.Confirmation is not null)
        {
            return;
        }

        if (group.LastConfirmationEnded > 0
            && _clock.Age(group.LastConfirmationEnded) < MonitorConstants.ConfirmationCooldown)
        {
            return;
        }

        BeginConfirmation(group, _clock.Now, "check-now");
        AbsorbRelevantInflight(group);
    }

    private void BeginConfirmation(GroupRuntime group, long episodeStart, string reason)
    {
        foreach (EndpointSlot slot in group.Slots)
        {
            slot.EpisodeCovered = false;
            slot.EpisodeIssuedNewProbe = false;
        }

        group.Confirmation = new ConfirmationState
        {
            EpisodeStart = episodeStart,
            Deadline = _clock.Add(episodeStart, MonitorConstants.EpisodeDuration),
            Reason = reason
        };
        ConfirmationEpisodeCount++;
        _log.Add(_clock.UtcNow, "confirm", $"{group.Group}:{reason}");
        AbsorbRelevantInflight(group);
    }

    private void AbsorbRelevantInflight(GroupRuntime group)
    {
        if (group.Confirmation is null)
        {
            return;
        }

        foreach (EndpointSlot slot in group.Slots)
        {
            if (slot.PhysicalInFlight && slot.LastStarted >= group.Confirmation.EpisodeStart)
            {
                slot.EpisodeCovered = true;
            }

            if (slot.Applied is { } applied
                && applied.Epoch == _epoch
                && applied.Started >= group.Confirmation.EpisodeStart
                && applied.Outcome is not ProbeOutcome.Cancelled)
            {
                slot.EpisodeCovered = true;
            }
        }
    }

    private void MarkEpisodeCoverage(GroupRuntime group, EndpointSlot slot, EndpointObservation observation)
    {
        if (group.Confirmation is null)
        {
            return;
        }

        if (observation.MonotonicStarted >= group.Confirmation.EpisodeStart)
        {
            slot.EpisodeCovered = true;
            if (observation.Outcome == ProbeOutcome.Reachable)
            {
                group.Confirmation.NewReachableInfras.Add(slot.Definition.InfrastructureId);
            }
        }
    }

    private void TryFinishConfirmation(GroupRuntime group, List<WorkItem> work)
    {
        if (group.Confirmation is null)
        {
            return;
        }

        if (group.Confirmation.NewReachableInfras.Count >= 2)
        {
            group.Confirmation.Succeeded = true;
            EndConfirmation(group, "two-successes");
            return;
        }

        if (group.Slots.All(s => s.EpisodeCovered && !s.ApplicationInFlight))
        {
            EndConfirmation(group, "covered");
        }
    }

    private void ExpireEpisodes(List<WorkItem> work)
    {
        ExpireOne(_ru);
        ExpireOne(_vpn);
    }

    private void ExpireOne(GroupRuntime group)
    {
        if (group.Confirmation is null)
        {
            return;
        }

        if (_clock.IsDue(group.Confirmation.Deadline))
        {
            EndConfirmation(group, "timeout");
        }
    }

    private void EndConfirmation(GroupRuntime group, string reason)
    {
        group.Confirmation = null;
        group.LastConfirmationEnded = _clock.Now;
        foreach (EndpointSlot slot in group.Slots)
        {
            slot.EpisodeCovered = false;
            slot.EpisodeIssuedNewProbe = false;
        }

        _log.Add(_clock.UtcNow, "confirm-end", $"{group.Group}:{reason}");
        AddTrace("confirm-end-" + reason, group.Slots[0], 0);
    }

    private void CancelInflight(List<WorkItem> work, string reason)
    {
        foreach (EndpointSlot slot in _ru.Slots.Concat(_vpn.Slots))
        {
            if (slot.ApplicationInFlight)
            {
                work.Add(new WorkItem(WorkKind.CancelAttempt, slot.Definition, _epoch, slot.InFlightAttemptId, TimeSpan.Zero, reason));
                slot.ApplicationInFlight = false;
            }
        }
    }

    private void ResetGroupEvidence(GroupRuntime group)
    {
        group.ConsecutiveFailures.Clear();
        group.LastReachableAt = 0;
        group.Confirmation = null;
        group.SawOnline = false;
        foreach (EndpointSlot slot in group.Slots)
        {
            slot.Applied = null;
            slot.EpisodeCovered = false;
            slot.EpisodeIssuedNewProbe = false;
        }
    }

    private GroupAvailability CurrentAvailability(GroupRuntime group)
        => BuildGroupSnapshot(group).Availability;

    private void MaybePublish(List<WorkItem> work, bool force)
    {
        MonitorSnapshot next = BuildSnapshot();
        bool changed = force || !SameUi(next, _snapshot);
        _snapshot = next;
        if (changed)
        {
            work.Add(new WorkItem(WorkKind.PublishSnapshot, null, _epoch, 0, TimeSpan.Zero, null));
        }
    }

    private static bool SameUi(MonitorSnapshot a, MonitorSnapshot b)
        => a.Ru.Availability == b.Ru.Availability
           && a.Vpn.Availability == b.Vpn.Availability
           && a.Ru.Reason == b.Ru.Reason
           && a.Vpn.Reason == b.Vpn.Reason
           && a.CapacityExhausted == b.CapacityExhausted
           && a.MonitorError == b.MonitorError
           && SameEndpoints(a.Ru, b.Ru)
           && SameEndpoints(a.Vpn, b.Vpn);

    private static bool SameEndpoints(GroupSnapshot a, GroupSnapshot b)
    {
        if (a.Endpoints.Count != b.Endpoints.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Endpoints.Count; i++)
        {
            EndpointView left = a.Endpoints[i];
            EndpointView right = b.Endpoints[i];
            if (left.Id != right.Id
                || left.Outcome != right.Outcome
                || left.HttpStatus != right.HttpStatus
                || left.Fresh != right.Fresh
                || left.Paused != right.Paused
                || left.InFlight != right.InFlight
                || left.Failure?.Kind != right.Failure?.Kind
                || RoundedMs(left.Elapsed) != RoundedMs(right.Elapsed))
            {
                return false;
            }
        }

        return true;
    }

    private static int RoundedMs(TimeSpan? elapsed)
        => elapsed is { } value ? (int)Math.Round(value.TotalMilliseconds) : -1;

    private MonitorSnapshot BuildSnapshot()
    {
        return new MonitorSnapshot(
            _epoch,
            _clock.UtcNow,
            _clock.Now,
            BuildGroupSnapshot(_ru),
            BuildGroupSnapshot(_vpn),
            _config.UsingBuiltinPool,
            _config.ConfigWarning,
            _capacityExhausted,
            _monitorError);
    }

    private GroupSnapshot BuildGroupSnapshot(GroupRuntime group)
    {
        var evidence = new List<EndpointEvidence>(group.Slots.Length);
        var views = new List<EndpointView>(group.Slots.Length);
        DateTimeOffset? lastSuccess = null;
        foreach (EndpointSlot slot in group.Slots)
        {
            bool inOutageVote = group.Confirmation is not null;
            bool fresh = slot.Applied is { } applied
                && applied.Epoch == _epoch
                && _clock.IsFresh(applied.Completed, _config.Freshness);
            if (fresh
                && inOutageVote
                && slot.Applied!.Outcome == ProbeOutcome.Reachable
                && slot.Applied.Started < group.Confirmation!.EpisodeStart)
            {
                fresh = false;
            }

            ProbeOutcome? outcome = fresh ? slot.Applied!.Outcome : null;
            evidence.Add(new EndpointEvidence(
                slot.Definition.Id,
                slot.Definition.InfrastructureId,
                outcome,
                slot.Applied?.Completed ?? 0,
                fresh));
            bool paused = slot.NextAllowed > _clock.Now
                          && slot.Applied?.HttpStatus is 403 or 429 or 503;
            DateTimeOffset? pauseUtc = paused
                ? _clock.UtcNow + _clock.Elapsed(_clock.Now, slot.NextAllowed)
                : null;
            views.Add(new EndpointView(
                slot.Definition.Id,
                slot.Definition.Group,
                slot.Definition.InfrastructureId,
                slot.Definition.Uri,
                outcome,
                fresh ? slot.Applied?.HttpStatus : null,
                fresh ? slot.Applied?.Failure : null,
                fresh ? slot.Applied?.Elapsed : null,
                slot.Applied?.CompletedUtc,
                fresh,
                paused,
                pauseUtc,
                slot.PhysicalInFlight));
            if (fresh && outcome == ProbeOutcome.Reachable)
            {
                DateTimeOffset at = slot.Applied!.CompletedUtc;
                if (lastSuccess is null || at > lastSuccess)
                {
                    lastSuccess = at;
                }
            }
        }

        GroupAvailability availability;
        string reason;
        if (_monitorError is not null)
        {
            availability = GroupAvailability.Unknown;
            reason = "Локальная ошибка монитора.";
        }
        else if (_networkUnavailable)
        {
            availability = GroupAvailability.Offline;
            reason = "нет сетевого подключения";
        }
        else
        {
            availability = GroupStateCalculator.Evaluate(
                group.Slots.Select(s => s.Definition).ToList(),
                evidence,
                out reason,
                out _,
                allowPartialOffline: group.SawOnline);
        }

        if (availability == GroupAvailability.Online)
        {
            group.SawOnline = true;
        }

        return new GroupSnapshot(
            group.Group,
            availability,
            reason,
            lastSuccess,
            group.Confirmation is not null,
            views);
    }

    private GroupRuntime CreateGroup(EndpointGroup group, long firstSlot)
    {
        EndpointSlot[] slots = _config.Endpoints
            .Where(e => e.Group == group)
            .Select(e => new EndpointSlot { Definition = e })
            .ToArray();
        return new GroupRuntime
        {
            Group = group,
            Slots = slots,
            NextSlotAt = firstSlot
        };
    }

    private GroupRuntime GroupOf(EndpointGroup group)
    {
        return group switch
        {
            EndpointGroup.Ru => _ru,
            EndpointGroup.Vpn => _vpn,
            _ => throw new InvalidOperationException($"Unknown group {group}.")
        };
    }

    private EndpointSlot? FindSlot(string id)
        => _ru.Slots.Concat(_vpn.Slots).FirstOrDefault(s => s.Definition.Id == id);

    private void AddTrace(string kind, EndpointSlot slot, long attemptId)
    {
        if (!_tracing)
        {
            return;
        }

        _trace.Add(new KernelTrace(_clock.Now, kind, slot.Definition.Id, slot.Definition.Group, attemptId, _epoch));
        if (_trace.Count > 20_000)
        {
            _trace.RemoveRange(0, 5_000);
        }
    }

    private sealed class GroupRuntime
    {
        public required EndpointGroup Group { get; init; }
        public required EndpointSlot[] Slots { get; init; }
        public int RrIndex { get; set; }
        public long NextSlotAt { get; set; }
        public ConfirmationState? Confirmation { get; set; }
        public long LastConfirmationEnded { get; set; }
        public long LastReachableAt { get; set; }
        public bool SawOnline { get; set; }
        public List<FailureMark> ConsecutiveFailures { get; } = [];
        public int PhysicalCount => Slots.Count(s => s.PhysicalInFlight);
    }

    private sealed class EndpointSlot
    {
        public required EndpointDefinition Definition { get; init; }
        public long LastAttemptId { get; set; }
        public long LastStarted { get; set; }
        public long NextAllowed { get; set; }
        public bool PhysicalInFlight { get; set; }
        public bool ApplicationInFlight { get; set; }
        public long InFlightAttemptId { get; set; }
        public AppliedSample? Applied { get; set; }
        public bool EpisodeCovered { get; set; }
        public bool EpisodeIssuedNewProbe { get; set; }
    }

    private sealed class AppliedSample
    {
        public long AttemptId { get; set; }
        public long Epoch { get; set; }
        public long Started { get; set; }
        public long Completed { get; set; }
        public ProbeOutcome Outcome { get; set; }
        public int? HttpStatus { get; set; }
        public StructuredFailure? Failure { get; set; }
        public TimeSpan Elapsed { get; set; }
        public DateTimeOffset CompletedUtc { get; set; }
    }

    private sealed class ConfirmationState
    {
        public long EpisodeStart { get; set; }
        public long Deadline { get; set; }
        public string Reason { get; set; } = "";
        public int NewProbesIssued { get; set; }
        public bool Succeeded { get; set; }
        public HashSet<string> NewReachableInfras { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct FailureMark(string InfrastructureId, long Started);
}

public sealed record EndpointSlotSnapshot(
    EndpointDefinition Definition,
    long LastAttemptId,
    long LastStarted,
    long NextAllowed,
    bool PhysicalInFlight,
    ProbeOutcome? Outcome,
    long AppliedAttemptId);
