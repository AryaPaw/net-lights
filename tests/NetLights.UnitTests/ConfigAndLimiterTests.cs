using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class ConfigAndLimiterTests
{
    [Fact]
    public void ValidatorRejectsNonHttpsAndWrongCounts()
    {
        var bad = BuiltinEndpoints.All.ToList();
        bad[0] = bad[0] with { Uri = new Uri("http://yandex.ru/robots.txt") };
        Assert.False(EndpointPoolValidator.Validate(bad).IsValid);
        Assert.False(EndpointPoolValidator.Validate(bad.Take(6).ToList()).IsValid);
        var fewInfra = BuiltinEndpoints.All.Select(e => e with { InfrastructureId = e.Group == EndpointGroup.Ru ? "one" : e.InfrastructureId }).ToList();
        Assert.False(EndpointPoolValidator.Validate(fewInfra).IsValid);
        Assert.True(EndpointPoolValidator.Validate(BuiltinEndpoints.All).IsValid);
    }

    [Fact]
    public void LimiterWindowIsOpenClosed()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var clock = new MonotonicClock(time);
        var limiter = new StartLimiter();
        limiter.Record(clock.Now);
        Assert.Equal(1, limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, limiter.CountInOpenClosedWindow(clock, clock.Now, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void BuiltinPoolHasSevenIndependentInfraPerGroup()
    {
        Assert.Equal(7, BuiltinEndpoints.All.Count(e => e.Group == EndpointGroup.Ru));
        Assert.Equal(7, BuiltinEndpoints.All.Count(e => e.Group == EndpointGroup.Vpn));
        Assert.Equal(7, BuiltinEndpoints.All.Where(e => e.Group == EndpointGroup.Ru).Select(e => e.InfrastructureId).Distinct().Count());
    }
}
