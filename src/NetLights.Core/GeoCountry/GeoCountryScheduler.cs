using System.Net;

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
    private bool _needsFullConfirm = true;
    private string? _confirmedIp;
    private string? _confirmedCountry;
    private DateTimeOffset _confirmedAt;
    private DateTimeOffset _confirmPauseUntil;
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
            _needsFullConfirm = true;
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
            _confirmedIp = null;
            _confirmedCountry = null;
            _needsFullConfirm = true;
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

            _needsFullConfirm = true;
            _confirmedIp = null;
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

        TimeSpan delay = GeoCountryPolicy.IpWatchInterval;
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
        GeoCountrySelfResult self = await _source.GetSelfAsync(token).ConfigureAwait(false);
        if (IsStale(gen) || token.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }

        if (!self.Ok || self.Ip is null || !GeoCountryParsers.IsIso3166Alpha2(self.CountryCode))
        {
            KeepLastKnown();
            return self.RetryAfter ?? NextBackoff();
        }

        IPAddress ip = self.Ip;
        string ipText = ip.ToString();
        bool sameIp;
        bool fresh;
        lock (_gate)
        {
            sameIp = string.Equals(_confirmedIp, ipText, StringComparison.Ordinal);
            fresh = sameIp
                    && _confirmedCountry is not null
                    && !_needsFullConfirm
                    && _time.GetUtcNow() - _confirmedAt < GeoCountryPolicy.ConfirmMaxAge;
        }

        if (fresh)
        {
            ResetFailures();
            return GeoCountryPolicy.IpWatchInterval;
        }

        GeoCountryConfirmResult confirm = default;
        bool skipConfirm;
        lock (_gate)
        {
            skipConfirm = _time.GetUtcNow() < _confirmPauseUntil;
        }

        if (!skipConfirm)
        {
            confirm = await _source.ConfirmAsync(ip, token).ConfigureAwait(false);
            if (confirm.RetryAfter is { } pause && pause > TimeSpan.Zero)
            {
                lock (_gate)
                {
                    _confirmPauseUntil = _time.GetUtcNow() + pause;
                }
            }
        }

        if (IsStale(gen) || token.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }

        string iso = self.CountryCode!;
        lock (_gate)
        {
            if (IsStale(gen) || !_enabled)
            {
                return TimeSpan.Zero;
            }

            _confirmedIp = ipText;
            _confirmedCountry = iso;
            _confirmedAt = _time.GetUtcNow();
            _needsFullConfirm = false;
            _failStreak = 0;
            PublishLocked(GeoCountryDisplay.Confirmed(iso));
        }

        return GeoCountryPolicy.IpWatchInterval;
    }

    private void KeepLastKnown()
    {
        lock (_gate)
        {
            string? iso = GeoCountryParsers.IsIso3166Alpha2(_current.Letters)
                ? _current.Letters
                : GeoCountryParsers.IsIso3166Alpha2(_confirmedCountry) ? _confirmedCountry : null;
            if (iso is null)
            {
                PublishLocked(GeoCountryDisplay.Unconfirmed);
                return;
            }

            if (_time.GetUtcNow() - _confirmedAt > GeoCountryPolicy.ConfirmMaxAge)
            {
                PublishLocked(GeoCountryDisplay.LastKnown(iso));
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
                : GeoCountryPolicy.IpWatchInterval;
        }
    }

    private void ResetFailures()
    {
        lock (_gate)
        {
            _failStreak = 0;
        }
    }

    private void Publish(GeoCountryDisplay display)
    {
        lock (_gate)
        {
            PublishLocked(display);
        }
    }

    private void PublishLocked(GeoCountryDisplay display)
    {
        _current = display;
        _onChanged(display);
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
}
