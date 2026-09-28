namespace NetLights.Core;

public static class LocationChartWindows
{
    public static readonly TimeSpan Hours3 = TimeSpan.FromHours(3);
    public static readonly TimeSpan Hours6 = TimeSpan.FromHours(6);
    public static readonly TimeSpan Hours12 = TimeSpan.FromHours(12);
    public static readonly TimeSpan Hours24 = TimeSpan.FromHours(24);
    public static readonly TimeSpan Days3 = TimeSpan.FromDays(3);
    public static readonly TimeSpan Days5 = TimeSpan.FromDays(5);
    public static readonly TimeSpan Days7 = TimeSpan.FromDays(7);

    public static readonly TimeSpan Default = Hours12;

    public static readonly TimeSpan[] All = [Hours3, Hours6, Hours12, Hours24, Days3, Days5, Days7];

    public static readonly string[] Captions = ["3 ч", "6 ч", "12 ч", "24 ч", "3 д", "5 д", "7 д"];

    // Fixed samples represent time intervals, never screen pixels.
    public static int FrequencyIntervalCount(TimeSpan window) => ToHours(window) switch
    {
        3 => 90,    // 2 minutes
        6 => 90,    // 4 minutes
        12 => 72,   // 10 minutes
        24 => 96,   // 15 minutes
        72 => 72,   // 1 hour
        120 => 60,  // 2 hours
        168 => 84,  // 2 hours
        _ => 72
    };

    public static TimeSpan ParseHours(int hours)
        => hours switch
        {
            3 => Hours3,
            6 => Hours6,
            12 => Hours12,
            24 => Hours24,
            72 => Days3,
            120 => Days5,
            168 => Days7,
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

        if (window == Days5)
        {
            return 120;
        }

        if (window == Days7)
        {
            return 168;
        }

        return 12;
    }

    public static string Label(TimeSpan window)
        => window == Days3 ? "3 дня"
            : window == Days5 ? "5 дней"
            : window == Days7 ? "7 дней"
            : $"{Math.Max(1, (int)Math.Round(window.TotalHours))} ч";
}
