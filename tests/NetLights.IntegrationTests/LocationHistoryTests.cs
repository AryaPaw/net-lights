using System.Drawing;
using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class LocationHistoryTests
{
    [Fact]
    public void Store_RoundtripsOpenStay()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-locations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset first = DateTimeOffset.UtcNow.AddHours(-2);
            DateTimeOffset second = DateTimeOffset.UtcNow.AddMinutes(-20);
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
        Label duration = FindNamed<Label>(form, "currentLocationDuration");
        Assert.Contains("Длительность:", duration.Text, StringComparison.Ordinal);
        Assert.Contains("2 ч", duration.Text, StringComparison.Ordinal);
        Assert.Contains("Германия", CollectText(form), StringComparison.Ordinal);
        Assert.DoesNotContain("??", CollectText(form), StringComparison.Ordinal);
        Assert.DoesNotContain("8.8.8.8", CollectText(form), StringComparison.Ordinal);
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
    public void LocationsTab_CurrentCardMatchesLiveDisplayNotStaleHistory()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        var log = new LocationHistory();
        log.NoteIso("PL", DateTimeOffset.UtcNow.AddHours(-1));
        form.BindLocations(log, GeoCountryDisplay.Unconfirmed);
        FindNamed<Button>(form, "locationsTab").PerformClick();
        Assert.Equal("??", FindNamed<IsoChip>(form, "isoChip").Text);
        Assert.Contains("Неизвестно", CollectText(form), StringComparison.Ordinal);
        Assert.DoesNotContain("Польша", CollectText(form), StringComparison.Ordinal);

        form.BindLocations(log, GeoCountryDisplay.Confirmed("PL"));
        Assert.Equal("PL", FindNamed<IsoChip>(form, "isoChip").Text);
        Assert.Contains("Польша", CollectText(form), StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentDuration_IsFullyVisibleBelowCountryName()
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
        Label duration = FindNamed<Label>(form, "currentLocationDuration");
        StayCard card = Assert.IsType<StayCard>(duration.Parent);
        Assert.True(duration.Height >= 16, "duration height " + duration.Height);
        Assert.True(duration.Bottom <= card.ClientSize.Height - card.Padding.Bottom, "bottom=" + duration.Bottom + " card=" + card.Height);
        Assert.True(duration.Right <= card.ClientSize.Width - card.Padding.Right, "right=" + duration.Right + " cardW=" + card.Width);
        Assert.Null(card.Region);
        Assert.True(card.Height >= 72, "card height " + card.Height);
        Assert.Contains("Финляндия", CollectText(form), StringComparison.Ordinal);
        IsoChip chip = FindNamed<IsoChip>(form, "isoChip");
        Assert.Equal("FI", chip.Text);
        Assert.Equal(chip.LogicalToDeviceUnits(IsoChip.Edge), chip.Width);
        Assert.Contains(chip.Font.Name, new[] { "Consolas", FontFamily.GenericMonospace.Name }, StringComparer.OrdinalIgnoreCase);
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
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        for (int i = 0; i < 18; i++)
        {
            log.NoteIso(i % 2 == 0 ? "DE" : "FI", t0.AddHours(i));
        }

        panel.Bind(log);
        host.PerformLayout();
        panel.PerformLayout();
        Assert.True(panel.VerticalScroll.Visible, "expected vertical scroll");
        Assert.False(panel.HorizontalScroll.Visible);
        Assert.True(panel.DisplayRectangle.Width <= panel.ClientSize.Width);
        IsoChip first = FindNamed<IsoChip>(panel, "isoChip");
        Assert.Contains(first.Font.Name, new[] { "Consolas", FontFamily.GenericMonospace.Name }, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocationChartPalette_SpreadsCommonCountriesAcrossDistinctHues()
    {
        string[] codes = ["DE", "NL", "FI", "PL", "US", "GB", "TR", "JP", "BR", "IN", "AU", "CA"];
        var fills = codes.Select(LocationChartPalette.Fill).Select(c => c.ToArgb()).ToHashSet();
        Assert.True(fills.Count >= 7, "distinct fills " + fills.Count);
        Assert.Equal(LocationChartPalette.Fill("DE"), LocationChartPalette.Fill("DE"));
        Assert.NotEqual(LocationChartPalette.Fill("DE"), LocationChartPalette.Fill("NL"));
        Assert.Equal(UiTheme.OnBrand, LocationChartPalette.Ink("DE"));
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
