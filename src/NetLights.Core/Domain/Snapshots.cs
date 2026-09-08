namespace NetLights.Core;

public sealed record EndpointView(
    string Id,
    EndpointGroup Group,
    string InfrastructureId,
    Uri Uri,
    ProbeOutcome? Outcome,
    int? HttpStatus,
    StructuredFailure? Failure,
    TimeSpan? Elapsed,
    DateTimeOffset? LastCompletedUtc,
    bool Fresh,
    bool Paused,
    DateTimeOffset? PauseUntilUtc,
    bool InFlight);

public sealed record GroupSnapshot(
    EndpointGroup Group,
    GroupAvailability Availability,
    string Reason,
    DateTimeOffset? LastSuccessUtc,
    bool ConfirmationActive,
    IReadOnlyList<EndpointView> Endpoints);

public sealed record MonitorSnapshot(
    long NetworkEpoch,
    DateTimeOffset GeneratedUtc,
    long GeneratedTimestamp,
    GroupSnapshot Ru,
    GroupSnapshot Vpn,
    bool UsingBuiltinPool,
    string? ConfigWarning,
    bool CapacityExhausted,
    string? MonitorError);
