namespace NetLights.App;

internal enum GeoCountryLetterScale
{
    Compact = 0,
    Regular = 1,
    Large = 2
}

internal static class GeoCountryLetterScales
{
    public static readonly string[] Captions = ["Компактный", "Обычный", "Крупный"];

    public static GeoCountryLetterScale Parse(int? stored)
    {
        if (stored is >= 0 and <= 2)
        {
            return (GeoCountryLetterScale)stored.Value;
        }

        return GeoCountryLetterScale.Regular;
    }

    public static float Fill(GeoCountryLetterScale scale)
        => scale switch
        {
            GeoCountryLetterScale.Compact => 0.74f,
            GeoCountryLetterScale.Large => 0.96f,
            _ => 0.86f
        };
}
