using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class NetworkAvailabilityGateTests
{
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(500);

    [Theory]
    [InlineData(100)]
    [InlineData(499)]
    public void FastRestoreIsDeferredUntilDebounceThenHeartbeatDelivers(int restoreAfterMs)
    {
        var gate = new NetworkAvailabilityGate(_debounce);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        Assert.Equal(NetworkAvailabilitySignal.Unavailable, gate.OnChange(false, t0));
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnChange(true, t0.AddMilliseconds(restoreAfterMs)));
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnHeartbeat(true, t0.AddMilliseconds(restoreAfterMs)));
        Assert.Equal(NetworkAvailabilitySignal.Available, gate.OnHeartbeat(true, t0.AddMilliseconds(500)));
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnHeartbeat(true, t0.AddSeconds(30)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    public void RestoreAtOrAfterDebounceIsDeliveredOnChange(int restoreAfterMs)
    {
        var gate = new NetworkAvailabilityGate(_debounce);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        Assert.Equal(NetworkAvailabilitySignal.Unavailable, gate.OnChange(false, t0));
        Assert.Equal(NetworkAvailabilitySignal.Available, gate.OnChange(true, t0.AddMilliseconds(restoreAfterMs)));
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnHeartbeat(true, t0.AddSeconds(30)));
    }

    [Fact]
    public void DelayedNetworkEventCannotUndoNewerHeartbeatState()
    {
        var gate = new NetworkAvailabilityGate(_debounce);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

        Assert.Equal(NetworkAvailabilitySignal.Unavailable, gate.OnChange(false, t0));
        Assert.Equal(NetworkAvailabilitySignal.Available, gate.OnHeartbeat(true, t0.AddSeconds(1)));

        // System network notifications can arrive after the UI heartbeat that observed
        // the same transition. An older queued notification must not roll state back.
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnChange(false, t0.AddMilliseconds(500)));
        Assert.Equal(NetworkAvailabilitySignal.None, gate.OnHeartbeat(true, t0.AddSeconds(2)));
    }

    [Fact]
    public void ConcurrentSignalsRemainValidAndLaterStateWins()
    {
        var gate = new NetworkAvailabilityGate(_debounce);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        Parallel.For(0, 8, worker =>
        {
            for (int index = 0; index < 1_000; index++)
            {
                DateTimeOffset now = t0.AddMilliseconds(worker * 1_000 + index);
                NetworkAvailabilitySignal change = gate.OnChange((index & 1) == 0, now);
                NetworkAvailabilitySignal heartbeat = gate.OnHeartbeat((index & 1) == 0, now.AddMilliseconds(1));
                Assert.True(Enum.IsDefined(change));
                Assert.True(Enum.IsDefined(heartbeat));
            }
        });

        var orderedGate = new NetworkAvailabilityGate(_debounce);
        DateTimeOffset latest = t0.AddHours(1);
        Assert.Equal(NetworkAvailabilitySignal.Unavailable, orderedGate.OnChange(false, latest));
        Assert.Equal(NetworkAvailabilitySignal.Available, orderedGate.OnHeartbeat(true, latest.AddSeconds(1)));
        Assert.Equal(NetworkAvailabilitySignal.None, orderedGate.OnChange(false, latest.AddMilliseconds(500)));
    }
}
