using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using NetLights.Networking;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class PollingSettingsTests
{
    [Theory]
    [InlineData(0, "Б")]
    [InlineData(2048, "КиБ")]
    [InlineData(2097152, "МиБ")]
    [InlineData(2147483648, "ГиБ")]
    public void TrafficLabelsHaveReadableUnits(long bytes, string suffix)
        => Assert.EndsWith(suffix, NetworkTrafficDialog.FormatBytes(bytes));

    [Fact]
    public void TrafficRefreshPreservesSelectedRowAndScrollPosition()
    {
        for (int i = 0; i < 25; i++)
            using (Stream stream = NetworkTraffic.Track(new MemoryStream(), "test-ui", $"traffic-{i}.example.test"))
                stream.WriteByte(1);
        using var dialog = new NetworkTrafficDialog();
        dialog.Show();
        Application.DoEvents();
        DataGridView grid = Assert.Single(dialog.Controls.OfType<TableLayoutPanel>()).Controls.OfType<DataGridView>().Single();
        Assert.True(grid.Rows.Count >= 25);
        grid.CurrentCell = grid.Rows[12].Cells[0];
        grid.FirstDisplayedScrollingRowIndex = 8;
        DataGridViewRow selected = grid.CurrentRow!;
        int firstVisible = grid.FirstDisplayedScrollingRowIndex;
        typeof(NetworkTrafficDialog).GetMethod("RefreshTraffic", System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)!.Invoke(dialog, null);
        Assert.Same(selected, grid.CurrentRow);
        Assert.Equal(firstVisible, grid.FirstDisplayedScrollingRowIndex);
    }

    [Fact]
    public void OldSettingsUseNewDefaultAndSelectedIntervalsSurviveRestart()
    {
        string previous = SettingsStore.RootDirectory;
        string directory = Path.Combine(Path.GetTempPath(), "NetLightsPolling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SettingsStore.RootDirectory = directory;
            File.WriteAllText(SettingsStore.SettingsPath, "{\"settingsVersion\":8,\"windowWidth\":1520}");
            AppSettings settings = SettingsStore.Load();
            Assert.Equal(5, settings.AvailabilityPollingSeconds);
            Assert.Equal(10, settings.CountryPollingSeconds);
            Assert.Equal(1520, settings.WindowWidth);
            settings.AvailabilityPollingSeconds = 3;
            settings.CountryPollingSeconds = 60;
            SettingsStore.Save(settings);
            AppSettings reloaded = SettingsStore.Load();
            Assert.Equal(3, reloaded.AvailabilityPollingSeconds);
            Assert.Equal(60, reloaded.CountryPollingSeconds);
            Assert.Equal(1520, reloaded.WindowWidth);
            reloaded.AvailabilityPollingSeconds = int.MaxValue;
            reloaded.CountryPollingSeconds = -1;
            SettingsStore.Save(reloaded);
            Assert.Equal(5, SettingsStore.Load().AvailabilityPollingSeconds);
            Assert.Equal(10, SettingsStore.Load().CountryPollingSeconds);
        }
        finally { SettingsStore.RootDirectory = previous; Directory.Delete(directory, true); }
    }

    [Fact]
    public void SelectorsShowDefaultAndBindingDoesNotWriteBack()
    {
        using var form = new StatusForm();
        ComboBox availability = Assert.IsType<ComboBox>(Find(form, "availabilityPollingInterval"));
        ComboBox country = Assert.IsType<ComboBox>(Find(form, "countryPollingInterval"));
        Assert.Equal(new[] { "2 с", "3 с", "4 с", "5 с (по умолчанию)" }, availability.Items.Cast<string>().Take(4));
        Assert.Equal("5 с (по умолчанию)", availability.SelectedItem);
        int changes = 0;
        int selected = 0;
        form.AvailabilityPollingChanged = seconds => { changes++; selected = seconds; };
        form.BindSettings(false, true, false, GeoCountryLetterScale.Regular, null,
            availabilityPollingSeconds: 30, countryPollingSeconds: 60);
        Assert.Equal("30 с", availability.SelectedItem);
        Assert.Equal("60 с", country.SelectedItem);
        Assert.Equal(0, changes);
        availability.SelectedIndex = Array.IndexOf(PollingIntervals.AvailabilitySeconds, 4);
        Assert.Equal(1, changes);
        Assert.Equal(4, selected);
    }

    private static Control Find(Control parent, string name)
    {
        if (parent.AccessibleName == name) return parent;
        foreach (Control child in parent.Controls)
        {
            Control? match = FindOrNull(child, name);
            if (match is not null) return match;
        }
        throw new InvalidOperationException("Control not found: " + name);
    }

    private static Control? FindOrNull(Control parent, string name)
        => parent.AccessibleName == name ? parent : parent.Controls.Cast<Control>()
            .Select(child => FindOrNull(child, name)).FirstOrDefault(child => child is not null);
}
