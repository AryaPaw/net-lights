namespace NetLights.App;

internal static class AppBranding
{
    private const string IconResourceSuffix = ".Assets.app.ico";

    public static Icon? LoadWindowIcon()
    {
        string? name = typeof(AppBranding).Assembly.GetManifestResourceNames()
            .FirstOrDefault(item => item.EndsWith(IconResourceSuffix, StringComparison.Ordinal));
        if (name is null)
        {
            return null;
        }

        try
        {
            using Stream? stream = typeof(AppBranding).Assembly.GetManifestResourceStream(name);
            return stream is null ? null : new Icon(stream);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
