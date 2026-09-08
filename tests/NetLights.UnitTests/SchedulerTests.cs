using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class SchedulerTests
{
    [Fact]
    public void T01_StartWithoutResults_IsUnknown()
    {
        var runner = new VirtualRunner();
        Assert.Equal(GroupAvailability.Unknown, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Unknown, runner.Kernel.Snapshot.Vpn.Availability);
        runner.Run(TimeSpan.FromSeconds(6));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Vpn.Availability);
    }

    [Fact]
    public void T02_AllHealthy_NoConfirmStorm()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(40));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        int afterWarmup = runner.Kernel.ConfirmationEpisodeCount;
        runner.Run(TimeSpan.FromSeconds(30));
        Assert.Equal(afterWarmup, runner.Kernel.ConfirmationEpisodeCount);
        AssertEqualRotation(runner);
    }

    [Fact]
    public void T03_OneDead_StaysOnlineForVirtual72h()
    {
        var runner = new VirtualRunner();
        runner.Kill("ru-0");
        runner.Run(TimeSpan.FromHours(72), TimeSpan.FromSeconds(1));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        Assert.True(runner.Kernel.ConfirmationEpisodeCount < 10);
        Assert.True(runner.Kernel.PhysicalInflight <= MonitorConstants.MaxPhysicalInflight);
    }

    [Theory]
    [MemberData(nameof(TwoLivePairs))]
    public void T04_TwoLive_IsOnline(int a, int b)
    {
        var runner = new VirtualRunner();
        string[] live = [$"ru-{a}", $"ru-{b}"];
        runner.RestoreOnly(live.Concat(Enumerable.Range(0, 7).Select(i => $"vpn-{i}")).ToArray());
        runner.Run(TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(200));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void T05_OneLive_IsLimited()
    {
        var runner = new VirtualRunner();
        runner.RestoreOnly("ru-3", "vpn-0", "vpn-1", "vpn-2", "vpn-3", "vpn-4", "vpn-5", "vpn-6");
        runner.Run(TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(200));
        Assert.Equal(GroupAvailability.Limited, runner.Kernel.Snapshot.Ru.Availability);
        Assert.DoesNotContain(runner.Snapshots, s => s.Ru.Availability == GroupAvailability.Offline);
    }

    [Fact]
    public void T09_AttemptIdWinsOverDeliveryOrder()
    {
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, TimeProvider.System);
        EndpointDefinition ep = TestPools.Independent()[0];
        EndpointObservation newer = ObservationFactory.Create(ep, 1, 2, 20, 30, ProbeOutcome.Unreachable, TimeSpan.FromMilliseconds(10));
        EndpointObservation older = ObservationFactory.Create(ep, 1, 1, 10, 40, ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(10), 200);
        kernel.ApplyObservation(newer);
        kernel.ApplyObservation(older);
        Assert.Equal(ProbeOutcome.Unreachable, kernel.Inspect(ep.Id).Outcome);
        kernel.ApplyObservation(newer);
        Assert.Equal(2, kernel.Inspect(ep.Id).AppliedAttemptId);
    }

    [Fact]
    public void T10_OldEpochIsIgnoredAndCancelIsNotFailure()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(6));
        runner.Kernel.BeginNewEpoch("test");
        EndpointDefinition ep = TestPools.Independent()[0];
        var stale = ObservationFactory.Create(ep, 1, 99, 1, 2, ProbeOutcome.Unreachable, TimeSpan.FromMilliseconds(5));
        runner.Kernel.ApplyObservation(stale);
        Assert.Null(runner.Kernel.Inspect(ep.Id).Outcome);
        var cancelled = ObservationFactory.Create(ep, 2, 1, 1, 2, ProbeOutcome.Cancelled, TimeSpan.FromMilliseconds(5));
        runner.Kernel.ApplyObservation(cancelled);
        Assert.Null(runner.Kernel.Inspect(ep.Id).Outcome);
        Assert.NotEqual(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void T11_TtlExpiresWithoutNewTraffic()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(8));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        int started = runner.Started;
        runner.Script = _ => new ProbeScript { Frozen = true };
        runner.Run(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(1));
        Assert.NotEqual(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        Assert.True(runner.Started - started <= 16);
    }

    [Fact]
    public void T12_UtcJumpDoesNotChangeMonotonicTtl()
    {
        var time = new SplitTimeProvider();
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = TestPools.Independent() }, time);
        EndpointDefinition a = TestPools.Independent().First(e => e.Id == "ru-0");
        EndpointDefinition b = TestPools.Independent().First(e => e.Id == "ru-1");
        kernel.ApplyObservation(ObservationFactory.Create(a, 1, 1, time.GetTimestamp(), time.GetTimestamp(), ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(10), 200));
        kernel.ApplyObservation(ObservationFactory.Create(b, 1, 2, time.GetTimestamp(), time.GetTimestamp(), ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(10), 200));
        kernel.Tick();
        Assert.Equal(GroupAvailability.Online, kernel.Snapshot.Ru.Availability);
        time.UtcShift = TimeSpan.FromDays(4);
        kernel.Tick();
        Assert.Equal(GroupAvailability.Online, kernel.Snapshot.Ru.Availability);
        time.UtcShift = TimeSpan.FromDays(-4);
        kernel.Tick();
        Assert.Equal(GroupAvailability.Online, kernel.Snapshot.Ru.Availability);
        time.Advance(TimeSpan.FromSeconds(21));
        kernel.Tick();
        Assert.NotEqual(GroupAvailability.Online, kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void OutageAfterOnline_DoesNotStayLimitedOnStaleSuccess()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(20));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        runner.BreakAll();
        runner.Run(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));
        Assert.NotEqual(GroupAvailability.Limited, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Vpn.Availability);
    }

    [Fact]
    public void OsUnavailable_IsImmediateOffline()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(8));
        runner.Kernel.SetNetworkUnavailable(true);
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Vpn.Availability);
    }

    [Fact]
    public void T14_RotationCadence()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(28));
        AssertEqualRotation(runner);
        ILookup<string, string> byEndpoint = runner.StartLog.ToLookup(x => x);
        foreach (IGrouping<string, string> group in byEndpoint)
        {
            Assert.InRange(group.Count(), 1, 4);
        }
    }

    [Fact]
    public void T15_RateLimits()
    {
        var runner = new VirtualRunner();
        for (int i = 0; i < 30; i++)
        {
            runner.Kernel.RequestCheckNow();
            runner.Run(TimeSpan.FromMilliseconds(100));
        }

        runner.Run(TimeSpan.FromSeconds(5));
        var clock = new MonotonicClock(runner.Time);
        Assert.InRange(runner.Kernel.Limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(1)), 0, 4);
        Assert.InRange(runner.Kernel.Limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(60)), 0, 120);
        Assert.InRange(runner.MaxInflight, 0, 4);
    }

    [Fact]
    public void T16_FrozenTasksHoldPhysicalPermit()
    {
        var runner = new VirtualRunner();
        runner.Script = _ => new ProbeScript { Frozen = true };
        runner.Run(TimeSpan.FromSeconds(8));
        Assert.Equal(MonitorConstants.MaxPhysicalInflight, runner.Kernel.PhysicalInflight);
        Assert.True(runner.Kernel.Snapshot.CapacityExhausted);
    }

    [Fact]
    public void T17_MinIntervalAppliesToConfirmationAndClick()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
        runner.Kernel.RequestCheckNow();
        runner.Run(TimeSpan.FromSeconds(16), TimeSpan.FromMilliseconds(50));
        var clock = new MonotonicClock(runner.Time);
        foreach (IGrouping<string, long> group in runner.StartTimes.GroupBy(x => x.Id, x => x.Timestamp))
        {
            List<long> stamps = group.OrderBy(x => x).ToList();
            for (int i = 1; i < stamps.Count; i++)
            {
                Assert.True(
                    clock.Elapsed(stamps[i - 1], stamps[i]) >= MonitorConstants.MinEndpointInterval,
                    $"{group.Key} interval {clock.Elapsed(stamps[i - 1], stamps[i])}");
            }
        }
    }

    [Fact]
    public void T18_SingleFailureDoesNotConfirm()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(20));
        int episodes = runner.Kernel.ConfirmationEpisodeCount;
        runner.Kill("ru-0");
        runner.Run(TimeSpan.FromSeconds(16));
        Assert.Equal(episodes, runner.Kernel.ConfirmationEpisodeCount);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void T19_TwoInfraFailuresStartOneEpisode()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(20));
        int episodes = runner.Kernel.ConfirmationEpisodeCount;
        runner.Kill("ru-0", "ru-1");
        runner.Run(TimeSpan.FromSeconds(20));
        Assert.Equal(episodes + 1, runner.Kernel.ConfirmationEpisodeCount);
    }

    [Fact]
    public void T20_TwoNewSuccessesStopEpisode()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(20));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        int episodes = runner.Kernel.ConfirmationEpisodeCount;
        runner.Kill("ru-0", "ru-1");
        runner.Run(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
        Assert.True(runner.Kernel.ConfirmationEpisodeCount > episodes);
        runner.RestoreAll();
        runner.Run(TimeSpan.FromSeconds(16), TimeSpan.FromMilliseconds(50));
        Assert.False(runner.Kernel.Snapshot.Ru.ConfirmationActive);
    }

    [Fact]
    public void T21_FailedConfirmationCoversAtMostSeven()
    {
        var runner = new VirtualRunner();
        runner.BreakAll();
        runner.Run(TimeSpan.FromSeconds(20));
        Assert.True(runner.Kernel.Snapshot.Ru.Availability is GroupAvailability.Offline or GroupAvailability.Unknown);
        int ruStarts = runner.StartLog.Count(id => id.StartsWith("ru-", StringComparison.Ordinal));
        Assert.True(ruStarts <= 14);
    }

    [Fact]
    public void T22_CooldownAndSingleEpisode()
    {
        var runner = new VirtualRunner();
        runner.Kernel.RequestCheckNow();
        runner.Kernel.RequestCheckNow();
        runner.Kernel.BeginNewEpoch("net");
        runner.Kernel.RequestCheckNow();
        Assert.True(runner.Kernel.Snapshot.Ru.ConfirmationActive);
        int count = runner.Kernel.ConfirmationEpisodeCount;
        runner.Kernel.RequestCheckNow();
        Assert.Equal(count, runner.Kernel.ConfirmationEpisodeCount);
    }

    [Fact]
    public void T23_RetryAfterDoesNotInventCoverage()
    {
        var runner = new VirtualRunner();
        runner.Script = endpoint => endpoint.Group == EndpointGroup.Ru
            ? new ProbeScript { Status = 429, RetryAfter = "120", Outcome = ProbeOutcome.Reachable }
            : new ProbeScript();
        runner.Run(TimeSpan.FromSeconds(16));
        Assert.NotEqual(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        runner.Script = _ => new ProbeScript { Frozen = true };
        runner.Run(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(1));
        Assert.Equal(GroupAvailability.Unknown, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void T24_DualOutageWithin14s()
    {
        foreach (int phaseMs in Enumerable.Range(0, 280).Select(i => i * 50))
        {
            var runner = new VirtualRunner();
            runner.Run(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
            runner.Run(TimeSpan.FromMilliseconds(phaseMs), TimeSpan.FromMilliseconds(50));
            runner.BreakAll();
            DateTimeOffset start = runner.Time.GetUtcNow();
            runner.Run(TimeSpan.FromSeconds(14), TimeSpan.FromMilliseconds(50));
            Assert.True(
                runner.Kernel.Snapshot.Ru.Availability == GroupAvailability.Offline
                && runner.Kernel.Snapshot.Vpn.Availability == GroupAvailability.Offline,
                $"phase {phaseMs} ru={runner.Kernel.Snapshot.Ru.Availability} vpn={runner.Kernel.Snapshot.Vpn.Availability} elapsed={(runner.Time.GetUtcNow() - start).TotalSeconds}");
            Assert.InRange(runner.MaxInflight, 0, 4);
        }
    }

    [Fact]
    public void T25_FullRecoveryBounds()
    {
        var runner = new VirtualRunner();
        runner.BreakAll();
        runner.Run(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        runner.RestoreAll();
        DateTimeOffset start = runner.Time.GetUtcNow();
        TimeSpan leftRed = TimeSpan.MaxValue;
        TimeSpan online = TimeSpan.MaxValue;
        for (int i = 0; i < 100; i++)
        {
            runner.Run(TimeSpan.FromMilliseconds(50));
            TimeSpan elapsed = runner.Time.GetUtcNow() - start;
            if (leftRed == TimeSpan.MaxValue && runner.Kernel.Snapshot.Ru.Availability != GroupAvailability.Offline)
            {
                leftRed = elapsed;
            }

            if (online == TimeSpan.MaxValue && runner.Kernel.Snapshot.Ru.Availability == GroupAvailability.Online)
            {
                online = elapsed;
                break;
            }
        }

        Assert.True(leftRed <= TimeSpan.FromSeconds(3), $"left red {leftRed}");
        Assert.True(online <= TimeSpan.FromSeconds(5), $"online {online}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void T25_PartialRecoveryWithin15s(int liveIndex)
    {
        var runner = new VirtualRunner();
        runner.BreakAll();
        runner.Run(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
        runner.RestoreOnly($"ru-{liveIndex}", "vpn-0", "vpn-1");
        DateTimeOffset start = runner.Time.GetUtcNow();
        for (int i = 0; i < 300; i++)
        {
            runner.Run(TimeSpan.FromMilliseconds(50));
            if (runner.Kernel.Snapshot.Ru.Availability is GroupAvailability.Limited or GroupAvailability.Online)
            {
                Assert.True(runner.Time.GetUtcNow() - start <= TimeSpan.FromSeconds(15));
                return;
            }
        }

        Assert.Fail($"position {liveIndex} did not recover");
    }

    [Fact]
    public void T26_Offline72hHasNoQueueGrowth()
    {
        var runner = new VirtualRunner();
        runner.BreakAll();
        runner.Run(TimeSpan.FromHours(72), TimeSpan.FromSeconds(1));
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        Assert.InRange(runner.Kernel.PhysicalInflight, 0, 4);
        Assert.True(runner.Kernel.ConfirmationEpisodeCount < 30);
        runner.RestoreAll();
        runner.Run(TimeSpan.FromSeconds(16), TimeSpan.FromMilliseconds(50));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Fact]
    public void T27_RetryAfterVariants()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new MonotonicClock(time);
        long sec = RetryAfterParser.ResolvePauseUntil(429, "5", clock, time.GetUtcNow());
        Assert.InRange(clock.Elapsed(clock.Now, sec).TotalSeconds, 4, 6);

        long future = RetryAfterParser.ResolvePauseUntil(429, time.GetUtcNow().AddMinutes(1).ToString("r"), clock, time.GetUtcNow());
        Assert.InRange(clock.Elapsed(clock.Now, future).TotalSeconds, 59, 61);

        long past = RetryAfterParser.ResolvePauseUntil(429, time.GetUtcNow().AddDays(-2).ToString("r"), clock, time.GetUtcNow());
        Assert.InRange(clock.Elapsed(clock.Now, past).TotalSeconds, 59, 61);

        long invalid = RetryAfterParser.ResolvePauseUntil(429, "not-a-date", clock, time.GetUtcNow());
        Assert.InRange(clock.Elapsed(clock.Now, invalid).TotalSeconds, 14 * 60, 16 * 60);

        long huge = RetryAfterParser.ResolvePauseUntil(429, "999999999", clock, time.GetUtcNow());
        Assert.True(huge > clock.Now);

        long none = RetryAfterParser.ResolvePauseUntil(429, null, clock, time.GetUtcNow());
        Assert.InRange(clock.Elapsed(clock.Now, none).TotalSeconds, 14 * 60, 16 * 60);
    }

    [Fact]
    public void T28_LoopExceptionIsUnknownNotOffline()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(8));
        runner.Kernel.NotifyInternalError("boom");
        Assert.Equal(GroupAvailability.Unknown, runner.Kernel.Snapshot.Ru.Availability);
        Assert.NotEqual(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal("boom", runner.Kernel.Snapshot.MonitorError);
    }

    public static TheoryData<int, int> TwoLivePairs()
    {
        var data = new TheoryData<int, int>();
        for (int a = 0; a < 7; a++)
        {
            for (int b = a + 1; b < 7; b++)
            {
                data.Add(a, b);
            }
        }

        return data;
    }

    private static void AssertEqualRotation(VirtualRunner runner)
    {
        int ru = runner.StartLog.Count(id => id.StartsWith("ru-", StringComparison.Ordinal));
        int vpn = runner.StartLog.Count(id => id.StartsWith("vpn-", StringComparison.Ordinal));
        Assert.InRange(Math.Abs(ru - vpn), 0, 2);
    }
}
