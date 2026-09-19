using System.Globalization;

namespace NetLights.Core;

public sealed record LocationStay(
    string Iso,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc);

public sealed class LocationHistory
{
    public const int MaxStays = 40;
    public static readonly TimeSpan ResumeMergeWindow = TimeSpan.FromHours(6);

    private readonly List<LocationStay> _stays;

    public LocationHistory(IEnumerable<LocationStay>? stays = null)
    {
        _stays = stays?.ToList() ?? [];
    }

    public IReadOnlyList<LocationStay> Stays => _stays;

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

    public bool NoteIso(string? iso, DateTimeOffset utcNow)
    {
        if (!GeoCountryParsers.TryNormalizeIso3166Alpha2(iso, out string? code))
        {
            return false;
        }

        if (_stays.Count == 0)
        {
            _stays.Add(new LocationStay(code, utcNow, null));
            return true;
        }

        LocationStay last = _stays[^1];
        if (last.EndedUtc is null && last.Iso == code)
        {
            return false;
        }

        if (last.EndedUtc is DateTimeOffset ended
            && last.Iso == code
            && utcNow - ended <= ResumeMergeWindow)
        {
            _stays[^1] = last with { EndedUtc = null };
            return true;
        }

        if (last.EndedUtc is null)
        {
            _stays[^1] = last with { EndedUtc = utcNow };
        }

        _stays.Add(new LocationStay(code, utcNow, null));
        while (_stays.Count > MaxStays)
        {
            _stays.RemoveAt(0);
        }

        return true;
    }

    public void SealStaleOpens(DateTimeOffset now)
    {
        if (_stays.Count == 0)
        {
            return;
        }

        LocationStay last = _stays[^1];
        if (last.EndedUtc is null && now - last.StartedUtc > ResumeMergeWindow)
        {
            _stays[^1] = last with { EndedUtc = last.StartedUtc };
        }
    }

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
