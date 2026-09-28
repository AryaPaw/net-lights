using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class AvailabilityHistoryTests
{
    [Fact]
    public void Observe_StoresOnlyChangesAndTracksPause()
    {
        var history = new AvailabilityHistory();
        DateTimeOffset t = DateTimeOffset.UtcNow;
        Assert.True(history.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Offline, false), t));
        Assert.False(history.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Offline, false), t.AddSeconds(5)));
        Assert.True(history.Observe(Snapshot(GroupAvailability.Limited, GroupAvailability.Offline, true), t.AddMinutes(1)));
        Assert.Equal(4, history.Spans.Count);
        Assert.Equal(t.AddMinutes(1), history.Spans[0].EndedUtc);
        Assert.True(history.Spans[^1].Paused);
    }

    [Fact]
    public void SealStale_ClosesAtLastObservation()
    {
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddHours(-4);
        var history = new AvailabilityHistory([new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, false, observed.AddMinutes(-1), null)], observed);
        history.SealStale(observed.AddHours(3));
        Assert.Equal(observed, history.Spans[0].EndedUtc);
    }

    [Fact]
    public void Touch_KeepsPausedSpansOpenWhileHeartbeatConfirmsPause()
    {
        DateTimeOffset pausedAt = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory();
        history.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Limited, true), pausedAt);

        DateTimeOffset heartbeat = pausedAt.AddMinutes(3);
        history.Touch(heartbeat);
        history.SealStale(heartbeat);

        Assert.Equal(heartbeat, history.LastObservedUtc);
        Assert.All(history.Spans, span => Assert.Null(span.EndedUtc));

        DateTimeOffset resumedAt = heartbeat.AddSeconds(5);
        history.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Limited, false), resumedAt);
        Assert.All(history.Spans.Take(2), span => Assert.Equal(resumedAt, span.EndedUtc));
        Assert.All(history.Spans.TakeLast(2), span => Assert.Null(span.EndedUtc));
    }

    [Fact]
    public void Prune_ClipsAnOpenPausedSpanToSevenDayRetention()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory([
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, true, now.AddDays(-10), null)
        ], now.AddDays(-10));

        history.Touch(now);
        history.Prune(now);

        AvailabilitySpan span = Assert.Single(history.Spans);
        Assert.Equal(now - AvailabilityHistory.Retention, span.StartedUtc);
        Assert.Null(span.EndedUtc);
    }

    [Fact]
    public void Retention_MatchesMaximumSevenDayChartWindow()
    {
        Assert.Equal(TimeSpan.FromDays(7), AvailabilityHistory.Retention);
        Assert.Equal(TimeSpan.FromDays(7), LocationHistory.Retention);
    }

    [Fact]
    public void Prune_ClipsAnOverlappingSpanAndRemovesOlderHistory()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
        DateTimeOffset cutoff = now - AvailabilityHistory.Retention;
        var history = new AvailabilityHistory(
        [
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, cutoff.AddDays(-2), cutoff),
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, false, cutoff.AddDays(-1), now)
        ]);

        history.Prune(now);

        AvailabilitySpan span = Assert.Single(history.Spans);
        Assert.Equal(cutoff, span.StartedUtc);
        Assert.Equal(now, span.EndedUtc);
    }

    private static MonitorSnapshot Snapshot(GroupAvailability ru, GroupAvailability world, bool paused)
        => new(1, DateTimeOffset.UtcNow, 0, new GroupSnapshot(EndpointGroup.Ru, ru, "", null, false, []), new GroupSnapshot(EndpointGroup.World, world, "", null, false, []), true, null, false, null, paused);
}
