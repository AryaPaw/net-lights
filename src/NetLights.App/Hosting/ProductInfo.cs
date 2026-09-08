using System.Reflection;

namespace NetLights.App;

internal static class ProductInfo
{
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
