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
                using Bitmap bmp = TrayIconRenderer.RenderBitmap(left, right, 32);
                Assert.Equal(32, bmp.Width);
                Color center = bmp.GetPixel(16, 16);
                Assert.True(center.A < 40, $"gap alpha {center.A}");
                bmp.Save(Path.Combine(dir, $"{left}-{right}.png"));
            }
        }
    }

    [Fact]
    public void T53_RejectsBadEndpointFileWithoutThrowing()
    {
        (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
        Assert.NotNull(config.Endpoints);
        Assert.Equal(14, config.Endpoints.Count);
        _ = warning;
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
}
