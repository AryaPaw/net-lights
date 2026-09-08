using System.Threading.Channels;

namespace NetLights.Core;

public sealed class MonitorHost : IAsyncDisposable
{
    private readonly MonitorKernel _kernel;
    private readonly IProbe _probe;
    private readonly TimeProvider _time;
    private readonly Action? _closeConnections;
    private readonly Channel<Action> _events;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<long, AttemptRuntime> _attempts = new();
    private readonly object _gate = new();
    private CancellationTokenSource _epochCts = new();
    private Task? _loop;
    private MonitorSnapshot _published;
    private int _uiQueued;

    public MonitorHost(MonitorKernel kernel, IProbe probe, TimeProvider time, Action? closeConnections = null)
    {
        _kernel = kernel;
        _probe = probe;
        _time = time;
        _closeConnections = closeConnections;
        _events = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _published = kernel.Snapshot;
    }

    public MonitorKernel Kernel => _kernel;
    public MonitorSnapshot Snapshot => _published;
    public event Action<MonitorSnapshot>? SnapshotChanged;
    public int UiQueueDepth => _uiQueued;

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_shutdown.Token));
        ITimer timer = _time.CreateTimer(
            static state =>
            {
                var host = (MonitorHost)state!;
                host.Post(() => host.Dispatch(host._kernel.Tick()));
            },
            this,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50));
        _timer = timer;
        Post(() => Dispatch(_kernel.Tick()));
    }

    private ITimer? _timer;

    public void Post(Action action)
    {
        _events.Writer.TryWrite(action);
    }

    public void RequestCheckNow() => Post(() => Dispatch(_kernel.RequestCheckNow()));

    public void NotifyNetworkChange() => Post(() => Dispatch(_kernel.BeginNewEpoch("network-change")));

    public void NotifyUnavailable(bool unavailable) => Post(() => Dispatch(_kernel.SetNetworkUnavailable(unavailable)));

    public void NotifyInternalError(string message) => Post(() => Dispatch(_kernel.NotifyInternalError(message)));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _timer?.Dispose();
        _events.Writer.TryComplete();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<AttemptRuntime> running;
        lock (_gate)
        {
            running = _attempts.Values.ToList();
            _attempts.Clear();
        }

        foreach (AttemptRuntime attempt in running)
        {
            attempt.Cts.Cancel();
        }

        foreach (AttemptRuntime attempt in running)
        {
            try
            {
                await attempt.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }

            attempt.Cts.Dispose();
        }

        _epochCts.Cancel();
        _epochCts.Dispose();
        _shutdown.Dispose();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (Action action in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                action();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Dispatch(_kernel.NotifyInternalError(ex.GetType().Name));
        }
    }

    private void Dispatch(IReadOnlyList<WorkItem> work)
    {
        foreach (WorkItem item in work)
        {
            switch (item.Kind)
            {
                case WorkKind.StartProbe:
                    StartAttempt(item);
                    break;
                case WorkKind.CancelAttempt:
                    CancelAttempt(item.AttemptId);
                    break;
                case WorkKind.CloseIdleConnections:
                    _epochCts.Cancel();
                    _epochCts.Dispose();
                    _epochCts = new CancellationTokenSource();
                    _closeConnections?.Invoke();
                    break;
                case WorkKind.PublishSnapshot:
                    Publish();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown work {item.Kind}.");
            }
        }
    }

    private void StartAttempt(WorkItem item)
    {
        EndpointDefinition endpoint = item.Endpoint ?? throw new InvalidOperationException("Probe without endpoint.");
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, _epochCts.Token);
        attemptCts.CancelAfter(item.Deadline);
        long started = _time.GetTimestamp();
        var runtime = new AttemptRuntime(item.AttemptId, item.NetworkEpoch, endpoint, started, attemptCts);
        lock (_gate)
        {
            _attempts[item.AttemptId] = runtime;
        }

        runtime.Task = ProbeSafe(runtime);
    }

    private async Task ProbeSafe(AttemptRuntime runtime)
    {
        EndpointObservation observation;
        try
        {
            observation = await _probe.ProbeAsync(
                runtime.Endpoint,
                runtime.Epoch,
                runtime.AttemptId,
                MonitorConstants.ProbeDeadline,
                runtime.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || runtime.Epoch != _kernel.Epoch)
        {
            observation = Cancelled(runtime, ProbeOutcome.Cancelled, StructuredFailureKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            observation = Cancelled(runtime, ProbeOutcome.Unreachable, StructuredFailureKind.TransportTimeout);
        }
        catch (Exception ex)
        {
            observation = Cancelled(runtime, ProbeOutcome.Indeterminate, StructuredFailureKind.LocalResource, ex.GetType().Name);
        }

        Post(() =>
        {
            Dispatch(_kernel.ApplyObservation(observation));
            Dispatch(_kernel.NotifyPhysicalFinished(runtime.AttemptId));
            runtime.Cts.Dispose();
            lock (_gate)
            {
                _attempts.Remove(runtime.AttemptId);
            }
        });
    }

    private EndpointObservation Cancelled(AttemptRuntime runtime, ProbeOutcome outcome, StructuredFailureKind kind, string? detail = null)
    {
        long completed = _time.GetTimestamp();
        return ObservationFactory.Create(
            runtime.Endpoint,
            runtime.Epoch,
            runtime.AttemptId,
            runtime.Started,
            completed,
            outcome,
            _time.GetElapsedTime(runtime.Started, completed),
            failure: new StructuredFailure(kind, detail ?? kind.ToString()));
    }

    private void CancelAttempt(long attemptId)
    {
        lock (_gate)
        {
            if (_attempts.TryGetValue(attemptId, out AttemptRuntime? runtime))
            {
                runtime.Cts.Cancel();
            }
        }
    }

    private void Publish()
    {
        _published = _kernel.Snapshot;
        int queued = Interlocked.Increment(ref _uiQueued);
        if (queued > 2)
        {
            Interlocked.Decrement(ref _uiQueued);
            SnapshotChanged?.Invoke(_published);
            return;
        }

        SnapshotChanged?.Invoke(_published);
        Interlocked.Decrement(ref _uiQueued);
    }

    private sealed class AttemptRuntime
    {
        public AttemptRuntime(long attemptId, long epoch, EndpointDefinition endpoint, long started, CancellationTokenSource cts)
        {
            AttemptId = attemptId;
            Epoch = epoch;
            Endpoint = endpoint;
            Started = started;
            Cts = cts;
            Task = Task.CompletedTask;
        }

        public long AttemptId { get; }
        public long Epoch { get; }
        public EndpointDefinition Endpoint { get; }
        public long Started { get; }
        public CancellationTokenSource Cts { get; }
        public Task Task { get; set; }
    }
}
