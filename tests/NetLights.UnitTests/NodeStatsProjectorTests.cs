using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class NodeStatsProjectorTests
{
    [Fact]
    public void OrdersWorstNodesFirstAndExplainsRestriction()
    {
        var good = new EndpointView(
            "ok",
            EndpointGroup.Ru,
            "ok",
            new Uri("https://ok.test/"),
            ProbeOutcome.Reachable,
            200,
            null,
            TimeSpan.FromMilliseconds(40),
            DateTimeOffset.UtcNow,
            true,
            false,
            null,
            false,
            EndpointStats.Empty.Record(ProbeOutcome.Reachable, 200, null));
        EndpointStats bannedStats = EndpointStats.Empty.Record(
            ProbeOutcome.Unreachable,
            403,
            new StructuredFailure(StructuredFailureKind.InvalidHeaders, "403"));
        var banned = good with
        {
            Id = "bad",
            InfrastructureId = "bad",
            Outcome = ProbeOutcome.Unreachable,
            HttpStatus = 403,
            Stats = bannedStats
        };
        var snapshot = new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Limited, "", null, false, [banned, good]),
            new GroupSnapshot(EndpointGroup.Vpn, GroupAvailability.Unknown, "", null, false, []),
            true,
            null,
            false,
            null,
            false);

        List<NodeStatRow> rows = NodeStatsProjector.Rows(snapshot);
        Assert.Equal("bad", rows[0].Id);
        Assert.Contains("огранич", rows[0].Verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("стабильно", rows[1].Verdict);
    }
}
