using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal static class AvailabilityHistoryStore
{
    private const long MaxHistoryFileBytes = 16L * 1024 * 1024;
    private const long MaxLegacyFileBytes = 512 * 1024;
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long _snapshotRevision;
    private static long _writtenRevision;
    private static string? _writtenPath;
    private static string? _recoveredPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(SettingsStore.RootDirectory, "availability-history.json");
    public static HistoryLoadStatus LastLoadStatus { get; private set; } = HistoryLoadStatus.Absent;
    public static AvailabilityHistory Load()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
                return Recover(path, HistoryLoadStatus.Absent);

            var file = ReadHistory(path);
            if (file?.Spans is null)
                throw new JsonException("Availability history has no spans collection.");
            var rows = file.Spans.Where(s => s is not null && Enum.IsDefined(s.Group) && Enum.IsDefined(s.State) && s.StartedUtc != default && (s.EndedUtc is null || s.EndedUtc >= s.StartedUtc)).Select(s => new AvailabilitySpan(s!.Group, s.State, s.Paused, s.StartedUtc, s.EndedUtc)).TakeLast(AvailabilityHistory.MaxSpans).ToArray();
            var history = new AvailabilityHistory(rows, file.LastObservedUtc);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            history.SealStale(now);
            history.Prune(now);
            _recoveredPath = null;
            LastLoadStatus = HistoryLoadStatus.Loaded;
            return history;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _recoveredPath = path;
            return Recover(path, HistoryLoadStatus.Corrupt);
        }
    }

    private static AvailabilityHistory Recover(string path, HistoryLoadStatus fallbackStatus)
    {
        _recoveredPath = path;
        string backupPath = path + ".bak";
        try
        {
            if (File.Exists(backupPath))
            {
                var backup = ReadHistory(backupPath);
                if (backup?.Spans is not null)
                {
                    AvailabilityHistory restored = BuildHistory(backup, DateTimeOffset.UtcNow);
                    LastLoadStatus = HistoryLoadStatus.Recovered;
                    _recoveredPath = path;
                    try { Save(restored); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    return restored;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Leave the backup untouched and try the legacy recovery source below.
        }

        AvailabilityHistory legacy = LoadLegacyStateHistory(saveCanonical: !File.Exists(path));
        LastLoadStatus = legacy.Spans.Count > 0 ? HistoryLoadStatus.Recovered : fallbackStatus;
        return legacy;
    }

    private static AvailabilityHistory BuildHistory(HistoryFile file, DateTimeOffset now)
    {
        AvailabilitySpan[] rows = file.Spans
            .Where(s => s is not null && Enum.IsDefined(s.Group) && Enum.IsDefined(s.State) && s.StartedUtc != default && (s.EndedUtc is null || s.EndedUtc >= s.StartedUtc))
            .Select(s => new AvailabilitySpan(s!.Group, s.State, s.Paused, s.StartedUtc, s.EndedUtc))
            .TakeLast(AvailabilityHistory.MaxSpans)
            .ToArray();
        var history = new AvailabilityHistory(rows, file.LastObservedUtc);
        history.SealStale(now);
        history.Prune(now);
        return history;
    }

    private static HistoryFile? ReadHistory(string path)
    {
        if (new FileInfo(path).Length > MaxHistoryFileBytes)
            throw new InvalidDataException("Availability history exceeds the supported file size.");
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Availability history must be an object.");
        if (root.TryGetProperty("spans", out JsonElement spans)
            && spans.ValueKind == JsonValueKind.Array
            && spans.GetArrayLength() > AvailabilityHistory.MaxSpans * 2)
            throw new InvalidDataException("Availability history exceeds the supported number of records.");
        return root.Deserialize<HistoryFile>(Options);
    }

    private static AvailabilityHistory LoadLegacyStateHistory(bool saveCanonical)
    {
        string path = Path.Combine(SettingsStore.RootDirectory, "state-history.jsonl");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxLegacyFileBytes)
                return new AvailabilityHistory();

            var records = new List<LegacyStateRecord>();
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    LegacyStateRecord? record = JsonSerializer.Deserialize<LegacyStateRecord>(line, Options);
                    if (record is not null && record.Utc != default)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    // A torn final line must not hide the valid history before it.
                }
            }

            if (records.Count == 0)
                return new AvailabilityHistory();

            records.Sort(static (left, right) => left.Utc.CompareTo(right.Utc));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset cutoff = now - AvailabilityHistory.Retention;
            var spans = new List<AvailabilitySpan>();
            var openIndexes = new Dictionary<EndpointGroup, int>();
            DateTimeOffset? lastObservedUtc = null;
            foreach (LegacyStateRecord record in records)
            {
                if (record.Utc < cutoff || record.Utc > now
                    || !TryParseState(record.Ru, out GroupAvailability ru)
                    || !TryParseState(record.World, out GroupAvailability world))
                {
                    continue;
                }

                AppendLegacyState(spans, openIndexes, EndpointGroup.Ru, ru, record.Utc);
                AppendLegacyState(spans, openIndexes, EndpointGroup.World, world, record.Utc);
                lastObservedUtc = record.Utc;
            }

            if (spans.Count == 0)
                return new AvailabilityHistory();

            var history = new AvailabilityHistory(spans, lastObservedUtc);
            history.SealStale(now);
            history.Prune(now);
            // Keep the old JSONL intact as a recovery source; the compact transition
            // log becomes canonical only after this successful migration.
            if (saveCanonical)
            {
                try { Save(history); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return history;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AvailabilityHistory();
        }
    }

    private static bool TryParseState(string? value, out GroupAvailability state)
        => Enum.TryParse(value, true, out state) && Enum.IsDefined(state);

    private static void AppendLegacyState(
        List<AvailabilitySpan> spans,
        Dictionary<EndpointGroup, int> openIndexes,
        EndpointGroup group,
        GroupAvailability state,
        DateTimeOffset observedUtc)
    {
        if (openIndexes.TryGetValue(group, out int index))
        {
            AvailabilitySpan current = spans[index];
            if (current.State == state)
                return;
            spans[index] = current with { EndedUtc = observedUtc };
        }

        openIndexes[group] = spans.Count;
        spans.Add(new AvailabilitySpan(group, state, false, observedUtc, null));
    }
    public static AvailabilityHistorySnapshot Snapshot(AvailabilityHistory history)
        => new(Interlocked.Increment(ref _snapshotRevision), history.LastObservedUtc, history.Spans.ToArray());

    public static void Save(AvailabilityHistory history) => Save(Snapshot(history));

    public static void Save(AvailabilityHistorySnapshot snapshot)
        => Save(snapshot, Timeout.InfiniteTimeSpan);

    public static void Save(AvailabilityHistorySnapshot snapshot, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            SaveGate.Wait();
        else if (!SaveGate.Wait(timeout))
            throw new TimeoutException("Timed out waiting to save availability history.");
        try { Write(snapshot); }
        finally { SaveGate.Release(); }
    }

    public static Task SaveAsync(AvailabilityHistorySnapshot snapshot)
        => SaveAsync(snapshot, Timeout.InfiniteTimeSpan);

    public static async Task SaveAsync(AvailabilityHistorySnapshot snapshot, TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            await SaveGate.WaitAsync().ConfigureAwait(false);
        else if (!await SaveGate.WaitAsync(timeout).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting to save availability history.");
        try { await Task.Run(() => Write(snapshot)).ConfigureAwait(false); }
        finally { SaveGate.Release(); }
    }

    private static void Write(AvailabilityHistorySnapshot snapshot)
    {
        string path = FilePath;
        if (string.Equals(path, _writtenPath, StringComparison.OrdinalIgnoreCase) && snapshot.Revision < _writtenRevision)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new HistoryWriteFile { LastObservedUtc = snapshot.LastObservedUtc, Spans = snapshot.Spans }, Options);
        }
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
                string backupTemp = path + ".bak.tmp";
                File.Copy(path, backupTemp, true);
                File.Move(backupTemp, path + ".bak", true);
            }
        }

        File.Move(temp, path, true);
        if (recoveredCurrent)
            _recoveredPath = null;
        _writtenPath = path;
        _writtenRevision = snapshot.Revision;
    }
    private sealed class HistoryFile { public DateTimeOffset? LastObservedUtc { get; set; } public List<AvailabilitySpan> Spans { get; set; } = []; }
    private sealed class HistoryWriteFile { public DateTimeOffset? LastObservedUtc { get; init; } public IEnumerable<AvailabilitySpan> Spans { get; init; } = []; }
    private sealed record LegacyStateRecord(DateTimeOffset Utc, string Ru, string World);
}

internal sealed record AvailabilityHistorySnapshot(long Revision, DateTimeOffset? LastObservedUtc, IReadOnlyList<AvailabilitySpan> Spans);
