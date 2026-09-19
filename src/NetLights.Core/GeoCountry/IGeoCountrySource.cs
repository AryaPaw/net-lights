using System.Net;

namespace NetLights.Core;

public readonly record struct GeoCountrySelfResult(
    bool Ok,
    IPAddress? Ip,
    string? CountryCode,
    TimeSpan? RetryAfter);

public readonly record struct GeoCountryConfirmResult(
    bool Ok,
    string? CountryCode,
    TimeSpan? RetryAfter);

public interface IGeoCountrySource
{
    Task<GeoCountrySelfResult> GetSelfAsync(CancellationToken cancellationToken);

    Task<GeoCountryConfirmResult> ConfirmAsync(IPAddress ip, CancellationToken cancellationToken);
}
