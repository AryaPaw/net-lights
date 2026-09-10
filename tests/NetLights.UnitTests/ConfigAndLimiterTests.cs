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
        time.Advance(TimeSpan.FromSeconds(61));
        limiter.RemoveOlderThan(clock, TimeSpan.FromSeconds(60));
        Assert.Empty(limiter.Starts);
        for (int i = 0; i < 200; i++)
        {
            limiter.Record(clock.Now);
        }

        Assert.Equal(MonitorConstants.MaxStartsPerMinute, limiter.Starts.Count);
    }

    [Fact]
    public void BuiltinPoolHasSevenIndependentInfraPerGroup()
    {
        var ru = BuiltinEndpoints.All.Where(e => e.Group == EndpointGroup.Ru).ToList();
        var vpn = BuiltinEndpoints.All.Where(e => e.Group == EndpointGroup.Vpn).ToList();
        Assert.Equal(7, ru.Count);
        Assert.Equal(7, vpn.Count);
        Assert.Equal(7, ru.Select(e => e.InfrastructureId).Distinct().Count());
        Assert.Equal(7, vpn.Select(e => e.InfrastructureId).Distinct().Count());
        Assert.DoesNotContain(vpn, e => e.Uri.Host.Contains("firefox-portal-detection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vpn, e => e.Uri.Host.Contains("fedoraproject.org", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vpn, e => e.Uri.Host.Contains("debian.org", StringComparison.OrdinalIgnoreCase));
    }
}
