using System.Text.Json;
using System.Text.Json.Serialization;
using NetLights.Core;

namespace NetLights.App;

internal static class AvailabilityHistoryStore
{
    private const long MaxLegacyFileBytes = 512 * 1024;
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static long _snapshotRevision;
    private static long _writtenRevision;
    private static string? _writtenPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public static string FilePath => Path.Combine(SettingsStore.RootDirectory, "availability-history.json");
    public static AvailabilityHistory Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return LoadLegacyStateHistory(saveCanonical: true);
            var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FilePath), Options);
            if (file?.Spans is null) return LoadLegacyStateHistory(saveCanonical: false);
            var rows = file.Spans.Where(s => s is not null && Enum.IsDefined(s.Group) && Enum.IsDefined(s.State) && s.StartedUtc != default && (s.EndedUtc is null || s.EndedUtc >= s.StartedUtc)).Select(s => new AvailabilitySpan(s!.Group, s.State, s.Paused, s.StartedUtc, s.EndedUtc)).TakeLast(AvailabilityHistory.MaxSpans).ToArray();
            var history = new AvailabilityHistory(rows, file.LastObservedUtc);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            history.SealStale(now);
            history.Prune(now);
            return history;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return LoadLegacyStateHistory(saveCanonical: false); }
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
    {
        SaveGate.Wait();
        try { Write(snapshot); }
        finally { SaveGate.Release(); }
    }

    public static async Task SaveAsync(AvailabilityHistorySnapshot snapshot)
    {
        await SaveGate.WaitAsync().ConfigureAwait(false);
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
        File.WriteAllText(temp, JsonSerializer.Serialize(new HistoryFile { LastObservedUtc = snapshot.LastObservedUtc, Spans = snapshot.Spans.ToList() }, Options));
        File.Move(temp, path, true);
        _writtenPath = path;
        _writtenRevision = snapshot.Revision;
    }
    private sealed class HistoryFile { public DateTimeOffset? LastObservedUtc { get; set; } public List<AvailabilitySpan> Spans { get; set; } = []; }
    private sealed record LegacyStateRecord(DateTimeOffset Utc, string Ru, string World);
}

internal sealed record AvailabilityHistorySnapshot(long Revision, DateTimeOffset? LastObservedUtc, IReadOnlyList<AvailabilitySpan> Spans);
