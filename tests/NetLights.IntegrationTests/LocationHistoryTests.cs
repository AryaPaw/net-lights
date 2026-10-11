using System.Drawing;
using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class LocationHistoryTests
{
    [Fact]
    public void AvailabilityChart_CachesSegmentsBetweenClockUpdatesAndRebuildsOnStateChange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory();
        using var chart = new AvailabilityHistoryChart { Width = 640, Height = 160 };
        history.Observe(StatusSnapshot(GroupAvailability.Online, GroupAvailability.Limited, false), now);
        chart.Bind(history, now, TimeSpan.FromHours(12));
        int initialBuilds = chart.SegmentBuildCount;

        chart.Bind(history, now.AddSeconds(1), TimeSpan.FromHours(12));
        Assert.Equal(initialBuilds, chart.SegmentBuildCount);

        chart.Height += 8;
        Assert.Equal(initialBuilds, chart.SegmentBuildCount);

        chart.Width += 8;
        Assert.Equal(initialBuilds + 1, chart.SegmentBuildCount);

        history.Observe(StatusSnapshot(GroupAvailability.Offline, GroupAvailability.Limited, false), now.AddSeconds(2));
        chart.Bind(history, now.AddSeconds(2), TimeSpan.FromHours(12));
        Assert.Equal(initialBuilds + 2, chart.SegmentBuildCount);
    }

    [Fact]
    public void AvailabilityChart_TracksAreSixteenLogicalPixelsHigh()
    {
        using var chart = new AvailabilityHistoryChart { Width = 640, Height = 160 };
        int expected = (16 * chart.DeviceDpi + 48) / 96;
        Assert.Equal(expected, chart.TrackFor(EndpointGroup.Ru).Height);
        Assert.Equal(expected, chart.TrackFor(EndpointGroup.World).Height);
    }

    [Fact]
    public void LocationDebugCharts_AreNotCreatedUntilExpanded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new LocationHistory();
        history.NoteIso("DE", now);
        history.NoteObservation(new LocationObservation(now, new string('a', 64), "DE", "DE", "DE", null,
            "v2fly-r1", false, false, false, true, "203.0.113.8", ResolutionSource: "country.is"));
        using var panel = new LocationTimelinePanel(TimeProvider.System) { Width = 800, Height = 600 };

        panel.Bind(history, GeoCountryDisplay.Confirmed("DE"));
        Assert.Equal(0, panel.DebugSourceChartCount);
        panel.ApplyDebugSourceCharts(true);
        Assert.Equal(3, panel.DebugSourceChartCount);
        panel.ApplyDebugSourceCharts(false);
        Assert.Equal(0, panel.DebugSourceChartCount);
    }

    [Fact]
    public void AvailabilityChart_HoverUsesCachedStatusMapForTheCorrectRow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset start = now.AddHours(-1);
        DateTimeOffset middle = start.AddMinutes(30);
        var history = new AvailabilityHistory(
        [
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, false, start, middle),
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, middle, now),
            new AvailabilitySpan(EndpointGroup.World, GroupAvailability.Limited, true, start, now)
        ], now);
        using var chart = new MouseMoveAvailabilityChart { Width = 640, Height = 160 };
        chart.Bind(history, now, TimeSpan.FromHours(1));
        Rectangle providerTrack = chart.TrackFor(EndpointGroup.Ru);
        Rectangle worldTrack = chart.TrackFor(EndpointGroup.World);

        chart.DispatchMouseMove(new Point(providerTrack.Left + providerTrack.Width * 3 / 4, providerTrack.Top + 2));
        Assert.Equal(GroupAvailability.Offline, chart.HoveredState);
        Assert.False(chart.HoveredIsPaused);

        chart.DispatchMouseMove(new Point(worldTrack.Left + worldTrack.Width / 2, worldTrack.Top + 2));
        Assert.Equal(GroupAvailability.Limited, chart.HoveredState);
        Assert.True(chart.HoveredIsPaused);
    }

    [Fact]
    public void AvailabilityChart_ShortMarkerOwnsHoverAndPartialRepaintClearsWholeHoverLine()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory([
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, false, now.AddHours(-6), now.AddMinutes(-61)),
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, now.AddMinutes(-61), now.AddMinutes(-60)),
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Online, false, now.AddMinutes(-60), now)
        ], now);
        using var chart = new MouseMoveAvailabilityChart { Width = 640, Height = 160 };
        chart.Bind(history, now, TimeSpan.FromHours(12));
        using var beforeHover = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(beforeHover, new Rectangle(Point.Empty, chart.Size));
        Rectangle marker = Assert.Single(chart.ShortMarkerRectangles(EndpointGroup.Ru));
        Point markerCenter = new(marker.Left + marker.Width / 2, marker.Top + marker.Height / 2);

        chart.DispatchMouseMove(markerCenter);
        Assert.Equal(GroupAvailability.Offline, chart.HoveredState);

        chart.DispatchMouseMove(new Point(markerCenter.X, marker.Top));
        Assert.Equal(GroupAvailability.Offline, chart.HoveredState);

        using var full = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(full, new Rectangle(Point.Empty, chart.Size));
        int lineX = Assert.IsType<int>(chart.HoveredLineX);
        Rectangle providerTrack = chart.TrackFor(EndpointGroup.Ru);
        Rectangle hoverInvalidation = chart.HoverInvalidationBounds(lineX);
        int lineTop = providerTrack.Top - chart.LogicalToDeviceUnits(3);
        int lineBottom = providerTrack.Bottom + chart.LogicalToDeviceUnits(3);
        Assert.True(hoverInvalidation.Contains(new Point(lineX, lineTop)));
        Assert.True(hoverInvalidation.Contains(new Point(lineX, lineBottom)));
        Assert.NotEqual(beforeHover.GetPixel(lineX, lineTop).ToArgb(), full.GetPixel(lineX, lineTop).ToArgb());
        Assert.NotEqual(beforeHover.GetPixel(lineX, lineBottom).ToArgb(), full.GetPixel(lineX, lineBottom).ToArgb());

        using var partial = new Bitmap(chart.Width, chart.Height);
        using (Graphics graphics = Graphics.FromImage(partial))
            graphics.Clear(chart.BackColor);
        Rectangle capClip = new(marker.Left, marker.Top, marker.Width, chart.LogicalToDeviceUnits(2));
        chart.PaintClip(partial, capClip);
        for (int y = capClip.Top; y < capClip.Bottom; y++)
        for (int x = capClip.Left; x < capClip.Right; x++)
            Assert.Equal(full.GetPixel(x, y).ToArgb(), partial.GetPixel(x, y).ToArgb());

        using var afterMouseLeave = new Bitmap(chart.Width, chart.Height);
        using (Graphics graphics = Graphics.FromImage(afterMouseLeave))
            graphics.DrawImageUnscaled(full, 0, 0);
        chart.DispatchMouseLeave();
        chart.PaintClip(afterMouseLeave, hoverInvalidation);
        Assert.Equal(beforeHover.GetPixel(lineX, lineTop).ToArgb(), afterMouseLeave.GetPixel(lineX, lineTop).ToArgb());
        Assert.Equal(beforeHover.GetPixel(lineX, lineBottom).ToArgb(), afterMouseLeave.GetPixel(lineX, lineBottom).ToArgb());
    }

    [Fact]
    public void FrequencyChart_HoverMapsRightmostPlotPixelToLatestSample()
    {
        Assert.Equal(99, LocationFrequencyChart.SampleIndexAtX(219, 120, 100, 101));
        Assert.Equal(0, LocationFrequencyChart.SampleIndexAtX(120, 120, 100, 101));
    }

    [Fact]
    public void FrequencyChart_ClockRefreshKeepsTooltipUnderStationaryPointer()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var form = new Form { Width = 640, Height = 260, ShowInTaskbar = false };
        var chart = new LocationFrequencyChart { Dock = DockStyle.Fill };
        form.Controls.Add(chart);
        form.Show();
        chart.Bind([new LocationStay("DE", now.AddMinutes(-20), null)], 1, now, TimeSpan.FromHours(3));
        Rectangle plot = chart.PlotBounds();
        chart.DispatchMouseMoveForTests(new Point(plot.Left + plot.Width * 95 / 100, plot.Top + plot.Height / 2));
        Assert.StartsWith("0 смен", chart.HoverTipForTests, StringComparison.Ordinal);

        chart.Tick(now.AddSeconds(31));

        Assert.StartsWith("0 смен", chart.HoverTipForTests, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationsPanel_ChangingWindowUpdatesBothChartPeriods()
    {
        using var form = new Form { Width = 620, Height = 500, ShowInTaskbar = false };
        var panel = new LocationTimelinePanel(TimeProvider.System) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        var history = new LocationHistory();
        history.NoteIso("DE", DateTimeOffset.UtcNow.AddHours(-1));
        panel.Bind(history, GeoCountryDisplay.Confirmed("DE"));
        LocationFrequencyChart frequency = FindNamed<LocationFrequencyChart>(panel, "Частота смен страны");

        Assert.Equal(TimeSpan.FromHours(12), frequency.Model.EndUtc - frequency.Model.StartUtc);
        Assert.Contains("1 страна", frequency.Summary, StringComparison.Ordinal);
        Assert.True(panel.ApplyChartWindowHours(3));
        Assert.Equal(TimeSpan.FromHours(3), frequency.Model.EndUtc - frequency.Model.StartUtc);
    }

    [Fact]
    public void LocationsPanel_NewObservationReusesExistingChartControls()
    {
        using var form = new Form { Width = 700, Height = 600, ShowInTaskbar = false };
        var panel = new LocationTimelinePanel(TimeProvider.System) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new LocationHistory();
        history.NoteObservation(new LocationObservation(now.AddMinutes(-2), null, "DE", "DE", "DE", null,
            "test", false, false, false, true, "203.0.113.10", now.AddMinutes(-2)));
        panel.Bind(history, GeoCountryDisplay.Confirmed("DE"));
        int structureBuilds = panel.StructureBuildCount;
        LocationFrequencyChart frequency = FindNamed<LocationFrequencyChart>(panel, "Частота смен страны");

        history.NoteObservation(new LocationObservation(now, null, "DE", "DE", "DE", null,
            "test", false, false, false, true, "203.0.113.10", now));
        panel.Bind(history, GeoCountryDisplay.Confirmed("DE"));

        Assert.Equal(structureBuilds, panel.StructureBuildCount);
        Assert.Same(frequency, FindNamed<LocationFrequencyChart>(panel, "Частота смен страны"));
    }

    [Fact]
    public void LocationsPanel_RendersVirtualRowsAndDebugToggleUsesVisibleState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var stays = new List<LocationStay>(151);
        for (int i = 0; i < 148; i++)
        {
            DateTimeOffset start = now.AddMinutes(-650 + i * 2);
            stays.Add(new LocationStay(i % 2 == 0 ? "DE" : "FR", start, start.AddMinutes(1)));
        }
        stays.Add(new LocationStay("DE", now.AddHours(-2), now.AddHours(-1)));
        stays.Add(new LocationStay("DE", now.AddHours(-1).AddSeconds(30), now.AddMinutes(-30)));
        stays.Add(new LocationStay("PL", now.AddMinutes(-10), null));
        var history = new LocationHistory(stays, now);
        history.NoteObservation(new LocationObservation(now.AddMinutes(-90), null, "DE", "DE", "DE", null,
            "test", false, false, false, true, "203.0.113.8", now.AddMinutes(-90)));
        history.NoteObservation(new LocationObservation(now.AddMinutes(-45), null, "DE", "DE", "DE", null,
            "test", false, false, false, true, "203.0.113.9", now.AddMinutes(-45)));
        history.NoteObservation(new LocationObservation(now.AddMinutes(-5), null, "PL", "PL", "PL", null,
            "test", false, false, false, true, "203.0.113.10", now.AddMinutes(-5)));

        using var form = new Form { Width = 800, Height = 520, ShowInTaskbar = false };
        using var panel = new LocationTimelinePanel(TimeProvider.System) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        var debugChanges = new List<bool>();
        panel.DebugSourceChartsChanged = debugChanges.Add;
        panel.Bind(history, GeoCountryDisplay.Confirmed("RU"));

        Assert.Equal(151, panel.VisibleHistoryRowCount);
        Assert.True(panel.AutoScrollMinSize.Height > panel.ClientSize.Height);
        Assert.Contains("Россия · RU", panel.HistoryRowTipForTests(0), StringComparison.Ordinal);
        Assert.Contains("Последний публичный IP: 203.0.113.10", panel.HistoryRowTipForTests(1), StringComparison.Ordinal);
        Assert.Contains("Кратких перерывов без подтверждения: 1", panel.HistoryRowTipForTests(2), StringComparison.Ordinal);
        Assert.Contains("203.0.113.9", panel.HistoryRowTipForTests(2), StringComparison.Ordinal);
        using var visibleRows = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(visibleRows, new Rectangle(Point.Empty, panel.Size));

        ThemedButton toggle = FindNamed<ThemedButton>(panel, "toggleLocationDebugSources")!;
        toggle.PerformClick();
        Assert.Equal(new[] { true }, debugChanges);
        Assert.Equal(3, panel.DebugSourceChartCount);
        using var debugView = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(debugView, new Rectangle(Point.Empty, panel.Size));
        toggle.PerformClick();
        Assert.Equal(new[] { true, false }, debugChanges);
        Assert.Equal(0, panel.DebugSourceChartCount);
        Assert.False(panel.HorizontalScroll.Visible);

        panel.AutoScrollPosition = new Point(0, panel.AutoScrollMinSize.Height);
        Rectangle last = panel.HistoryRowBounds(panel.VisibleHistoryRowCount - 1);
        Assert.True(panel.ClientRectangle.IntersectsWith(last), $"Last virtual row is not reachable: {last}");
        using var scrolledRows = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(scrolledRows, new Rectangle(Point.Empty, panel.Size));
    }

    [Fact]
    public void LocationSpanChart_UsesCardInsetWhileFrequencyPlotReservesYAxisSpace()
    {
        using var span = new LocationSpanChart();
        using var frequency = new LocationFrequencyChart();
        using var local = new LocationSourceHistoryChart(LocationSourceKind.LocalDatabase);
        using var online = new LocationSourceHistoryChart(LocationSourceKind.Online);
        using var iplocate = new LocationSourceHistoryChart(LocationSourceKind.IplocateDatabase);
        span.Width = 600;
        frequency.Width = 600;
        local.Width = online.Width = 600;
        iplocate.Width = 600;

        Rectangle spanPlot = span.TrackBounds();
        Rectangle frequencyPlot = frequency.PlotBounds();
        Assert.Equal(14, spanPlot.Left);
        Assert.Equal(spanPlot.Left, local.TrackBounds().Left);
        Assert.Equal(spanPlot.Left, online.TrackBounds().Left);
        Assert.Equal(spanPlot.Right, local.TrackBounds().Right);
        Assert.Equal(spanPlot.Right, online.TrackBounds().Right);
        Assert.Equal(spanPlot.Left, iplocate.TrackBounds().Left);
        Assert.Equal(spanPlot.Right, iplocate.TrackBounds().Right);
        Assert.True(frequencyPlot.Left > spanPlot.Left);
        Assert.Equal(spanPlot.Right, frequencyPlot.Right);
    }

    [Fact]
    public void SourceChart_HitTestPrioritizesVisibleShortConflictMarker()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        DateTimeOffset conflictAt = now.AddMinutes(-15);
        LocationObservation[] observations =
        [
            new(now.AddMinutes(-30), null, "DE", "DE", "DE", null, "test",
                false, false, false, true, null, conflictAt),
            new(conflictAt, null, null, "TR", "DE", null, "test",
                false, false, false, false, null, conflictAt),
            new(conflictAt.AddSeconds(5), null, "DE", "DE", "DE", null, "test",
                false, false, false, true, null, conflictAt.AddSeconds(5))
        ];
        using var chart = new LocationSourceHistoryChart(LocationSourceKind.LocalDatabase)
        {
            Width = 600,
            Height = 112
        };
        chart.Bind(observations, [], now, TimeSpan.FromHours(1));
        Rectangle track = chart.TrackBounds();
        int x = track.Left + track.Width * 3 / 4;

        Assert.True(chart.HitTest(new Point(x, track.Top + track.Height / 2))?.Conflict);
    }

    [Fact]
    public void SourceCharts_RenderCountryGapsAndConflictsAndDescribeTheSameIntervals()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        DateTimeOffset conflictAt = now.AddMinutes(-20);
        DateTimeOffset agreedAt = now.AddMinutes(-8);
        LocationObservation[] observations =
        [
            new(conflictAt, new string('a', 64), "DE", "FR", "DE", "FR", "v2fly-test",
                true, false, false, true, "203.0.113.8", now.AddMinutes(-8), "JP", "iplocate-test", "country.is"),
            new(agreedAt, new string('b', 64), "DE", "DE", "DE", null, "v2fly-test",
                false, false, false, true, "203.0.113.9", now.AddMinutes(-4), "DE", "iplocate-test", "country.is"),
            new(now.AddMinutes(-1), null, null, null, null, null, null,
                false, false, false, false)
        ];
        LocationStay[] stays = [new("DE", conflictAt, null)];
        using var local = new LocationSourceHistoryChart(LocationSourceKind.LocalDatabase) { Width = 600, Height = 112 };
        using var online = new LocationSourceHistoryChart(LocationSourceKind.Online) { Width = 600, Height = 112 };
        using var iplocate = new LocationSourceHistoryChart(LocationSourceKind.IplocateDatabase) { Width = 600, Height = 112 };
        local.Bind(observations, stays, now, TimeSpan.FromHours(1));
        online.Bind(observations, stays, now, TimeSpan.FromHours(1));
        iplocate.Bind(observations, stays, now, TimeSpan.FromHours(1));

        using var localImage = new Bitmap(local.Width, local.Height);
        using var onlineImage = new Bitmap(online.Width, online.Height);
        using var iplocateImage = new Bitmap(iplocate.Width, iplocate.Height);
        local.DrawToBitmap(localImage, new Rectangle(Point.Empty, local.Size));
        online.DrawToBitmap(onlineImage, new Rectangle(Point.Empty, online.Size));
        iplocate.DrawToBitmap(iplocateImage, new Rectangle(Point.Empty, iplocate.Size));

        Rectangle track = local.TrackBounds();
        int conflictX = track.Left + (track.Width * 2 / 3) + track.Width / 12;
        Point conflictPoint = new(conflictX, track.Top + track.Height / 2);
        LocationSourceSpan localSpan = Assert.IsType<LocationSourceSpan>(local.HitTest(conflictPoint));
        LocationSourceSpan onlineSpan = Assert.IsType<LocationSourceSpan>(online.HitTest(conflictPoint));
        LocationSourceSpan iplocateSpan = Assert.IsType<LocationSourceSpan>(iplocate.HitTest(conflictPoint));
        Assert.True(localSpan.Conflict);
        Assert.Equal("FR", localSpan.LocalCountry);
        Assert.Equal("DE", onlineSpan.OnlineCountry);
        Assert.Equal("JP", iplocateSpan.IplocateCountry);
        Assert.Contains("КОНФЛИКТ", local.BuildTipForTests(localSpan), StringComparison.Ordinal);
        Assert.Contains("Публичный IP: 203.0.113.8", online.BuildTipForTests(onlineSpan), StringComparison.Ordinal);
        Assert.Contains("IPLocate.io · CC BY-SA 4.0", iplocate.BuildTipForTests(iplocateSpan), StringComparison.Ordinal);
        Assert.NotEqual(local.BackColor.ToArgb(), localImage.GetPixel(conflictX, track.Top + 1).ToArgb());
        Assert.NotEqual(online.BackColor.ToArgb(), onlineImage.GetPixel(conflictX, track.Top + 1).ToArgb());
        Assert.NotEqual(iplocate.BackColor.ToArgb(), iplocateImage.GetPixel(conflictX, track.Top + 1).ToArgb());

        local.Tick(now.AddSeconds(31));
        Assert.True(local.SegmentCount > 0);
    }

    [Fact]
    public void SourceChart_DistinguishesNoChecksFromARecordedNoResult()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T12:00:30Z");
        using var chart = new LocationSourceHistoryChart(LocationSourceKind.Online) { Width = 600, Height = 112 };
        using var image = new Bitmap(chart.Width, chart.Height);
        chart.Bind([], [], now, TimeSpan.FromHours(1));
        chart.DrawToBitmap(image, new Rectangle(Point.Empty, chart.Size));
        LocationSourceSpan noChecks = Assert.IsType<LocationSourceSpan>(chart.HitTest(new Point(100, chart.TrackBounds().Top + 4)));
        Assert.Null(noChecks.FirstObservedUtc);
        Assert.Contains("Проверок в этом промежутке нет", chart.BuildTipForTests(noChecks), StringComparison.Ordinal);

        DateTimeOffset observed = now.AddMinutes(-1);
        LocationObservation[] unknown =
        [new(observed, null, null, null, null, null, null, false, false, false, false)];
        chart.Bind(unknown, [], now, TimeSpan.FromMinutes(1));
        using var noResultImage = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(noResultImage, new Rectangle(Point.Empty, chart.Size));
        LocationSourceSpan noResult = Assert.IsType<LocationSourceSpan>(chart.HitTest(new Point(100, chart.TrackBounds().Top + 4)));
        Assert.Equal(observed, noResult.FirstObservedUtc);
        Assert.Contains("нет результата", chart.BuildTipForTests(noResult), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не означает потерю интернета", chart.BuildTipForTests(noResult), StringComparison.Ordinal);
    }

    [Fact]
    public void LocationSpanChart_OutlinesOnlyHoveredCountrySegment()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        LocationStay[] stays =
        [
            new("DE", now.AddHours(-2), now.AddHours(-1)),
            new("PL", now.AddHours(-1), null)
        ];
        using var chart = new LocationSpanChart { Width = 600, Height = 126 };
        chart.Bind(stays, now, now.AddHours(-3));
        Rectangle track = chart.TrackBounds();
        Point german = new(track.Left + track.Width / 2, track.Top + track.Height / 2);
        Point polish = new(track.Left + track.Width * 5 / 6, track.Top + track.Height / 2);

        using var before = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(before, new Rectangle(Point.Empty, chart.Size));

        chart.DispatchMouseMoveForTests(german);
        Assert.Equal("DE", chart.HoveredSegmentForTests?.Iso);
        using var highlighted = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(highlighted, new Rectangle(Point.Empty, chart.Size));
        Assert.NotEqual(before.GetPixel(german.X, track.Top - 3).ToArgb(),
            highlighted.GetPixel(german.X, track.Top - 3).ToArgb());
        chart.DispatchMouseMoveForTests(polish);
        Assert.Equal("PL", chart.HoveredSegmentForTests?.Iso);
        chart.DispatchMouseMoveForTests(new Point(track.Left, track.Top - 1));
        Assert.Null(chart.HoveredSegmentForTests);
    }

    [Fact]
    public void LocationSpanChart_LabelsEachRepeatedCountrySegmentThatFits()
    {
        var model = new LocationChartModel(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
        [
            new("PL", 0, 100, false, 0.4, DateTimeOffset.UtcNow.AddMinutes(-60), DateTimeOffset.UtcNow.AddMinutes(-36)),
            new(null, 100, 10, false, 0.04, DateTimeOffset.UtcNow.AddMinutes(-36), DateTimeOffset.UtcNow.AddMinutes(-33.6)),
            new("PL", 110, 120, true, 0.56, DateTimeOffset.UtcNow.AddMinutes(-33.6), DateTimeOffset.UtcNow)
        ]);

        IReadOnlyList<LocationChartSegment> labels = LocationSpanChart.CountryLabelCandidates(model, minimumWidth: 20);

        Assert.Equal(2, labels.Count);
        Assert.All(labels, segment => Assert.Equal("PL", segment.Iso));
    }

    [Fact]
    public void LocationSpanChart_GroupsCrowdedShortEventLabelsWithoutOverlap()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        LocationChartEvent[] events =
        [
            new("DE", now, now, 50),
            new("PL", now.AddSeconds(1), now.AddSeconds(1), 60),
            new("RU", now.AddSeconds(2), now.AddSeconds(2), 70),
            new("FR", now.AddMinutes(1), now.AddMinutes(1), 190),
            new("PL", now.AddMinutes(2), now.AddMinutes(2), 198),
            new("FI", now.AddMinutes(3), now.AddMinutes(3), 500)
        ];

        IReadOnlyList<LocationChartEventLabel> labels = LocationSpanChart.BuildEventLabels(events, trackWidth: 600, maximumLabelWidth: 120, minimumLabelSpacing: 36);

        Assert.Equal(3, labels.Count);
        Assert.Equal("DE · PL · RU", labels[0].Text);
        Assert.Equal("FR · PL", labels[1].Text);
        Assert.Equal("FI", labels[2].Text);
        Assert.Equal(new[] { 3, 2, 1 }, labels.Select(label => label.Events.Count));
        for (int i = 1; i < labels.Count; i++)
        {
            int previousRight = labels[i - 1].CenterPx + labels[i - 1].WidthPx / 2;
            int nextLeft = labels[i].CenterPx - labels[i].WidthPx / 2;
            Assert.True(nextLeft >= previousRight + 4, $"Labels overlap: {labels[i - 1].Text} / {labels[i].Text}");
        }

        LocationChartEvent[] dense = Enumerable.Range(0, 10)
            .Select(index => new LocationChartEvent($"C{index}", now.AddSeconds(index), now.AddSeconds(index), 100 + index))
            .ToArray();
        LocationChartEventLabel compact = Assert.Single(LocationSpanChart.BuildEventLabels(dense, 600, 120, 36));
        Assert.Equal(10, compact.Events.Count);
        Assert.InRange(compact.WidthPx, 1, 120);
        Assert.Contains("+", compact.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationHistoryRows_CombineSameCountryAcrossBriefUnconfirmedGaps()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocationStay[] stays =
        [
            new("PL", now.AddMinutes(-30), now.AddMinutes(-20)),
            new("PL", now.AddMinutes(-20).AddSeconds(2), now.AddMinutes(-10)),
            new("PL", now.AddMinutes(-10).AddSeconds(2), null)
        ];
        var history = new LocationHistory(stays, now);
        using var panel = new LocationTimelinePanel(TimeProvider.System) { Width = 700, Height = 600 };
        panel.ApplyChartWindowHours(3);
        panel.Bind(history, GeoCountryDisplay.Confirmed("PL"));

        Assert.Equal(1, panel.VisibleHistoryRowCount);
        Assert.Contains("Кратких перерывов без подтверждения: 2", panel.HistoryRowTipForTests(0), StringComparison.Ordinal);

        LocationStay[] longGap =
        [
            new("PL", now.AddMinutes(-30), now.AddMinutes(-20)),
            new("PL", now.AddMinutes(-16), null)
        ];
        panel.Bind(new LocationHistory(longGap, now), GeoCountryDisplay.Confirmed("PL"));
        Assert.Equal(2, panel.VisibleHistoryRowCount);
    }

    [Fact]
    public void ZeroDurationRussianObservation_IsVisibleAsEventAndHasUnknownDuration()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T13:00:00Z");
        DateTimeOffset start = now.AddHours(-1);
        DateTimeOffset at = start.AddMinutes(34);
        LocationStay[] stays =
        [
            new("PL", start, at),
            new("RU", at, at),
            new("PL", start.AddMinutes(50), null)
        ];
        using var chart = new LocationSpanChart { Width = 600, Height = 126 };
        chart.Bind(stays, now, start);
        Rectangle track = chart.TrackBounds();
        int x = track.Left + (int)Math.Round(track.Width * 34d / 60d);
        chart.DispatchMouseMoveForTests(new Point(x, track.Top - 15));
        Assert.Equal("RU", chart.HoveredEventForTests?.Iso);
        Assert.Contains("Длительность неизвестна", chart.HoverTipForTests, StringComparison.Ordinal);

        using var panel = new LocationTimelinePanel(TimeProvider.System) { Width = 700, Height = 600 };
        DateTimeOffset recent = DateTimeOffset.UtcNow;
        LocationStay[] recentStays =
        [
            new("PL", recent.AddMinutes(-50), recent.AddMinutes(-26)),
            new("RU", recent.AddMinutes(-26), recent.AddMinutes(-26)),
            new("PL", recent.AddMinutes(-10), null)
        ];
        panel.ApplyChartWindowHours(3);
        panel.Bind(new LocationHistory(recentStays, recent), GeoCountryDisplay.Confirmed("PL"));
        Assert.Equal(3, panel.VisibleHistoryRowCount);
        Assert.Contains("Длительность неизвестна", panel.HistoryRowTipForTests(1), StringComparison.Ordinal);
    }

    [Fact]
    public void FrequencyChart_ShadesOnlyBucketsWithoutAnyLocationHistory()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset start = now.AddHours(-3);
        LocationStay[] stays =
        [
            new("DE", start.AddHours(1), start.AddHours(1).AddMinutes(10)),
            new("DE", start.AddHours(2), null)
        ];
        using var chart = new LocationFrequencyChart { Width = 640, Height = 176 };
        chart.Bind(stays, 1, now, TimeSpan.FromHours(3));
        Rectangle plot = chart.PlotBounds();
        int count = chart.Model.HistoryAvailablePerInterval.Count;
        int noHistoryX = plot.Left + plot.Width / count / 2;
        int quietHistoryX = plot.Left + plot.Width * 70 / count + plot.Width / count / 2;

        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));

        Assert.False(chart.Model.HistoryAvailablePerInterval[0]);
        Assert.True(chart.Model.HistoryAvailablePerInterval[70]);
        Assert.Equal(UiTheme.Brand50.ToArgb(), bitmap.GetPixel(noHistoryX, plot.Top + 2).ToArgb());
        Assert.Equal(UiTheme.Card.ToArgb(), bitmap.GetPixel(quietHistoryX, plot.Top + 2).ToArgb());

        chart.DispatchMouseMoveForTests(new Point(noHistoryX, plot.Top + plot.Height / 2));
        Assert.StartsWith("Нет данных", chart.HoverTipForTests, StringComparison.Ordinal);
        chart.DispatchMouseMoveForTests(new Point(quietHistoryX, plot.Top + plot.Height / 2));
        Assert.StartsWith("0 смен", chart.HoverTipForTests, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationCharts_DeferRepeatedHistoryRebuildsButRefreshWithinThirtySeconds()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new LocationHistory();
        history.NoteIso("DE", now.AddMinutes(-30));
        using var span = new LocationSpanChart { Width = 600 };
        using var frequency = new LocationFrequencyChart { Width = 600 };
        span.Bind(history.Stays, now, now - TimeSpan.FromHours(3));
        frequency.Bind(history.Stays, history.Revision, now, TimeSpan.FromHours(3));
        DateTimeOffset spanBuilt = span.LastModelBuiltAt;
        DateTimeOffset frequencyBuilt = frequency.LastModelBuiltAt;

        history.Touch(now.AddSeconds(1));
        span.Bind(history.Stays, now.AddSeconds(1), now.AddSeconds(1) - TimeSpan.FromHours(3));
        frequency.Bind(history.Stays, history.Revision, now.AddSeconds(1), TimeSpan.FromHours(3));
        Assert.Equal(spanBuilt, span.LastModelBuiltAt);
        Assert.Equal(frequencyBuilt, frequency.LastModelBuiltAt);

        span.Tick(now.AddSeconds(30));
        frequency.Tick(now.AddSeconds(30));
        Assert.Equal(now.AddSeconds(30), span.LastModelBuiltAt);
        Assert.Equal(now.AddSeconds(30), frequency.LastModelBuiltAt);

        history.NoteIso("NL", now.AddSeconds(31));
        span.Bind(history.Stays, now.AddSeconds(31), now.AddSeconds(31) - TimeSpan.FromHours(3));
        frequency.Bind(history.Stays, history.Revision, now.AddSeconds(31), TimeSpan.FromHours(3));
        Assert.Equal(now.AddSeconds(31), span.LastModelBuiltAt);
        Assert.Equal(now.AddSeconds(31), frequency.LastModelBuiltAt);
    }

    [Fact]
    public void Store_RoundtripsOpenStay()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset first = DateTimeOffset.UtcNow.AddMinutes(-2);
            DateTimeOffset second = DateTimeOffset.UtcNow.AddSeconds(-20);
            var log = new LocationHistory();
            log.NoteIso("DE", first);
            log.NoteIso("NL", second);
            LocationHistoryStore.Save(log);
            LocationHistory loaded = LocationHistoryStore.Load();
            Assert.Equal(2, loaded.Stays.Count);
            Assert.Equal("NL", loaded.Current!.Iso);
            Assert.Null(loaded.Current.EndedUtc);
            Assert.Equal("DE", loaded.Past[0].Iso);
            Assert.Equal(second, loaded.Past[0].EndedUtc);
            Assert.NotNull(loaded.LastObservedUtc);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Store_RoundtripsGeoIpObservationsWithPublicIp()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-geo-observations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset observed = DateTimeOffset.UtcNow;
            var log = new LocationHistory();
            log.NoteIso("DE", observed);
            log.NoteObservation(new LocationObservation(observed, new string('a', 64), "DE", "DE", "DE", null,
                "v2fly-r1", false, false, false, true, "203.0.113.8", IplocateCountry: "FR",
                IplocateDatabaseVersion: "mirror · 2026-09-29", ResolutionSource: "country.is"));

            LocationHistoryStore.Save(log);
            LocationHistory loaded = LocationHistoryStore.Load();

            LocationObservation saved = Assert.Single(loaded.Observations);
            Assert.Equal(new string('a', 64), saved.PublicIpHash);
            Assert.Equal("v2fly-r1", saved.DatabaseVersion);
            Assert.Equal("DE", saved.CountryCode);
            Assert.Equal("203.0.113.8", saved.PublicIp);
            Assert.Equal("FR", saved.IplocateCountry);
            Assert.Equal("mirror · 2026-09-29", saved.IplocateDatabaseVersion);
            Assert.Equal("country.is", saved.ResolutionSource);
            Assert.Contains("203.0.113.8", File.ReadAllText(LocationHistoryStore.FilePath), StringComparison.Ordinal);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try { Directory.Delete(temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Store_RoundtripsMoreThan256RecentChanges()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-72);
            var history = new LocationHistory();
            for (int i = 0; i < 300; i++)
                history.NoteIso(i % 2 == 0 ? "DE" : "NL", start.AddMinutes(i * 14));
            LocationHistoryStore.Save(history);

            LocationHistory loaded = LocationHistoryStore.Load();

            Assert.Equal(300, loaded.Stays.Count);
            Assert.Equal(history.Stays[^1].StartedUtc, loaded.Stays[^1].StartedUtc);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Store_RestoresPreviousHistoryAndPreservesCorruptFile()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            var original = new LocationHistory();
            original.NoteIso("DE", DateTimeOffset.UtcNow.AddHours(-1));
            LocationHistoryStore.Save(original);

            var newer = new LocationHistory(original.Stays, original.LastObservedUtc);
            newer.NoteIso("NL", DateTimeOffset.UtcNow.AddMinutes(-20));
            LocationHistoryStore.Save(newer);
            string corruptJson = "{ broken json";
            File.WriteAllText(LocationHistoryStore.FilePath, corruptJson);

            LocationHistory loaded = LocationHistoryStore.Load();
            Assert.Single(loaded.Stays);
            Assert.Equal("DE", loaded.Stays[0].Iso);

            LocationHistoryStore.Save(loaded);

            Assert.Contains(Directory.GetFiles(temp, "location-history.json.corrupt-*.json"), path => File.ReadAllText(path) == corruptJson);
            Assert.Equal("DE", LocationHistoryStore.Load().Stays.Single().Iso);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Store_RestoresBackupWhenPrimaryFileIsMissing()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            var original = new LocationHistory();
            original.NoteIso("DE", DateTimeOffset.UtcNow.AddHours(-1));
            LocationHistoryStore.Save(original);

            var newer = new LocationHistory(original.Stays, original.LastObservedUtc);
            newer.NoteIso("NL", DateTimeOffset.UtcNow.AddMinutes(-20));
            LocationHistoryStore.Save(newer);
            File.Delete(LocationHistoryStore.FilePath);

            LocationHistory recovered = LocationHistoryStore.Load();

            Assert.Single(recovered.Stays);
            Assert.Equal("DE", recovered.Stays[0].Iso);
            Assert.True(File.Exists(LocationHistoryStore.FilePath));
            Assert.Equal("DE", LocationHistoryStore.Load().Stays.Single().Iso);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Store_ClosesOpenStayAfterMachineOffGap()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset started = DateTimeOffset.UtcNow.AddHours(-4);
            DateTimeOffset lastSeen = DateTimeOffset.UtcNow.AddHours(-3);
            var log = new LocationHistory([new LocationStay("FI", started, null)], lastSeen);
            LocationHistoryStore.Save(log);
            LocationHistory loaded = LocationHistoryStore.Load();
            Assert.Null(loaded.Current);
            Assert.Equal(lastSeen, loaded.Stays[0].EndedUtc);
            Assert.True(LocationCopy.Elapsed(loaded.Stays[0], DateTimeOffset.UtcNow) < TimeSpan.FromHours(1.5));
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Store_NullStayDoesNotThrow()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(LocationHistoryStore.FilePath, """{"stays":[null]}""");
            LocationHistory loaded = LocationHistoryStore.Load();
            Assert.Empty(loaded.Stays);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            try
            {
                Directory.Delete(temp, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void StatusForm_LocationsTabShowsCurrentStayAndIgnoresUnknown()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        var log = new LocationHistory();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-5);
        log.NoteIso("DE", started);
        log.NoteIso(null, started.AddMinutes(10));
        form.BindLocations(log);
        Button tab = FindNamed<Button>(form, "locationsTab");
        Assert.Equal("Локации", tab.Text);
        tab.PerformClick();
        LocationTimelinePanel panel = FindNamed<LocationTimelinePanel>(form, "locationTimeline");
        Assert.Equal(1, panel.VisibleHistoryRowCount);
        Assert.True(panel.HistoryRowBounds(0).Height < 60);
        LocationSpanChart chart = FindNamed<LocationSpanChart>(form, "locationSpanChart");
        Assert.True(chart.Height >= 48, "chart height " + chart.Height);
        Assert.True(chart.Width >= 100, "chart width " + chart.Width);
        Button twelve = FindNamed<Button>(form, "locationChartWindow12");
        Assert.Equal("12 ч", twelve.Text);
        FindNamed<Button>(form, "locationChartWindow3").PerformClick();
        Assert.Same(chart, FindNamed<LocationSpanChart>(form, "locationSpanChart"));
        Assert.Equal("3 ч", FindNamed<Button>(form, "locationChartWindow3").Text);
    }

    [Fact]
    public void LocationsPanel_ShowsAllHistoryInOneVirtualizedList()
    {
        using var form = new Form { Width = 620, Height = 720, ShowInTaskbar = false };
        var panel = new LocationTimelinePanel(TimeProvider.System) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        var history = new LocationHistory();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-72);
        for (int i = 0; i < 320; i++)
            history.NoteIso(i % 2 == 0 ? "DE" : "NL", start.AddMinutes(i * 13));
        panel.ApplyChartWindowHours(168);
        panel.Bind(history, GeoCountryDisplay.Confirmed("NL"));
        Assert.Equal(320, panel.VisibleHistoryRowCount);
        Assert.DoesNotContain(panel.Controls.OfType<ThemedButton>(), button =>
            button.AccessibleName is "olderLocations" or "newerLocations");
        Assert.True(panel.AutoScrollMinSize.Height >= 320 * panel.HistoryRowHeight);
        Assert.Equal(panel.HistoryRowHeight, panel.HistoryRowBounds(319).Height);
    }

    [Fact]
    public void StatusForm_EmptyLocationsHasUsefulCopy()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        form.BindLocations(new LocationHistory());
        FindNamed<Button>(form, "locationsTab").PerformClick();
        Label empty = FindNamed<Label>(form, "emptyLocations");
        Assert.Contains("только смены страны", empty.Text, StringComparison.Ordinal);
        Assert.Null(FindNamedOrNull<LocationSpanChart>(form, "locationSpanChart"));
    }

    [Fact]
    public void StatusAvailabilityChart_SharesFiveAndSevenDayWindowsWithLocations()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        _ = form.Handle;
        form.Reveal();
        var locations = new LocationHistory();
        locations.NoteIso("DE", DateTimeOffset.UtcNow.AddHours(-1));
        form.BindLocations(locations, GeoCountryDisplay.Confirmed("DE"));
        form.BindAvailabilityHistory(new AvailabilityHistory(), LocationChartWindows.Hours12);
        int selectedHours = 0;
        form.LocationChartWindowHoursChanged = hours => selectedHours = hours;

        ThemedButton fiveDays = FindNamed<ThemedButton>(form, "availabilityChartWindow120");
        ThemedButton sevenDays = FindNamed<ThemedButton>(form, "availabilityChartWindow168");
        ThemedButton oneHour = FindNamed<ThemedButton>(form, "availabilityChartWindow1");
        Assert.Equal("5 д", fiveDays.Text);
        Assert.Equal("7 д", sevenDays.Text);
        Assert.Equal("1 ч", oneHour.Text);

        Assert.True(fiveDays.Visible && fiveDays.Enabled, "the five-day status period button must be interactive");
        fiveDays.Focus();
        fiveDays.PerformClick();
        Application.DoEvents();

        Assert.Equal(120, selectedHours);
        Assert.True(FindNamed<ThemedButton>(form, "locationChartWindow120").Primary);
        Assert.True(fiveDays.Primary);
        Assert.False(sevenDays.Primary);

        oneHour.PerformClick();
        Application.DoEvents();

        Assert.Equal(1, selectedHours);
        Assert.True(FindNamed<ThemedButton>(form, "locationChartWindow1").Primary);
        Assert.True(oneHour.Primary);
    }

    [Fact]
    public void StatusAvailabilityChart_ReceivesVisibleLayoutSpace()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        form.Reveal();
        form.BindAvailabilityHistory(new AvailabilityHistory(), LocationChartWindows.Hours12);
        Application.DoEvents();
        form.PerformLayout();

        AvailabilityHistoryChart chart = FindNamed<AvailabilityHistoryChart>(form, "availabilityHistoryChart");
        ListView list = FindNamed<ListView>(form, "Текущие проверки");

        Assert.True(chart.Visible);
        Assert.True(chart.Height >= 150, "availability chart height " + chart.Height);
        CheckBox toggle = FindNamed<CheckBox>(form, "emphasizeShortStatuses");
        Assert.Same(chart, toggle.Parent);
        Assert.InRange(chart.ClientSize.Width - toggle.Right, 0, chart.LogicalToDeviceUnits(16));
        Assert.True(toggle.Left > chart.ClientSize.Width / 2, $"toggle should sit at the right side of the legend: {toggle.Bounds}");
        Assert.True(chart.Bottom < list.Top, "chart bottom=" + chart.Bottom + " list top=" + list.Top);
    }

    [Fact]
    public void ShortStatusToggleHidesMouseFocusOutlineAndRemainsKeyboardAccessible()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        form.Reveal();
        Application.DoEvents();

        MouseFocusCueCheckBox toggle = FindNamed<MouseFocusCueCheckBox>(form, "emphasizeShortStatuses");
        toggle.Select();

        Assert.False(toggle.FocusFrameVisibleForTests);
        Assert.True(toggle.TabStop);
        Assert.True(toggle.CanSelect);
        Assert.True(toggle.Focused);
        Assert.Equal("emphasizeShortStatuses", toggle.AccessibleName);
    }

    [Fact]
    public void ShortStatusToggleNeverShowsFrameAndKeepsKeyboardFocusVisibleAsFill()
    {
        using var form = new Form();
        using var toggle = new FocusCueProbeCheckBox { AccessibleName = "emphasizeShortStatuses" };
        form.Controls.Add(toggle);
        form.Show();

        toggle.Focus();
        toggle.DispatchKeyDownForTests(Keys.Tab);
        Application.DoEvents();

        Assert.False(toggle.FocusFrameVisibleForTests);
        Assert.True(toggle.Focused);
        Assert.True(toggle.TabStop);
        Assert.Equal(UiTheme.Brand50, toggle.BackColor);
        Assert.Equal("emphasizeShortStatuses", toggle.AccessibleName);
    }

    [Fact]
    public void StatusAvailabilityChart_KeepsLegendAndToggleInsideCardAtMinimumWindowWidth()
    {
        using var form = new StatusForm { ShowInTaskbar = false, Width = UiTheme.WindowMinWidth, Height = UiTheme.WindowMinHeight };
        form.Reveal();
        form.BindAvailabilityHistory(new AvailabilityHistory(), LocationChartWindows.Hours12);
        Application.DoEvents();

        AvailabilityHistoryChart chart = FindNamed<AvailabilityHistoryChart>(form, "availabilityHistoryChart");
        CheckBox toggle = FindNamed<CheckBox>(form, "emphasizeShortStatuses");
        Assert.Same(chart, toggle.Parent);
        Assert.True(toggle.Left >= 0 && toggle.Right <= chart.ClientSize.Width, $"toggle {toggle.Bounds}, chart {chart.ClientSize}");
        Assert.True(toggle.Bottom <= chart.ClientSize.Height, $"toggle {toggle.Bounds}, chart {chart.ClientSize}");
    }

    [Fact]
    public void ShortStatusToggle_AddsVisibleMarkersWithoutRebuildingTimeline()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        form.Reveal();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory([
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, now.AddMinutes(-1), now)
        ], now);
        form.BindAvailabilityHistory(history, LocationChartWindows.Hours12);
        Application.DoEvents();

        AvailabilityHistoryChart chart = FindNamed<AvailabilityHistoryChart>(form, "availabilityHistoryChart");
        CheckBox toggle = FindNamed<CheckBox>(form, "emphasizeShortStatuses");
        Assert.Equal(1, chart.EmphasizedSpanCount);
        Assert.True(toggle.Checked);
        Assert.Equal("Увеличивать короткие отметки", toggle.Text);
        Assert.Equal(1, chart.EmphasizedMarkerCount);
        int buildCount = chart.SegmentBuildCount;

        toggle.Checked = false;
        Assert.Equal(0, chart.EmphasizedMarkerCount);
        toggle.Checked = true;

        Assert.Equal(1, chart.EmphasizedMarkerCount);
        Assert.Equal(buildCount, chart.SegmentBuildCount);
    }

    [Fact]
    public void ShortStatusHighlightUsesACompactMinimumWidthInTheStatusColor()
    {
        using var form = new StatusForm { ShowInTaskbar = false, Width = UiTheme.WindowDefaultWidth, Height = UiTheme.WindowDefaultHeight };
        form.Reveal();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory([
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, now.AddMinutes(-1), now)
        ], now);
        form.BindAvailabilityHistory(history, LocationChartWindows.Hours12);
        Application.DoEvents();

        AvailabilityHistoryChart chart = FindNamed<AvailabilityHistoryChart>(form, "availabilityHistoryChart");
        Rectangle track = chart.TrackFor(EndpointGroup.Ru);
        Rectangle marker = Assert.Single(chart.ShortMarkerRectangles(EndpointGroup.Ru));
        Assert.Equal(track.Top, marker.Top);
        Assert.Equal(track.Height, marker.Height);
        Assert.Equal(chart.LogicalToDeviceUnits(AvailabilityHistoryChart.ShortSpanMinimumWidth), marker.Width);

        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));

        Color markerFill = bitmap.GetPixel(marker.Left + marker.Width / 2, marker.Top + marker.Height / 2);
        Assert.Equal(Color.FromArgb(198, 62, 66).ToArgb(), markerFill.ToArgb());
        Color outlinePixel = bitmap.GetPixel(marker.Left + marker.Width / 2, marker.Top);
        Assert.NotEqual(markerFill.ToArgb(), outlinePixel.ToArgb());
    }

    [Fact]
    public void ShortStatusMinimumWidthCoversOneThroughThreePixelEventsButPreservesFourPixels()
    {
        using var chart = new AvailabilityHistoryChart { Width = 640, Height = 160 };
        Rectangle track = chart.TrackFor(EndpointGroup.Ru);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cursor = now.AddHours(-8);
        long windowTicks = TimeSpan.FromHours(12).Ticks;
        var spans = new List<AvailabilitySpan>();
        for (int naturalWidth = 1; naturalWidth <= 4; naturalWidth++)
        {
            long durationTicks = (long)Math.Ceiling(windowTicks * naturalWidth / (double)track.Width);
            DateTimeOffset end = cursor.AddTicks(durationTicks);
            spans.Add(new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, cursor, end));
            cursor = end.AddHours(1);
        }

        chart.Bind(new AvailabilityHistory(spans, now), now, TimeSpan.FromHours(12));

        Assert.Equal(3, chart.EmphasizedSpanCount);
        Rectangle[] markers = chart.ShortMarkerRectangles(EndpointGroup.Ru);
        Assert.Equal(3, markers.Length);
        Assert.All(markers, marker => Assert.True(marker.Width >= chart.LogicalToDeviceUnits(AvailabilityHistoryChart.ShortSpanMinimumWidth)));
    }

    [Fact]
    public void ShortOfflineRedMarkerRendersAboveOverlappingYellowMarker()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var history = new AvailabilityHistory([
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Offline, false, now.AddMinutes(-4), now.AddMinutes(-2)),
            new AvailabilitySpan(EndpointGroup.Ru, GroupAvailability.Limited, false, now.AddMinutes(-2), now)
        ], now);
        using var chart = new MouseMoveAvailabilityChart { Width = 640, Height = 160 };
        chart.Bind(history, now, TimeSpan.FromHours(12));

        Rectangle[] markers = chart.ShortMarkerRectangles(EndpointGroup.Ru);
        Assert.Equal(2, markers.Length);
        Rectangle overlap = Rectangle.Intersect(markers[0], markers[1]);
        Assert.True(overlap.Width > 0, $"expected widened short markers to overlap: {markers[0]} and {markers[1]}");

        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
        Color overlapColor = bitmap.GetPixel(overlap.Left + overlap.Width / 2, overlap.Top + overlap.Height / 2);
        Assert.Equal(Color.FromArgb(198, 62, 66).ToArgb(), overlapColor.ToArgb());
        chart.DispatchMouseMove(new Point(overlap.Left + overlap.Width / 2, overlap.Top + overlap.Height / 2));
        Assert.Equal(GroupAvailability.Offline, chart.HoveredState);
        Assert.False(chart.HoveredIsPaused);
    }

    [Fact]
    public void AvailabilityLegendAndToggleFitWhenLegendWrapsAtNarrowWidth()
    {
        using var chart = new AvailabilityHistoryChart { Width = 320, Height = 160 };
        var toggle = new CheckBox { Text = "Увеличивать короткие отметки", AutoSize = true };
        chart.SetShortStatusesToggle(toggle);

        Size preferred = chart.GetPreferredSize(new Size(320, 0));
        chart.Size = preferred;
        chart.PerformLayout();

        Assert.True(preferred.Height > chart.LogicalToDeviceUnits(160), $"wrapped legend should increase card height: {preferred}");
        Assert.All(chart.LegendItemTextBounds(), bounds =>
        {
            Assert.True(bounds.Left >= 0 && bounds.Right <= chart.ClientSize.Width, $"legend text outside card: {bounds}, card {chart.ClientSize}");
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= chart.ClientSize.Height, $"legend text outside card: {bounds}, card {chart.ClientSize}");
        });
        Rectangle[] textBounds = chart.LegendItemTextBounds();
        string[] labels = AvailabilityHistoryChart.LegendLabels;
        Assert.Equal(labels.Length, textBounds.Length);
        Assert.True(AvailabilityHistoryChart.LegendTextFlags.HasFlag(TextFormatFlags.NoPadding),
            "legend draw flags must match the no-padding measurement used to allocate each label");
        for (int i = 0; i < labels.Length; i++)
        {
            int requiredWidth = TextRenderer.MeasureText(labels[i], UiTheme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width;
            Assert.True(textBounds[i].Width >= requiredWidth, $"legend label '{labels[i]}' needs {requiredWidth}px but has {textBounds[i].Width}px");
        }

        Assert.True(toggle.Top >= chart.LogicalToDeviceUnits(112));
        Assert.True(toggle.Bottom <= chart.ClientSize.Height, $"toggle outside card: {toggle.Bounds}, card {chart.ClientSize}");
        Assert.DoesNotContain(chart.LegendItemTextBounds(), bounds => bounds.IntersectsWith(toggle.Bounds));
    }

    [Fact]
    public void StatusForm_SavesNormalWindowGeometryWhenHiddenToTray()
    {
        using var form = new StatusForm { ShowInTaskbar = false, Width = 980, Height = 840 };
        form.Reveal();
        Rectangle? saved = null;
        form.WindowGeometryChanged = () => saved = form.Bounds;

        form.Close();

        Assert.True(form.Visible == false);
        Assert.Equal(form.Bounds, saved);
    }

    [Fact]
    public void StatusForm_RestoresSavedWindowSizeAndKeepsItOnTheCurrentScreen()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Rectangle requested = new(area.Left + 60, area.Top + 40, 840, 720);

        form.RestoreWindowBounds(requested);
        form.Reveal();
        Application.DoEvents();

        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(Math.Min(requested.Width, area.Width), form.Width);
        Assert.Equal(Math.Min(requested.Height, area.Height), form.Height);
        Assert.Equal(new Point(
            Math.Clamp(requested.X, area.Left, Math.Max(area.Left, area.Right - form.Width)),
            Math.Clamp(requested.Y, area.Top, Math.Max(area.Top, area.Bottom - form.Height))), form.Location);
        Assert.True(area.Contains(form.Bounds), $"restored bounds {form.Bounds}, working area {area}");
    }

    [Fact]
    public void StatusForm_CentersMigratedCustomSizeInsteadOfReplacingItWithDefault()
    {
        var settings = new AppSettings { SettingsVersion = 3, WindowX = 80, WindowY = 80, WindowWidth = 980, WindowHeight = 840 };
        Assert.True(SettingsStore.MigrateToSettingsVersion4(settings));
        using var form = new StatusForm { ShowInTaskbar = false };
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;

        form.RestoreWindowBounds(new Rectangle(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight));
        form.Reveal();
        Application.DoEvents();

        Assert.Equal(Math.Min(settings.WindowWidth, area.Width), form.Width);
        Assert.Equal(Math.Min(settings.WindowHeight, area.Height), form.Height);
        Assert.True(area.Contains(form.Bounds));
    }

    [Fact]
    public void SettingsPage_ExposesSettingsFolderAndWindowSizeActions()
    {
        using var form = new StatusForm { ShowInTaskbar = false };
        _ = form.Handle;
        int opened = 0;
        int reset = 0;
        form.OpenSettingsFolderRequested = () => opened++;
        form.ResetWindowSizeRequested = () => reset++;

        form.ShowPageAndReveal(3);
        Application.DoEvents();
        FindNamed<ThemedButton>(form, "openSettingsFolder").PerformClick();
        FindNamed<ThemedButton>(form, "resetWindowSize").PerformClick();

        Assert.Equal(1, opened);
        Assert.Equal(1, reset);
    }

    [Fact]
    public void SettingsPage_KeepsDataCreditsInSeparateDialog()
    {
        using var form = new StatusForm();
        using var credits = new DataSourcesDialog();

        Assert.NotNull(FindNamed<LinkLabel>(form, "dataSourcesLink"));
        Assert.NotNull(FindNamed<LinkLabel>(credits, "dbIpGeoIpAttribution"));
        Assert.NotNull(FindNamed<LinkLabel>(credits, "v2FlyGeoIpAttribution"));
        Assert.Null(FindNamedOrNull<LinkLabel>(form, "dbIpGeoIpAttribution"));
    }

    [Fact]
    public void StatusForm_ResetWindowSizeRestoresDefaultAndCentersWindow()
    {
        using var form = new StatusForm { ShowInTaskbar = false, WindowState = FormWindowState.Maximized };
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.ResetWindowSize();

        Assert.Equal(FormWindowState.Normal, form.WindowState);
        Assert.Equal(Math.Min(UiTheme.WindowDefaultWidth, Math.Max(320, area.Width)), form.Width);
        Assert.Equal(Math.Min(UiTheme.WindowDefaultHeight, Math.Max(240, area.Height)), form.Height);
        Assert.True(area.Contains(form.Bounds));
    }

    [Fact]
    public void LocationsTab_CurrentCardMatchesLiveDisplayNotStaleHistory()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        var log = new LocationHistory();
        log.NoteIso("PL", DateTimeOffset.UtcNow.AddHours(-1));
        form.BindLocations(log, GeoCountryDisplay.Unconfirmed);
        FindNamed<Button>(form, "locationsTab").PerformClick();
        LocationTimelinePanel panel = FindNamed<LocationTimelinePanel>(form, "locationTimeline");
        Assert.Equal(0, panel.VisibleHistoryRowCount);
        Assert.Null(FindNamedOrNull<LocationSpanChart>(form, "locationSpanChart"));

        form.BindLocations(log, GeoCountryDisplay.Confirmed("PL"));
        Assert.Equal(1, panel.VisibleHistoryRowCount);
        Assert.True(panel.HistoryRowBounds(0).Width > 200);
    }

    [Fact]
    public void HistoryRow_KeepsDurationOnOneCompactLine()
    {
        using var form = new StatusForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        form.Show();
        var log = new LocationHistory();
        log.NoteIso("FI", DateTimeOffset.UtcNow.AddSeconds(-10));
        form.BindLocations(log);
        FindNamed<Button>(form, "locationsTab").PerformClick();
        form.PerformLayout();
        FindNamed<LocationTimelinePanel>(form, "locationTimeline").Relayout();
        LocationTimelinePanel panel = FindNamed<LocationTimelinePanel>(form, "locationTimeline");
        Assert.Equal(1, panel.VisibleHistoryRowCount);
        Rectangle row = panel.HistoryRowBounds(0);
        Assert.Equal(panel.HistoryRowHeight, row.Height);
        Assert.True(row.Width > 300, "row width " + row.Width);
        Assert.True(row.Height < 60, "row height " + row.Height);
    }

    [Fact]
    public void LocationsPanel_ScrollsVerticallyWithoutHorizontalBar()
    {
        using var host = new Form
        {
            Width = 420,
            Height = 260,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000)
        };
        var panel = new LocationTimelinePanel(TimeProvider.System) { Dock = DockStyle.Fill };
        host.Controls.Add(panel);
        host.Show();
        var log = new LocationHistory();
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddHours(-20);
        for (int i = 0; i < 18; i++)
        {
            log.NoteIso(i % 2 == 0 ? "DE" : "FI", t0.AddHours(i));
        }

        panel.ApplyChartWindowHours(168);
        panel.Bind(log);
        host.PerformLayout();
        panel.PerformLayout();
        Assert.True(panel.VerticalScroll.Visible, "expected vertical scroll");
        Assert.False(panel.HorizontalScroll.Visible);
        Assert.True(panel.DisplayRectangle.Width <= panel.ClientSize.Width);
        Assert.Equal(18, panel.VisibleHistoryRowCount);
    }

    [Fact]
    public void LocationChartPalette_SpreadsCommonCountriesAcrossDistinctHues()
    {
        string[] codes = ["DE", "NL", "FI", "PL", "US", "GB", "TR", "JP", "BR", "IN", "AU", "CA"];
        var fills = codes.Select(LocationChartPalette.Fill).Select(c => c.ToArgb()).ToHashSet();
        Assert.True(fills.Count >= 7, "distinct fills " + fills.Count);
        Assert.Equal(LocationChartPalette.Fill("DE"), LocationChartPalette.Fill("DE"));
        Assert.NotEqual(LocationChartPalette.Fill("DE"), LocationChartPalette.Fill("NL"));
        Assert.Equal(Color.FromArgb(220, 0, 0).ToArgb(), LocationChartPalette.Fill("RU").ToArgb());
        Assert.Equal(Color.FromArgb(220, 0, 0).ToArgb(), LocationChartPalette.HistoryLabel("ru").ToArgb());
        foreach (string code in codes)
        {
            Color fill = LocationChartPalette.Fill(code);
            Color ink = LocationChartPalette.Ink(code);
            Assert.True(Contrast(fill, ink) >= 4.5, $"{code}: contrast {Contrast(fill, ink):F2}");
        }
    }

    private static double Contrast(Color background, Color foreground)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte component)
            {
                double value = component / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        double first = Luminance(background);
        double second = Luminance(foreground);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static T FindNamed<T>(Control root, string accessibleName) where T : Control
    {
        if (root is T match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            T? nested = FindNamedOrNull<T>(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException(typeof(T).Name + " " + accessibleName);
    }

    private static T? FindNamedOrNull<T>(Control root, string accessibleName) where T : Control
    {
        if (root is T match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            T? nested = FindNamedOrNull<T>(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string CollectText(Control root)
    {
        var parts = new List<string>();
        Walk(root, parts);
        return string.Join(" ", parts);
    }

    private static MonitorSnapshot StatusSnapshot(GroupAvailability provider, GroupAvailability world, bool paused)
        => new(1, DateTimeOffset.UtcNow, 0, new GroupSnapshot(EndpointGroup.Ru, provider, "", null, false, []), new GroupSnapshot(EndpointGroup.World, world, "", null, false, []), true, null, false, null, paused);

    private sealed class MouseMoveAvailabilityChart : AvailabilityHistoryChart
    {
        public void DispatchMouseMove(Point point)
            => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));

        public void DispatchMouseLeave()
            => OnMouseLeave(EventArgs.Empty);

        public void PaintClip(Bitmap bitmap, Rectangle clip)
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SetClip(clip);
            OnPaint(new PaintEventArgs(graphics, clip));
        }
    }

    private sealed class FocusCueProbeCheckBox : MouseFocusCueCheckBox
    {
        public void DispatchKeyDownForTests(Keys key)
            => OnKeyDown(new KeyEventArgs(key));
    }

    private static int CountInkPixels(Bitmap bitmap, Rectangle bounds)
    {
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        for (int x = bounds.Left; x < bounds.Right; x++)
        {
            Color pixel = bitmap.GetPixel(x, y);
            if (pixel.R < 180 && pixel.G < 180 && pixel.B < 180)
                count++;
        }

        return count;
    }

    private static int CountControls<T>(Control root) where T : Control
        => (root is T ? 1 : 0) + root.Controls.Cast<Control>().Sum(CountControls<T>);

    private static void Walk(Control root, List<string> parts)
    {
        if (!string.IsNullOrWhiteSpace(root.Text))
        {
            parts.Add(root.Text);
        }

        foreach (Control child in root.Controls)
        {
            Walk(child, parts);
        }
    }
}
