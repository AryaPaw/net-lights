using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal static class LocationHistoryStore
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long _snapshotRevision;
    private static long _writtenRevision;
    private static string? _writtenPath;
    private static string? _recoveredPath;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(SettingsStore.RootDirectory, "location-history.json");

    public static LocationHistory Load()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return RecoverBackup(path);
            }

            LocationHistoryFile? file = ReadFile(path);
            if (file?.Stays is null)
            {
                throw new JsonException("Location history has no stays collection.");
            }

            _recoveredPath = null;
            return BuildHistory(file, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _recoveredPath = path;
            string backupPath = path + ".bak";
            try
            {
                LocationHistoryFile? backup = ReadFile(backupPath);
                if (backup?.Stays is not null)
                {
                    return RestoreBackup(path, backup, backupPath);
                }
            }
            catch (Exception backupException) when (backupException is JsonException or IOException or UnauthorizedAccessException)
            {
                // Leave both files intact; the caller can continue with an empty in-memory history.
            }

            return new LocationHistory();
        }
    }

    private static LocationHistory RecoverBackup(string path)
    {
        string backupPath = path + ".bak";
        try
        {
            LocationHistoryFile? backup = ReadFile(backupPath);
            if (backup?.Stays is not null)
            {
                return RestoreBackup(path, backup, backupPath);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the backup intact; the app can still start with an empty in-memory history.
        }

        _recoveredPath = null;
        return new LocationHistory();
    }

    private static LocationHistory RestoreBackup(string path, LocationHistoryFile backup, string backupPath)
    {
        _recoveredPath = path;
        LocationHistory restored = BuildHistory(backup, backupPath);
        try
        {
            Save(restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep serving the recovered in-memory history; retry persistence on the next save.
        }

        return restored;
    }

    private static LocationHistoryFile? ReadFile(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<LocationHistoryFile>(File.ReadAllText(path), Options) : null;

    private static LocationHistory BuildHistory(LocationHistoryFile file, string sourcePath)
    {
        var stays = new List<LocationStay>(file.Stays!.Count);
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

        var history = new LocationHistory(stays, file.LastObservedUtc);
        if (history.LastObservedUtc is null)
            history.Touch(File.GetLastWriteTimeUtc(sourcePath));

        history.Prune(DateTimeOffset.UtcNow);
        history.SplitIfStale(DateTimeOffset.UtcNow);
        return history;
    }

    public static LocationHistorySnapshot Snapshot(LocationHistory history)
        => new(Interlocked.Increment(ref _snapshotRevision), history.LastObservedUtc, history.Stays.ToArray());

    public static void Save(LocationHistory history) => Save(Snapshot(history));

    public static void Save(LocationHistorySnapshot snapshot)
    {
        SaveGate.Wait();
        try { Write(snapshot); }
        finally { SaveGate.Release(); }
    }

    public static async Task SaveAsync(LocationHistorySnapshot snapshot)
    {
        await SaveGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => Write(snapshot)).ConfigureAwait(false); }
        finally { SaveGate.Release(); }
    }

    private static void Write(LocationHistorySnapshot snapshot)
    {
        string path = FilePath;
        if (string.Equals(path, _writtenPath, StringComparison.OrdinalIgnoreCase) && snapshot.Revision < _writtenRevision)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new LocationHistoryFile
        {
            Version = 1,
            LastObservedUtc = snapshot.LastObservedUtc,
            Stays = snapshot.Stays.Select(stay => (StayFile?)new StayFile
            {
                Iso = stay.Iso,
                StartedUtc = stay.StartedUtc,
                EndedUtc = stay.EndedUtc
            }).ToList()
        };
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        bool recoveredCurrent = string.Equals(path, _recoveredPath, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            if (recoveredCurrent)
            {
                string preservedPath = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".json";
                File.Copy(path, preservedPath);
            }
            else
            {
                string backupTmp = path + ".bak.tmp";
                File.Copy(path, backupTmp, true);
                File.Move(backupTmp, path + ".bak", true);
            }
        }
        File.Move(tmp, path, true);
        if (recoveredCurrent)
            _recoveredPath = null;
        _writtenPath = path;
        _writtenRevision = snapshot.Revision;
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

internal sealed record LocationHistorySnapshot(long Revision, DateTimeOffset? LastObservedUtc, IReadOnlyList<LocationStay> Stays);
