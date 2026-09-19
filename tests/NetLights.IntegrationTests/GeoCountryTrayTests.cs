using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class GeoCountryTrayTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void LetterIcon_HasNoCircleAndKeepsTransparentCorners(int size)
    {
        using Bitmap de = CountryTrayIconRenderer.RenderBitmap("DE", size);
        using Bitmap unknown = CountryTrayIconRenderer.RenderBitmap("??", size);
        using Bitmap compact = CountryTrayIconRenderer.RenderBitmap("DE", size, GeoCountryLetterScale.Compact);
        using Bitmap large = CountryTrayIconRenderer.RenderBitmap("DE", size, GeoCountryLetterScale.Large);
        Assert.Equal(size, de.Width);
        Assert.Equal(size, de.Height);
        Assert.True(de.GetPixel(0, 0).A < 40);
        Assert.True(de.GetPixel(size - 1, 0).A < 40);
        Assert.True(unknown.GetPixel(0, 0).A < 40);
        Assert.Equal(PixelFormat.Format32bppArgb, de.PixelFormat);
        Assert.True(CountDarkOpaque(de) < size * size * 0.35);
        int deInk = CountOpaque(de);
        int unknownInk = CountOpaque(unknown);
        Assert.True(deInk >= size * size / 6, $"DE ink {deInk} at {size}px");
        Assert.True(unknownInk >= size * size / 6, $"?? ink {unknownInk} at {size}px");
        Assert.True(CountOpaque(compact) < CountOpaque(large));
    }

    [Fact]
    public void SettingsStore_MissingFieldDefaultsCountryIconOffAndRegularLetters()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-country-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(SettingsStore.SettingsPath, """{"autoStart":true,"autoUpdateEnabled":true,"settingsVersion":1}""");
            AppSettings loaded = SettingsStore.Load();
            Assert.False(loaded.GeoCountryIconEnabled);
            Assert.Equal((int)GeoCountryLetterScale.Regular, loaded.GeoCountryLetterSize);
            loaded.GeoCountryIconEnabled = true;
            loaded.GeoCountryLetterSize = (int)GeoCountryLetterScale.Compact;
            SettingsStore.Save(loaded);
            AppSettings again = SettingsStore.Load();
            Assert.True(again.GeoCountryIconEnabled);
            Assert.Equal((int)GeoCountryLetterScale.Compact, again.GeoCountryLetterSize);
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
    public void SettingsStore_BumpsShortSavedHeightToDefault()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(SettingsStore.SettingsPath, """{"windowWidth":1040,"windowHeight":800}""");
            AppSettings loaded = SettingsStore.Load();
            Assert.Equal(UiTheme.WindowDefaultWidth, loaded.WindowWidth);
            Assert.Equal(UiTheme.WindowDefaultHeight, loaded.WindowHeight);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void SettingsCheckbox_TogglesCallback()
    {
        using var form = new StatusForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        form.Reveal();
        FindButton(form, "settingsTab").PerformClick();
        form.BindSettings(true, true, true, GeoCountryLetterScale.Regular, null);
        int calls = 0;
        bool last = true;
        form.GeoCountryIconChanged = value =>
        {
            calls++;
            last = value;
        };
        CheckBox box = FindCheckBox(form, "geoCountryIcon");
        Assert.Equal("Показывать страну в трее", box.Text);
        Assert.True(box.Checked);
        ComboBox scale = FindCombo(form, "geoCountryLetterSize");
        Assert.True(scale.Visible);
        Assert.Equal("Обычный", scale.SelectedItem);
        int scaleCalls = 0;
        GeoCountryLetterScale lastScale = GeoCountryLetterScale.Regular;
        form.GeoCountryLetterScaleChanged = value =>
        {
            scaleCalls++;
            lastScale = value;
        };
        scale.SelectedIndex = (int)GeoCountryLetterScale.Compact;
        Assert.Equal(1, scaleCalls);
        Assert.Equal(GeoCountryLetterScale.Compact, lastScale);
        box.Checked = false;
        Assert.Equal(1, calls);
        Assert.False(last);
        Assert.False(scale.Visible);
        form.BindSettings(true, true, false, GeoCountryLetterScale.Regular, null);
        Assert.False(scale.Visible);
        form.BindSettings(true, true, true, GeoCountryLetterScale.Regular, null);
        Assert.True(scale.Visible);
    }

    [Fact]
    public async Task Host_DisabledStopsRequestsAndHidesIcon()
    {
        var source = new FakeSource();
        int opens = 0;
        using var host = new GeoCountryTrayHost(() => opens++, source, TimeProvider.System, ui: null);
        Assert.False(host.Visible);
        Assert.False(host.TrayIconCreated);
        Assert.Equal(0, source.SelfCount);
        host.SetEnabled(true);
        await WaitUntil(() => source.SelfCount >= 1);
        Assert.True(host.Visible);
        Assert.Equal("DE", host.Current.Letters);
        host.SetEnabled(false);
        int selves = source.SelfCount;
        await Task.Delay(80);
        Assert.Equal(selves, source.SelfCount);
        Assert.False(host.Visible);
        host.Restore();
        Assert.False(host.Visible);
        host.SetEnabled(true);
        await WaitUntil(() => source.SelfCount > selves);
        host.RaiseClickForTests(MouseButtons.Left);
        Assert.Equal(1, opens);
        host.RaiseClickForTests(MouseButtons.Right);
        Assert.Equal(1, opens);
        host.Restore();
        Assert.True(host.Visible);
    }

    private static int CountOpaque(Bitmap bmp)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 40)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountDarkOpaque(Bitmap bmp)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Color p = bmp.GetPixel(x, y);
                if (p.A > 200 && p.R < 30 && p.G < 30 && p.B < 30)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static CheckBox FindCheckBox(Control root, string accessibleName)
    {
        if (root is CheckBox match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            CheckBox? nested = FindCheckBoxOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException("checkbox " + accessibleName);
    }

    private static CheckBox? FindCheckBoxOrNull(Control root, string accessibleName)
    {
        if (root is CheckBox match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            CheckBox? nested = FindCheckBoxOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ThemedButton FindButton(Control root, string accessibleName)
    {
        if (root is ThemedButton match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            ThemedButton? nested = FindButtonOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException("button " + accessibleName);
    }

    private static ThemedButton? FindButtonOrNull(Control root, string accessibleName)
    {
        if (root is ThemedButton match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            ThemedButton? nested = FindButtonOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ComboBox FindCombo(Control root, string accessibleName)
    {
        if (root is ComboBox match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            ComboBox? nested = FindComboOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new InvalidOperationException("combo " + accessibleName);
    }

    private static ComboBox? FindComboOrNull(Control root, string accessibleName)
    {
        if (root is ComboBox match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            ComboBox? nested = FindComboOrNull(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class FakeSource : IGeoCountrySource
    {
        public int SelfCount { get; private set; }

        public Task<GeoCountrySelfResult> GetSelfAsync(CancellationToken cancellationToken)
        {
            SelfCount++;
            return Task.FromResult(new GeoCountrySelfResult(true, IPAddress.Parse("8.8.8.8"), "DE", null));
        }

        public Task<GeoCountryConfirmResult> ConfirmAsync(IPAddress ip, CancellationToken cancellationToken)
            => Task.FromResult(new GeoCountryConfirmResult(true, "DE", null));
    }
}
