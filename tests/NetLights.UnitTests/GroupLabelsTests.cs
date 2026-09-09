using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GroupLabelsTests
{
    [Fact]
    public void ProviderLabel_IsProvider()
    {
        Assert.Equal("Провайдер", GroupLabels.Provider);
        Assert.Equal("VPN", GroupLabels.Vpn);
    }

    [Fact]
    public void NodeStatsRows_UseProviderGroupName()
    {
        var endpoint = new EndpointView(
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
        var snapshot = new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Online, "", null, false, [endpoint]),
            new GroupSnapshot(EndpointGroup.Vpn, GroupAvailability.Unknown, "", null, false, []),
            true,
            null,
            false,
            null,
            false);

        List<NodeStatRow> rows = NodeStatsProjector.Rows(snapshot);

        Assert.Equal(GroupLabels.Provider, rows[0].Group);
    }
}
