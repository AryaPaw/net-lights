using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class LocationHistoryTests
{
    [Fact]
    public void NoteIso_StartsOpenStay()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T09:00:00Z");
        Assert.True(log.NoteIso("de", t0));
        Assert.False(log.NoteIso("DE", t0.AddMinutes(3)));
        Assert.Single(log.Stays);
        Assert.Equal("DE", log.Current!.Iso);
        Assert.Null(log.Current.EndedUtc);
    }

    [Fact]
    public void Unknown_DoesNotCloseCurrentStay()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T09:00:00Z");
        log.NoteIso("DE", t0);
        Assert.False(log.NoteIso(null, t0.AddMinutes(1)));
        Assert.False(log.NoteIso("??", t0.AddMinutes(2)));
        Assert.Equal("DE", log.Current!.Iso);
        Assert.Null(log.Current.EndedUtc);
    }

    [Fact]
    public void CountryChange_ClosesPreviousAndOpensNext()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T09:00:00Z");
        log.NoteIso("DE", t0);
        Assert.True(log.NoteIso("NL", t0.AddHours(2)));
        Assert.Equal(2, log.Stays.Count);
        Assert.Equal("NL", log.Current!.Iso);
        Assert.Equal(t0.AddHours(2), log.Stays[0].EndedUtc);
        Assert.Equal("DE", log.Past[0].Iso);
        Assert.Equal(TimeSpan.FromHours(2), LocationCopy.Elapsed(log.Past[0], t0.AddHours(3)));
    }

    [Fact]
    public void SameCountrySoonAfterClose_Reopens()
    {
        DateTimeOffset ended = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var log = new LocationHistory([
            new LocationStay("DE", DateTimeOffset.Parse("2026-09-19T07:00:00Z"), ended)
        ]);
        Assert.True(log.NoteIso("DE", ended.AddMinutes(1)));
        Assert.Single(log.Stays);
        Assert.Null(log.Stays[0].EndedUtc);
    }

    [Fact]
    public void SameCountryAfterLongGap_StartsNewStay()
    {
        DateTimeOffset ended = DateTimeOffset.Parse("2026-09-19T08:00:00Z");
        var log = new LocationHistory([
            new LocationStay("DE", DateTimeOffset.Parse("2026-09-19T07:00:00Z"), ended)
        ]);
        Assert.True(log.NoteIso("DE", ended.AddHours(7)));
        Assert.Equal(2, log.Stays.Count);
        Assert.Equal(ended, log.Stays[0].EndedUtc);
        Assert.Null(log.Current!.EndedUtc);
    }

    [Fact]
    public void SameCountryAfterPcOff_DoesNotCountDowntime()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        DateTimeOffset lastSeen = DateTimeOffset.Parse("2026-09-19T13:00:00Z");
        DateTimeOffset boot = lastSeen.AddHours(3);
        var log = new LocationHistory(
            [new LocationStay("FI", started, null)],
            lastSeen);
        log.SplitIfStale(boot);
        Assert.Null(log.Current);
        Assert.Equal(lastSeen, log.Stays[0].EndedUtc);
        Assert.Equal(TimeSpan.FromHours(1), LocationCopy.Elapsed(log.Stays[0], boot));
        Assert.True(log.NoteIso("FI", boot));
        Assert.Equal(2, log.Stays.Count);
        Assert.Equal(boot, log.Current!.StartedUtc);
    }

    [Fact]
    public void SameCountryUnchanged_TouchesLastObserved()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-19T09:00:00Z");
        log.NoteIso("DE", t0);
        Assert.False(log.NoteIso("DE", t0.AddMinutes(20)));
        Assert.Equal(t0.AddMinutes(20), log.LastObservedUtc);
        Assert.Single(log.Stays);
        Assert.Null(log.Current!.EndedUtc);
    }

    [Fact]
    public void SplitIfStale_KeepsOpenStayWhenObservedRecently()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-09-19T09:00:00Z");
        DateTimeOffset now = started.AddHours(8);
        var log = new LocationHistory(
            [new LocationStay("DE", started, null)],
            now.AddSeconds(-30));
        log.SplitIfStale(now);
        Assert.Equal("DE", log.Current!.Iso);
        Assert.Null(log.Current.EndedUtc);
    }

    [Fact]
    public void KeepsManyStaysInsideThreeDays()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        for (int i = 0; i < 45; i++)
        {
            string iso = i % 2 == 0 ? "DE" : "NL";
            log.NoteIso(iso, t0.AddHours(i));
        }

        Assert.Equal(45, log.Stays.Count);
        Assert.Equal("DE", log.Current!.Iso);
    }

    [Fact]
    public void Prune_DropsClosedStaysOlderThanThreeDays()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var log = new LocationHistory([
            new LocationStay("DE", now.AddDays(-5), now.AddDays(-4)),
            new LocationStay("NL", now.AddHours(-2), null)
        ]);
        log.Prune(now);
        Assert.Single(log.Stays);
        Assert.Equal("NL", log.Stays[0].Iso);
    }

    [Fact]
    public void Prune_ClipsStayThatOverlapsRetention()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        DateTimeOffset cutoff = now - LocationHistory.Retention;
        var log = new LocationHistory([
            new LocationStay("FI", now.AddDays(-5), now.AddDays(-1))
        ]);
        log.Prune(now);
        Assert.Single(log.Stays);
        Assert.Equal(cutoff, log.Stays[0].StartedUtc);
        Assert.Equal(now.AddDays(-1), log.Stays[0].EndedUtc);
    }

    [Theory]
    [InlineData(-12, "0 с")]
    [InlineData(0, "0 с")]
    [InlineData(9, "9 с")]
    [InlineData(90, "1 мин")]
    [InlineData(3660, "1 ч 1 мин")]
    [InlineData(7200, "2 ч")]
    [InlineData(86400, "1 д")]
    [InlineData(90000, "1 д 1 ч")]
    public void Duration_UsesShortRussian(int seconds, string expected)
        => Assert.Equal(expected, LocationCopy.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void SealAndClose_HandleEmptyAndStaleOpenStays()
    {
        var empty = new LocationHistory();
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-19T18:00:00Z");
        empty.SealStaleOpens(now);
        empty.CloseOpen(now);
        Assert.Empty(empty.Stays);
        Assert.Empty(empty.Past);
        Assert.Null(empty.Current);

        var log = new LocationHistory();
        DateTimeOffset started = now.AddHours(-8);
        log.NoteIso("DE", started);
        Assert.Empty(log.Past);
        log.SealStaleOpens(now);
        Assert.Null(log.Current);
        Assert.Equal(started, log.Stays[0].EndedUtc);
        Assert.Single(log.Past);

        log.NoteIso("NL", now);
        log.CloseOpen(now.AddMinutes(20));
        Assert.Null(log.Current);
        Assert.Equal(now.AddMinutes(20), log.Stays[^1].EndedUtc);
        Assert.Equal(TimeSpan.Zero, LocationCopy.Elapsed(new LocationStay("DE", now, now.AddMinutes(-5)), now));
        Assert.Contains(" - ", LocationCopy.Range(started, now), StringComparison.Ordinal);
        Assert.Contains(
            LocationCopy.When(now.AddHours(1)).Split(',')[1].Trim(),
            LocationCopy.Range(now, now.AddHours(1)),
            StringComparison.Ordinal);
    }
}
