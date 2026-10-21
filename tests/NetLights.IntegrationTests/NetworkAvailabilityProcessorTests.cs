using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class NetworkAvailabilityProcessorTests
{
    [Fact]
    public void DelayedWorkerNotificationCannotReorderMonitorState()
    {
        var owner = new QueuedSynchronizationContext();
        var signals = new List<NetworkAvailabilitySignal>();
        bool stopping = false;
        int ownerThread = Environment.CurrentManagedThreadId;
        var processor = new NetworkAvailabilityProcessor(
            owner,
            ownerThread,
            TimeSpan.FromMilliseconds(500),
            signals.Add,
            () => stopping,
            exception => throw new InvalidOperationException("network dispatch failed", exception));
        DateTimeOffset start = DateTimeOffset.Parse("2026-10-03T12:00:00Z");

        processor.ObserveChange(false, start);
        RunOnWorker(() => processor.ObserveChange(false, start.AddMilliseconds(500)));
        Assert.Equal(1, owner.PendingCount);
        processor.ObserveHeartbeat(true, start.AddSeconds(1));
        owner.Drain();

        Assert.Equal(
            [NetworkAvailabilitySignal.Unavailable, NetworkAvailabilitySignal.Available],
            signals);
    }

    [Fact]
    public void QueuedNotificationIsDiscardedOnceShutdownStarts()
    {
        var owner = new QueuedSynchronizationContext();
        var signals = new List<NetworkAvailabilitySignal>();
        bool stopping = false;
        var processor = new NetworkAvailabilityProcessor(
            owner,
            Environment.CurrentManagedThreadId,
            TimeSpan.FromMilliseconds(500),
            signals.Add,
            () => stopping,
            _ => { });

        RunOnWorker(() => processor.ObserveChange(false, DateTimeOffset.UtcNow));
        Assert.Equal(1, owner.PendingCount);
        Assert.Empty(signals);
        stopping = true;
        owner.Drain();

        Assert.Empty(signals);
    }

    private static void RunOnWorker(Action action)
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
                action();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "Network notification worker did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public int PendingCount => _pending.Count;

        public override void Post(SendOrPostCallback d, object? state)
            => _pending.Enqueue((d, state));

        public void Drain()
        {
            while (_pending.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }
}
