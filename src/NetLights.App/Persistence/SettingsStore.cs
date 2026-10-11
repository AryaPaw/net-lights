using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal sealed class AppSettings
{
    public bool AutoStart { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool EmphasizeShortStatuses { get; set; } = true;
    public bool ShowLocationDebugSources { get; set; }
    public bool GeoCountryDetectionEnabled { get; set; }
    public bool GeoCountryIconEnabled { get; set; }
    public int GeoCountryLetterSize { get; set; } = (int)GeoCountryLetterScale.Regular;
    public int LocationChartWindowHours { get; set; } = 12;
    public int SettingsVersion { get; set; }
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = UiTheme.WindowDefaultWidth;
    public int WindowHeight { get; set; } = UiTheme.WindowDefaultHeight;
}

internal enum SettingsLoadStatus
{
    Loaded,
    Recovered,
    Absent,
    Corrupt,
    IoError
}

internal static class SettingsStore
{
    private const long MaxSettingsBytes = 1024 * 1024;
    private const long MaxEndpointPoolBytes = 1024 * 1024;
    private static string? _recoveredPath;
    private static string? _blockedPath;
    public static bool MigrateToSettingsVersion4(AppSettings settings)
    {
        if (settings.SettingsVersion >= 4)
            return false;

        if (settings.WindowWidth == 1040 && settings.WindowHeight == 920)
        {
            settings.WindowWidth = UiTheme.WindowDefaultWidth;
            settings.WindowHeight = UiTheme.WindowDefaultHeight;
        }

        if (settings.WindowX == 80 && settings.WindowY == 80)
        {
            settings.WindowX = int.MinValue;
            settings.WindowY = int.MinValue;
        }

        settings.SettingsVersion = 4;
        return true;
    }

    public static bool MigrateToSettingsVersion5(AppSettings settings)
    {
        if (settings.SettingsVersion >= 5)
            return false;

        settings.EmphasizeShortStatuses = true;
        settings.SettingsVersion = 5;
        return true;
    }

    public static bool MigrateToSettingsVersion6(AppSettings settings)
    {
        if (settings.SettingsVersion >= 6)
            return false;

        bool hasPreviousDefault = (settings.WindowWidth, settings.WindowHeight) is (900, 800)
            or (1040, 920)
            or (1360, 1177);
        if (hasPreviousDefault)
        {
            settings.WindowWidth = UiTheme.WindowDefaultWidth;
            settings.WindowHeight = UiTheme.WindowDefaultHeight;
        }

        settings.SettingsVersion = 6;
        return true;
    }

    public static bool MigrateToSettingsVersion7(AppSettings settings)
    {
        if (settings.SettingsVersion >= 7)
            return false;

        bool hasPreviousDefault = (settings.WindowWidth, settings.WindowHeight) is (900, 800)
            or (1040, 920)
            or (1360, 1177)
            or (1400, 1200)
            or (1421, 1273);
        if (hasPreviousDefault)
        {
            settings.WindowWidth = UiTheme.WindowDefaultWidth;
            settings.WindowHeight = UiTheme.WindowDefaultHeight;
        }

        settings.SettingsVersion = 7;
        return true;
    }

    public static bool MigrateToSettingsVersion8(AppSettings settings)
    {
        if (settings.SettingsVersion >= 8)
            return false;

        settings.ShowLocationDebugSources = false;
        settings.SettingsVersion = 8;
        return true;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetLights");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string EndpointsPath => Path.Combine(RootDirectory, "endpoints.json");

    public static AppSettings Load()
    {
        (AppSettings settings, _) = LoadDetailed();
        return settings;
    }

    public static (AppSettings Settings, SettingsLoadStatus Status) LoadDetailed()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return LoadBackupOrDefault(SettingsLoadStatus.Absent);
            }

            AppSettings? settings = ReadSettings(SettingsPath);
            if (settings is null)
            {
                return LoadBackupOrDefault(SettingsLoadStatus.Corrupt);
            }

            _blockedPath = null;
            _recoveredPath = null;

            Normalize(settings);
            return (settings, SettingsLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return LoadBackupOrDefault(SettingsLoadStatus.Corrupt);
        }
        catch (IOException)
        {
            return LoadBackupOrDefault(SettingsLoadStatus.IoError);
        }
        catch (UnauthorizedAccessException)
        {
            return LoadBackupOrDefault(SettingsLoadStatus.IoError);
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.WindowWidth < UiTheme.WindowMinWidth)
            settings.WindowWidth = UiTheme.WindowDefaultWidth;
        if (settings.WindowHeight < UiTheme.WindowMinHeight)
            settings.WindowHeight = UiTheme.WindowDefaultHeight;
        settings.GeoCountryLetterSize = (int)GeoCountryLetterScales.Parse(settings.GeoCountryLetterSize);
        settings.LocationChartWindowHours = LocationChartWindows.ToHours(
            LocationChartWindows.ParseHours(settings.LocationChartWindowHours));
    }

    private static AppSettings? ReadSettings(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxSettingsBytes)
            throw new JsonException("Settings file is too large.");
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || (!document.RootElement.TryGetProperty("settingsVersion", out _)
                && !document.RootElement.TryGetProperty("autoStart", out _)
                && !document.RootElement.TryGetProperty("windowWidth", out _)))
            throw new JsonException("Settings file has no recognized fields.");
        return JsonSerializer.Deserialize<AppSettings>(json, Options);
    }

    private static (AppSettings Settings, SettingsLoadStatus Status) LoadBackupOrDefault(SettingsLoadStatus failure)
    {
        string path = SettingsPath;
        try
        {
            AppSettings? backup = ReadSettings(path + ".bak");
            if (backup is not null)
            {
                Normalize(backup);
                _blockedPath = null;
                _recoveredPath = File.Exists(path) ? path : null;
                return (backup, SettingsLoadStatus.Recovered);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        _recoveredPath = null;
        _blockedPath = failure == SettingsLoadStatus.Absent ? null : path;
        return (new AppSettings(), failure);
    }

    public static void Save(AppSettings settings)
    {
        string path = SettingsPath;
        if (string.Equals(_blockedPath, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Settings are unreadable and no valid backup exists; original file was preserved.");
        Directory.CreateDirectory(RootDirectory);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        if (File.Exists(path))
        {
            if (string.Equals(_recoveredPath, path, StringComparison.OrdinalIgnoreCase))
            {
                string preserved = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".json";
                File.Copy(path, preserved);
            }
            else
            {
                string backupTemp = path + ".bak.tmp";
                File.Copy(path, backupTemp, true);
                File.Move(backupTemp, path + ".bak", true);
            }
        }
        File.Move(tmp, path, true);
        _recoveredPath = null;
    }

    public static (MonitorConfiguration Config, string? Warning) LoadPool()
    {
        if (!File.Exists(EndpointsPath))
        {
            return (BuiltinEndpoints.CreateDefault(), null);
        }

        try
        {
            if (new FileInfo(EndpointsPath).Length > MaxEndpointPoolBytes)
            {
                return (WarnBuiltin("Файл адресов превышает допустимый размер."),
                    "Файл адресов превышает 1 МиБ, используется встроенный набор.");
            }

            string json = File.ReadAllText(EndpointsPath);
            List<StoredEndpoint>? stored = JsonSerializer.Deserialize<List<StoredEndpoint>>(json, Options);
            if (stored is null)
            {
                return (WarnBuiltin("Файл адресов пуст."), "Файл адресов пуст, используется встроенный набор.");
            }

            var parsed = new List<EndpointDefinition>();
            foreach (StoredEndpoint item in stored)
            {
                if (!TryParseGroup(item.Group, out EndpointGroup group))
                {
                    return (WarnBuiltin("Неизвестная группа."), "Неизвестная группа в файле адресов, используется встроенный набор.");
                }

                parsed.Add(new EndpointDefinition(item.Id, group, new Uri(item.Uri), item.InfrastructureId));
            }

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

    private static bool TryParseGroup(string value, out EndpointGroup group)
    {
        if (value.Equals("ru", StringComparison.OrdinalIgnoreCase) || value.Equals("рф", StringComparison.OrdinalIgnoreCase))
        {
            group = EndpointGroup.Ru;
            return true;
        }

        if (value.Equals("world", StringComparison.OrdinalIgnoreCase))
        {
            group = EndpointGroup.World;
            return true;
        }

        group = EndpointGroup.Ru;
        return false;
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
