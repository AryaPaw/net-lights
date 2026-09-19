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
}
