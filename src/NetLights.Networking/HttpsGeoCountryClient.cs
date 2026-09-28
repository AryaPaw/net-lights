using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using NetLights.Core;

namespace NetLights.Networking;

public sealed class HttpsGeoCountryClient : IGeoCountrySource, IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _deadline;
    private readonly Uri _endpoint;
    private readonly TimeProvider _time;
    private readonly string _userAgent;

    public HttpsGeoCountryClient(
        string version,
        HttpMessageHandler? handler = null,
        Uri? endpoint = null,
        TimeSpan? deadline = null,
        TimeProvider? time = null)
    {
        _userAgent = "NetLights/" + version;
        _deadline = deadline ?? GeoCountryPolicy.RequestDeadline;
        _endpoint = endpoint ?? GeoCountryPolicy.IpWhoUri;
        _time = time ?? TimeProvider.System;
        HttpMessageHandler inner = handler ?? CreateProductionHandler();
        if (inner is SocketsHttpHandler sockets)
        {
            UsesCookies = sockets.UseCookies;
            UsesProxy = sockets.UseProxy;
            AllowsAutoRedirect = sockets.AllowAutoRedirect;
        }

        _client = new HttpClient(inner, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        _client.DefaultRequestHeaders.ExpectContinue = false;
        RequestVersion = _client.DefaultRequestVersion;
        VersionPolicy = _client.DefaultVersionPolicy;
    }

    public bool UsesCookies { get; }
    public bool UsesProxy { get; }
    public bool AllowsAutoRedirect { get; }
    public Version RequestVersion { get; }
    public HttpVersionPolicy VersionPolicy { get; }

    public static HttpsGeoCountryClient CreateProduction(string version) => new(version);

    public Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
        => GetAsync(
            _endpoint,
            static body => GeoCountryParsers.TryParseIpWhoCurrent(body, out string country)
                ? new GeoCountryLookupResult(true, country, null)
                : default,
            static retry => new GeoCountryLookupResult(false, null, retry),
            cancellationToken);

    public void Dispose() => _client.Dispose();

    private async Task<T> GetAsync<T>(
        Uri uri,
        Func<byte[], T> parseOk,
        Func<TimeSpan?, T> fail,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_deadline);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        try
        {
            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status == 429)
            {
                return fail(ReadRetryAfter(response));
            }

            if (status is < 200 or >= 300)
            {
                return fail(null);
            }

            if (response.Content.Headers.ContentLength is > GeoCountryPolicy.MaxBodyBytes)
            {
                return fail(null);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] body = await ReadCappedAsync(stream, GeoCountryPolicy.MaxBodyBytes, timeout.Token).ConfigureAwait(false);
            if (body.Length == 0)
            {
                return fail(null);
            }

            return parseOk(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return fail(null);
        }
    }

    private TimeSpan ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (header?.Date is DateTimeOffset when)
        {
            TimeSpan until = when - _time.GetUtcNow();
            if (until > TimeSpan.Zero)
            {
                return until;
            }
        }

        return GeoCountryPolicy.MissingRetryAfter;
    }

    private static async Task<byte[]> ReadCappedAsync(Stream input, int maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[maxBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await input.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read == 0 || read > maxBytes)
        {
            return [];
        }

        return buffer.AsSpan(0, read).ToArray();
    }

    private static SocketsHttpHandler CreateProductionHandler()
        => new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = MonitorConstants.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = MonitorConstants.PooledConnectionIdleTimeout,
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = MonitorConstants.MaxResponseHeadersKiB,
            ConnectTimeout = GeoCountryPolicy.RequestDeadline,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };
}
