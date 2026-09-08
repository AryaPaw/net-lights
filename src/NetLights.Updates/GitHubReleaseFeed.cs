using System.Security.Cryptography;
using System.Text.Json;

namespace NetLights.Updates;

public sealed class GitHubRelease
{
    public string TagName { get; init; } = "";
    public bool Prerelease { get; init; }
    public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
}

public sealed class GitHubAsset
{
    public string Name { get; init; } = "";
    public Uri? BrowserDownloadUrl { get; init; }
    public long Size { get; init; }
    public string? Digest { get; init; }
}

public sealed class GitHubReleaseFeed : IDisposable
{
    private readonly HttpClient _http;

    public GitHubReleaseFeed(HttpMessageHandler? handler = null)
    {
        HttpMessageHandler inner = handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        _http = new HttpClient(inner, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(UpdatePolicy.LatestApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is 404 or 429 or >= 500)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > UpdatePolicy.MaxManifestBytes)
        {
            throw new InvalidOperationException("Manifest too large.");
        }

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadCappedAsync(input, (int)UpdatePolicy.MaxManifestBytes, cancellationToken).ConfigureAwait(false);

        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsEl.EnumerateArray())
            {
                assets.Add(new GitHubAsset
                {
                    Name = asset.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement url)
                        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? parsed)
                        ? parsed
                        : null,
                    Size = asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0,
                    Digest = asset.TryGetProperty("digest", out JsonElement digest) ? digest.GetString() : null
                });
            }
        }

        return new GitHubRelease
        {
            TagName = root.GetProperty("tag_name").GetString() ?? "",
            Prerelease = root.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean(),
            Assets = assets
        };
    }

    public void Dispose() => _http.Dispose();

    internal static async Task<byte[]> ReadCappedAsync(Stream input, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 4096));
        byte[] buffer = new byte[4096];
        int total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("Manifest too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}

public static class IntegrityVerifier
{
    public static string Sha256Hex(Stream stream)
    {
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Matches(string expected, string actual)
    {
        string left = Normalize(expected);
        string right = Normalize(actual);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static string? DigestSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    private static string Normalize(string value)
    {
        string trimmed = value.Trim();
        return DigestSha256(trimmed) ?? trimmed;
    }
}
