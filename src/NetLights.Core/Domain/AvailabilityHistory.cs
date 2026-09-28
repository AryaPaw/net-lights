namespace NetLights.Core;

public sealed record AvailabilitySpan(EndpointGroup Group, GroupAvailability State, bool Paused, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc);

public sealed class AvailabilityHistory
{
    public const int MaxSpans = 10000;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(10);
    private readonly List<AvailabilitySpan> _spans;
    private DateTimeOffset _lastPrunedUtc;
    public AvailabilityHistory(IEnumerable<AvailabilitySpan>? spans = null, DateTimeOffset? lastObservedUtc = null)
    {
        _spans = spans?.ToList() ?? [];
        LastObservedUtc = lastObservedUtc;
    }
    public IReadOnlyList<AvailabilitySpan> Spans => _spans;
    public long Revision { get; private set; }
    public DateTimeOffset? LastObservedUtc { get; private set; }
    public bool Observe(MonitorSnapshot snapshot, DateTimeOffset now)
    {
        bool changed = Observe(EndpointGroup.Ru, snapshot.Ru.Availability, snapshot.Paused, now);
        changed |= Observe(EndpointGroup.World, snapshot.World.Availability, snapshot.Paused, now);
        LastObservedUtc = now;
        if (changed)
        {
            Revision++;
            Prune(now);
        }
        else if (_lastPrunedUtc == default || now - _lastPrunedUtc >= PruneInterval)
        {
            Prune(now);
        }

        return changed;
    }
    private bool Observe(EndpointGroup group, GroupAvailability state, bool paused, DateTimeOffset now)
    {
        AvailabilitySpan? current = _spans.LastOrDefault(s => s.Group == group && s.EndedUtc is null);
        if (current is not null && current.State == state && current.Paused == paused) return false;
        if (current is not null) _spans[_spans.IndexOf(current)] = current with { EndedUtc = now };
        _spans.Add(new AvailabilitySpan(group, state, paused, now, null));
        while (_spans.Count > MaxSpans) _spans.RemoveAt(0);
        return true;
    }
    public void Touch(DateTimeOffset now) => LastObservedUtc = now;
    public void CloseOpen(DateTimeOffset now)
    {
        if (LastObservedUtc is DateTimeOffset observed && now - observed > TimeSpan.FromMinutes(2)) now = observed;
        bool changed = false;
        for (int i = 0; i < _spans.Count; i++)
        {
            if (_spans[i].EndedUtc is null)
            {
                _spans[i] = _spans[i] with { EndedUtc = now };
                changed = true;
            }
        }

        if (changed)
        {
            Revision++;
        }
    }
    public void SealStale(DateTimeOffset now)
    {
        if (LastObservedUtc is not DateTimeOffset observed) return;
        if (now - observed > TimeSpan.FromMinutes(2)) CloseOpen(observed);
    }
    public void Prune(DateTimeOffset now)
    {
        if (_lastPrunedUtc != default && now - _lastPrunedUtc < PruneInterval) return;

        DateTimeOffset cutoff = now - Retention;
        int oldCount = _spans.Count;
        _spans.RemoveAll(s => (s.EndedUtc ?? now) <= cutoff);
        bool changed = _spans.Count != oldCount;
        for (int i = 0; i < _spans.Count; i++)
        {
            if (_spans[i].StartedUtc < cutoff)
            {
                _spans[i] = _spans[i] with { StartedUtc = cutoff };
                changed = true;
            }
        }

        _lastPrunedUtc = now;
        if (changed)
        {
            Revision++;
        }
    }
}
