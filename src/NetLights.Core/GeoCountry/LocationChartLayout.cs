namespace NetLights.Core;

public sealed record LocationChartSegment(
    string? Iso,
    int StartPx,
    int WidthPx,
    bool Live,
    double Fraction,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record LocationChartEvent(string Iso, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int XPx);

public sealed record LocationChartModel(
    DateTimeOffset OriginUtc,
    DateTimeOffset HorizonUtc,
    IReadOnlyList<LocationChartSegment> Segments)
{
    public IReadOnlyList<LocationChartEvent> Events { get; init; } = [];
}

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
        RawSpan[] points = raw.Where(span => span.Iso is not null && span.End == span.Start).ToArray();
        raw.RemoveAll(span => span.Iso is not null && span.End == span.Start);
        DateTimeOffset horizon = now;
        DateTimeOffset origin;
        if (windowStart is DateTimeOffset window && window < horizon)
        {
            origin = window;
            raw = Clip(raw, origin, horizon);
        }
        else if (raw.Count == 0 && points.Length == 0)
        {
            return new LocationChartModel(default, default, []);
        }
        else
        {
            origin = raw.Count == 0 ? points[0].Start : raw[0].Start;
            if (points.Length > 0 && points[0].Start < origin)
                origin = points[0].Start;
        }

        if (horizon <= origin)
        {
            return new LocationChartModel(default, default, []);
        }

        if (raw.Count == 0)
            raw.Add(new RawSpan(null, origin, horizon, false));

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
        var events = new List<LocationChartEvent>(points.Length + raw.Count);
        foreach (RawSpan point in points)
            if (point.Start >= origin && point.Start <= horizon)
                events.Add(new LocationChartEvent(point.Iso!, point.Start, point.End,
                    EventX(point.Start, origin, horizon, widthPx)));
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
            if (raw[i].Iso is not null && pixels[i] == 0)
            {
                DateTimeOffset middle = raw[i].Start + TimeSpan.FromTicks((raw[i].End - raw[i].Start).Ticks / 2);
                events.Add(new LocationChartEvent(raw[i].Iso!, raw[i].Start, raw[i].End,
                    EventX(middle, origin, horizon, widthPx)));
            }
            x += pixels[i];
        }

        return new LocationChartModel(origin, horizon, segments) { Events = events };
    }

    private static int EventX(DateTimeOffset at, DateTimeOffset origin, DateTimeOffset horizon, int widthPx)
    {
        double fraction = (at - origin).Ticks / (double)(horizon - origin).Ticks;
        return Math.Clamp((int)Math.Round(fraction * widthPx), 0, widthPx - 1);
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
