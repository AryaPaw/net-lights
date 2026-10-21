using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal enum HistoryLoadStatus { Loaded, Recovered, Absent, Corrupt }

internal static class LocationHistoryStore
{
    private const long MaxHistoryFileBytes = 64L * 1024 * 1024;
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
    public static HistoryLoadStatus LastLoadStatus { get; private set; } = HistoryLoadStatus.Absent;

    public static LocationHistory Load()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return RecoverBackup(path, HistoryLoadStatus.Absent);
            }

            LocationHistoryFile? file = ReadFile(path);
            if (file?.Stays is null)
            {
                throw new JsonException("Location history has no stays collection.");
            }

            _recoveredPath = null;
            LocationHistory history = BuildHistory(file, path);
            LastLoadStatus = HistoryLoadStatus.Loaded;
            return history;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
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
            catch (Exception backupException) when (backupException is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Leave both files intact; the caller can continue with an empty in-memory history.
            }

            LastLoadStatus = HistoryLoadStatus.Corrupt;
            return new LocationHistory();
        }
    }

    private static LocationHistory RecoverBackup(string path, HistoryLoadStatus fallbackStatus)
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep the backup intact; the app can still start with an empty in-memory history.
        }

        if (File.Exists(backupPath)) fallbackStatus = HistoryLoadStatus.Corrupt;
        _recoveredPath = fallbackStatus == HistoryLoadStatus.Corrupt ? path : null;
        LastLoadStatus = fallbackStatus;
        return new LocationHistory();
    }

    private static LocationHistory RestoreBackup(string path, LocationHistoryFile backup, string backupPath)
    {
        _recoveredPath = path;
        LocationHistory restored = BuildHistory(backup, backupPath);
        LastLoadStatus = HistoryLoadStatus.Recovered;
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
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxHistoryFileBytes)
            throw new InvalidDataException("Location history exceeds the supported file size.");
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Location history must be an object.");
        if ((root.TryGetProperty("observations", out JsonElement observations)
                && observations.ValueKind == JsonValueKind.Array && observations.GetArrayLength() > 70_000)
            || (root.TryGetProperty("stays", out JsonElement stays)
                && stays.ValueKind == JsonValueKind.Array && stays.GetArrayLength() > 70_000))
            throw new InvalidDataException("Location history exceeds the supported number of records.");
        return root.Deserialize<LocationHistoryFile>(Options);
    }

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
        if (file.Observations is not null)
        {
            foreach (LocationObservationFile? row in file.Observations)
            {
                if (row is null || row.ObservedUtc == default) continue;
                if (row.CountryCode is not null
                    && !GeoCountryParsers.TryNormalizeIso3166Alpha2(row.CountryCode, out _)) continue;
                history.NoteObservation(new LocationObservation(
                    row.ObservedUtc, row.PublicIpHash, row.CountryCode?.ToUpperInvariant(),
                    row.LocalCountry, row.OnlineCountry, row.ThirdCountry, row.DatabaseVersion,
                    row.PublicIpChanged, row.CountryChanged, row.ClassificationChanged, row.Confirmed,
                    row.PublicIp, row.ObservedUntilUtc, row.IplocateCountry, row.IplocateDatabaseVersion,
                    row.ResolutionSource));
            }
        }
        if (history.LastObservedUtc is null)
            history.Touch(File.GetLastWriteTimeUtc(sourcePath));

        history.Prune(DateTimeOffset.UtcNow);
        history.SplitIfStale(DateTimeOffset.UtcNow);
        return history;
    }

    public static LocationHistorySnapshot Snapshot(LocationHistory history)
        => new(Interlocked.Increment(ref _snapshotRevision), history.LastObservedUtc,
            history.Stays.ToArray(), history.Observations.ToArray());

    public static void Save(LocationHistory history) => Save(Snapshot(history));

    public static void Save(LocationHistorySnapshot snapshot)
        => Save(snapshot, Timeout.InfiniteTimeSpan);

    public static void Save(LocationHistorySnapshot snapshot, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            SaveGate.Wait();
        else if (!SaveGate.Wait(timeout))
            throw new TimeoutException("Timed out waiting to save location history.");
        try { Write(snapshot); }
        finally { SaveGate.Release(); }
    }

    public static Task SaveAsync(LocationHistorySnapshot snapshot)
        => SaveAsync(snapshot, Timeout.InfiniteTimeSpan);

    public static async Task SaveAsync(LocationHistorySnapshot snapshot, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            await SaveGate.WaitAsync().ConfigureAwait(false);
        else if (!await SaveGate.WaitAsync(timeout).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting to save location history.");
        try { await Task.Run(() => Write(snapshot)).ConfigureAwait(false); }
        finally { SaveGate.Release(); }
    }

    private static void Write(LocationHistorySnapshot snapshot)
    {
        string path = FilePath;
        if (string.Equals(path, _writtenPath, StringComparison.OrdinalIgnoreCase) && snapshot.Revision < _writtenRevision)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new LocationHistoryWriteFile
        {
            Version = 3,
            LastObservedUtc = snapshot.LastObservedUtc,
            Stays = ProjectStays(snapshot.Stays),
            Observations = ProjectObservations(snapshot.Observations)
        };
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, file, Options);
        }
        bool recoveredCurrent = string.Equals(path, _recoveredPath, StringComparison.OrdinalIgnoreCase);
        if (recoveredCurrent && LastLoadStatus == HistoryLoadStatus.Corrupt && File.Exists(path + ".bak"))
            File.Copy(path + ".bak", path + ".bak.corrupt-" + Guid.NewGuid().ToString("N") + ".json");
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

    private static IEnumerable<StayFile?> ProjectStays(IReadOnlyList<LocationStay> stays)
    {
        foreach (LocationStay stay in stays)
            yield return new StayFile { Iso = stay.Iso, StartedUtc = stay.StartedUtc, EndedUtc = stay.EndedUtc };
    }

    private static IEnumerable<LocationObservationFile?> ProjectObservations(IReadOnlyList<LocationObservation> observations)
    {
        foreach (LocationObservation observation in observations)
        {
            yield return new LocationObservationFile
            {
                ObservedUtc = observation.ObservedUtc,
                ObservedUntilUtc = observation.ObservedUntilUtc,
                PublicIpHash = observation.PublicIpHash,
                PublicIp = observation.PublicIp,
                CountryCode = observation.CountryCode,
                LocalCountry = observation.LocalCountry,
                OnlineCountry = observation.OnlineCountry,
                ThirdCountry = observation.ThirdCountry,
                DatabaseVersion = observation.DatabaseVersion,
                PublicIpChanged = observation.PublicIpChanged,
                CountryChanged = observation.CountryChanged,
                ClassificationChanged = observation.ClassificationChanged,
                Confirmed = observation.Confirmed,
                IplocateCountry = observation.IplocateCountry,
                IplocateDatabaseVersion = observation.IplocateDatabaseVersion,
                ResolutionSource = observation.ResolutionSource
            };
        }
    }

    private sealed class LocationHistoryFile
    {
        public int Version { get; set; }
        public DateTimeOffset? LastObservedUtc { get; set; }
        public List<StayFile?> Stays { get; set; } = [];
        public List<LocationObservationFile?>? Observations { get; set; }
    }

    private sealed class LocationHistoryWriteFile
    {
        public int Version { get; init; }
        public DateTimeOffset? LastObservedUtc { get; init; }
        public IEnumerable<StayFile?> Stays { get; init; } = [];
        public IEnumerable<LocationObservationFile?>? Observations { get; init; }
    }

    private sealed class StayFile
    {
        public string Iso { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? EndedUtc { get; set; }
    }

    private sealed class LocationObservationFile
    {
        public DateTimeOffset ObservedUtc { get; set; }
        public DateTimeOffset? ObservedUntilUtc { get; set; }
        public string? PublicIpHash { get; set; }
        public string? PublicIp { get; set; }
        public string? CountryCode { get; set; }
        public string? LocalCountry { get; set; }
        public string? OnlineCountry { get; set; }
        public string? ThirdCountry { get; set; }
        public string? DatabaseVersion { get; set; }
        public bool PublicIpChanged { get; set; }
        public bool CountryChanged { get; set; }
        public bool ClassificationChanged { get; set; }
        public bool Confirmed { get; set; }
        public string? IplocateCountry { get; set; }
        public string? IplocateDatabaseVersion { get; set; }
        public string? ResolutionSource { get; set; }
    }
}

internal sealed record LocationHistorySnapshot(
    long Revision,
    DateTimeOffset? LastObservedUtc,
    IReadOnlyList<LocationStay> Stays,
    IReadOnlyList<LocationObservation> Observations);
