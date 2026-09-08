namespace NetLights.Core;

public sealed record StructuredFailure(StructuredFailureKind Kind, string SafeDetail);
