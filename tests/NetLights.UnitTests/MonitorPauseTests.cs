using Microsoft.Extensions.Time.Testing;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class MonitorPauseTests
{
    [Fact]
    public void Pause_StopsStartsUntilResumed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        Assert.Contains(kernel.Tick(), item => item.Kind == WorkKind.StartProbe);

        IReadOnlyList<WorkItem> paused = kernel.SetPaused(true);
        Assert.True(kernel.Snapshot.Paused);
        Assert.DoesNotContain(paused, item => item.Kind == WorkKind.StartProbe);
        Assert.DoesNotContain(kernel.Tick(), item => item.Kind == WorkKind.StartProbe);
        Assert.DoesNotContain(kernel.RequestCheckNow(), item => item.Kind == WorkKind.StartProbe);

        foreach (WorkItem item in paused)
        {
            if (item.Kind == WorkKind.CancelAttempt)
            {
                kernel.NotifyPhysicalFinished(item.AttemptId);
            }
        }

        time.Advance(TimeSpan.FromSeconds(10));
        kernel.SetPaused(false);
        Assert.False(kernel.Snapshot.Paused);
        Assert.Contains(kernel.Tick(), item => item.Kind == WorkKind.StartProbe);
    }
}
