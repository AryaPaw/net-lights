using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NetLights.TestSupport;

public enum FixtureMode
{
    Ok,
    Status,
    Redirect,
    HangTls,
    HangHeaders,
    SlowHeaders,
    PlainHttp,
    CloseAfterAccept,
    HugeHeaders,
    Http100ThenHang,
    HeadWithBody,
    Reset
}

public sealed class LocalCertificateChain(X509Certificate2 authority, X509Certificate2 server) : IDisposable
{
    public X509Certificate2 Authority { get; } = authority;
    public X509Certificate2 Server { get; } = server;

    public void Dispose()
    {
        Authority.Dispose();
        Server.Dispose();
    }
}

public sealed class LocalHttpsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly LocalCertificateChain? _certificates;
    private string? _lastServerError;
    private int _hits;
    private int _redirectHits;
    private int _resourcesDisposed;

    public LocalHttpsFixture(FixtureMode mode, LocalCertificateChain? certificates, int status = 200, string? retryAfter = null, string? location = "/other")
    {
        Mode = mode;
        Status = status;
        RetryAfter = retryAfter;
        Location = location;
        _certificates = certificates;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public FixtureMode Mode { get; }
    public int Status { get; }
    public string? RetryAfter { get; }
    public string? Location { get; }
    public int Hits => Volatile.Read(ref _hits);
    public int RedirectHits => Volatile.Read(ref _redirectHits);
    public string? LastServerError => Volatile.Read(ref _lastServerError);
    public Uri Uri => new($"https://127.0.0.1:{Port}/");

    public static LocalCertificateChain CreateCertificate(string cn, DateTimeOffset notBefore, DateTimeOffset notAfter, string? sanDns = null, bool includeLoopback = true)
    {
        X509Certificate2? server = null;
        X509Certificate2? root = null;
        RSA? serverKey = null;
        try
        {
            serverKey = RSA.Create(2048);
            var req = new CertificateRequest("CN=" + cn, serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            var enhancedUsage = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedUsage, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(sanDns ?? cn);
            if (includeLoopback)
            {
                san.AddIpAddress(IPAddress.Loopback);
                san.AddIpAddress(IPAddress.IPv6Loopback);
            }

            req.CertificateExtensions.Add(san.Build());
            using X509Certificate2 serverWithEphemeralKey = req.CreateSelfSigned(notBefore, notAfter);
            root = X509CertificateLoader.LoadCertificate(serverWithEphemeralKey.Export(X509ContentType.Cert));
            byte[] pkcs12 = serverWithEphemeralKey.Export(X509ContentType.Pkcs12);
            try
            {
                // Schannel cannot use ephemeral server keys. Importing the short-lived PFX
                // into the current user's temporary key store gives TLS a real CNG key
                // which is removed when the certificate is disposed.
                server = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }

            serverKey.Dispose();
            serverKey = null;
            var certificates = new LocalCertificateChain(root, server);
            root = null;
            server = null;
            return certificates;
        }
        catch
        {
            root?.Dispose();
            server?.Dispose();
            serverKey?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Listener shutdown is best-effort; tests dispose the fixture after assertions
        }

        Task handlers = Task.WhenAll(_handlers.ToArray());
        bool handlersStopped = handlers.IsCompleted;
        try
        {
            await handlers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            handlersStopped = true;
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _lastServerError, "Timed out waiting for HTTPS fixture connections to stop.");
        }
        catch (Exception ex)
        {
            handlersStopped = true;
            Volatile.Write(ref _lastServerError, ex.ToString());
        }

        if (handlersStopped)
            DisposeResources();
        else
            _ = handlers.ContinueWith(
                static (_, state) => ((LocalHttpsFixture)state!).DisposeResources(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _hits);
                TcpClient accepted = client;
                client = null;
                Task handler = Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(accepted).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref _lastServerError, ex.ToString());
                    }
                });
                _handlers.Add(handler);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                return;
            }
            catch (Exception)
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (NetworkStream raw = client.GetStream())
        {
            if (Mode == FixtureMode.HangTls)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                return;
            }

            if (Mode == FixtureMode.PlainHttp)
            {
                byte[] plain = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
                await raw.WriteAsync(plain, _cts.Token).ConfigureAwait(false);
                return;
            }

            if (Mode == FixtureMode.CloseAfterAccept)
            {
                client.Close();
                return;
            }

            if (Mode == FixtureMode.Reset)
            {
                client.LingerState = new LingerOption(true, 0);
                client.Close();
                return;
            }

            if (_certificates is null)
            {
                return;
            }

            using var ssl = new SslStream(raw, false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificate = _certificates.Server
            }, _cts.Token).ConfigureAwait(false);

            if (Mode == FixtureMode.HangHeaders)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                return;
            }

            if (Mode == FixtureMode.SlowHeaders)
            {
                byte[] one = "H"u8.ToArray();
                for (int i = 0; i < 40; i++)
                {
                    await ssl.WriteAsync(one, _cts.Token).ConfigureAwait(false);
                    await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                }

                return;
            }

            if (Mode == FixtureMode.Http100ThenHang)
            {
                byte[] cont = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
                await ssl.WriteAsync(cont, _cts.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                return;
            }

            using var reader = new StreamReader(ssl, Encoding.ASCII, false, 1024, true);
            string? line;
            do
            {
                line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            }
            while (!string.IsNullOrEmpty(line));

            string response = BuildResponse();
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await ssl.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
    }

    private string BuildResponse()
    {
        if (Mode == FixtureMode.HugeHeaders)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("X-Big: ");
            sb.Append('A', 40 * 1024);
            sb.Append("\r\n\r\n");
            return sb.ToString();
        }

        if (Mode == FixtureMode.Redirect)
        {
            Interlocked.Increment(ref _redirectHits);
            return $"HTTP/1.1 302 Found\r\nLocation: {Location}\r\nContent-Length: 0\r\n\r\n";
        }

        if (Mode == FixtureMode.HeadWithBody)
        {
            return "HTTP/1.1 200 OK\r\nContent-Length: 999999999\r\n\r\n" + new string('x', 1024);
        }

        string extra = "";
        if (!string.IsNullOrEmpty(RetryAfter))
        {
            extra = "Retry-After: " + RetryAfter + "\r\n";
        }

        return $"HTTP/1.1 {Status} Test\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n";
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;

        _cts.Dispose();
        _certificates?.Dispose();
    }
}
