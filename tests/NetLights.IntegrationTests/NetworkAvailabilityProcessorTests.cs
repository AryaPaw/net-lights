using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class NetworkAvailabilityProcessorTests
{
    [Fact]
    public async Task DelayedWorkerNotificationCannotReorderMonitorState()
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
        await Task.Run(() => processor.ObserveChange(false, start.AddMilliseconds(500)));
        processor.ObserveHeartbeat(true, start.AddSeconds(1));
        owner.Drain();

        Assert.Equal(
            [NetworkAvailabilitySignal.Unavailable, NetworkAvailabilitySignal.Available],
            signals);
    }

    [Fact]
    public async Task QueuedNotificationIsDiscardedOnceShutdownStarts()
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

        await Task.Run(() => processor.ObserveChange(false, DateTimeOffset.UtcNow));
        stopping = true;
        owner.Drain();

        Assert.Empty(signals);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state)
            => _pending.Enqueue((d, state));

        public void Drain()
        {
            while (_pending.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }
}
