using System.Text;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountryParserTests
{
    [Theory]
    [InlineData("""{"success":true,"country_code":"AE","ip":"203.0.113.7"}""", "AE")]
    [InlineData("""{"success":true,"country_code":"nl","ip":"203.0.113.7","extra":true}""", "NL")]
    public void IpWhoCurrent_AcceptsSuccessfulIso2(string json, string expected)
    {
        Assert.True(GeoCountryParsers.TryParseIpWhoCurrent(Encoding.UTF8.GetBytes(json), out string country));
        Assert.Equal(expected, country);
    }

    [Theory]
    [InlineData("""{"success":false,"country_code":"NL"}""")]
    [InlineData("""{"country_code":"NL"}""")]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"country_code":"ZZ"}""")]
    [InlineData("""{"success":true,"country_code":12}""")]
    [InlineData("""{"success":true,"country_code":"DE"}""")]
    [InlineData("""{"success":true,"country_code":"DE","ip":"not-ip"}""")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void IpWhoCurrent_RejectsInvalid(string json)
        => Assert.False(GeoCountryParsers.TryParseIpWhoCurrent(Encoding.UTF8.GetBytes(json), out _));

    [Fact]
    public void RejectsUnassignedIsoLetters()
        => Assert.False(GeoCountryParsers.IsIso3166Alpha2("ZZ"));

    [Fact]
    public void CountryIs_RequiresCountryAndPublicIp()
    {
        Assert.True(GeoCountryParsers.TryParseCountryIsCurrent(
            Encoding.UTF8.GetBytes("""{"country":"DE","ip":"203.0.113.7"}"""), out string country, out string ip));
        Assert.Equal("DE", country);
        Assert.Equal("203.0.113.7", ip);
        Assert.False(GeoCountryParsers.TryParseCountryIsCurrent(
            Encoding.UTF8.GetBytes("""{"country":"TR"}"""), out _, out _));
    }
}
