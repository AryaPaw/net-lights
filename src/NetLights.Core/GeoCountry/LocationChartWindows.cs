namespace NetLights.Core;

public static class LocationChartWindows
{
    public static readonly TimeSpan Hours3 = TimeSpan.FromHours(3);
    public static readonly TimeSpan Hours6 = TimeSpan.FromHours(6);
    public static readonly TimeSpan Hours12 = TimeSpan.FromHours(12);
    public static readonly TimeSpan Hours24 = TimeSpan.FromHours(24);
    public static readonly TimeSpan Days3 = TimeSpan.FromDays(3);

    public static readonly TimeSpan Default = Hours12;

    public static readonly TimeSpan[] All = [Hours3, Hours6, Hours12, Hours24, Days3];

    public static readonly string[] Captions = ["3 ч", "6 ч", "12 ч", "24 ч", "3 д"];

    public static TimeSpan ParseHours(int hours)
        => hours switch
        {
            3 => Hours3,
            6 => Hours6,
            12 => Hours12,
            24 => Hours24,
            72 => Days3,
            _ => Default
        };

    public static int ToHours(TimeSpan window)
    {
        if (window == Hours3)
        {
            return 3;
        }

        if (window == Hours6)
        {
            return 6;
        }

        if (window == Hours12)
        {
            return 12;
        }

        if (window == Hours24)
        {
            return 24;
        }

        if (window == Days3)
        {
            return 72;
        }

        return 12;
    }
}
