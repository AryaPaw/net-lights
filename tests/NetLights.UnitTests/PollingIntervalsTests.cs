using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class PollingIntervalsTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void IntervalControlsSteadyPollingWithoutFalseUnknown(int seconds)
    {
        var config = PollingIntervals.Apply(BuiltinEndpoints.CreateDefault(), seconds);
        var runner = new VirtualRunner(config);
        runner.Run(TimeSpan.FromSeconds(20));
        int started = runner.Started;
        runner.Run(TimeSpan.FromSeconds(seconds * 15));
        Assert.InRange(runner.Started - started, 28, 32);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.World.Availability);
        Assert.True(config.Freshness > config.GroupInterval * MonitorConstants.EndpointsPerGroup);
    }

    [Fact]
    public void RuntimeChangePreservesEpochAndSamplesAndChangesActualSchedule()
    {
        var runner = new VirtualRunner();
        runner.Run(TimeSpan.FromSeconds(30));
        long epoch = runner.Kernel.Epoch;
        int applied = runner.Kernel.AppliedCompletions;
        int started = runner.Started;
        runner.Kernel.SetPollingInterval(60);
        runner.Run(TimeSpan.FromSeconds(120));
        Assert.Equal(epoch, runner.Kernel.Epoch);
        Assert.True(runner.Kernel.AppliedCompletions > applied);
        Assert.InRange(runner.Started - started, 3, 5);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.World.Availability);
    }

    [Fact]
    public void SlowPollingStillConfirmsAndRecoversFromAnOutage()
    {
        var runner = new VirtualRunner(PollingIntervals.Apply(BuiltinEndpoints.CreateDefault(), 30));
        runner.Run(TimeSpan.FromSeconds(30));
        runner.BreakAll();
        runner.Run(TimeSpan.FromMinutes(3));
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.World.Availability);
        Assert.Equal(GroupAvailability.Offline, runner.Kernel.Snapshot.Ru.Availability);
        runner.RestoreAll();
        runner.Run(TimeSpan.FromMinutes(3));
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.World.Availability);
        Assert.Equal(GroupAvailability.Online, runner.Kernel.Snapshot.Ru.Availability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(9)]
    public void UnsupportedValuesUseSafeDefaults(int value)
    {
        Assert.Equal(5, PollingIntervals.NormalizeAvailability(value));
        Assert.Equal(10, PollingIntervals.NormalizeCountry(value));
    }
}
