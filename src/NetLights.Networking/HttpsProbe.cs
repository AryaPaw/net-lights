using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NetLights.Core;

namespace NetLights.Networking;

public sealed class HttpsProbe : IProbe, IDisposable
{
    private HttpClient _client;
    private SocketsHttpHandler _handler;
    private readonly TimeProvider _time;
    private readonly string _userAgent;
    private readonly SslClientAuthenticationOptions? _testSsl;
    private readonly Dictionary<string, IPAddress> _lastAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public HttpsProbe(TimeProvider time, string version, SslClientAuthenticationOptions? testSsl = null)
    {
        _time = time;
        _userAgent = "NetLights/" + version;
        _testSsl = testSsl;
        (_handler, _client) = CreateClient();
    }

    public IReadOnlyDictionary<string, IPAddress> LastAddresses
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, IPAddress>(_lastAddresses, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void RecycleConnections()
    {
        lock (_gate)
        {
            HttpClient oldClient = _client;
            (_handler, _client) = CreateClient();
            oldClient.Dispose();
        }
    }

    public void CloseIdleConnections() => RecycleConnections();

    public async Task<EndpointObservation> ProbeAsync(
        EndpointDefinition endpoint,
        long networkEpoch,
        long attemptId,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint.Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        HttpClient client;
        lock (_gate)
        {
            client = _client;
        }

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            long completed = _time.GetTimestamp();
            TimeSpan elapsed = _time.GetElapsedTime(started, completed);
            if (status is < 200 or > 599)
            {
                return ObservationFactory.Create(
                    endpoint,
                    networkEpoch,
                    attemptId,
                    started,
                    completed,
                    ProbeOutcome.Indeterminate,
                    elapsed,
                    status,
                    new StructuredFailure(StructuredFailureKind.InvalidHeaders, "status"));
            }

            string? retryAfter = response.Headers.RetryAfter?.ToString();
            long next = RetryAfterParser.ResolvePauseUntil(status, retryAfter, new MonotonicClock(_time), _time.GetUtcNow());
            return ObservationFactory.Create(
                endpoint,
                networkEpoch,
                attemptId,
                started,
                completed,
                ProbeOutcome.Reachable,
                elapsed,
                status,
                nextAllowedAt: next);
        }
        catch (Exception ex)
        {
            long completed = _time.GetTimestamp();
            TimeSpan elapsed = _time.GetElapsedTime(started, completed);
            (ProbeOutcome outcome, StructuredFailure failure) = Classify(ex, cancellationToken, timeout.IsCancellationRequested);
            return ObservationFactory.Create(
                endpoint,
                networkEpoch,
                attemptId,
                started,
                completed,
                outcome,
                elapsed,
                failure: failure);
        }
    }

    public bool TryGetObservedAddress(Uri uri, out IPAddress? address)
    {
        lock (_gate)
        {
            return _lastAddresses.TryGetValue(uri.IdnHost, out address);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private (SocketsHttpHandler Handler, HttpClient Client) CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = MonitorConstants.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = MonitorConstants.PooledConnectionIdleTimeout,
            MaxConnectionsPerServer = MonitorConstants.MaxConnectionsPerServer,
            MaxResponseHeadersLength = MonitorConstants.MaxResponseHeadersKiB,
            ConnectTimeout = MonitorConstants.ProbeDeadline,
            SslOptions = _testSsl ?? new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            ConnectCallback = ConnectAsync
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        client.DefaultRequestHeaders.ExpectContinue = false;
        return (handler, client);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            if (socket.RemoteEndPoint is IPEndPoint ip)
            {
                lock (_gate)
                {
                    _lastAddresses[context.DnsEndPoint.Host] = ip.Address;
                }
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static (ProbeOutcome Outcome, StructuredFailure Failure) Classify(
        Exception ex,
        CancellationToken caller,
        bool deadline)
    {
        if (ex is OperationCanceledException)
        {
            if (caller.IsCancellationRequested && !deadline)
            {
                return (ProbeOutcome.Cancelled, new StructuredFailure(StructuredFailureKind.Cancelled, "cancelled"));
            }

            return (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TransportTimeout, "deadline"));
        }

        for (Exception? cursor = ex; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor is AuthenticationException)
            {
                return (ProbeOutcome.Indeterminate, new StructuredFailure(StructuredFailureKind.CertificateTrust, "tls-auth"));
            }

            if (cursor is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                        => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.DnsFailure, socket.SocketErrorCode.ToString())),
                    SocketError.ConnectionRefused
                        => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TcpRefused, "refused")),
                    SocketError.ConnectionReset
                        => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TcpReset, "reset")),
                    SocketError.TimedOut
                        => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TransportTimeout, "socket-timeout")),
                    _ => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TcpRefused, socket.SocketErrorCode.ToString()))
                };
            }
        }

        if (ex is HttpRequestException http)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError
                    => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.DnsFailure, "dns")),
                HttpRequestError.SecureConnectionError
                    => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TlsHandshake, "tls")),
                HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded
                    => (ProbeOutcome.Indeterminate, new StructuredFailure(StructuredFailureKind.InvalidHeaders, http.HttpRequestError.ToString())),
                HttpRequestError.ConfigurationLimitExceeded
                    => (ProbeOutcome.Indeterminate, new StructuredFailure(StructuredFailureKind.OversizedHeaders, "headers")),
                _ => (ProbeOutcome.Unreachable, new StructuredFailure(StructuredFailureKind.TlsProtocol, http.HttpRequestError.ToString()))
            };
        }

        return (ProbeOutcome.Indeterminate, new StructuredFailure(StructuredFailureKind.LocalResource, ex.GetType().Name));
    }
}
