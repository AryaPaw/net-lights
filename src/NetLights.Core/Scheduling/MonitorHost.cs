using System.Threading.Channels;

namespace NetLights.Core;

public sealed class MonitorHost : IAsyncDisposable
{
    private readonly MonitorKernel _kernel;
    private readonly IProbe _probe;
    private readonly TimeProvider _time;
    private readonly Action? _closeConnections;
    private readonly SynchronizationContext? _ui;
    private readonly Channel<HostCommand> _events;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<long, AttemptRuntime> _attempts = new();
    private readonly List<EpochGeneration> _generations = [];
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private CancellationTokenSource _epochCts = new();
    private Task? _loop;
    private ITimer? _timer;
    private MonitorSnapshot _published;
    private int _tickQueued;
    private int _uiQueued;
    private int _recyclePending;
    private Task? _disposeTask;

    public MonitorHost(
        MonitorKernel kernel,
        IProbe probe,
        TimeProvider time,
        Action? closeConnections = null,
        SynchronizationContext? uiContext = null)
    {
        _kernel = kernel;
        _probe = probe;
        _time = time;
        _closeConnections = closeConnections;
        _ui = uiContext;
        _events = Channel.CreateUnbounded<HostCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        _published = kernel.Snapshot;
        _generations.Add(new EpochGeneration(kernel.Epoch, _epochCts));
    }

    public MonitorKernel Kernel => _kernel;
    public MonitorSnapshot Snapshot => _published;
    public event Action<MonitorSnapshot>? SnapshotChanged;
    public int UiQueueDepth => Volatile.Read(ref _uiQueued);

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_shutdown.Token));
        _timer = _time.CreateTimer(
            static state => ((MonitorHost)state!).PostTick(),
            this,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50));
        PostCritical(() => Dispatch(_kernel.Tick()));
    }

    public void RequestCheckNow() => PostCritical(() => Dispatch(_kernel.RequestCheckNow()));

    public void SetPaused(bool paused) => PostCritical(() => Dispatch(_kernel.SetPaused(paused)));

    public void NotifyNetworkChange() => PostCritical(() => Dispatch(_kernel.BeginNewEpoch("network-change")));

    public void NotifyUnavailable(bool unavailable) => PostCritical(() => Dispatch(_kernel.SetNetworkUnavailable(unavailable)));

    public void NotifyInternalError(string message) => PostCritical(() => Dispatch(_kernel.NotifyInternalError(message)));

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= BeginDisposeAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task BeginDisposeAsync()
    {
        _timer?.Dispose();
        _shutdown.Cancel();
        _events.Writer.TryComplete();

        Task shutdown = StopLoopAndAttemptsAsync();
        try
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = shutdown.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private async Task StopLoopAndAttemptsAsync()
    {
        List<AttemptRuntime> running = [];
        try
        {
            // The loop owns dispatch and attempt registration. Stop it before taking
            // the attempt snapshot so an in-progress Dispatch cannot escape shutdown.
            if (_loop is not null)
                await _loop.ConfigureAwait(false);

            lock (_gate)
            {
                running = _attempts.Values.ToList();
                _attempts.Clear();
            }

            foreach (AttemptRuntime attempt in running)
                attempt.Cts.Cancel();

            await Task.WhenAll(running.Select(attempt => attempt.Task)).ConfigureAwait(false);
        }
        finally
        {
            if (running.Count > 0 || _loop is null || _loop.IsCompleted)
                ReleaseShutdownResources(running);
        }
    }

    private void ReleaseShutdownResources(IEnumerable<AttemptRuntime> attempts)
    {
        foreach (AttemptRuntime attempt in attempts)
            attempt.Cts.Dispose();

        lock (_gate)
        {
            foreach (EpochGeneration generation in _generations)
            {
                generation.Cts.Cancel();
                generation.Cts.Dispose();
            }

            _generations.Clear();
        }

        _shutdown.Dispose();
    }

    private void PostTick()
    {
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0)
        {
            _events.Writer.TryWrite(HostCommand.Tick);
        }
    }

    private void PostCritical(Action action)
    {
        _events.Writer.TryWrite(HostCommand.Critical(action));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (HostCommand command in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (command.IsTick)
                    {
                        Interlocked.Exchange(ref _tickQueued, 0);
                        Dispatch(_kernel.Tick());
                    }
                    else
                    {
                        command.Action?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Dispatch(_kernel.NotifyInternalError(ex.GetType().Name));
                    }
                    catch (Exception)
                    {
                        // Keep the reader alive even if error publication fails
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                Dispatch(_kernel.NotifyInternalError(ex.GetType().Name));
            }
            catch (Exception)
            {
            }
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
                    RotateEpoch(item.NetworkEpoch);
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
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || runtime.Epoch != _kernel.Epoch || runtime.RequestedCancel)
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

        PostCritical(() => CompleteAttempt(runtime, observation));
    }

    private void CompleteAttempt(AttemptRuntime runtime, EndpointObservation observation)
    {
        Dispatch(_kernel.ApplyObservation(observation));
        Dispatch(_kernel.NotifyPhysicalFinished(runtime.AttemptId));
        runtime.Cts.Dispose();
        lock (_gate)
        {
            _attempts.Remove(runtime.AttemptId);
        }

        MaybeRecycleRetiredGenerations();
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
                runtime.RequestedCancel = true;
                runtime.Cts.Cancel();
            }
        }
    }

    private void RotateEpoch(long newEpoch)
    {
        CancellationTokenSource retiring;
        lock (_gate)
        {
            retiring = _epochCts;
            retiring.Cancel();
            _epochCts = new CancellationTokenSource();
            _generations.Add(new EpochGeneration(newEpoch, _epochCts));
            _recyclePending++;
        }

        MaybeRecycleRetiredGenerations();
    }

    private void MaybeRecycleRetiredGenerations()
    {
        bool recycle = false;
        lock (_gate)
        {
            HashSet<long> liveEpochs = _attempts.Values.Select(a => a.Epoch).ToHashSet();
            for (int i = _generations.Count - 1; i >= 0; i--)
            {
                EpochGeneration generation = _generations[i];
                if (generation.Cts == _epochCts)
                {
                    continue;
                }

                if (liveEpochs.Contains(generation.Epoch))
                {
                    continue;
                }

                generation.Cts.Dispose();
                _generations.RemoveAt(i);
            }

            if (_recyclePending > 0 && _attempts.Count == 0)
            {
                _recyclePending = 0;
                recycle = true;
            }
        }

        if (recycle)
        {
            _closeConnections?.Invoke();
        }
    }

    private void Publish()
    {
        _published = _kernel.Snapshot;
        if (Interlocked.CompareExchange(ref _uiQueued, 1, 0) != 0)
        {
            return;
        }

        MarshalUi(FlushUi);
    }

    private void FlushUi()
    {
        MonitorSnapshot sent = _published;
        try
        {
            SnapshotChanged?.Invoke(sent);
        }
        catch (Exception ex)
        {
            NotifyInternalError(ex.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _uiQueued, 0);
            if (!ReferenceEquals(sent, _published)
                && Interlocked.CompareExchange(ref _uiQueued, 1, 0) == 0)
            {
                MarshalUi(FlushUi);
            }
        }
    }

    private void MarshalUi(Action action)
    {
        if (_ui is not null)
        {
            _ui.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private readonly record struct HostCommand(bool IsTick, Action? Action)
    {
        public static HostCommand Tick { get; } = new(true, null);
        public static HostCommand Critical(Action action) => new(false, action);
    }

    private sealed class EpochGeneration
    {
        public EpochGeneration(long epoch, CancellationTokenSource cts)
        {
            Epoch = epoch;
            Cts = cts;
        }

        public long Epoch { get; }
        public CancellationTokenSource Cts { get; }
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
        public bool RequestedCancel { get; set; }
    }
}
