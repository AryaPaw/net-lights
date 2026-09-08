namespace NetLights.Core;

public static class ObservationFactory
{
    public static EndpointObservation Create(
        EndpointDefinition endpoint,
        long epoch,
        long attemptId,
        long started,
        long completed,
        ProbeOutcome outcome,
        TimeSpan elapsed,
        int? status = null,
        StructuredFailure? failure = null,
        long nextAllowedAt = 0)
    {
        return new EndpointObservation(
            endpoint.Id,
            epoch,
            attemptId,
            started,
            completed,
            outcome,
            status,
            failure,
            elapsed,
            nextAllowedAt);
    }
}
