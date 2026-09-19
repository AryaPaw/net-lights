using System.Reflection;
using NetLights.Updates;

namespace NetLights.App;

internal static class ProductInfo
{
    public const string Name = "Net Lights";

    public static string DisplayName(string? applicationDirectory = null)
    {
        string? dir = applicationDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.GetDirectoryName(Application.ExecutablePath);
        }

        if (string.IsNullOrWhiteSpace(dir) || !SilentUpdatePolicy.HasInnoUninstaller(dir))
        {
            return Name + " (локальная)";
        }

        return Name;
    }

    public static string Version
    {
        get
        {
            Assembly assembly = typeof(ProductInfo).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus >= 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }
}
