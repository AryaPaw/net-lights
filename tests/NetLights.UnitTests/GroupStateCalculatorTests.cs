using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GroupStateCalculatorTests
{
    [Fact]
    public void T01_NoEvidence_IsUnknown()
    {
        IReadOnlyList<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        GroupAvailability state = GroupStateCalculator.Evaluate(pool, [], out string reason, out int votes);
        Assert.Equal(GroupAvailability.Unknown, state);
        Assert.Equal(0, votes);
        Assert.Contains("доказательств", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T06_FourUnreachableThreeMissing_IsUnknown()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = pool.Take(4).Select(e => Fresh(e, ProbeOutcome.Unreachable)).ToList();
        GroupAvailability state = GroupStateCalculator.Evaluate(pool, evidence, out _, out _);
        Assert.Equal(GroupAvailability.Unknown, state);
    }

    [Fact]
    public void PartialOffline_ThreeIndependentUnreachable_IsOfflineWhenAllowed()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = pool.Take(3).Select(e => Fresh(e, ProbeOutcome.Unreachable)).ToList();
        Assert.Equal(GroupAvailability.Unknown, GroupStateCalculator.Evaluate(pool, evidence, out _, out _));
        Assert.Equal(
            GroupAvailability.Offline,
            GroupStateCalculator.Evaluate(pool, evidence, out _, out _, allowPartialOffline: true));
    }

    [Fact]
    public void T07_AllFreshUnreachable_IsOffline()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = pool.Select(e => Fresh(e, ProbeOutcome.Unreachable)).ToList();
        Assert.Equal(GroupAvailability.Offline, GroupStateCalculator.Evaluate(pool, evidence, out _, out _));
    }

    [Fact]
    public void T08_SharedInfrastructure_CountsOnce()
    {
        List<EndpointDefinition> pool = TestPools.SharedCdn().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = new List<EndpointEvidence>
        {
            Fresh(pool[0], ProbeOutcome.Reachable),
            Fresh(pool[1], ProbeOutcome.Reachable)
        };
        GroupAvailability state = GroupStateCalculator.Evaluate(pool, evidence, out _, out int votes);
        Assert.Equal(GroupAvailability.Limited, state);
        Assert.Equal(1, votes);
    }

    [Fact]
    public void T13_PermutationsHonorInvariants()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        ProbeOutcome?[] options = [null, ProbeOutcome.Reachable, ProbeOutcome.Unreachable, ProbeOutcome.Indeterminate];
        int combinations = 1;
        for (int i = 0; i < 7; i++)
        {
            combinations *= options.Length;
        }

        for (int mask = 0; mask < combinations; mask++)
        {
            var evidence = new List<EndpointEvidence>();
            int cursor = mask;
            for (int i = 0; i < 7; i++)
            {
                ProbeOutcome? outcome = options[cursor % options.Length];
                cursor /= options.Length;
                if (outcome is null)
                {
                    continue;
                }

                evidence.Add(Fresh(pool[i], outcome.Value));
            }

            GroupAvailability state = GroupStateCalculator.Evaluate(pool, evidence, out _, out int votes);
            if (evidence.Any(e => e.Outcome == ProbeOutcome.Reachable && e.Fresh))
            {
                Assert.NotEqual(GroupAvailability.Offline, state);
            }

            if (votes >= 2)
            {
                Assert.Equal(GroupAvailability.Online, state);
            }

            bool completeUnreachable = pool.All(p => evidence.Any(e => e.EndpointId == p.Id && e.Fresh && e.Outcome == ProbeOutcome.Unreachable));
            if (!completeUnreachable)
            {
                Assert.NotEqual(GroupAvailability.Offline, state);
            }
        }
    }

    [Fact]
    public void T05_SingleReachable_IsLimitedNeverOffline()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = pool.Select((e, i) => Fresh(e, i == 0 ? ProbeOutcome.Reachable : ProbeOutcome.Unreachable)).ToList();
        Assert.Equal(GroupAvailability.Limited, GroupStateCalculator.Evaluate(pool, evidence, out _, out _));
    }

    [Fact]
    public void StaleReachableDoesNotKeepOnline()
    {
        List<EndpointDefinition> pool = TestPools.Independent().Where(e => e.Group == EndpointGroup.Ru).ToList();
        var evidence = pool.Take(2).Select(e => new EndpointEvidence(e.Id, e.InfrastructureId, ProbeOutcome.Reachable, 1, false)).ToList();
        Assert.Equal(GroupAvailability.Unknown, GroupStateCalculator.Evaluate(pool, evidence, out _, out _));
    }

    private static EndpointEvidence Fresh(EndpointDefinition endpoint, ProbeOutcome outcome)
        => new(endpoint.Id, endpoint.InfrastructureId, outcome, 1, true);
}
