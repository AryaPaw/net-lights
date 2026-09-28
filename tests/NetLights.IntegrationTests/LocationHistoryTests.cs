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
    public void LocationSpanChart_UsesCardInsetWhileFrequencyPlotReservesYAxisSpace()
    {
        using var span = new LocationSpanChart();
        using var frequency = new LocationFrequencyChart();
        span.Width = 600;
        frequency.Width = 600;

        Rectangle spanPlot = span.TrackBounds();
        Rectangle frequencyPlot = frequency.PlotBounds();
        Assert.Equal(14, spanPlot.Left);
        Assert.True(frequencyPlot.Left > spanPlot.Left);
        Assert.Equal(spanPlot.Right, frequencyPlot.Right);
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
        Assert.Equal("5 д", fiveDays.Text);
        Assert.Equal("7 д", sevenDays.Text);

        Assert.True(fiveDays.Visible && fiveDays.Enabled, "the five-day status period button must be interactive");
        fiveDays.Focus();
        fiveDays.PerformClick();
        Application.DoEvents();

        Assert.Equal(120, selectedHours);
        Assert.True(FindNamed<ThemedButton>(form, "locationChartWindow120").Primary);
        Assert.True(fiveDays.Primary);
        Assert.False(sevenDays.Primary);
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
