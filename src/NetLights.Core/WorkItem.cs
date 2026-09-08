namespace NetLights.Core;

public enum WorkKind
{
    StartProbe,
    CancelAttempt,
    CloseIdleConnections,
    PublishSnapshot
}

public sealed record WorkItem(
    WorkKind Kind,
    EndpointDefinition? Endpoint,
    long NetworkEpoch,
    long AttemptId,
    TimeSpan Deadline,
    string? Reason);

public sealed record KernelTrace(
    long Timestamp,
    string Kind,
    string? EndpointId,
    EndpointGroup? Group,
    long AttemptId,
    long NetworkEpoch);
