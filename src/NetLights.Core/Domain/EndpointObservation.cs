namespace NetLights.Core;

public sealed record EndpointObservation(
    string EndpointId,
    long NetworkEpoch,
    long AttemptId,
    long MonotonicStarted,
    long MonotonicCompleted,
    ProbeOutcome Outcome,
    int? HttpStatus,
    StructuredFailure? Failure,
    TimeSpan Elapsed,
    long NextAllowedAt);
