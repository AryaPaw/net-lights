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
    public async Task AgreeingSources_ReturnConfirmedLookupParts()
    {
        var handler = new ScriptedHandler()
            .On("https://api.country.is/", """{"ip":"8.8.8.8","country":"DE"}""")
            .On("https://ipwho.is/8.8.8.8?fields=success,country_code", """{"success":true,"country_code":"DE"}""");
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountrySelfResult self = await client.GetSelfAsync(CancellationToken.None);
        GeoCountryConfirmResult confirm = await client.ConfirmAsync(self.Ip!, CancellationToken.None);
        Assert.True(self.Ok);
        Assert.Equal("DE", self.CountryCode);
        Assert.True(confirm.Ok);
        Assert.Equal("DE", confirm.CountryCode);
        Assert.Equal(HttpMethod.Get, handler.Methods[0]);
        Assert.Equal(HttpVersion.Version11, handler.Versions[0]);
    }

    [Fact]
    public async Task Confirm_EncodesIpv6AndUsesSameAddress()
    {
        IPAddress ip = IPAddress.Parse("2001:db8::1");
        var handler = new ScriptedHandler()
            .On("https://ipwho.is/2001%3Adb8%3A%3A1?fields=success,country_code", """{"success":true,"country_code":"DE"}""");
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountryConfirmResult confirm = await client.ConfirmAsync(ip, CancellationToken.None);
        Assert.True(confirm.Ok);
        Assert.Equal("https://ipwho.is/2001%3Adb8%3A%3A1?fields=success,country_code", handler.Uris[0].OriginalString);
    }

    [Fact]
    public async Task Redirect_IsUnconfirmed()
    {
        var handler = new ScriptedHandler { Status = HttpStatusCode.Found };
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        GeoCountrySelfResult self = await client.GetSelfAsync(CancellationToken.None);
        Assert.False(self.Ok);
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task Timeout_IsUnconfirmed()
    {
        using var client = new HttpsGeoCountryClient("1.1.1", new HangHandler(), deadline: TimeSpan.FromMilliseconds(50));
        GeoCountrySelfResult self = await client.GetSelfAsync(CancellationToken.None);
        Assert.False(self.Ok);
    }

    [Fact]
    public async Task OversizedBody_IsUnconfirmed()
    {
        var handler = new ScriptedHandler()
            .On("https://api.country.is/", new string('x', GeoCountryPolicy.MaxBodyBytes + 8));
        using var client = new HttpsGeoCountryClient("1.1.1", handler);
        Assert.False((await client.GetSelfAsync(CancellationToken.None)).Ok);
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
        GeoCountrySelfResult self = await client.GetSelfAsync(CancellationToken.None);
        Assert.False(self.Ok);
        Assert.Equal(TimeSpan.FromSeconds(12), self.RetryAfter);
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
