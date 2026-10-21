using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetLights.Networking;

internal sealed class V2FlyGeoIpDatabaseManager : IGeoIpDatabaseProvider, IDisposable
{
    private const string ReleaseApi = "https://api.github.com/repos/v2fly/geoip/releases/latest";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);
    private readonly string _databasePath;
    private readonly string _versionPath;
    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly TimeSpan _requestDeadline;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private V2FlyGeoIpDatabase? _current;
    private string? _version;
    private string? _lastFailure;
    private DateTimeOffset _lastCheck;
    private DateTimeOffset _retryAfter;
    private int _started;
    private int _initialLoadFinished;
    private int _disposed;

    public V2FlyGeoIpDatabaseManager(string dataDirectory, TimeProvider? time = null, HttpMessageHandler? handler = null,
        TimeSpan? requestDeadline = null)
    {
        string directory = Path.Combine(dataDirectory, "GeoIP");
        _databasePath = Path.Combine(directory, "geoip.dat");
        _versionPath = Path.Combine(directory, "version.txt");
        _time = time ?? TimeProvider.System;
        _requestDeadline = requestDeadline ?? RequestDeadline;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectCallback = (context, token) => NetworkTraffic.ConnectAsync(context, token, "База V2Fly")
        }, disposeHandler: true);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights/1.0 GeoIP database updater");
    }

    public string? Version => Volatile.Read(ref _version);

    internal string? LastFailure => Volatile.Read(ref _lastFailure);

    public V2FlyGeoIpDatabase? Current => Volatile.Read(ref _current);

    public void CheckForUpdate()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // Start() owns the initial load and download. A concurrent forced refresh here
        // can fetch and parse the same large release while the local file is loading.
        if (Volatile.Read(ref _initialLoadFinished) == 0) return;
        _ = RefreshIfDueAsync(force: false);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(LoadAndUpdateAsync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _client.Dispose();
    }

    private async Task LoadAndUpdateAsync()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                byte[] stored = await ReadLocalDatabaseAsync(_databasePath, _shutdown.Token).ConfigureAwait(false);
                V2FlyGeoIpDatabase database = V2FlyGeoIpDatabase.Parse(stored);
                if (File.Exists(_versionPath))
                    Volatile.Write(ref _version, Encoding.UTF8.GetString(await ReadLocalDatabaseAsync(_versionPath, _shutdown.Token, 1024).ConfigureAwait(false)).Trim());
                // Current is the readiness signal for consumers. Publish it only after
                // the paired version metadata has also been loaded.
                Volatile.Write(ref _current, database);
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && (ex is OperationCanceledException or ObjectDisposedException))
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A corrupt or unreadable local file is kept for diagnosis; a fresh validated copy is fetched below.
        }

        try
        {
            if (!_shutdown.IsCancellationRequested)
                await RefreshIfDueAsync(force: Current is null).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _initialLoadFinished, 1);
        }
    }

    internal static async Task<byte[]> ReadLocalDatabaseAsync(string path, CancellationToken cancellationToken,
        int maximumBytes = V2FlyGeoIpDatabase.MaximumFileBytes)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The local GeoIP database size is outside the allowed range.");
        byte[] bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    internal async Task RefreshIfDueAsync(bool force)
    {
        if (_shutdown.IsCancellationRequested) return;
        DateTimeOffset now = _time.GetUtcNow();
        if (now < _retryAfter || (!force && now - _lastCheck < RefreshInterval)) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_shutdown.IsCancellationRequested) return;
            now = _time.GetUtcNow();
            if (now < _retryAfter || (!force && now - _lastCheck < RefreshInterval)) return;
            using JsonDocument release = await GetJsonAsync(new Uri(ReleaseApi)).ConfigureAwait(false);
            JsonElement root = release.RootElement;
            string tag = RequiredString(root, "tag_name");
            JsonElement assets = root.GetProperty("assets");
            Uri dataUri = FindAsset(assets, "geoip.dat", out long declaredSize);
            Uri checksumUri = FindAsset(assets, "geoip.dat.sha256sum", out _);
            if (declaredSize is <= 0 or > V2FlyGeoIpDatabase.MaximumFileBytes)
                throw new InvalidDataException("The published GeoIP database size is outside the allowed range.");
            if (string.Equals(tag, Version, StringComparison.Ordinal) && Current is not null)
            {
                _lastCheck = _time.GetUtcNow();
                _retryAfter = default;
                return;
            }

            byte[] checksumBytes = await DownloadBoundedAsync(checksumUri, 512).ConfigureAwait(false);
            string expectedHash = ParseChecksum(checksumBytes);
            byte[] databaseBytes = await DownloadBoundedAsync(dataUri, V2FlyGeoIpDatabase.MaximumFileBytes).ConfigureAwait(false);
            if (databaseBytes.LongLength != declaredSize)
                throw new InvalidDataException("The downloaded GeoIP database size does not match the release metadata.");
            string actualHash = Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
                throw new InvalidDataException("The GeoIP database checksum does not match the published checksum.");

            V2FlyGeoIpDatabase database = V2FlyGeoIpDatabase.Parse(databaseBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            string temporaryPath = _databasePath + ".tmp";
            string versionTemporaryPath = _versionPath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, databaseBytes, _shutdown.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(versionTemporaryPath, tag, _shutdown.Token).ConfigureAwait(false);
            _shutdown.Token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _databasePath, overwrite: true);
            File.Move(versionTemporaryPath, _versionPath, overwrite: true);
            Volatile.Write(ref _version, tag);
            Volatile.Write(ref _current, database);
            _lastCheck = _time.GetUtcNow();
            _retryAfter = default;
            Volatile.Write(ref _lastFailure, null);
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && (ex is OperationCanceledException or ObjectDisposedException))
        {
            // Closing the app cancels an in-flight download without changing the last good database.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or FormatException or KeyNotFoundException
            or InvalidOperationException or TaskCanceledException)
        {
            // Keep serving the previous parsed database; retry on a later network signal or restart.
            Volatile.Write(ref _lastFailure, ex.ToString());
            _retryAfter = _time.GetUtcNow() + (ex is InvalidDataException or FormatException
                ? RefreshInterval : TimeSpan.FromMinutes(5));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(_requestDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 512 * 1024)
            throw new InvalidDataException("The GitHub release metadata is too large.");
        byte[] bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), 512 * 1024, deadline.Token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private async Task<byte[]> DownloadBoundedAsync(Uri uri, int maximumBytes)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/repos/v2fly/geoip/releases/assets/", StringComparison.Ordinal))
            throw new InvalidDataException("The release asset URL is not an approved HTTPS host.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(_requestDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && (size <= 0 || size > maximumBytes))
            throw new InvalidDataException("The release asset size is outside the allowed range.");
        return await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), maximumBytes, deadline.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 1024 * 1024));
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes) throw new InvalidDataException("The downloaded file exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri FindAsset(JsonElement assets, string name, out long size)
    {
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!string.Equals(RequiredString(asset, "name"), name, StringComparison.Ordinal)) continue;
            size = asset.GetProperty("size").GetInt64();
            return new Uri(RequiredString(asset, "url"));
        }
        throw new InvalidDataException("Required GeoIP asset is missing from the GitHub release.");
    }

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidDataException($"GitHub release field {name} is empty.")
            : throw new InvalidDataException($"GitHub release field {name} is missing.");

    private static string ParseChecksum(byte[] bytes)
    {
        string text = System.Text.Encoding.ASCII.GetString(bytes).Trim();
        string hash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The published GeoIP checksum is invalid.");
        return hash.ToLowerInvariant();
    }
}
