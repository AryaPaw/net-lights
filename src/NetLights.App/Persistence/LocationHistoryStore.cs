using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal static class LocationHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(SettingsStore.RootDirectory, "location-history.json");

    public static LocationHistory Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new LocationHistory();
            }

            LocationHistoryFile? file = JsonSerializer.Deserialize<LocationHistoryFile>(File.ReadAllText(FilePath), Options);
            if (file?.Stays is null)
            {
                return new LocationHistory();
            }

            var stays = new List<LocationStay>(file.Stays.Count);
            foreach (StayFile? row in file.Stays)
            {
                if (row is null
                    || !GeoCountryParsers.TryNormalizeIso3166Alpha2(row.Iso, out string? iso)
                    || row.StartedUtc == default)
                {
                    continue;
                }

                DateTimeOffset? ended = row.EndedUtc;
                if (ended is { } end && end < row.StartedUtc)
                {
                    continue;
                }

                stays.Add(new LocationStay(iso, row.StartedUtc, ended));
            }

            if (stays.Count > LocationHistory.MaxStays)
            {
                stays.RemoveRange(0, stays.Count - LocationHistory.MaxStays);
            }

            var history = new LocationHistory(stays, file.LastObservedUtc);
            if (history.LastObservedUtc is null)
            {
                history.Touch(File.GetLastWriteTimeUtc(FilePath));
            }

            history.Prune(DateTimeOffset.UtcNow);
            history.SplitIfStale(DateTimeOffset.UtcNow);
            return history;
        }
        catch (JsonException)
        {
            return new LocationHistory();
        }
        catch (IOException)
        {
            return new LocationHistory();
        }
        catch (UnauthorizedAccessException)
        {
            return new LocationHistory();
        }
    }

    public static void Save(LocationHistory history)
    {
        Directory.CreateDirectory(SettingsStore.RootDirectory);
        var file = new LocationHistoryFile
        {
            Version = 1,
            LastObservedUtc = history.LastObservedUtc,
            Stays = history.Stays.Select(stay => (StayFile?)new StayFile
            {
                Iso = stay.Iso,
                StartedUtc = stay.StartedUtc,
                EndedUtc = stay.EndedUtc
            }).ToList()
        };
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, FilePath, true);
    }

    private sealed class LocationHistoryFile
    {
        public int Version { get; set; }
        public DateTimeOffset? LastObservedUtc { get; set; }
        public List<StayFile?> Stays { get; set; } = [];
    }

    private sealed class StayFile
    {
        public string Iso { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? EndedUtc { get; set; }
    }
}
