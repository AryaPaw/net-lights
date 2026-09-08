using System.Drawing;
using NetLights.App;
using NetLights.Core;
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
    public void Tooltip_IncludesPauseMarker()
    {
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = BuiltinEndpoints.All }, TimeProvider.System);
        Assert.DoesNotContain("пауза", DiagnosticExport.FormatTooltip(kernel.Snapshot), StringComparison.Ordinal);
        kernel.SetPaused(true);
        Assert.Contains("пауза", DiagnosticExport.FormatTooltip(kernel.Snapshot), StringComparison.Ordinal);
    }
}
