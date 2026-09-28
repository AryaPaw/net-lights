namespace NetLights.Core;

public static class GeoCountryPolicy
{
    public static readonly Uri IpWhoUri = new("https://ipwho.is/?fields=success,country_code");
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan[] FailureBackoff =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2)
    ];
    public static readonly TimeSpan MissingRetryAfter = TimeSpan.FromSeconds(60);
    public const int MaxBodyBytes = 4 * 1024;
}
