using NetLights.Core;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class CorroboratedGeoCountrySourceTests
{
    [Fact]
    public async Task Agreement_ConfirmsCountry()
    {
        using var source = new CorroboratedGeoCountrySource(
            new FixedSource(new(true, "DE", null, "203.0.113.7")),
            new FixedSource(new(true, "DE", null, "203.0.113.7")));
        GeoCountryLookupResult result = await source.GetCurrentAsync(CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("DE", result.CountryCode);
        Assert.Equal("203.0.113.7", result.PublicIp);
    }

    [Fact]
    public async Task Disagreement_DoesNotPublishEitherCountry()
    {
        using var source = new CorroboratedGeoCountrySource(
            new FixedSource(new(true, "TR", null, "203.0.113.7")),
            new FixedSource(new(true, "DE", null, "203.0.113.7")));
        GeoCountryLookupResult result = await source.GetCurrentAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Null(result.CountryCode);
        Assert.True(result.Conflicted);
    }

    [Fact]
    public async Task MissingSecondResult_DoesNotConfirmCountry()
    {
        using var source = new CorroboratedGeoCountrySource(
            new FixedSource(new(true, "TR", null, "203.0.113.7")),
            new FixedSource(new(false, null, TimeSpan.FromMinutes(10))));
        GeoCountryLookupResult result = await source.GetCurrentAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(TimeSpan.FromMinutes(10), result.RetryAfter);
    }

    [Fact]
    public async Task MatchingCountriesFromDifferentExitIps_AreNotConfirmed()
    {
        using var source = new CorroboratedGeoCountrySource(
            new FixedSource(new(true, "DE", null, "203.0.113.7")),
            new FixedSource(new(true, "DE", null, "203.0.113.8")));
        GeoCountryLookupResult result = await source.GetCurrentAsync(CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(result.DifferentIp);
    }

    private sealed class FixedSource(GeoCountryLookupResult result) : IGeoCountrySource
    {
        public Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
