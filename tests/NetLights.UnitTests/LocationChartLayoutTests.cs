using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class LocationChartLayoutTests
{
    [Fact]
    public void Empty_YieldsNoSegments()
    {
        LocationChartModel model = LocationChartLayout.Build([], DateTimeOffset.UtcNow, 400);
        Assert.Empty(model.Segments);
        Assert.Equal(default, model.OriginUtc);
        Assert.Equal(default, model.HorizonUtc);
    }

    [Fact]
    public void TwoStays_SplitOneToThreeHours()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        DateTimeOffset t1 = t0.AddHours(1);
        DateTimeOffset now = t0.AddHours(4);
        LocationStay[] stays =
        [
            new("DE", t0, t1),
            new("NL", t1, null)
        ];

        LocationChartModel model = LocationChartLayout.Build(stays, now, 400, minStayPx: 1);
        Assert.Equal(t0, model.OriginUtc);
        Assert.Equal(now, model.HorizonUtc);
        Assert.Equal(2, model.Segments.Count);
        Assert.Equal("DE", model.Segments[0].Iso);
        Assert.False(model.Segments[0].Live);
        Assert.Equal("NL", model.Segments[1].Iso);
        Assert.True(model.Segments[1].Live);
        Assert.Equal(0.25, model.Segments[0].Fraction, 3);
        Assert.Equal(0.75, model.Segments[1].Fraction, 3);
        Assert.Equal(100, model.Segments[0].WidthPx);
        Assert.Equal(300, model.Segments[1].WidthPx);
        Assert.Equal(0, model.Segments[0].StartPx);
        Assert.Equal(100, model.Segments[1].StartPx);
    }

    [Fact]
    public void MiddleGap_IsEmptyTrack()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        DateTimeOffset now = t0.AddHours(4);
        LocationStay[] stays =
        [
            new("DE", t0, t0.AddHours(1)),
            new("FI", t0.AddHours(2), null)
        ];

        LocationChartModel model = LocationChartLayout.Build(stays, now, 400, minStayPx: 1);
        Assert.Equal(3, model.Segments.Count);
        Assert.Equal("DE", model.Segments[0].Iso);
        Assert.Null(model.Segments[1].Iso);
        Assert.False(model.Segments[1].Live);
        Assert.Equal("FI", model.Segments[2].Iso);
        Assert.Equal(0.25, model.Segments[1].Fraction, 3);
        Assert.Equal(400, model.Segments.Sum(s => s.WidthPx));
    }

    [Fact]
    public void OpenStay_ExtendsToNow()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T10:00:00Z");
        DateTimeOffset now = t0.AddHours(2);
        LocationChartModel model = LocationChartLayout.Build(
            [new LocationStay("PL", t0, null)],
            now,
            200,
            minStayPx: 1);
        Assert.Single(model.Segments);
        Assert.True(model.Segments[0].Live);
        Assert.Equal(200, model.Segments[0].WidthPx);
        Assert.Equal(1, model.Segments[0].Fraction);
    }

    [Fact]
    public void SkipsDefaultStartAndInvalidIso()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        DateTimeOffset now = t0.AddHours(1);
        LocationChartModel model = LocationChartLayout.Build(
            [
                new LocationStay("??", t0, now),
                new LocationStay("DE", default, now),
                new LocationStay("NL", t0, null)
            ],
            now,
            100,
            minStayPx: 1);
        Assert.Single(model.Segments);
        Assert.Equal("NL", model.Segments[0].Iso);
    }

    [Fact]
    public void ShortStay_GetsMinimumPixelsWithoutOverflow()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        DateTimeOffset now = t0.AddHours(100);
        LocationStay[] stays =
        [
            new("DE", t0, t0.AddMinutes(1)),
            new("NL", t0.AddMinutes(1), null)
        ];

        LocationChartModel model = LocationChartLayout.Build(stays, now, 100, minStayPx: 6);
        Assert.Equal(2, model.Segments.Count);
        Assert.True(model.Segments[0].WidthPx >= 6);
        Assert.Equal(100, model.Segments.Sum(s => s.WidthPx));
        Assert.Equal(0, model.Segments[0].StartPx);
        Assert.Equal(model.Segments[0].WidthPx, model.Segments[1].StartPx);
    }

    [Fact]
    public void WindowStart_PadsLeadingGapAndFixesHorizon()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        DateTimeOffset window = now.AddHours(-12);
        LocationChartModel model = LocationChartLayout.Build(
            [new LocationStay("DE", now.AddHours(-2), null)],
            now,
            360,
            minStayPx: 1,
            windowStart: window);
        Assert.Equal(window, model.OriginUtc);
        Assert.Equal(now, model.HorizonUtc);
        Assert.Equal(2, model.Segments.Count);
        Assert.Null(model.Segments[0].Iso);
        Assert.Equal("DE", model.Segments[1].Iso);
        Assert.Equal(300, model.Segments[0].WidthPx);
        Assert.Equal(60, model.Segments[1].WidthPx);
    }

    [Fact]
    public void ZeroWidth_IsEmpty()
    {
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        LocationChartModel model = LocationChartLayout.Build(
            [new LocationStay("DE", t0, null)],
            t0.AddHours(1),
            0);
        Assert.Empty(model.Segments);
    }

    [Fact]
    public void ParseHours_MapsKnownWindowsAndFallsBackToTwelve()
    {
        Assert.Equal(LocationChartWindows.Hours3, LocationChartWindows.ParseHours(3));
        Assert.Equal(LocationChartWindows.Hours12, LocationChartWindows.ParseHours(12));
        Assert.Equal(LocationChartWindows.Days3, LocationChartWindows.ParseHours(72));
        Assert.Equal(LocationChartWindows.Default, LocationChartWindows.ParseHours(9));
        Assert.Equal(12, LocationChartWindows.ToHours(LocationChartWindows.Hours12));
        Assert.Equal(72, LocationChartWindows.ToHours(LocationChartWindows.Days3));
    }
}
