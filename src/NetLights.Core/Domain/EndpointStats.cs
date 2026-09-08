namespace NetLights.Core;

public sealed record EndpointStats(
    int Samples,
    int Reachable,
    int Unreachable,
    int Indeterminate,
    int Restricted,
    int Timeouts,
    int DnsFailures,
    int? LastHttp,
    StructuredFailureKind? LastFailure)
{
    public static EndpointStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, null, null);

    public int SuccessPercent => Samples == 0 ? 0 : (int)Math.Round(100.0 * Reachable / Samples);

    public int ProblemScore => Restricted * 4 + Unreachable * 2 + Indeterminate + Timeouts + DnsFailures;

    public EndpointStats Record(ProbeOutcome outcome, int? httpStatus, StructuredFailure? failure)
    {
        if (outcome is ProbeOutcome.Cancelled)
        {
            return this;
        }

        int reachable = Reachable;
        int unreachable = Unreachable;
        int indeterminate = Indeterminate;
        int restricted = Restricted;
        int timeouts = Timeouts;
        int dns = DnsFailures;
        if (outcome == ProbeOutcome.Reachable)
        {
            reachable++;
        }
        else if (outcome == ProbeOutcome.Unreachable)
        {
            unreachable++;
        }
        else
        {
            indeterminate++;
        }

        if (httpStatus is 403 or 429)
        {
            restricted++;
        }

        if (failure?.Kind is StructuredFailureKind.TransportTimeout or StructuredFailureKind.DeadlineExceeded)
        {
            timeouts++;
        }

        if (failure?.Kind == StructuredFailureKind.DnsFailure)
        {
            dns++;
        }

        return new EndpointStats(
            Samples + 1,
            reachable,
            unreachable,
            indeterminate,
            restricted,
            timeouts,
            dns,
            httpStatus ?? LastHttp,
            failure?.Kind ?? LastFailure);
    }
}
