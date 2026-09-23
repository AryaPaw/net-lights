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
        int minStayPx = 5,
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
        for (int i = 0; i < raw.Count; i++)
        {
            long end = Math.Min(raw[i].End.UtcTicks, horizon.UtcTicks);
            long start = Math.Max(raw[i].Start.UtcTicks, origin.UtcTicks);
            fractions[i] = Math.Max(0, end - start) / spanTicks;
        }

        int[] pixels = ToPixels(raw, fractions, widthPx, minStayPx);
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

        ordered.Sort(static (a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
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

    private static int[] ToPixels(List<RawSpan> raw, double[] fractions, int widthPx, int minStayPx)
    {
        int stayCount = 0;
        foreach (RawSpan span in raw)
        {
            if (span.Iso is not null)
            {
                stayCount++;
            }
        }

        int minPx = minStayPx;
        if (stayCount > 0)
        {
            minPx = Math.Clamp(minStayPx, 1, Math.Max(1, widthPx / Math.Max(stayCount, 1)));
        }

        var pixels = new int[raw.Count];
        int used = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            pixels[i] = (int)Math.Round(fractions[i] * widthPx, MidpointRounding.AwayFromZero);
            used += pixels[i];
        }

        int drift = used - widthPx;
        if (drift != 0)
        {
            int index = LargestIndex(pixels, skipGaps: false, raw);
            pixels[index] = Math.Max(0, pixels[index] - drift);
        }

        int extra = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i].Iso is null || pixels[i] >= minPx || fractions[i] <= 0)
            {
                continue;
            }

            extra += minPx - pixels[i];
            pixels[i] = minPx;
        }

        while (extra > 0)
        {
            int index = LargestIndex(pixels, skipGaps: false, raw, above: minPx);
            if (index < 0 || pixels[index] <= minPx)
            {
                break;
            }

            pixels[index]--;
            extra--;
        }

        int sum = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            sum += pixels[i];
        }

        int fix = sum - widthPx;
        if (fix != 0)
        {
            int index = LargestIndex(pixels, skipGaps: false, raw);
            if (index >= 0)
            {
                pixels[index] = Math.Max(0, pixels[index] - fix);
            }
        }

        return pixels;
    }

    private static int LargestIndex(int[] pixels, bool skipGaps, List<RawSpan> raw, int above = int.MinValue)
    {
        int best = -1;
        int bestPx = above;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (skipGaps && raw[i].Iso is null)
            {
                continue;
            }

            if (pixels[i] > bestPx)
            {
                bestPx = pixels[i];
                best = i;
            }
        }

        return best;
    }

    private readonly record struct RawSpan(string? Iso, DateTimeOffset Start, DateTimeOffset End, bool Live);
}
