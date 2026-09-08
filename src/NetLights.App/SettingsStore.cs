using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal sealed class AppSettings
{
    public bool AutoStart { get; set; }
    public int WindowX { get; set; } = 80;
    public int WindowY { get; set; } = 80;
    public int WindowWidth { get; set; } = 1040;
    public int WindowHeight { get; set; } = 720;
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetLights");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string EndpointsPath => Path.Combine(RootDirectory, "endpoints.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
            if (settings.WindowWidth < 980)
            {
                settings.WindowWidth = 1040;
            }

            if (settings.WindowHeight < 680)
            {
                settings.WindowHeight = 720;
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(RootDirectory);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, SettingsPath, true);
    }

    public static (MonitorConfiguration Config, string? Warning) LoadPool()
    {
        if (!File.Exists(EndpointsPath))
        {
            return (BuiltinEndpoints.CreateDefault(), null);
        }

        try
        {
            string json = File.ReadAllText(EndpointsPath);
            List<StoredEndpoint>? stored = JsonSerializer.Deserialize<List<StoredEndpoint>>(json, Options);
            if (stored is null)
            {
                return (WarnBuiltin("Файл адресов пуст."), "Файл адресов пуст, используется встроенный набор.");
            }

            var parsed = stored.Select(s => new EndpointDefinition(
                s.Id,
                s.Group.Equals("vpn", StringComparison.OrdinalIgnoreCase) ? EndpointGroup.Vpn : EndpointGroup.Ru,
                new Uri(s.Uri),
                s.InfrastructureId)).ToList();
            EndpointPoolValidation validation = EndpointPoolValidator.Validate(parsed);
            if (!validation.IsValid)
            {
                return (WarnBuiltin(validation.Error!), validation.Error + " Используется встроенный набор.");
            }

            return (new MonitorConfiguration { Endpoints = parsed, UsingBuiltinPool = false }, null);
        }
        catch (Exception ex)
        {
            return (WarnBuiltin(ex.Message), "Некорректный файл адресов, используется встроенный набор.");
        }
    }

    private static MonitorConfiguration WarnBuiltin(string warning)
        => new()
        {
            Endpoints = BuiltinEndpoints.All,
            UsingBuiltinPool = true,
            ConfigWarning = warning
        };

    private sealed class StoredEndpoint
    {
        public string Id { get; set; } = "";
        public string Group { get; set; } = "";
        public string Uri { get; set; } = "";
        public string InfrastructureId { get; set; } = "";
    }
}
