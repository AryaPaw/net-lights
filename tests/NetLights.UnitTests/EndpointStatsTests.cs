using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class EndpointStatsTests
{
    [Fact]
    public void IgnoresCancelledAndRanksRestrictedNodesWorse()
    {
        EndpointStats empty = EndpointStats.Empty;
        EndpointStats afterCancel = empty.Record(ProbeOutcome.Cancelled, null, new StructuredFailure(StructuredFailureKind.Cancelled, "x"));
        Assert.Equal(0, afterCancel.Samples);

        EndpointStats ok = empty.Record(ProbeOutcome.Reachable, 200, null);
        EndpointStats banned = empty
            .Record(ProbeOutcome.Unreachable, 403, new StructuredFailure(StructuredFailureKind.InvalidHeaders, "403"))
            .Record(ProbeOutcome.Unreachable, 429, null);
        Assert.Equal(1, ok.Samples);
        Assert.Equal(100, ok.SuccessPercent);
        Assert.True(banned.ProblemScore > ok.ProblemScore);
        Assert.Equal(2, banned.Restricted);
        Assert.Equal(429, banned.LastHttp);
    }

    [Fact]
    public void KernelKeepsPerEndpointStatsAcrossApplies()
    {
        EndpointDefinition ep = TestPools.Independent()[0];
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, TimeProvider.System);
        kernel.ApplyObservation(ObservationFactory.Create(ep, 1, 1, 10, 20, ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(12), 200));
        kernel.ApplyObservation(ObservationFactory.Create(ep, 1, 2, 30, 40, ProbeOutcome.Unreachable, TimeSpan.FromMilliseconds(10), 403));
        EndpointStats stats = kernel.Snapshot.Ru.Endpoints.First(e => e.Id == ep.Id).Stats;
        Assert.Equal(2, stats.Samples);
        Assert.Equal(1, stats.Reachable);
        Assert.Equal(1, stats.Restricted);
        Assert.Equal(50, stats.SuccessPercent);
    }

    [Fact]
    public void CountsTimeoutAndDnsSeparately()
    {
        EndpointStats stats = EndpointStats.Empty
            .Record(ProbeOutcome.Unreachable, null, new StructuredFailure(StructuredFailureKind.TransportTimeout, "deadline"))
            .Record(ProbeOutcome.Unreachable, null, new StructuredFailure(StructuredFailureKind.DnsFailure, "dns"));
        Assert.Equal(1, stats.Timeouts);
        Assert.Equal(1, stats.DnsFailures);
        Assert.Equal(2, stats.Unreachable);
    }
}
