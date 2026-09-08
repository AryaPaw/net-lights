namespace NetLights.Updates;

public sealed class UpdatePolicy
{
    public const string Owner = "AryaPaw";
    public const string Repository = "net-lights";
    public const long MaxManifestBytes = 64 * 1024;
    public const long MaxInstallerBytes = 80 * 1024 * 1024;

    public static Uri LatestApi { get; } = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    public static bool IsAllowedAssetUrl(Uri url) => IsGitHubReleaseDownload(url);

    public static bool IsAllowedRedirectUrl(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (url.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsGitHubReleaseDownload(url);
    }

    public static string? SafeInstallerFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return null;
        }

        string file = Path.GetFileName(name);
        if (!string.Equals(file, name, StringComparison.Ordinal)
            || file.Contains("..", StringComparison.Ordinal)
            || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        if (!file.StartsWith("NetLights-Setup-win-x64-", StringComparison.OrdinalIgnoreCase)
            || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return file;
    }

    private static bool IsGitHubReleaseDownload(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps
            || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string prefix = $"/{Owner}/{Repository}/releases/download/";
        string path = url.AbsolutePath;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !path.Contains("..", StringComparison.Ordinal)
            && path.Length > prefix.Length;
    }

    public static bool IsInsideRoot(string root, string candidate)
    {
        string rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(candidate);
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewerStable(string current, string candidate, bool prerelease)
    {
        if (prerelease)
        {
            return false;
        }

        if (!Version.TryParse(Normalize(current), out Version? currentVersion))
        {
            return false;
        }

        if (!Version.TryParse(Normalize(candidate), out Version? next))
        {
            return false;
        }

        return next > currentVersion;
    }

    public static string Normalize(string version)
    {
        string value = version.Trim();
        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            value = value[1..];
        }

        int plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return value;
    }
}
