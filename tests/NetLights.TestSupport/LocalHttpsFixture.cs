using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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

public sealed class LocalHttpsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly X509Certificate2? _serverCert;

    public LocalHttpsFixture(FixtureMode mode, X509Certificate2? serverCert, int status = 200, string? retryAfter = null, string? location = "/other")
    {
        Mode = mode;
        Status = status;
        RetryAfter = retryAfter;
        Location = location;
        _serverCert = serverCert;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Hits = 0;
        RedirectHits = 0;
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public FixtureMode Mode { get; }
    public int Status { get; }
    public string? RetryAfter { get; }
    public string? Location { get; }
    public int Hits { get; private set; }
    public int RedirectHits { get; private set; }
    public Uri Uri => new($"https://127.0.0.1:{Port}/");

    public static X509Certificate2 CreateCertificate(string cn, DateTimeOffset notBefore, DateTimeOffset notAfter, string? sanDns = null, bool includeLoopback = true)
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + cn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(sanDns ?? cn);
        if (includeLoopback)
        {
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
        }

        req.CertificateExtensions.Add(san.Build());
        X509Certificate2 cert = req.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
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

        _cts.Dispose();
        _serverCert?.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Hits++;
                _ = Task.Run(() => HandleAsync(client));
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

            if (_serverCert is null)
            {
                return;
            }

            using var ssl = new SslStream(raw, false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
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
            RedirectHits++;
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
}
