using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using NetLights.App;
using NetLights.Core;
using NetLights.Updates;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class UiRendererTests
{
    [Fact]
    public void T45_SixteenCombinationsRenderTransparentGap()
    {
        GroupAvailability[] states =
        [
            GroupAvailability.Online,
            GroupAvailability.Limited,
            GroupAvailability.Offline,
            GroupAvailability.Unknown
        ];
        string dir = Path.Combine(Path.GetTempPath(), "net-lights-icons");
        Directory.CreateDirectory(dir);
        foreach (GroupAvailability left in states)
        {
            foreach (GroupAvailability right in states)
            {
                using Bitmap bmp16 = TrayIconRenderer.RenderBitmap(left, right, 16);
                using Bitmap bmp32 = TrayIconRenderer.RenderBitmap(left, right, 32);
                Assert.Equal(16, bmp16.Width);
                Color center16 = bmp16.GetPixel(8, 8);
                Color center32 = bmp32.GetPixel(16, 16);
                Assert.True(center16.A < 40, $"16 gap alpha {center16.A}");
                Assert.True(center32.A < 40, $"32 gap alpha {center32.A}");
                bmp32.Save(Path.Combine(dir, $"{left}-{right}.png"));
            }
        }
    }

    [Fact]
    public void T53_RejectsBadEndpointFileWithoutThrowing()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "net-lights-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(
                SettingsStore.EndpointsPath,
                """[{"id":"x","group":"mars","uri":"https://example.test/","infrastructureId":"x"}]""");
            (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
            Assert.NotNull(warning);
            Assert.True(config.UsingBuiltinPool);
            Assert.Equal(14, config.Endpoints.Count);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void AgeLabel_CountsWholeSecondsAndNeverGoesNegative()
    {
        DateTimeOffset now = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("", StatusForm.AgeLabel(null, now));
        Assert.Equal("0 сек назад", StatusForm.AgeLabel(now, now));
        Assert.Equal("1 сек назад", StatusForm.AgeLabel(now.AddMilliseconds(-1499), now));
        Assert.Equal("12 сек назад", StatusForm.AgeLabel(now.AddSeconds(-12), now));
        Assert.Equal("0 сек назад", StatusForm.AgeLabel(now.AddSeconds(3), now));
    }

    [Fact]
    public void RowStyle_UsesCoolBlueForRuAndWarmAmberForVpn()
    {
        (Color ruOkBack, Color ruOkFore) = StatusForm.RowStyle(EndpointGroup.Ru, ProbeOutcome.Reachable);
        (Color ruDownBack, _) = StatusForm.RowStyle(EndpointGroup.Ru, ProbeOutcome.Unreachable);
        (Color vpnOkBack, _) = StatusForm.RowStyle(EndpointGroup.Vpn, ProbeOutcome.Reachable);
        (Color vpnDownBack, _) = StatusForm.RowStyle(EndpointGroup.Vpn, ProbeOutcome.Unreachable);
        Assert.True(ruOkBack.B > ruOkBack.R && ruOkBack.B > ruOkBack.G);
        Assert.True(ruDownBack.GetBrightness() < ruOkBack.GetBrightness());
        Assert.True(vpnOkBack.R > vpnOkBack.B && vpnOkBack.G > vpnOkBack.B);
        Assert.True(vpnDownBack.GetBrightness() < vpnOkBack.GetBrightness());
        Assert.True(ruOkFore.GetBrightness() < 0.45f);
    }

    [Fact]
    public void StatusForm_ClockTicksDoNotBeginUpdate()
    {
        using var form = new StatusForm();
        form.CreateControl();
        var empty = new MonitorKernel(new MonitorConfiguration { Endpoints = BuiltinEndpoints.All }, TimeProvider.System).Snapshot;
        form.Bind(empty);
        int binds = form.DataBindCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            form.RefreshAges();
        }

        sw.Stop();
        Assert.Equal(binds, form.DataBindCount);
        Assert.Equal(14, StatusSnapshotProjector.Rows(empty).Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"1000 clock ticks took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void StatusForm_UserCloseHidesWithoutRecreatingTaskbarWindow()
    {
        using var form = new StatusForm();
        form.Reveal();
        Assert.True(form.Visible);
        Assert.True(form.ShowInTaskbar);
        Assert.True(form.HideToTrayIfUserClosing(CloseReason.UserClosing));
        Assert.False(form.Visible);
        Assert.True(form.ShowInTaskbar);
        Assert.False(form.HideToTrayIfUserClosing(CloseReason.ApplicationExitCall));
    }

    [Fact]
    public void PausedIcon_UsesCoolBlueNotUnknownGray()
    {
        using Bitmap paused = TrayIconRenderer.RenderBitmap(GroupAvailability.Online, GroupAvailability.Online, 32, paused: true);
        using Bitmap unknown = TrayIconRenderer.RenderBitmap(GroupAvailability.Unknown, GroupAvailability.Unknown, 32);
        Color pausePixel = paused.GetPixel(8, 16);
        Color unknownPixel = unknown.GetPixel(8, 16);
        Assert.True(pausePixel.B > pausePixel.R + 20 && pausePixel.B > pausePixel.G, $"pause {pausePixel}");
        Assert.NotEqual(unknownPixel, pausePixel);
        Color gap = paused.GetPixel(16, 16);
        Assert.True(gap.A < 40, $"gap alpha {gap.A}");
    }

    [Fact]
    public void Settings_CheckUpdatesAndExportButtonsShareHeightAndBaseline()
    {
        using var form = new StatusForm();
        ThemedButton check = FindButton(form, "checkUpdates");
        ThemedButton export = FindButton(form, "exportLog");
        Assert.Equal("Проверить обновления", check.Text);
        Assert.Equal("Экспорт журнала", export.Text);
        Assert.Equal(UiTheme.ButtonHeight, check.Height);
        Assert.Equal(check.Height, export.Height);
        Assert.Equal(check.Top, export.Top);
        Assert.Equal(check.Margin.Top, export.Margin.Top);
        Assert.Equal(check.Margin.Bottom, export.Margin.Bottom);
        Assert.True(check.Width >= export.Width);
        Assert.True(export.Left >= check.Right);
    }

    [Fact]
    public void Settings_CheckUpdatesClickInvokesCallbackAndBusyDisablesButton()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        FindButton(form, "settingsTab").PerformClick();
        int clicks = 0;
        form.CheckUpdatesRequested = () => clicks++;
        ThemedButton check = FindButton(form, "checkUpdates");
        Label status = FindLabel(form, "manualUpdateStatus");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(check, [EventArgs.Empty]);
        Assert.Equal(1, clicks);
        form.SetManualUpdateState(true, ManualUpdateCopy.Checking);
        Assert.False(check.Enabled);
        Assert.Equal(ManualUpdateCopy.Checking, status.Text);
        form.SetManualUpdateState(false, ManualUpdateCopy.For(SilentUpdateOutcome.NoUpdate));
        Assert.True(check.Enabled);
        Assert.Equal(ManualUpdateCopy.For(SilentUpdateOutcome.NoUpdate), status.Text);
    }

    [Fact]
    public void TooltipAndBadges_UseProviderLabel()
    {
        using var form = new StatusForm();
        _ = form.Handle;
        var snapshot = new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Online, "", null, false, []),
            new GroupSnapshot(EndpointGroup.Vpn, GroupAvailability.Online, "", null, false, []),
            true,
            null,
            false,
            null,
            false);
        form.Bind(snapshot);
        string tooltip = DiagnosticExport.FormatTooltip(snapshot);
        Assert.Contains(GroupLabels.Provider, tooltip, StringComparison.Ordinal);
        Assert.NotNull(FindBadge(form, GroupLabels.Provider));
    }

    private static StatusBadge? FindBadge(Control root, string prefix)
    {
        if (root is StatusBadge badge && badge.Text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return badge;
        }

        foreach (Control child in root.Controls)
        {
            StatusBadge? nested = FindBadge(child, prefix);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static ThemedButton FindButton(Control root, string accessibleName)
    {
        ThemedButton? found = Find<ThemedButton>(root, accessibleName);
        Assert.NotNull(found);
        return found;
    }

    private static Label FindLabel(Control root, string accessibleName)
    {
        Label? found = Find<Label>(root, accessibleName);
        Assert.NotNull(found);
        return found;
    }

    private static T? Find<T>(Control root, string accessibleName) where T : Control
    {
        if (root is T match && root.AccessibleName == accessibleName)
        {
            return match;
        }

        foreach (Control child in root.Controls)
        {
            T? nested = Find<T>(child, accessibleName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
