namespace NetLights.Core;

public sealed record GeoCountryDisplay(string Letters, string Tooltip, bool Fresh = true)
{
    public static GeoCountryDisplay Unconfirmed { get; } = new("??", "??", false);

    public static GeoCountryDisplay Disabled { get; } = new("??", "определение выключено", false);

    public static GeoCountryDisplay Confirmed(string iso2)
        => new(iso2, GeoCountryNames.Tooltip(iso2));

    public static GeoCountryDisplay LastKnown(string iso2)
    {
        string text = GeoCountryNames.Russian(iso2) + " (устарело)";
        if (text.Length > GeoCountryNames.TrayTooltipMaxChars)
        {
            text = text[..GeoCountryNames.TrayTooltipMaxChars];
        }

        return new(iso2, text, false);
    }
}
