namespace NetLights.Core;

public enum LocationSourceKind
{
    LocalDatabase,
    Online,
    IplocateDatabase
}

public sealed record LocationSourceSpan(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string? LocalCountry,
    string? OnlineCountry,
    bool Conflict,
    string? ThirdCountry = null,
    string? PublicIp = null,
    DateTimeOffset? LastObservedUtc = null,
    bool InferredFromStay = false,
    DateTimeOffset? FirstObservedUtc = null,
    bool PublicIpChanged = false,
    string? IplocateCountry = null,
    string? IplocateDatabaseVersion = null);

public sealed record LocationSourceCountryRun(DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string Country);

public static class LocationSourceHistoryLayout
{
    private static readonly TimeSpan ObservationFreshness = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<LocationSourceCountryRun> CountryRuns(
        IReadOnlyList<LocationSourceSpan> spans, LocationSourceKind source)
    {
        var runs = new List<LocationSourceCountryRun>();
        foreach (LocationSourceSpan span in spans)
        {
            string? country = source switch
            {
                LocationSourceKind.LocalDatabase => span.LocalCountry,
                LocationSourceKind.Online => span.OnlineCountry,
                LocationSourceKind.IplocateDatabase => span.IplocateCountry,
                _ => null
            };
            if (country is null || span.EndedUtc <= span.StartedUtc)
                continue;

            if (runs.Count > 0 && runs[^1] is LocationSourceCountryRun previous
                && previous.EndedUtc == span.StartedUtc
                && string.Equals(previous.Country, country, StringComparison.OrdinalIgnoreCase))
                runs[^1] = previous with { EndedUtc = span.EndedUtc };
            else
                runs.Add(new LocationSourceCountryRun(span.StartedUtc, span.EndedUtc, country));
        }

        return runs;
    }

    public static IReadOnlyList<LocationSourceSpan> Build(
        IReadOnlyList<LocationObservation> observations,
        DateTimeOffset now,
        TimeSpan window,
        IReadOnlyList<LocationStay>? stays = null)
    {
        if (now == default || window <= TimeSpan.Zero)
            return [];

        DateTimeOffset start = now - window;
        LocationObservation[] ordered = observations
            .Where(item => item.ObservedUtc != default && item.ObservedUtc <= now)
            .OrderBy(item => item.ObservedUtc)
            .ToArray();
        if (ordered.Length == 0)
            return [new LocationSourceSpan(start, now, null, null, false)];

        var spans = new List<LocationSourceSpan>(ordered.Length + 1);
        int firstVisible = Array.FindIndex(ordered, item => item.ObservedUtc >= start);
        int index = firstVisible < 0 ? ordered.Length - 1 : Math.Max(0, firstVisible - 1);
        DateTimeOffset cursor = start;
        int stayIndex = 0;
        if (ordered[index].ObservedUtc > start)
        {
            spans.Add(new LocationSourceSpan(start, ordered[index].ObservedUtc, null, null, false));
            cursor = ordered[index].ObservedUtc;
        }

        for (; index < ordered.Length; index++)
        {
            LocationObservation item = ordered[index];
            DateTimeOffset from = item.ObservedUtc < start ? start : item.ObservedUtc;
            DateTimeOffset freshUntil = (item.ObservedUntilUtc ?? item.ObservedUtc) + ObservationFreshness;
            bool inferredFromStay = false;
            if (stays is not null && item.Confirmed && item.CountryCode is not null)
            {
                while (stayIndex < stays.Count && stays[stayIndex].EndedUtc is DateTimeOffset ended
                    && ended <= item.ObservedUtc)
                    stayIndex++;
                if (stayIndex < stays.Count)
                {
                    LocationStay stay = stays[stayIndex];
                    if (stay.StartedUtc <= item.ObservedUtc
                        && (stay.EndedUtc is null || stay.EndedUtc > item.ObservedUtc)
                        && string.Equals(stay.Iso, item.CountryCode, StringComparison.OrdinalIgnoreCase))
                    {
                        DateTimeOffset stayEnd = stay.EndedUtc ?? now;
                        inferredFromStay = item.ObservedUntilUtc is null;
                        freshUntil = item.ObservedUntilUtc is null
                            ? stayEnd // Older files stored only changes; this part is inferred from the country stay.
                            : (freshUntil < stayEnd ? freshUntil : stayEnd);
                    }
                }
            }
            DateTimeOffset to = index + 1 < ordered.Length ? ordered[index + 1].ObservedUtc : now;
            if (to > freshUntil) to = freshUntil;
            if (to > now) to = now;
            if (from < cursor) from = cursor;
            if (to <= from) continue;

            if (cursor < from)
                spans.Add(new LocationSourceSpan(cursor, from, null, null, false));

            string? local = Normalize(item.LocalCountry);
            string? online = Normalize(item.OnlineCountry);
            string? accepted = online ?? Normalize(item.ThirdCountry);
            string? iplocate = Normalize(item.IplocateCountry);
            bool conflict = (local is not null && accepted is not null
                    && !string.Equals(local, accepted, StringComparison.OrdinalIgnoreCase))
                || (iplocate is not null && accepted is not null
                    && !string.Equals(iplocate, accepted, StringComparison.OrdinalIgnoreCase));
            spans.Add(new LocationSourceSpan(from, to, local, online, conflict, Normalize(item.ThirdCountry),
                item.PublicIp, item.ObservedUntilUtc ?? item.ObservedUtc, inferredFromStay,
                item.ObservedUtc, item.PublicIpChanged, iplocate, item.IplocateDatabaseVersion));
            cursor = to;
        }

        if (cursor < now)
            spans.Add(new LocationSourceSpan(cursor, now, null, null, false));
        return spans;
    }

    private static string? Normalize(string? value)
        => GeoCountryParsers.TryNormalizeIso3166Alpha2(value, out string? code) ? code : null;
}
