namespace NetLights.Core;

public sealed record EndpointDefinition(
    string Id,
    EndpointGroup Group,
    Uri Uri,
    string InfrastructureId);
