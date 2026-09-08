namespace NetLights.Core;

public interface IProbe
{
    Task<EndpointObservation> ProbeAsync(
        EndpointDefinition endpoint,
        long networkEpoch,
        long attemptId,
        TimeSpan deadline,
        CancellationToken cancellationToken);
}
