using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class MonitorHostTests
{
    [Fact]
    public async Task Host_SlowReaderKeepsCompletionsAndReturnsToZeroInflight()
    {
        TimeProvider time = TimeProvider.System;
        var probe = new ScriptedProbe { Delay = TimeSpan.FromMilliseconds(5) };
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        await using var host = new MonitorHost(kernel, probe, time);
        int deliveries = 0;
        host.SnapshotChanged += _ =>
        {
            Thread.Sleep(2);
            Interlocked.Increment(ref deliveries);
        };
        host.Start();
        for (int i = 0; i < 80; i++)
        {
            host.RequestCheckNow();
            await Task.Delay(15);
        }

        await WaitUntilAsync(() => host.Kernel.PhysicalInflight == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, host.Kernel.PhysicalInflight);
        Assert.True(deliveries > 0);
        Assert.True(kernel.AppliedCompletions > 0);
    }

    [Fact]
    public async Task Host_EpochMidFlight_IsCancelledNotUnreachable()
    {
        TimeProvider time = TimeProvider.System;
        var probe = new ScriptedProbe { Delay = TimeSpan.FromSeconds(1) };
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        await using var host = new MonitorHost(kernel, probe, time);
        host.Start();
        await WaitUntilAsync(() => host.Kernel.PhysicalInflight > 0, TimeSpan.FromSeconds(2));
        host.NotifyNetworkChange();
        await WaitUntilAsync(() => probe.LastOutcome is ProbeOutcome.Cancelled, TimeSpan.FromSeconds(3));
        Assert.Equal(ProbeOutcome.Cancelled, probe.LastOutcome);
        Assert.NotEqual(ProbeOutcome.Unreachable, probe.LastOutcome);
    }

    [Fact]
    public async Task Host_ActionException_DoesNotKillLoop()
    {
        TimeProvider time = TimeProvider.System;
        var probe = new ScriptedProbe();
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        await using var host = new MonitorHost(kernel, probe, time);
        int throws = 0;
        host.SnapshotChanged += _ =>
        {
            if (Interlocked.Increment(ref throws) == 1)
            {
                throw new InvalidOperationException("ui");
            }
        };
        host.Start();
        await Task.Delay(300);

        Assert.True(throws >= 1);
        Assert.True(host.Kernel.PhysicalInflight >= 0);
    }

    [Fact]
    public async Task Host_CheckNowStorm_HonorsGlobalAndGroupLimits()
    {
        TimeProvider time = TimeProvider.System;
        var probe = new ScriptedProbe { Delay = TimeSpan.FromSeconds(2) };
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        await using var host = new MonitorHost(kernel, probe, time);
        host.Start();
        for (int i = 0; i < 40; i++)
        {
            host.RequestCheckNow();
        }

        await Task.Delay(200);
        Assert.InRange(host.Kernel.PhysicalInflight, 0, MonitorConstants.MaxPhysicalInflight);
        var clock = new MonotonicClock(TimeProvider.System);
        Assert.InRange(kernel.Limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(1)), 0, MonitorConstants.MaxStartsPerSecond);
        Assert.InRange(kernel.Limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(60)), 0, MonitorConstants.MaxStartsPerMinute);
    }

    [Fact]
    public async Task Host_ShutdownWithFourActiveProbes_Completes()
    {
        TimeProvider time = TimeProvider.System;
        var probe = new ScriptedProbe { Delay = TimeSpan.FromSeconds(30) };
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        var host = new MonitorHost(kernel, probe, time);
        host.Start();
        await WaitUntilAsync(() => host.Kernel.PhysicalInflight >= 4, TimeSpan.FromSeconds(3));
        var dispose = host.DisposeAsync();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Host_ShutdownWaitsForProbeRegisteredByDispatchInProgress()
    {
        var probe = new BlockingStartProbe();
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, TimeProvider.System);
        var host = new MonitorHost(kernel, probe, TimeProvider.System);
        Task? shutdown = null;
        try
        {
            host.Start();
            Assert.True(probe.Started.Wait(TimeSpan.FromSeconds(3)), "probe did not start");

            shutdown = host.DisposeAsync().AsTask();
            await Task.Delay(100);
            Assert.False(shutdown.IsCompleted, "shutdown completed while Dispatch was still creating an attempt");

            probe.AllowStartToReturn.Set();
            Assert.True(probe.WaitingForCompletion.Wait(TimeSpan.FromSeconds(2)), "probe task was not registered");
            await Task.Delay(100);
            Assert.False(shutdown.IsCompleted, "shutdown did not await the registered probe task");

            probe.Complete();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            probe.AllowStartToReturn.Set();
            probe.Complete();
            try { await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            probe.Dispose();
        }
    }

    [Fact]
    public async Task Host_Pause_DrainsInflight()
    {
        var probe = new ScriptedProbe { Delay = TimeSpan.FromSeconds(8) };
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, TimeProvider.System);
        await using var host = new MonitorHost(kernel, probe, TimeProvider.System);
        host.Start();
        await WaitUntilAsync(() => host.Kernel.PhysicalInflight > 0, TimeSpan.FromSeconds(2));
        host.SetPaused(true);
        await WaitUntilAsync(() => host.Kernel.PhysicalInflight == 0 && host.Snapshot.Paused, TimeSpan.FromSeconds(3));
        Assert.Equal(0, host.Kernel.PhysicalInflight);
        Assert.True(host.Snapshot.Paused);
        Assert.Equal(ProbeOutcome.Cancelled, probe.LastOutcome);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "condition not met");
    }

    private sealed class ScriptedProbe : IProbe
    {
        public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(10);
        public ProbeOutcome? LastOutcome { get; private set; }

        public async Task<EndpointObservation> ProbeAsync(
            EndpointDefinition endpoint,
            long networkEpoch,
            long attemptId,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LastOutcome = ProbeOutcome.Cancelled;
                throw;
            }

            LastOutcome = ProbeOutcome.Reachable;
            long ts = TimeProvider.System.GetTimestamp();
            return ObservationFactory.Create(
                endpoint,
                networkEpoch,
                attemptId,
                ts,
                ts,
                ProbeOutcome.Reachable,
                TimeSpan.FromMilliseconds(1),
                200);
        }
    }

    private sealed class BlockingStartProbe : IProbe, IDisposable
    {
        private readonly TaskCompletionSource<EndpointObservation> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EndpointDefinition? _endpoint;
        private long _epoch;
        private long _attemptId;
        private long _startedTimestamp;

        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim AllowStartToReturn { get; } = new();
        public ManualResetEventSlim WaitingForCompletion { get; } = new();

        public Task<EndpointObservation> ProbeAsync(
            EndpointDefinition endpoint,
            long networkEpoch,
            long attemptId,
            TimeSpan deadline,
            CancellationToken cancellationToken)
        {
            _endpoint = endpoint;
            _epoch = networkEpoch;
            _attemptId = attemptId;
            _startedTimestamp = TimeProvider.System.GetTimestamp();
            Started.Set();
            if (!AllowStartToReturn.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("test did not release probe start");
            WaitingForCompletion.Set();
            return _completion.Task;
        }

        public void Complete()
        {
            EndpointDefinition? endpoint = _endpoint;
            if (endpoint is null)
                return;
            long completed = TimeProvider.System.GetTimestamp();
            _completion.TrySetResult(ObservationFactory.Create(
                endpoint,
                _epoch,
                _attemptId,
                _startedTimestamp,
                completed,
                ProbeOutcome.Reachable,
                TimeProvider.System.GetElapsedTime(_startedTimestamp, completed),
                200));
        }

        public void Dispose()
        {
            Started.Dispose();
            AllowStartToReturn.Dispose();
            WaitingForCompletion.Dispose();
        }
    }
}
