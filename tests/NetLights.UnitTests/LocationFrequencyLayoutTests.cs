using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class LocationFrequencyLayoutTests
{
    [Fact]
    public void Build_CountsOnlyTransitionsInsideSelectedPeriod()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] history =
        [
            new("DE", start.AddHours(-1), start.AddMinutes(-30)),
            new("NL", start.AddMinutes(-30), start.AddMinutes(20)),
            new("DE", start.AddMinutes(20), start.AddMinutes(40)),
            new("NL", start.AddMinutes(40), null)
        ];

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, TimeSpan.FromHours(3));

        Assert.Equal(90, model.ChangesPerInterval.Count);
        Assert.Equal(2, model.ChangeCount);
        Assert.Equal(model.ChangeCount, model.ChangesPerInterval.Sum());
        Assert.Equal(2, model.CountryCount);
        Assert.Equal(1, model.ChangesPerInterval[9]);
        Assert.Equal(1, model.ChangesPerInterval[19]);
        Assert.Equal(TimeSpan.FromMinutes(2), model.IntervalDuration);
    }

    [Fact]
    public void Build_ExcludesStartBoundaryAndIncludesEndBoundaryExactlyOnce()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] history =
        [
            new("DE", start.AddHours(-2), start.AddHours(-1)),
            new("NL", start.AddHours(-1), start.AddMinutes(90)),
            new("DE", start.AddMinutes(90), now),
            new("FR", now, null)
        ];

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, TimeSpan.FromHours(3));

        Assert.Equal(2, model.ChangeCount);
        Assert.Equal(1, model.ChangesPerInterval[44]);
        Assert.Equal(1, model.ChangesPerInterval[89]);
        Assert.Equal(model.ChangeCount, model.ChangesPerInterval.Sum());
    }

    [Fact]
    public void Build_DoesNotCountSameCountryAfterGapAsCountryChange()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] history =
        [
            new("DE", start.AddHours(-1), start.AddMinutes(-30)),
            new("DE", start.AddMinutes(-10), null)
        ];

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, TimeSpan.FromHours(3));

        Assert.All(model.ChangesPerInterval, changes => Assert.Equal(0, changes));
        Assert.Equal(0, model.ChangeCount);
    }

    [Theory]
    [InlineData(3, 90, 2)]
    [InlineData(6, 90, 4)]
    [InlineData(12, 72, 10)]
    [InlineData(24, 96, 15)]
    [InlineData(72, 72, 60)]
    [InlineData(120, 60, 120)]
    [InlineData(168, 84, 120)]
    public void Build_BucketSumMatchesBruteForceForSelectedWindow(int hours, int intervals, int minutesPerInterval)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        TimeSpan window = TimeSpan.FromHours(hours);
        DateTimeOffset start = now - window;
        var history = new List<LocationStay>();
        for (int i = 0; i <= 32; i++)
        {
            DateTimeOffset started = start - window + TimeSpan.FromTicks(window.Ticks * i / 16);
            DateTimeOffset? ended = i == 32
                ? null
                : start - window + TimeSpan.FromTicks(window.Ticks * (i + 1) / 16);
            history.Add(new LocationStay(i % 2 == 0 ? "DE" : "NL", started, ended));
        }

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, window);
        int expected = history.Skip(1).Count(stay => stay.StartedUtc > start && stay.StartedUtc <= now);

        Assert.Equal(expected, model.ChangeCount);
        Assert.Equal(expected, model.ChangesPerInterval.Sum());
        Assert.Equal(intervals, model.ChangesPerInterval.Count);
        Assert.Equal(TimeSpan.FromMinutes(minutesPerInterval), model.IntervalDuration);
        Assert.InRange(model.PeakChangesPerInterval, 0, expected);
    }

    [Fact]
    public void Build_CapsIntervalsAtReadableRenderLimit()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        LocationFrequencyModel model = LocationFrequencyLayout.Build([], now, TimeSpan.FromDays(7));

        Assert.Equal(84, model.ChangesPerInterval.Count);
    }

    [Fact]
    public void Build_SeparatesQuietHistoryFromBucketsWithNoHistory()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] history =
        [
            new("DE", start.AddHours(1), start.AddHours(1).AddMinutes(10)),
            new("DE", start.AddHours(2), null)
        ];

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, TimeSpan.FromHours(3));

        Assert.Equal(model.ChangesPerInterval.Count, model.HistoryAvailablePerInterval.Count);
        Assert.Equal(0, model.ChangeCount);
        Assert.All(model.ChangesPerInterval, AssertZero);
        Assert.False(model.HistoryAvailablePerInterval[0]);
        Assert.True(model.HistoryAvailablePerInterval[30]);
        Assert.False(model.HistoryAvailablePerInterval[40]);
        Assert.True(model.HistoryAvailablePerInterval[70]);

        static void AssertZero(int value) => Assert.Equal(0, value);
    }

    [Fact]
    public void Build_MarksAllBucketsAsUnrecordedWhenHistoryIsEmpty()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        LocationFrequencyModel model = LocationFrequencyLayout.Build([], now, TimeSpan.FromHours(3));

        Assert.All(model.HistoryAvailablePerInterval, AssertUnrecorded);

        static void AssertUnrecorded(bool available) => Assert.False(available);
    }

    [Fact]
    public void Build_MarksBoundaryTransitionBucketAsKnownAfterHistoryGap()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] history =
        [
            new("DE", start.AddHours(-1), start.AddMinutes(57)),
            new("NL", start.AddMinutes(60), null)
        ];

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history, now, TimeSpan.FromHours(3));

        Assert.Equal(1, model.ChangesPerInterval[29]);
        Assert.True(model.HistoryAvailablePerInterval[29]);
    }

    [Fact]
    public void LocationHistory_RetainsRecentCountryChangesWithinSevenDays()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(-6);
        var history = new LocationHistory();
        for (int i = 0; i < 300; i++)
            history.NoteIso(i % 2 == 0 ? "DE" : "NL", start.AddMinutes(i * 14));

        Assert.Equal(300, history.Stays.Count);
    }

    [Fact]
    public void Build_SumsThousandsOfVisibleTransitionsWithoutDroppingAny()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        DateTimeOffset start = now.AddDays(-3);
        var history = new LocationHistory();
        for (int i = 0; i < 2161; i++)
            history.NoteIso(i % 2 == 0 ? "DE" : "NL", start.AddMinutes(i * 2));

        LocationFrequencyModel model = LocationFrequencyLayout.Build(history.Stays, now, TimeSpan.FromDays(3));

        Assert.Equal(2160, model.ChangeCount);
        Assert.Equal(2160, model.ChangesPerInterval.Sum());
    }
}
