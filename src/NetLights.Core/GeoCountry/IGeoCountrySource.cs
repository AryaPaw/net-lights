namespace NetLights.Core;

public readonly record struct GeoCountryLookupResult(
    bool Ok,
    string? CountryCode,
    TimeSpan? RetryAfter,
    string? PublicIp = null,
    bool Conflicted = false,
    bool DifferentIp = false,
    string? LocalCountry = null,
    string? OnlineCountry = null,
    string? ThirdCountry = null,
    string? DatabaseVersion = null,
    string? IplocateCountry = null,
    string? IplocateDatabaseVersion = null,
    string? ResolutionSource = null);

public interface IGeoCountrySource
{
    Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken);
}
