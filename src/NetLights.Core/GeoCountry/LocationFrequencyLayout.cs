namespace NetLights.Core;

public sealed record LocationFrequencyModel(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<int> ChangesPerInterval,
    int PeakChangesPerInterval,
    int ChangeCount,
    int CountryCount,
    IReadOnlyList<bool> HistoryAvailablePerInterval)
{
    public TimeSpan IntervalDuration => ChangesPerInterval.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks((EndUtc - StartUtc).Ticks / ChangesPerInterval.Count);
}

public static class LocationFrequencyLayout
{
    public static LocationFrequencyModel Build(IReadOnlyList<LocationStay> stays, DateTimeOffset now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            return new LocationFrequencyModel(now - window, now, [], 0, 0, 0, []);

        int intervalCount = LocationChartWindows.FrequencyIntervalCount(window);
        DateTimeOffset start = now - window;
        LocationStay[] ordered = stays
            .Where(s => GeoCountryParsers.IsIso3166Alpha2(s.Iso) && s.StartedUtc != default && s.StartedUtc <= now)
            .ToArray();
        bool orderedByStart = true;
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartedUtc < ordered[i - 1].StartedUtc)
            {
                orderedByStart = false;
                break;
            }
        }

        if (!orderedByStart)
            Array.Sort(ordered, static (left, right) => left.StartedUtc.CompareTo(right.StartedUtc));

        var changes = new List<DateTimeOffset>(Math.Min(ordered.Length, intervalCount));
        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Length; i++)
        {
            LocationStay stay = ordered[i];
            if ((stay.EndedUtc is null || stay.EndedUtc > start) && stay.StartedUtc <= now)
                countries.Add(stay.Iso);

            DateTimeOffset changedAt = stay.StartedUtc;
            if (i > 0
                && changedAt > start
                && changedAt <= now
                && !string.Equals(ordered[i - 1].Iso, stay.Iso, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(changedAt);
            }
        }

        var intervals = new int[intervalCount];
        foreach (DateTimeOffset changedAt in changes)
        {
            long offsetTicks = (changedAt - start).Ticks;
            // Intervals are right-closed: (start, edge1], (edge1, edge2], ...
            // This excludes a transition exactly at the visible start and counts
            // a transition on a bucket edge exactly once.
            long scaledOffset = checked(offsetTicks * intervalCount);
            int index = (int)Math.Clamp((scaledOffset - 1) / window.Ticks, 0, intervalCount - 1);
            intervals[index]++;
        }

        var coverageDiff = new int[intervalCount + 1];
        foreach (LocationStay stay in ordered)
        {
            DateTimeOffset spanStart = stay.StartedUtc < start ? start : stay.StartedUtc;
            DateTimeOffset spanEnd = stay.EndedUtc is DateTimeOffset ended && ended < now ? ended : now;
            if (spanEnd <= spanStart)
                continue;

            long startOffset = (spanStart - start).Ticks;
            long endOffset = (spanEnd - start).Ticks;
            int firstInterval = (int)Math.Clamp(startOffset * intervalCount / window.Ticks, 0, intervalCount - 1);
            long scaledEnd = endOffset * intervalCount;
            int afterLastInterval = (int)Math.Clamp(
                scaledEnd / window.Ticks + (scaledEnd % window.Ticks == 0 ? 0 : 1),
                firstInterval + 1L,
                intervalCount);
            coverageDiff[firstInterval]++;
            coverageDiff[afterLastInterval]--;
        }

        var historyAvailable = new bool[intervalCount];
        int activeCoverage = 0;
        for (int i = 0; i < intervalCount; i++)
        {
            activeCoverage += coverageDiff[i];
            historyAvailable[i] = activeCoverage > 0 || intervals[i] > 0;
        }

        return new LocationFrequencyModel(
            start,
            now,
            intervals,
            intervals.Length == 0 ? 0 : intervals.Max(),
            changes.Count,
            countries.Count,
            historyAvailable);
    }
}
