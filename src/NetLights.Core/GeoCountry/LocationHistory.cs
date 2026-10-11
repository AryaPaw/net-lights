using System.Globalization;

namespace NetLights.Core;

public sealed record LocationStay(
    string Iso,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc);

public sealed record LocationObservation(
    DateTimeOffset ObservedUtc,
    string? PublicIpHash,
    string? CountryCode,
    string? LocalCountry,
    string? OnlineCountry,
    string? ThirdCountry,
    string? DatabaseVersion,
    bool PublicIpChanged,
    bool CountryChanged,
    bool ClassificationChanged,
    bool Confirmed,
    string? PublicIp = null,
    DateTimeOffset? ObservedUntilUtc = null,
    string? IplocateCountry = null,
    string? IplocateDatabaseVersion = null,
    string? ResolutionSource = null);

public sealed class LocationHistory
{
    private const int MaximumObservations = 70_000;
    public static readonly TimeSpan ResumeMergeWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OfflineGap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly List<LocationStay> _stays;
    private readonly List<LocationObservation> _observations = [];

    public LocationHistory(IEnumerable<LocationStay>? stays = null, DateTimeOffset? lastObservedUtc = null)
    {
        _stays = stays?.ToList() ?? [];
        LastObservedUtc = lastObservedUtc;
    }

    public DateTimeOffset? LastObservedUtc { get; private set; }

    public long Revision { get; private set; }

    public IReadOnlyList<LocationStay> Stays => _stays;

    public IReadOnlyList<LocationObservation> Observations => _observations;

    public LocationStay? Current
        => _stays.Count > 0 && _stays[^1].EndedUtc is null ? _stays[^1] : null;

    public IReadOnlyList<LocationStay> Past
    {
        get
        {
            if (_stays.Count == 0)
            {
                return [];
            }

            int take = Current is null ? _stays.Count : _stays.Count - 1;
            if (take <= 0)
            {
                return [];
            }

            var past = new LocationStay[take];
            for (int i = 0; i < take; i++)
            {
                past[i] = _stays[take - 1 - i];
            }

            return past;
        }
    }

    public bool NoteIso(string? iso, DateTimeOffset utcNow, bool mergeShortGap = true)
    {
        if (!GeoCountryParsers.TryNormalizeIso3166Alpha2(iso, out string? code))
        {
            return false;
        }

        LastObservedUtc = utcNow;

        if (_stays.Count == 0)
        {
            _stays.Add(new LocationStay(code, utcNow, null));
            Revision++;
            return true;
        }

        LocationStay last = _stays[^1];
        if (last.EndedUtc is null && last.Iso == code)
        {
            return false;
        }

        if (mergeShortGap && last.EndedUtc is DateTimeOffset ended
            && last.Iso == code
            && utcNow - ended <= ResumeMergeWindow)
        {
            _stays[^1] = last with { EndedUtc = null };
            Revision++;
            return true;
        }

        if (last.EndedUtc is null)
        {
            _stays[^1] = last with { EndedUtc = utcNow };
        }

        _stays.Add(new LocationStay(code, utcNow, null));
        Prune(utcNow);
        Revision++;
        return true;
    }

    public void Prune(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - Retention;
        bool changed = _observations.RemoveAll(observation
            => (observation.ObservedUntilUtc ?? observation.ObservedUtc) < cutoff) > 0;
        int transitionAnchor = -1;
        for (int i = 0; i < _stays.Count; i++)
        {
            if (_stays[i].EndedUtc is DateTimeOffset ended && ended <= cutoff)
            {
                transitionAnchor = i;
            }
        }

        for (int i = _stays.Count - 1; i >= 0; i--)
        {
            LocationStay stay = _stays[i];
            DateTimeOffset end = stay.EndedUtc ?? now;
            if (end <= cutoff && i != transitionAnchor)
            {
                _stays.RemoveAt(i);
                changed = true;
                continue;
            }

            if (i != transitionAnchor && stay.StartedUtc < cutoff)
            {
                _stays[i] = stay with { StartedUtc = cutoff };
                changed = true;
            }
        }

        if (changed) Revision++;
    }

    public bool NoteObservation(LocationObservation observation)
    {
        if (observation.ObservedUtc == default) return false;
        DateTimeOffset observedUntil = observation.ObservedUntilUtc ?? observation.ObservedUtc;
        if (observedUntil < observation.ObservedUtc) return false;
        if (observation.PublicIpHash is { } hash
            && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) return false;
        if (observation.PublicIp is { } publicIp
            && (!System.Net.IPAddress.TryParse(publicIp, out System.Net.IPAddress? address)
                || !string.Equals(publicIp, address.ToString(), StringComparison.OrdinalIgnoreCase))) return false;
        if (observation.DatabaseVersion?.Length > 64) return false;
        if (observation.CountryCode is not null
            && !GeoCountryParsers.TryNormalizeIso3166Alpha2(observation.CountryCode, out _)) return false;
        if (observation.LocalCountry is not null
            && !GeoCountryParsers.TryNormalizeIso3166Alpha2(observation.LocalCountry, out _)) return false;
        if (observation.OnlineCountry is not null
            && !GeoCountryParsers.TryNormalizeIso3166Alpha2(observation.OnlineCountry, out _)) return false;
        if (observation.ThirdCountry is not null
            && !GeoCountryParsers.TryNormalizeIso3166Alpha2(observation.ThirdCountry, out _)) return false;
        if (observation.IplocateCountry is not null
            && !GeoCountryParsers.TryNormalizeIso3166Alpha2(observation.IplocateCountry, out _)) return false;
        if (observation.IplocateDatabaseVersion?.Length > 64) return false;
        if (_observations.Count > 0)
        {
            LocationObservation last = _observations[^1];
            DateTimeOffset lastUntil = last.ObservedUntilUtc ?? last.ObservedUtc;
            if (observation.ObservedUtc >= lastUntil
                && observation.ObservedUtc - lastUntil <= OfflineGap
                && SameSourceState(last, observation))
            {
                if (observedUntil > lastUntil)
                {
                    _observations[^1] = last with { ObservedUntilUtc = observedUntil };
                    Revision++;
                }
                Prune(observation.ObservedUtc);
                return false;
            }
            if (last == observation) return false;
        }
        _observations.Add(observation);
        if (_observations.Count > MaximumObservations) _observations.RemoveAt(0);
        Prune(observation.ObservedUtc);
        Revision++;
        return true;
    }

    private static bool SameSourceState(LocationObservation left, LocationObservation right)
        => string.Equals(left.PublicIpHash, right.PublicIpHash, StringComparison.Ordinal)
            && string.Equals(left.PublicIp, right.PublicIp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CountryCode, right.CountryCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.LocalCountry, right.LocalCountry, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.OnlineCountry, right.OnlineCountry, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ThirdCountry, right.ThirdCountry, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DatabaseVersion, right.DatabaseVersion, StringComparison.Ordinal)
            && string.Equals(left.IplocateCountry, right.IplocateCountry, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IplocateDatabaseVersion, right.IplocateDatabaseVersion, StringComparison.Ordinal)
            && string.Equals(left.ResolutionSource, right.ResolutionSource, StringComparison.Ordinal)
            && left.Confirmed == right.Confirmed;

    public void Touch(DateTimeOffset utcNow)
        => LastObservedUtc = utcNow;

    public void SplitIfStale(DateTimeOffset now, TimeSpan? maxGap = null)
    {
        if (Current is null)
        {
            return;
        }

        DateTimeOffset observed = LastObservedUtc ?? Current.StartedUtc;
        if (now - observed > (maxGap ?? OfflineGap))
        {
            CloseOpen(observed);
        }
    }

    public void SealStaleOpens(DateTimeOffset now)
        => SplitIfStale(now);

    public void CloseOpen(DateTimeOffset now)
    {
        if (_stays.Count == 0)
        {
            return;
        }

        LocationStay last = _stays[^1];
        if (last.EndedUtc is null)
        {
            _stays[^1] = last with { EndedUtc = now };
            Revision++;
        }
    }
}

public static class LocationCopy
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return Math.Max(0, (int)span.TotalSeconds) + " с";
        }

        if (span.TotalMinutes < 60)
        {
            return (int)span.TotalMinutes + " мин";
        }

        int days = (int)span.TotalDays;
        int hours = span.Hours;
        int minutes = span.Minutes;
        if (days > 0)
        {
            return hours > 0 ? days + " д " + hours + " ч" : days + " д";
        }

        return minutes > 0 ? hours + " ч " + minutes + " мин" : hours + " ч";
    }

    public static string When(DateTimeOffset utc)
        => utc.ToLocalTime().ToString("d MMM, HH:mm", Ru).Replace(".", "");

    public static string Range(DateTimeOffset startedUtc, DateTimeOffset endedUtc)
    {
        DateTimeOffset start = startedUtc.ToLocalTime();
        DateTimeOffset end = endedUtc.ToLocalTime();
        string left = When(startedUtc);
        if (start.Date == end.Date)
        {
            return left + " - " + end.ToString("HH:mm", Ru);
        }

        return left + " - " + When(endedUtc);
    }

    public static TimeSpan Elapsed(LocationStay stay, DateTimeOffset utcNow)
    {
        DateTimeOffset end = stay.EndedUtc ?? utcNow;
        TimeSpan span = end - stay.StartedUtc;
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }
}
