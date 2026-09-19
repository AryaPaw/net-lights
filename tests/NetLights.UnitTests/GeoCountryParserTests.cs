using System.Net;
using System.Text;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountryParserTests
{
    [Fact]
    public void CountryIs_AcceptsPublicIpv4AndIso2()
    {
        Assert.True(GeoCountryParsers.TryParseCountryIs(
            """{"ip":"8.8.8.8","country":"DE"}"""u8.ToArray(),
            out IPAddress ip,
            out string country));
        Assert.Equal(IPAddress.Parse("8.8.8.8"), ip);
        Assert.Equal("DE", country);
    }

    [Fact]
    public void CountryIs_AcceptsPublicIpv6AndIgnoresExtraProperties()
    {
        Assert.True(GeoCountryParsers.TryParseCountryIs(
            """{"ip":"2001:4860:4860::8888","country":"US","extra":true}"""u8.ToArray(),
            out IPAddress ip,
            out string country));
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), ip);
        Assert.Equal("US", country);
    }

    [Theory]
    [InlineData("""{"ip":"127.0.0.1","country":"DE"}""")]
    [InlineData("""{"ip":"10.0.0.1","country":"DE"}""")]
    [InlineData("""{"ip":"192.168.1.1","country":"DE"}""")]
    [InlineData("""{"ip":"172.16.0.1","country":"DE"}""")]
    [InlineData("""{"ip":"169.254.1.1","country":"DE"}""")]
    [InlineData("""{"ip":"8.8.8.8","country":"DEU"}""")]
    [InlineData("""{"ip":"8.8.8.8","country":"D"}""")]
    [InlineData("""{"country":"DE"}""")]
    [InlineData("""[]""")]
    [InlineData("""{"ip":"8.8.8.8","country":12}""")]
    [InlineData("not-json")]
    public void CountryIs_RejectsInvalid(string json)
        => Assert.False(GeoCountryParsers.TryParseCountryIs(Encoding.UTF8.GetBytes(json), out _, out _));

    [Fact]
    public void CountryIs_NormalizesLowercaseIso()
    {
        Assert.True(GeoCountryParsers.TryParseCountryIs(
            """{"ip":"8.8.8.8","country":"de"}"""u8.ToArray(),
            out _,
            out string country));
        Assert.Equal("DE", country);
    }

    [Fact]
    public void IpWho_AcceptsSuccessIso2()
    {
        Assert.True(GeoCountryParsers.TryParseIpWho("""{"success":true,"country_code":"NL"}"""u8.ToArray(), out string country));
        Assert.Equal("NL", country);
    }

    [Fact]
    public void IpWho_AcceptsLowercaseAndMissingSuccessFlag()
    {
        Assert.True(GeoCountryParsers.TryParseIpWho("""{"success":true,"country_code":"nl"}"""u8.ToArray(), out string lower));
        Assert.Equal("NL", lower);
        Assert.True(GeoCountryParsers.TryParseIpWho("""{"country_code":"NL"}"""u8.ToArray(), out string bare));
        Assert.Equal("NL", bare);
    }

    [Theory]
    [InlineData("""{"success":false,"country_code":"NL"}""")]
    [InlineData("""{"success":true}""")]
    public void IpWho_RejectsInvalid(string json)
        => Assert.False(GeoCountryParsers.TryParseIpWho(Encoding.UTF8.GetBytes(json), out _));

    [Fact]
    public void IpWhoUri_EncodesIpv6InPath()
    {
        string url = GeoCountryParsers.IpWhoLookupUrl(new Uri("https://ipwho.is/"), IPAddress.Parse("2001:db8::1"));
        Assert.Equal("https://ipwho.is/2001%3Adb8%3A%3A1?fields=success,country_code", url);
    }

    [Fact]
    public void RejectsUnassignedIsoLetters()
    {
        Assert.False(GeoCountryParsers.IsIso3166Alpha2("ZZ"));
        Assert.False(GeoCountryParsers.TryParseCountryIs(
            """{"ip":"8.8.8.8","country":"ZZ"}"""u8.ToArray(),
            out _,
            out _));
    }
}
