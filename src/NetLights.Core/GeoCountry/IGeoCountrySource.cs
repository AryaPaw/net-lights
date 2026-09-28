namespace NetLights.Core;

public readonly record struct GeoCountryLookupResult(
    bool Ok,
    string? CountryCode,
    TimeSpan? RetryAfter);

public interface IGeoCountrySource
{
    Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken);
}
