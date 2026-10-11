using NetLights.Core;

namespace NetLights.Networking;

/// <summary>Accepts a country only when two independent services agree about the caller's public IP country.</summary>
public sealed class CorroboratedGeoCountrySource : IGeoCountrySource, IDisposable
{
    private readonly IGeoCountrySource _primary;
    private readonly IGeoCountrySource _secondary;
    private readonly bool _ownsSources;

    public CorroboratedGeoCountrySource(string version)
        : this(HttpsGeoCountryClient.CreateProduction(version),
            HttpsGeoCountryClient.CreateCountryIsProduction(version), ownsSources: true)
    {
    }

    public CorroboratedGeoCountrySource(IGeoCountrySource primary, IGeoCountrySource secondary, bool ownsSources = false)
    {
        _primary = primary;
        _secondary = secondary;
        _ownsSources = ownsSources;
    }

    public async Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        Task<GeoCountryLookupResult> first = _primary.GetCurrentAsync(cancellationToken);
        Task<GeoCountryLookupResult> second = _secondary.GetCurrentAsync(cancellationToken);
        await Task.WhenAll(first, second).ConfigureAwait(false);

        GeoCountryLookupResult a = first.Result;
        GeoCountryLookupResult b = second.Result;
        bool conflicted = a.Ok && b.Ok
            && GeoCountryParsers.IsIso3166Alpha2(a.CountryCode)
            && GeoCountryParsers.IsIso3166Alpha2(b.CountryCode)
            && !string.Equals(a.CountryCode, b.CountryCode, StringComparison.OrdinalIgnoreCase);
        bool differentIp = a.Ok && b.Ok
            && !string.IsNullOrWhiteSpace(a.PublicIp)
            && !string.IsNullOrWhiteSpace(b.PublicIp)
            && !string.Equals(a.PublicIp, b.PublicIp, StringComparison.Ordinal);
        if (a.Ok && b.Ok
            && GeoCountryParsers.TryNormalizeIso3166Alpha2(a.CountryCode, out string? iso)
            && string.Equals(a.CountryCode, b.CountryCode, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.PublicIp)
            && !differentIp)
        {
            return new GeoCountryLookupResult(true, iso, null, a.PublicIp);
        }

        TimeSpan? retry = a.RetryAfter is { } ar && b.RetryAfter is { } br
            ? (ar > br ? ar : br)
            : a.RetryAfter ?? b.RetryAfter;
        if (conflicted || differentIp)
            retry ??= GeoCountryPolicy.RefreshInterval;
        return new GeoCountryLookupResult(false, null, retry,
            Conflicted: conflicted, DifferentIp: differentIp);
    }

    public void Dispose()
    {
        if (!_ownsSources) return;
        (_primary as IDisposable)?.Dispose();
        (_secondary as IDisposable)?.Dispose();
    }
}
