namespace NetLights.Core;

public static class PollingIntervals
{
    public const int DefaultAvailabilitySeconds = 5;
    public const int DefaultCountrySeconds = 10;
    public static readonly int[] AvailabilitySeconds = [2, 3, 4, 5, 10, 15, 30, 60, 120, 300];
    public static readonly int[] CountrySeconds = [10, 15, 30, 60];

    public static int NormalizeAvailability(int seconds)
        => AvailabilitySeconds.Contains(seconds) ? seconds : DefaultAvailabilitySeconds;

    public static int NormalizeCountry(int seconds)
        => CountrySeconds.Contains(seconds) ? seconds : DefaultCountrySeconds;

    public static MonitorConfiguration Apply(MonitorConfiguration source, int seconds)
    {
        TimeSpan interval = TimeSpan.FromSeconds(NormalizeAvailability(seconds));
        int groupSize = source.Endpoints.GroupBy(endpoint => endpoint.Group).Max(group => group.Count());
        return new MonitorConfiguration
        {
            Endpoints = source.Endpoints,
            UsingBuiltinPool = source.UsingBuiltinPool,
            ConfigWarning = source.ConfigWarning,
            GroupInterval = interval,
            GroupOffset = interval / 2,
            ProbeDeadline = source.ProbeDeadline,
            MinEndpointInterval = source.MinEndpointInterval,
            Freshness = TimeSpan.FromTicks(Math.Max(MonitorConstants.Freshness.Ticks, (interval * (groupSize + 3)).Ticks))
        };
    }
}
