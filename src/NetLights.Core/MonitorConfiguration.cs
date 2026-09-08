namespace NetLights.Core;

public sealed class MonitorConfiguration
{
    public required IReadOnlyList<EndpointDefinition> Endpoints { get; init; }
    public bool UsingBuiltinPool { get; init; } = true;
    public string? ConfigWarning { get; init; }
    public TimeSpan GroupInterval { get; init; } = MonitorConstants.GroupInterval;
    public TimeSpan GroupOffset { get; init; } = MonitorConstants.GroupOffset;
    public TimeSpan ProbeDeadline { get; init; } = MonitorConstants.ProbeDeadline;
    public TimeSpan Freshness { get; init; } = MonitorConstants.Freshness;
    public TimeSpan MinEndpointInterval { get; init; } = MonitorConstants.MinEndpointInterval;
}
