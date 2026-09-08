using System.Text;
using System.Text.Json;
using NetLights.Core;

namespace NetLights.App;

internal static class StateHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string FilePath => Path.Combine(SettingsStore.RootDirectory, "state-history.jsonl");

    public static void Append(DateTimeOffset utc, GroupAvailability ru, GroupAvailability vpn)
    {
        Directory.CreateDirectory(SettingsStore.RootDirectory);
        var record = new HistoryRecord(utc, ru.ToString(), vpn.ToString());
        File.AppendAllText(FilePath, JsonSerializer.Serialize(record, Options) + Environment.NewLine, Encoding.UTF8);
        Prune();
    }

    public static void Prune()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - MonitorConstants.HistoryRetention;
        List<string> kept = [];
        int bytes = 0;
        foreach (string line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HistoryRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<HistoryRecord>(line, Options);
            }
            catch
            {
                continue;
            }

            if (record is null || record.Utc < cutoff)
            {
                continue;
            }

            kept.Add(line);
            bytes += Encoding.UTF8.GetByteCount(line) + 2;
        }

        while (kept.Count > MonitorConstants.MaxHistoryLines || bytes > MonitorConstants.MaxHistoryBytes)
        {
            bytes -= Encoding.UTF8.GetByteCount(kept[0]) + 2;
            kept.RemoveAt(0);
        }

        string tmp = FilePath + ".tmp";
        File.WriteAllLines(tmp, kept, Encoding.UTF8);
        File.Move(tmp, FilePath, true);
    }

    private sealed record HistoryRecord(DateTimeOffset Utc, string Ru, string Vpn);
}
