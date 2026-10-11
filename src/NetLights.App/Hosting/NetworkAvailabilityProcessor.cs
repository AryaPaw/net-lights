using NetLights.Core;

namespace NetLights.App;

/// <summary>Applies network observations and monitor notifications on one UI-owned sequence.</summary>
internal sealed class NetworkAvailabilityProcessor
{
    private readonly SynchronizationContext _owner;
    private readonly int _ownerThreadId;
    private readonly NetworkAvailabilityGate _gate;
    private readonly Action<NetworkAvailabilitySignal> _apply;
    private readonly Func<bool> _isStopping;
    private readonly Action<Exception> _reportFailure;

    public NetworkAvailabilityProcessor(
        SynchronizationContext owner,
        int ownerThreadId,
        TimeSpan debounce,
        Action<NetworkAvailabilitySignal> apply,
        Func<bool> isStopping,
        Action<Exception> reportFailure)
    {
        _owner = owner;
        _ownerThreadId = ownerThreadId;
        _gate = new NetworkAvailabilityGate(debounce);
        _apply = apply;
        _isStopping = isStopping;
        _reportFailure = reportFailure;
    }

    public void ObserveChange(bool available, DateTimeOffset observedAt)
        => Dispatch(() => _gate.OnChange(available, observedAt));

    public void ObserveHeartbeat(bool available, DateTimeOffset observedAt)
        => Dispatch(() => _gate.OnHeartbeat(available, observedAt));

    private void Dispatch(Func<NetworkAvailabilitySignal> transition)
    {
        void Apply()
        {
            if (_isStopping())
                return;

            try
            {
                NetworkAvailabilitySignal signal = transition();
                if (signal != NetworkAvailabilitySignal.None)
                    _apply(signal);
            }
            catch (Exception ex)
            {
                _reportFailure(ex);
            }
        }

        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            Apply();
            return;
        }

        try
        {
            _owner.Post(_ => Apply(), null);
        }
        catch (InvalidOperationException ex)
        {
            _reportFailure(ex);
        }
    }
}
