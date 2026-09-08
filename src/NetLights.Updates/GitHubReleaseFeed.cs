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

public sealed class GitHubReleaseFeed
{
    private readonly HttpClient _http;

    public GitHubReleaseFeed(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetLights-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<GitHubRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(UpdatePolicy.LatestApi, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is 404 or 429 or >= 500)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > UpdatePolicy.MaxManifestBytes)
        {
            throw new InvalidOperationException("Manifest too large.");
        }

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
            : digest;
    }

    private static string Normalize(string value)
    {
        string trimmed = value.Trim();
        return DigestSha256(trimmed) ?? trimmed;
    }
}
