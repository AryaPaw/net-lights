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
        var dupHost = BuiltinEndpoints.All.ToList();
        dupHost[1] = dupHost[1] with { Uri = dupHost[0].Uri, Id = "dup-host", InfrastructureId = "other" };
        Assert.False(EndpointPoolValidator.Validate(dupHost).IsValid);
        var sameHostDifferentPath = BuiltinEndpoints.All.ToList();
        Uri first = sameHostDifferentPath[0].Uri;
        sameHostDifferentPath[1] = sameHostDifferentPath[1] with
        {
            Uri = new Uri($"{first.Scheme}://{first.IdnHost}:{first.Port}/other-path"),
            Id = "same-host-other-path",
            InfrastructureId = "other-path-infra"
        };
        Assert.True(EndpointPoolValidator.Validate(sameHostDifferentPath).IsValid);
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
        var world = BuiltinEndpoints.All.Where(e => e.Group == EndpointGroup.World).ToList();
        Assert.Equal(7, ru.Count);
        Assert.Equal(7, world.Count);
        Assert.Equal(7, ru.Select(e => e.InfrastructureId).Distinct().Count());
        Assert.Equal(7, world.Select(e => e.InfrastructureId).Distinct().Count());
        Assert.DoesNotContain(world, e => e.Uri.Host.Contains("firefox-portal-detection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(world, e => e.Uri.Host.Contains("fedoraproject.org", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(world, e => e.Uri.Host.Contains("debian.org", StringComparison.OrdinalIgnoreCase));
    }
}
