using System.Net;
using System.Net.Http;
using System.Text;
using NetLights.Core;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class HttpsGeoCountryClientTests
{
    [Fact]
    public async Task CurrentLookup_UsesOneIpWhoRequestWithoutPuttingIpInUrl()
    {
        var handler = new ScriptedHandler()
            .On(GeoCountryPolicy.IpWhoUri.AbsoluteUri, """{"success":true,"country_code":"AE"}""");
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountryLookupResult lookup = await client.GetCurrentAsync(CancellationToken.None);
        Assert.True(lookup.Ok);
        Assert.Equal("AE", lookup.CountryCode);
        Assert.Equal(1, handler.Hits);
        Assert.Equal(HttpMethod.Get, handler.Methods.Single());
        Assert.Equal(GeoCountryPolicy.IpWhoUri, handler.Uris.Single());
        Assert.DoesNotContain("8.8.8.8", handler.Uris.Single().AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("fields=success,country_code", handler.Uris.Single().Query, StringComparison.Ordinal);
        Assert.Equal(HttpVersion.Version11, handler.Versions[0]);
    }

    [Fact]
    public async Task EndpointCanBeOverriddenForTests()
    {
        var endpoint = new Uri("https://geo.test/current?fields=success,country_code");
        var handler = new ScriptedHandler().On(endpoint.AbsoluteUri, """{"success":true,"country_code":"DE"}""");
        using var client = new HttpsGeoCountryClient("1.1.1", handler, endpoint);
        GeoCountryLookupResult lookup = await client.GetCurrentAsync(CancellationToken.None);
        Assert.True(lookup.Ok);
        Assert.Equal(endpoint, handler.Uris.Single());
    }

    [Fact]
    public async Task Redirect_IsRejected()
    {
        var handler = new ScriptedHandler { Status = HttpStatusCode.Found };
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountryLookupResult lookup = await client.GetCurrentAsync(CancellationToken.None);
        Assert.False(lookup.Ok);
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task Timeout_IsRejected()
    {
        using var client = new HttpsGeoCountryClient("1.1.1", new HangHandler(), deadline: TimeSpan.FromMilliseconds(50));
        GeoCountryLookupResult lookup = await client.GetCurrentAsync(CancellationToken.None);
        Assert.False(lookup.Ok);
    }

    [Fact]
    public async Task OversizedBody_IsRejected()
    {
        var handler = new ScriptedHandler()
            .On(GeoCountryPolicy.IpWhoUri.AbsoluteUri, new string('x', GeoCountryPolicy.MaxBodyBytes + 8));
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        Assert.False((await client.GetCurrentAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Status429_ReturnsRetryAfter()
    {
        var handler = new ScriptedHandler
        {
            Status = (HttpStatusCode)429,
            RetryAfter = TimeSpan.FromSeconds(12)
        };
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountryLookupResult lookup = await client.GetCurrentAsync(CancellationToken.None);
        Assert.False(lookup.Ok);
        Assert.Equal(TimeSpan.FromSeconds(12), lookup.RetryAfter);
    }

    [Fact]
    public void ProductionHandler_DisablesCookiesProxyAndRedirects()
    {
        using var client = HttpsGeoCountryClient.CreateProduction("1.1.1");
        Assert.False(client.UsesCookies);
        Assert.False(client.UsesProxy);
        Assert.False(client.AllowsAutoRedirect);
        Assert.Equal(HttpVersion.Version11, client.RequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.VersionPolicy);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public TimeSpan? RetryAfter { get; init; }
        public int Hits { get; private set; }
        public List<HttpMethod> Methods { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<Version> Versions { get; } = [];

        public ScriptedHandler On(string uri, string body)
        {
            _bodies[uri] = body;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;
            Methods.Add(request.Method);
            Uris.Add(request.RequestUri!);
            Versions.Add(request.Version);
            var response = new HttpResponseMessage(Status);
            if (RetryAfter is TimeSpan retry)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retry);
            }

            string body = request.RequestUri is not null && _bodies.TryGetValue(request.RequestUri.AbsoluteUri, out string? mapped)
                ? mapped
                : "{}";
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
