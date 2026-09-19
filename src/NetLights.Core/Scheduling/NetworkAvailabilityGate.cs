namespace NetLights.Core;

public enum NetworkAvailabilitySignal
{
    None,
    Unavailable,
    Available
}

public sealed class NetworkAvailabilityGate
{
    private readonly TimeSpan _debounce;
    private DateTimeOffset _lastEvent = DateTimeOffset.MinValue;
    private DateTimeOffset _pendingRestoreAt = DateTimeOffset.MinValue;
    private bool? _lastKnown;

    public NetworkAvailabilityGate(TimeSpan debounce)
    {
        _debounce = debounce;
    }

    public NetworkAvailabilitySignal OnChange(bool available, DateTimeOffset now)
    {
        if (!available)
        {
            return MarkUnavailable(now);
        }

        if (_lastEvent != DateTimeOffset.MinValue && now - _lastEvent < _debounce)
        {
            _pendingRestoreAt = _lastEvent + _debounce;
            return NetworkAvailabilitySignal.None;
        }

        return CommitAvailable(now);
    }

    public NetworkAvailabilitySignal OnHeartbeat(bool available, DateTimeOffset now)
    {
        if (!available)
        {
            return MarkUnavailable(now);
        }

        if (_lastKnown == true)
        {
            return NetworkAvailabilitySignal.None;
        }

        if (_pendingRestoreAt != DateTimeOffset.MinValue && now < _pendingRestoreAt)
        {
            return NetworkAvailabilitySignal.None;
        }

        return CommitAvailable(now);
    }

    private NetworkAvailabilitySignal MarkUnavailable(DateTimeOffset now)
    {
        _lastEvent = now;
        _pendingRestoreAt = DateTimeOffset.MinValue;
        if (_lastKnown == false)
        {
            return NetworkAvailabilitySignal.None;
        }

        _lastKnown = false;
        return NetworkAvailabilitySignal.Unavailable;
    }

    private NetworkAvailabilitySignal CommitAvailable(DateTimeOffset now)
    {
        _pendingRestoreAt = DateTimeOffset.MinValue;
        _lastEvent = now;
        if (_lastKnown == true)
        {
            return NetworkAvailabilitySignal.None;
        }

        _lastKnown = true;
        return NetworkAvailabilitySignal.Available;
    }
}
