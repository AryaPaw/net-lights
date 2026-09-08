namespace NetLights.Updates;

public sealed class UpdatePolicy
{
    public const string Owner = "AryaPaw";
    public const string Repository = "net-lights";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public const long MaxManifestBytes = 64 * 1024;
    public const long MaxInstallerBytes = 80 * 1024 * 1024;

    public static Uri LatestApi { get; } = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    public static bool IsAllowedAssetUrl(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = url.Host;
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = url.AbsolutePath;
        return path.Contains($"/{Owner}/{Repository}/", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
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
