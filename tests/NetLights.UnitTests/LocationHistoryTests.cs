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
        var log = new LocationHistory([
            new LocationStay("DE", DateTimeOffset.Parse("2026-09-19T07:00:00Z"), DateTimeOffset.Parse("2026-09-19T08:00:00Z"))
        ]);
        Assert.True(log.NoteIso("DE", DateTimeOffset.Parse("2026-09-19T08:10:00Z")));
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
    public void CapsAtFortyStays()
    {
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        for (int i = 0; i < 45; i++)
        {
            string iso = i % 2 == 0 ? "DE" : "NL";
            log.NoteIso(iso, t0.AddHours(i));
        }

        Assert.Equal(LocationHistory.MaxStays, log.Stays.Count);
        Assert.Equal("DE", log.Current!.Iso);
    }

    [Theory]
    [InlineData(0, "0 с")]
    [InlineData(9, "9 с")]
    [InlineData(90, "1 мин")]
    [InlineData(3660, "1 ч 1 мин")]
    [InlineData(7200, "2 ч")]
    [InlineData(90000, "1 д 1 ч")]
    public void Duration_UsesShortRussian(int seconds, string expected)
        => Assert.Equal(expected, LocationCopy.Duration(TimeSpan.FromSeconds(seconds)));
}
