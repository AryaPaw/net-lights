using System.Security.Cryptography;
using System.Text;

namespace NetLights.Core;

public sealed class GeoCountryScheduler : IDisposable
{
    private readonly IGeoCountrySource _source;
    private readonly TimeProvider _time;
    private readonly Action<GeoCountryDisplay> _onChanged;
    private readonly object _gate = new();
    private CancellationTokenSource _run = new();
    private ITimer? _timer;
    private int _inflight;
    private int _generation;
    private int _failStreak;
    private bool _enabled;
    private TimeSpan _refreshInterval = GeoCountryPolicy.RefreshInterval;
    private long _scheduledAt;
    private TimeSpan _scheduledDelay;
    private string? _lastSuccessfulCountry;
    private DateTimeOffset _lastSuccessfulAt;
    private string? _lastPublicIpHash;
    private string? _lastLocalCountry;
    private string? _lastOnlineCountry;
    private string? _lastDatabaseVersion;
    private string? _lastObservedCountry;
    private GeoCountryDisplay _current = GeoCountryDisplay.Unconfirmed;

    public GeoCountryScheduler(IGeoCountrySource source, TimeProvider time, Action<GeoCountryDisplay>? onChanged = null)
    {
        _source = source;
        _time = time;
        _onChanged = onChanged ?? (_ => { });
    }

    public GeoCountryDisplay Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void SetPollingInterval(int seconds)
    {
        lock (_gate)
        {
            _refreshInterval = TimeSpan.FromSeconds(PollingIntervals.NormalizeCountry(seconds));
            if (_enabled && Volatile.Read(ref _inflight) == 0)
            {
                TimeSpan remaining = _scheduledDelay - _time.GetElapsedTime(_scheduledAt);
                ArmLocked(remaining > _refreshInterval ? remaining : _refreshInterval);
            }
        }
    }

    private TimeSpan RefreshInterval { get { lock (_gate) return _refreshInterval; } }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_enabled)
            {
                return;
            }

            _enabled = true;
            _failStreak = 0;
            ReplaceRunLocked();
            ArmLocked(TimeSpan.Zero);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
            ReplaceRunLocked();
            _lastSuccessfulCountry = null;
            _lastSuccessfulAt = default;
            _failStreak = 0;
            PublishLocked(GeoCountryDisplay.Disabled);
        }
    }

    public void NotifyNetworkOrResume()
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _failStreak = 0;
            ReplaceRunLocked();
            ArmLocked(TimeSpan.Zero);
        }
    }

    public void Dispose() => Stop();

    private void OnTimer(object? _)
    {
        _ = RunCycleAsync();
    }

    private async Task RunCycleAsync()
    {
        if (Interlocked.CompareExchange(ref _inflight, 1, 0) != 0)
        {
            return;
        }

        int gen;
        CancellationToken token;
        lock (_gate)
        {
            if (!_enabled)
            {
                Interlocked.Exchange(ref _inflight, 0);
                return;
            }

            gen = _generation;
            token = _run.Token;
        }

        TimeSpan delay = RefreshInterval;
        try
        {
            delay = await ExecuteAsync(gen, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            delay = TimeSpan.Zero;
        }
        catch (Exception)
        {
            delay = NextBackoff();
            KeepLastKnown(gen);
        }
        finally
        {
            Interlocked.Exchange(ref _inflight, 0);
            lock (_gate)
            {
                if (_enabled)
                {
                    ArmLocked(gen == _generation ? delay : TimeSpan.Zero);
                }
            }
        }
    }

    private async Task<TimeSpan> ExecuteAsync(int gen, CancellationToken token)
    {
        GeoCountryLookupResult lookup = await _source.GetCurrentAsync(token).ConfigureAwait(false);
        if (IsStale(gen) || token.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }

        if (!lookup.Ok || !GeoCountryParsers.IsIso3166Alpha2(lookup.CountryCode))
        {
            if (lookup.ResolutionSource is null && !lookup.Conflicted && !lookup.DifferentIp)
            {
                KeepLastKnown(gen);
                return FailureDelay(lookup.RetryAfter);
            }
            lock (_gate)
            {
                if (IsStale(gen) || !_enabled) return TimeSpan.Zero;
                GeoCountryDisplay display = BuildDisplayLocked(lookup, country: null, confirmed: false);
                PublishIfChangedLocked(display);
            }
            return FailureDelay(lookup.RetryAfter);
        }

        string country = lookup.CountryCode!;
        lock (_gate)
        {
            if (IsStale(gen) || !_enabled)
            {
                return TimeSpan.Zero;
            }

            _lastSuccessfulAt = _time.GetUtcNow();
            _failStreak = 0;
            GeoCountryDisplay display = BuildDisplayLocked(lookup, country, confirmed: true);
            _lastSuccessfulCountry = country;
            PublishIfChangedLocked(display);
        }

        return RefreshInterval;
    }

    private void KeepLastKnown(int gen)
    {
        lock (_gate)
        {
            if (IsStale(gen) || !_enabled) return;
            string? iso = GeoCountryParsers.IsIso3166Alpha2(_current.Letters)
                ? _current.Letters
                : GeoCountryParsers.IsIso3166Alpha2(_lastSuccessfulCountry) ? _lastSuccessfulCountry : null;
            if (iso is null)
            {
                PublishLocked(GeoCountryDisplay.Unconfirmed);
                return;
            }

            if (_lastSuccessfulAt != default && _time.GetUtcNow() - _lastSuccessfulAt <= GeoCountryPolicy.StaleAfter)
            {
                GeoCountryDisplay stale = GeoCountryDisplay.LastKnown(iso);
                if (_current != stale)
                {
                    PublishLocked(stale);
                }
            }
            else if (_current != GeoCountryDisplay.Unconfirmed)
            {
                PublishLocked(GeoCountryDisplay.Unconfirmed);
            }
        }
    }

    private bool IsStale(int gen) => Volatile.Read(ref _generation) != gen;

    private TimeSpan NextBackoff()
    {
        lock (_gate)
        {
            int index = _failStreak;
            if (_failStreak < GeoCountryPolicy.FailureBackoff.Length)
            {
                _failStreak++;
            }

            TimeSpan delay = index < GeoCountryPolicy.FailureBackoff.Length
                ? GeoCountryPolicy.FailureBackoff[index]
                : _refreshInterval;
            return delay > _refreshInterval ? delay : _refreshInterval;
        }
    }

    private TimeSpan FailureDelay(TimeSpan? retryAfter)
    {
        TimeSpan delay = retryAfter ?? NextBackoff();
        return delay > RefreshInterval ? delay : RefreshInterval;
    }

    private void ReplaceRunLocked()
    {
        _generation++;
        _run.Cancel();
        _run.Dispose();
        _run = new CancellationTokenSource();
    }

    private void ArmLocked(TimeSpan delay)
    {
        _scheduledAt = _time.GetTimestamp();
        _scheduledDelay = delay;
        _timer ??= _time.CreateTimer(OnTimer, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void PublishLocked(GeoCountryDisplay display)
    {
        _current = display;
        _onChanged(display);
    }

    private GeoCountryDisplay BuildDisplayLocked(GeoCountryLookupResult lookup, string? country, bool confirmed)
    {
        string? publicIp = System.Net.IPAddress.TryParse(lookup.PublicIp, out System.Net.IPAddress? address)
            ? address.ToString() : null;
        string? ipHash = HashIp(publicIp);
        bool publicIpChanged = ipHash is not null && _lastPublicIpHash is not null
            && !string.Equals(ipHash, _lastPublicIpHash, StringComparison.Ordinal);
        bool sameIp = ipHash is not null && string.Equals(ipHash, _lastPublicIpHash, StringComparison.Ordinal);
        bool countryChanged = confirmed && _lastObservedCountry is not null
            && !string.Equals(_lastObservedCountry, country, StringComparison.OrdinalIgnoreCase);
        bool classificationChanged = sameIp && (
            Changed(lookup.LocalCountry, _lastLocalCountry)
            || Changed(lookup.OnlineCountry, _lastOnlineCountry)
            || Changed(lookup.DatabaseVersion, _lastDatabaseVersion));
        DateTimeOffset observedAt = _time.GetUtcNow();
        DateTimeOffset? confirmedThrough = confirmed ? null : _lastSuccessfulAt == default ? null : _lastSuccessfulAt;
        LocationObservation observation = new(observedAt, ipHash, country, lookup.LocalCountry,
            lookup.OnlineCountry, lookup.ThirdCountry, lookup.DatabaseVersion,
            publicIpChanged, countryChanged, classificationChanged, confirmed, publicIp, observedAt,
            lookup.IplocateCountry, lookup.IplocateDatabaseVersion, lookup.ResolutionSource ?? "legacy");

        if (ipHash is not null) _lastPublicIpHash = ipHash;
        _lastLocalCountry = lookup.LocalCountry;
        _lastOnlineCountry = lookup.OnlineCountry;
        _lastDatabaseVersion = lookup.DatabaseVersion;
        if (confirmed) _lastObservedCountry = country;

        GeoCountryDisplay display = confirmed
            ? GeoCountryDisplay.FromConfirmedLookup(lookup, observation)
            : lookup.ResolutionSource == "local-estimate"
                ? GeoCountryDisplay.FromLocalEstimate(lookup, observation, confirmedThrough)
                : GeoCountryDisplay.FromUnconfirmedLookup(lookup, observation) with { ConfirmedThroughUtc = confirmedThrough };
        return display with { PublicIpHash = ipHash };
    }

    private void PublishIfChangedLocked(GeoCountryDisplay display)
    {
        if (_current with { Observation = null } == display with { Observation = null })
        {
            if (display.Observation is not null) _onChanged(display);
            return;
        }
        PublishLocked(display);
    }

    private static bool Changed(string? value, string? previous)
        => !string.Equals(value, previous, StringComparison.OrdinalIgnoreCase);

    private static string? HashIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Net.IPAddress.TryParse(value, out System.Net.IPAddress? ip))
            return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ip.ToString()))).ToLowerInvariant();
    }
}
