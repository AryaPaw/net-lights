namespace NetLights.Core;

public sealed class StartLimiter
{
    private readonly Queue<long> _starts = new();

    public IReadOnlyCollection<long> Starts => _starts;

    public bool CanStart(MonotonicClock clock, int physicalInflight, int groupInflight, bool endpointPhysicalInflight)
    {
        RemoveOlderThan(clock, TimeSpan.FromSeconds(60));
        if (physicalInflight >= MonitorConstants.MaxPhysicalInflight)
        {
            return false;
        }

        if (groupInflight >= MonitorConstants.MaxGroupInflight)
        {
            return false;
        }

        if (endpointPhysicalInflight)
        {
            return false;
        }

        long now = clock.Now;
        if (CountInOpenClosedWindow(clock, now, TimeSpan.FromSeconds(1)) >= MonitorConstants.MaxStartsPerSecond)
        {
            return false;
        }

        if (CountInOpenClosedWindow(clock, now, TimeSpan.FromSeconds(60)) >= MonitorConstants.MaxStartsPerMinute)
        {
            return false;
        }

        return true;
    }

    public void Record(long timestamp)
    {
        _starts.Enqueue(timestamp);
        while (_starts.Count > MonitorConstants.MaxStartsPerMinute)
        {
            _starts.Dequeue();
        }
    }

    public int CountInOpenClosedWindow(MonotonicClock clock, long now, TimeSpan window)
    {
        long leftExclusive = clock.Add(now, window.Negate());
        int count = 0;
        foreach (long start in _starts)
        {
            if (start > leftExclusive && start <= now)
            {
                count++;
            }
        }

        return count;
    }

    public void RemoveOlderThan(MonotonicClock clock, TimeSpan keep)
    {
        long now = clock.Now;
        while (_starts.Count > 0 && clock.Elapsed(_starts.Peek(), now) > keep)
        {
            _starts.Dequeue();
        }
    }
}
