using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountryNamesTests
{
    [Fact]
    public void Tooltip_IsRussianSlashEnglish()
    {
        Assert.Equal("Германия / Germany", GeoCountryNames.Tooltip("DE"));
        Assert.Equal("США / United States", GeoCountryNames.Tooltip("US"));
        Assert.Equal("Нидерланды / Netherlands", GeoCountryNames.Tooltip("NL"));
    }

    [Fact]
    public void Tooltip_FitsNotifyIconLimit()
    {
        Assert.True(GeoCountryNames.Tooltip("DE").Length <= GeoCountryNames.TrayTooltipMaxChars);
        Assert.True(GeoCountryDisplay.Confirmed("DO").Tooltip.Length <= GeoCountryNames.TrayTooltipMaxChars);
    }

    [Fact]
    public void ConfirmedDisplay_UsesLocalizedTooltip()
        => Assert.Equal("Германия / Germany", GeoCountryDisplay.Confirmed("DE").Tooltip);

    [Fact]
    public void LastKnownDisplay_MarksStaleWithoutFreshFlag()
    {
        GeoCountryDisplay display = GeoCountryDisplay.LastKnown("DE");
        Assert.Equal("DE", display.Letters);
        Assert.False(display.Fresh);
        Assert.Equal("Германия (устарело)", display.Tooltip);
        Assert.True(display.Tooltip.Length <= GeoCountryNames.TrayTooltipMaxChars);
        Assert.Equal("определение выключено", GeoCountryDisplay.Disabled.Tooltip);
        Assert.False(GeoCountryDisplay.Disabled.Fresh);
    }
}
