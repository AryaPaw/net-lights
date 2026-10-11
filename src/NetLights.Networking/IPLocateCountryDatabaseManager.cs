using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NetLights.Networking;

/// <summary>Loads the comparison-only IPLocate mirror locally; it never sends an IP to IPLocate.</summary>
internal sealed class IPLocateCountryDatabaseManager : IDisposable
{
    private static readonly Uri PointerUri = new("https://raw.githubusercontent.com/iplocate/ip-address-databases/main/ip-to-country/ip-to-country.csv.zip");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private readonly string _path;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPLocateCountryDatabase? _database;
    private string? _version;
    private string? _contentHash;
    private DateTimeOffset _lastRefresh;
    private int _started;
    private int _initialLoadFinished;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IPLocateCountryDatabaseManager(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "GeoIP-Compare", "IPLocate-country.csv.zip");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights/1.5 GeoIP comparison");
    }

    internal string? Version => Volatile.Read(ref _version);
    internal Task Ready => _ready.Task;

    internal string? Lookup(System.Net.IPAddress ip) => Volatile.Read(ref _database)?.Lookup(ip);

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(LoadAndRefreshAsync);
    }

    internal void CheckForUpdate()
    {
        if (Volatile.Read(ref _initialLoadFinished) != 0)
            _ = RefreshAsync(force: false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _http.Dispose();
    }

    private async Task LoadAndRefreshAsync()
    {
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length is > 0 and <= IPLocateCountryDatabase.MaximumArchiveBytes)
            {
                byte[] bytes = await File.ReadAllBytesAsync(_path, _shutdown.Token).ConfigureAwait(false);
                IPLocateCountryDatabase database = await Task.Run(() => IPLocateCountryDatabase.ParseArchive(bytes), _shutdown.Token).ConfigureAwait(false);
                Volatile.Write(ref _database, database);
                string localHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Volatile.Write(ref _contentHash, localHash);
                Volatile.Write(ref _version, "Git LFS · SHA-256 " + localHash[..12]);
                _lastRefresh = File.GetLastWriteTimeUtc(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException) { }

        try { await RefreshAsync(force: _database is null).ConfigureAwait(false); }
        finally
        {
            Volatile.Write(ref _initialLoadFinished, 1);
            _ready.TrySetResult();
        }
    }

    private async Task RefreshAsync(bool force)
    {
        if (_shutdown.IsCancellationRequested || (!force && DateTimeOffset.UtcNow - _lastRefresh < RefreshInterval)) return;
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        if (_shutdown.IsCancellationRequested)
        {
            _gate.Release();
            return;
        }
        string? temporary = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            (Uri mediaUri, long expectedSize, string expectedHash) = await GetLfsPointerAsync(deadline.Token).ConfigureAwait(false);
            if (expectedSize is <= 0 or > IPLocateCountryDatabase.MaximumArchiveBytes)
                throw new InvalidDataException("IPLocate archive size is outside the permitted range.");
            if (string.Equals(Volatile.Read(ref _contentHash), expectedHash, StringComparison.Ordinal))
            {
                _lastRefresh = DateTimeOffset.UtcNow;
                return;
            }
            byte[] archive = await DownloadArchiveAsync(mediaUri, expectedSize, deadline.Token).ConfigureAwait(false);
            string downloadedHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(downloadedHash)))
                throw new InvalidDataException("IPLocate archive SHA-256 does not match the Git LFS pointer.");
            IPLocateCountryDatabase parsed = await Task.Run(() => IPLocateCountryDatabase.ParseArchive(archive), deadline.Token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + ".tmp";
            await File.WriteAllBytesAsync(temporary, archive, deadline.Token).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Volatile.Write(ref _database, parsed);
            Volatile.Write(ref _contentHash, downloadedHash);
            Volatile.Write(ref _version, "Git LFS · SHA-256 " + downloadedHash[..12]);
            _lastRefresh = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or TaskCanceledException or ObjectDisposedException)
        {
            // Candidate source is optional; retain the last validated copy and never affect main resolution.
            _lastRefresh = DateTimeOffset.UtcNow - RefreshInterval + TimeSpan.FromHours(1);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    private async Task<(Uri MediaUri, long Size, string Hash)> GetLfsPointerAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PointerUri);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(finalUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected Git LFS pointer host.");
        if (response.Content.Headers.ContentLength is long length && length is <= 0 or > 512)
            throw new InvalidDataException("Git LFS pointer size is invalid.");
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] pointerBytes = await ReadBoundedAsync(stream, 512, cancellationToken).ConfigureAwait(false);
        string pointer = System.Text.Encoding.UTF8.GetString(pointerBytes);
        Match sha = Regex.Match(pointer, "(?m)^oid sha256:([0-9a-f]{64})$");
        Match size = Regex.Match(pointer, "(?m)^size ([0-9]+)$");
        if (!pointer.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal)
            || !sha.Success || !size.Success || !long.TryParse(size.Groups[1].Value, out long parsedSize))
            throw new InvalidDataException("Git LFS pointer does not contain valid SHA-256 and size fields.");
        Uri mediaUri = new("https://media.githubusercontent.com/media/iplocate/ip-address-databases/main/ip-to-country/ip-to-country.csv.zip");
        return (mediaUri, parsedSize, sha.Groups[1].Value);
    }

    private async Task<byte[]> DownloadArchiveAsync(Uri uri, long expectedSize, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "media.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected IPLocate media host.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(finalUri.Host, "media.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unexpected IPLocate archive host.");
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("IPLocate archive size does not match the LFS pointer.");
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(input, IPLocateCountryDatabase.MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != expectedSize) throw new InvalidDataException("IPLocate archive size does not match the LFS pointer.");
        return bytes;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024));
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("IPLocate download exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
