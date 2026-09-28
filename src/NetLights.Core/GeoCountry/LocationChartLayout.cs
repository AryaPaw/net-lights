namespace NetLights.Core;

public sealed record LocationChartSegment(
    string? Iso,
    int StartPx,
    int WidthPx,
    bool Live,
    double Fraction,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record LocationChartModel(
    DateTimeOffset OriginUtc,
    DateTimeOffset HorizonUtc,
    IReadOnlyList<LocationChartSegment> Segments);

public static class LocationChartLayout
{
    public static LocationChartModel Build(
        IReadOnlyList<LocationStay> stays,
        DateTimeOffset now,
        int widthPx,
        DateTimeOffset? windowStart = null)
    {
        if (widthPx <= 0)
        {
            return new LocationChartModel(default, default, []);
        }

        List<RawSpan> raw = Collect(stays, now);
        DateTimeOffset horizon = now;
        DateTimeOffset origin;
        if (windowStart is DateTimeOffset window && window < horizon)
        {
            origin = window;
            raw = Clip(raw, origin, horizon);
        }
        else if (raw.Count == 0)
        {
            return new LocationChartModel(default, default, []);
        }
        else
        {
            origin = raw[0].Start;
        }

        if (horizon <= origin)
        {
            return new LocationChartModel(default, default, []);
        }

        if (raw.Count == 0)
        {
            return new LocationChartModel(origin, horizon, []);
        }

        if (raw[0].Start > origin)
        {
            raw.Insert(0, new RawSpan(null, origin, raw[0].Start, false));
        }

        DateTimeOffset last = raw[^1].End;
        if (last < horizon)
        {
            raw.Add(new RawSpan(null, last, horizon, false));
        }

        double spanTicks = horizon.UtcTicks - origin.UtcTicks;
        var fractions = new double[raw.Count];
        for (int i = 0; i < fractions.Length; i++)
        {
            long end = Math.Min(raw[i].End.UtcTicks, horizon.UtcTicks);
            long start = Math.Max(raw[i].Start.UtcTicks, origin.UtcTicks);
            fractions[i] = Math.Max(0, end - start) / spanTicks;
        }

        int[] pixels = ToPixels(fractions, widthPx);
        var segments = new LocationChartSegment[raw.Count];
        int x = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            segments[i] = new LocationChartSegment(
                raw[i].Iso,
                x,
                pixels[i],
                raw[i].Live,
                fractions[i],
                raw[i].Start,
                raw[i].End);
            x += pixels[i];
        }

        return new LocationChartModel(origin, horizon, segments);
    }

    private static List<RawSpan> Clip(List<RawSpan> raw, DateTimeOffset origin, DateTimeOffset horizon)
    {
        var clipped = new List<RawSpan>(raw.Count);
        foreach (RawSpan span in raw)
        {
            DateTimeOffset start = span.Start < origin ? origin : span.Start;
            DateTimeOffset end = span.End > horizon ? horizon : span.End;
            if (end <= start)
            {
                continue;
            }

            clipped.Add(span with { Start = start, End = end });
        }

        return clipped;
    }

    private static List<RawSpan> Collect(IReadOnlyList<LocationStay> stays, DateTimeOffset now)
    {
        var ordered = new List<LocationStay>(stays.Count);
        foreach (LocationStay stay in stays)
        {
            if (stay.StartedUtc == default || !GeoCountryParsers.IsIso3166Alpha2(stay.Iso))
            {
                continue;
            }

            ordered.Add(stay);
        }

        bool orderedByStart = true;
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].StartedUtc < ordered[i - 1].StartedUtc)
            {
                orderedByStart = false;
                break;
            }
        }

        if (!orderedByStart)
        {
            ordered.Sort(static (a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
        }
        var raw = new List<RawSpan>();
        DateTimeOffset? cursor = null;
        foreach (LocationStay stay in ordered)
        {
            DateTimeOffset end = stay.EndedUtc ?? now;
            if (end < stay.StartedUtc)
            {
                continue;
            }

            if (cursor is DateTimeOffset previous && stay.StartedUtc > previous)
            {
                raw.Add(new RawSpan(null, previous, stay.StartedUtc, false));
            }

            bool live = stay.EndedUtc is null;
            DateTimeOffset spanEnd = live ? now : end;
            raw.Add(new RawSpan(stay.Iso, stay.StartedUtc, spanEnd, live));
            cursor = spanEnd;
        }

        if (cursor is DateTimeOffset last && last < now)
        {
            raw.Add(new RawSpan(null, last, now, false));
        }

        return raw;
    }

    private static int[] ToPixels(double[] fractions, int widthPx)
    {
        var pixels = new int[fractions.Length];
        int previousEdge = 0;
        double accumulatedFraction = 0;
        for (int i = 0; i < fractions.Length; i++)
        {
            accumulatedFraction += fractions[i];
            int edge = i == fractions.Length - 1
                ? widthPx
                : Math.Clamp((int)Math.Round(accumulatedFraction * widthPx, MidpointRounding.AwayFromZero), previousEdge, widthPx);
            pixels[i] = edge - previousEdge;
            previousEdge = edge;
        }

        return pixels;
    }

    private readonly record struct RawSpan(string? Iso, DateTimeOffset Start, DateTimeOffset End, bool Live);
}
