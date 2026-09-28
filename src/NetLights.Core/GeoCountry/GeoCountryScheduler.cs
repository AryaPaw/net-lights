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
    private string? _lastSuccessfulCountry;
    private DateTimeOffset _lastSuccessfulAt;
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

        TimeSpan delay = GeoCountryPolicy.RefreshInterval;
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
            KeepLastKnown();
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
            KeepLastKnown();
            return lookup.RetryAfter ?? NextBackoff();
        }

        string country = lookup.CountryCode!;
        lock (_gate)
        {
            if (IsStale(gen) || !_enabled)
            {
                return TimeSpan.Zero;
            }

            _lastSuccessfulCountry = country;
            _lastSuccessfulAt = _time.GetUtcNow();
            _failStreak = 0;
            GeoCountryDisplay display = GeoCountryDisplay.Confirmed(country);
            if (_current != display)
            {
                PublishLocked(display);
            }
        }

        return GeoCountryPolicy.RefreshInterval;
    }

    private void KeepLastKnown()
    {
        lock (_gate)
        {
            string? iso = GeoCountryParsers.IsIso3166Alpha2(_current.Letters)
                ? _current.Letters
                : GeoCountryParsers.IsIso3166Alpha2(_lastSuccessfulCountry) ? _lastSuccessfulCountry : null;
            if (iso is null)
            {
                PublishLocked(GeoCountryDisplay.Unconfirmed);
                return;
            }

            if (_lastSuccessfulAt == default || _time.GetUtcNow() - _lastSuccessfulAt > GeoCountryPolicy.StaleAfter)
            {
                GeoCountryDisplay stale = GeoCountryDisplay.LastKnown(iso);
                if (_current != stale)
                {
                    PublishLocked(stale);
                }
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

            return index < GeoCountryPolicy.FailureBackoff.Length
                ? GeoCountryPolicy.FailureBackoff[index]
                : GeoCountryPolicy.RefreshInterval;
        }
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
        _timer ??= _time.CreateTimer(OnTimer, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void PublishLocked(GeoCountryDisplay display)
    {
        _current = display;
        _onChanged(display);
    }
}
