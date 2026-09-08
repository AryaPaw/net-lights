namespace NetLights.Core;

public readonly struct MonotonicClock
{
    private readonly TimeProvider _time;

    public MonotonicClock(TimeProvider time)
    {
        _time = time;
    }

    public long Now => _time.GetTimestamp();

    public DateTimeOffset UtcNow => _time.GetUtcNow();

    public TimeSpan Elapsed(long startInclusive, long endInclusive)
        => _time.GetElapsedTime(startInclusive, endInclusive);

    public TimeSpan Age(long timestamp)
        => _time.GetElapsedTime(timestamp, Now);

    public bool IsDue(long scheduledAt)
        => scheduledAt <= Now;

    public long Add(long timestamp, TimeSpan duration)
    {
        double delta = duration.TotalSeconds * _time.TimestampFrequency;
        if (double.IsNaN(delta) || double.IsInfinity(delta))
        {
            return long.MaxValue;
        }

        if (delta >= long.MaxValue - timestamp)
        {
            return long.MaxValue;
        }

        if (delta <= long.MinValue + timestamp)
        {
            return long.MinValue;
        }

        return timestamp + (long)delta;
    }

    public bool IsFresh(long completedAt, TimeSpan freshness)
    {
        if (completedAt <= 0)
        {
            return false;
        }

        return Age(completedAt) <= freshness;
    }
}
