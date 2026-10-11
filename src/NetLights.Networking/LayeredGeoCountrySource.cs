using System.Collections.Concurrent;
using System.Net;
using NetLights.Core;

namespace NetLights.Networking;

/// <summary>Uses country.is as the primary answer and records both local databases for comparison.</summary>
public sealed class LayeredGeoCountrySource : IGeoCountrySource, IDisposable
{
    private static readonly TimeSpan ThirdPartyCacheTtl = TimeSpan.FromHours(24);
    private const int ThirdPartyCacheLimit = 512;
    private readonly IGeoCountrySource _countryIs;
    private readonly IGeoCountrySource _ipWho;
    private readonly IGeoIpDatabaseProvider _database;
    private readonly IPLocateCountryDatabaseManager? _comparisonDatabase;
    private readonly IpWhoUsageLedger _usage;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedIpWho> _ipWhoCache = new(StringComparer.Ordinal);
    private long _nextCacheSweepUtcTicks;
    private long _countryIsRetryUntilUtcTicks;
    private IPAddress? _lastObservedIp;

    public LayeredGeoCountrySource(string version, string dataDirectory, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _countryIs = HttpsGeoCountryClient.CreateCountryIsProduction(version);
        _ipWho = HttpsGeoCountryClient.CreateProduction(version);
        _database = new V2FlyGeoIpDatabaseManager(dataDirectory, _time);
        _comparisonDatabase = new IPLocateCountryDatabaseManager(dataDirectory);
        _usage = new IpWhoUsageLedger(dataDirectory);
    }

    internal LayeredGeoCountrySource(
        IGeoCountrySource countryIs,
        IGeoCountrySource ipWho,
        IGeoIpDatabaseProvider database,
        IpWhoUsageLedger usage,
        TimeProvider? time = null)
    {
        _countryIs = countryIs;
        _ipWho = ipWho;
        _database = database;
        _usage = usage;
        _time = time ?? TimeProvider.System;
    }

    public async Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
    {
        _comparisonDatabase?.Start();
        _comparisonDatabase?.CheckForUpdate();
        _database.Start();
        _database.CheckForUpdate();

        DateTimeOffset now = _time.GetUtcNow();
        long retryUntil = Interlocked.Read(ref _countryIsRetryUntilUtcTicks);
        GeoCountryLookupResult countryIs;
        if (now.UtcTicks < retryUntil)
            countryIs = new GeoCountryLookupResult(false, null, TimeSpan.FromTicks(retryUntil - now.UtcTicks));
        else
        {
            countryIs = await _countryIs.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!countryIs.Ok && countryIs.RetryAfter is { } delay && delay > TimeSpan.Zero)
                Interlocked.Exchange(ref _countryIsRetryUntilUtcTicks,
                    _time.GetUtcNow().AddTicks(Math.Min(delay.Ticks, TimeSpan.FromDays(1).Ticks)).UtcTicks);
        }
        IPAddress? countryIsIp = ParseIp(countryIs.PublicIp);
        bool hasCountryIsAnswer = countryIs.Ok && IsCountry(countryIs.CountryCode) && countryIsIp is not null;
        IPAddress? ip = countryIsIp;
        string? onlineCountry = hasCountryIsAnswer ? countryIs.CountryCode : null;
        string? acceptedCountry = onlineCountry;
        string? thirdCountry = null;
        string? source = "none";
        bool differentIp = false;
        TimeSpan? retryAfter = countryIs.RetryAfter;

        if (!hasCountryIsAnswer)
        {
            GeoCountryLookupResult fallback = await GetCachedIpWhoAsync(countryIsIp, cancellationToken).ConfigureAwait(false);
            retryAfter = fallback.RetryAfter ?? retryAfter;
            IPAddress? fallbackIp = ParseIp(fallback.PublicIp);
            bool mismatch = countryIsIp is not null && fallbackIp is not null && !countryIsIp.Equals(fallbackIp);
            differentIp = mismatch;
            if (fallback.Ok && IsCountry(fallback.CountryCode) && fallbackIp is not null && !mismatch)
            {
                ip = fallbackIp;
                thirdCountry = fallback.CountryCode;
                acceptedCountry = fallback.CountryCode;
                source = "ipwho.is";
            }
            else
            {
                // Do not claim a newly observed public IP if online services supplied none.
                ip = _lastObservedIp;
            }
        }
        else
        {
            source = "country.is";
        }

        if (ip is not null && (hasCountryIsAnswer || source == "ipwho.is"))
            _lastObservedIp = ip;

        V2FlyGeoIpDatabase? v2fly = _database.Current;
        string? local = ip is null ? null : v2fly?.Lookup(ip);
        string? iplocate = ip is null ? null : _comparisonDatabase?.Lookup(ip);
        string? v2Version = _database.Version;
        string? iplocateVersion = _comparisonDatabase?.Version;

        if (acceptedCountry is not null && ip is not null)
        {
            return new GeoCountryLookupResult(true, acceptedCountry, null, ip.ToString(),
                Conflicted: (local is not null && !SameCountry(local, acceptedCountry))
                    || (iplocate is not null && !SameCountry(iplocate, acceptedCountry)),
                LocalCountry: local, OnlineCountry: onlineCountry, ThirdCountry: thirdCountry, DatabaseVersion: v2Version,
                IplocateCountry: iplocate, IplocateDatabaseVersion: iplocateVersion,
                ResolutionSource: source);
        }

        // An estimate may be shown for the last previously observed IP, but is never promoted
        // to a confirmed timeline country and never refreshes that observation's lifetime.
        return new GeoCountryLookupResult(false, null,
            retryAfter ?? GeoCountryPolicy.RefreshInterval,
            ip?.ToString(), DifferentIp: differentIp, LocalCountry: local, OnlineCountry: null, DatabaseVersion: v2Version,
            IplocateCountry: iplocate, IplocateDatabaseVersion: iplocateVersion,
            ResolutionSource: !differentIp && (local is not null || iplocate is not null) ? "local-estimate" : "none");
    }

    public void Dispose()
    {
        (_countryIs as IDisposable)?.Dispose();
        (_ipWho as IDisposable)?.Dispose();
        _database.Dispose();
        _comparisonDatabase?.Dispose();
    }

    private async Task<GeoCountryLookupResult> GetCachedIpWhoAsync(IPAddress? expectedIp, CancellationToken cancellationToken)
    {
        TrimIpWhoCache();
        string? key = expectedIp is null ? null : IpWhoUsageLedger.Hash(expectedIp);
        if (key is not null && _ipWhoCache.TryGetValue(key, out CachedIpWho? cached) && cached.ExpiresAt > _time.GetUtcNow())
            return cached.Result;

        if (!_usage.TryConsume(expectedIp))
            return new GeoCountryLookupResult(false, null, GeoCountryPolicy.RefreshInterval);

        GeoCountryLookupResult result = await _ipWho.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (result.Ok && ParseIp(result.PublicIp) is { } returnedIp)
        {
            string returnedKey = IpWhoUsageLedger.Hash(returnedIp);
            var answer = new CachedIpWho(result, _time.GetUtcNow() + ThirdPartyCacheTtl);
            _ipWhoCache[returnedKey] = answer;
            if (key is not null && expectedIp is not null && expectedIp.Equals(returnedIp))
                _ipWhoCache[key] = answer;
        }
        else
        {
            TimeSpan retry = result.RetryAfter is { } requested && requested > TimeSpan.Zero
                ? requested : TimeSpan.FromMinutes(5);
            if (key is not null) _ipWhoCache[key] = new CachedIpWho(result, _time.GetUtcNow() + retry);
        }
        return result;
    }

    private void TrimIpWhoCache()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (_ipWhoCache.Count <= ThirdPartyCacheLimit && now.UtcTicks < Interlocked.Read(ref _nextCacheSweepUtcTicks))
            return;

        Interlocked.Exchange(ref _nextCacheSweepUtcTicks, now.AddMinutes(10).UtcTicks);
        foreach (KeyValuePair<string, CachedIpWho> entry in _ipWhoCache)
            if (entry.Value.ExpiresAt <= now)
                _ipWhoCache.TryRemove(entry.Key, out _);

        int excess = _ipWhoCache.Count - ThirdPartyCacheLimit;
        if (excess <= 0) return;
        foreach (KeyValuePair<string, CachedIpWho> entry in _ipWhoCache.OrderBy(pair => pair.Value.ExpiresAt).Take(excess))
            _ipWhoCache.TryRemove(entry.Key, out _);
    }

    private static bool SameCountry(string? left, string? right)
        => IsCountry(left) && IsCountry(right) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsCountry(string? value) => GeoCountryParsers.IsIso3166Alpha2(value);

    private static IPAddress? ParseIp(string? value)
        => IPAddress.TryParse(value, out IPAddress? ip) && !IPAddress.IsLoopback(ip) ? ip : null;

    private sealed record CachedIpWho(GeoCountryLookupResult Result, DateTimeOffset ExpiresAt);
}
