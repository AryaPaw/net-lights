namespace NetLights.Core;

public static class GeoCountryPolicy
{
    public static readonly Uri CountryIsUri = new("https://api.country.is/");
    public static readonly Uri IpWhoBaseUri = new("https://ipwho.is/");
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IpWatchInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConfirmMaxAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan[] FailureBackoff =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2)
    ];
    public static readonly TimeSpan MissingRetryAfter = TimeSpan.FromSeconds(60);
    public const int MaxBodyBytes = 4 * 1024;
    public const string IpWhoFields = "success,country_code";
}
