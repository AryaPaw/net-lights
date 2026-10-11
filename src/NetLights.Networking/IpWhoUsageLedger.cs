using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetLights.Networking;

internal sealed class IpWhoUsageLedger
{
    public const int DailyLimit = 500;
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxEntries = 2048;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateOnly Date, int Count)> _counts = new(StringComparer.Ordinal);
    private bool _ledgerHealthy = true;

    public IpWhoUsageLedger(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "GeoIP", "ipwho-usage.json");
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxFileBytes)
            {
                _ledgerHealthy = false;
                return;
            }
            UsageFile? data = JsonSerializer.Deserialize<UsageFile>(stream, new JsonSerializerOptions { MaxDepth = 8 });
            if (data?.Entries is null || data.Entries.Count > MaxEntries)
            {
                _ledgerHealthy = false;
                return;
            }
            foreach (UsageEntry? entry in data.Entries)
            {
                if (entry is not null && IsHash(entry.EgressHash) && entry.Count is >= 0 and <= DailyLimit
                    && DateOnly.TryParseExact(entry.UtcDate, "yyyy-MM-dd", out DateOnly date))
                    _counts[entry.EgressHash] = (date, entry.Count);
                else
                {
                    _ledgerHealthy = false;
                    _counts.Clear();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Do not call the third-party service when persisted usage cannot be trusted.
            _ledgerHealthy = false;
        }
    }

    public bool TryConsume(IPAddress? egressIp)
    {
        _ = egressIp;
        lock (_gate)
        {
            if (!_ledgerHealthy) return false;
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
            long current = 0;
            foreach ((DateOnly date, int count) in _counts.Values)
            {
                if (date != today)
                    continue;
                current += count;
                if (current >= DailyLimit)
                    return false;
            }
            // Merge old per-egress accounting into a single per-install daily budget.
            foreach (string key in _counts.Where(pair => pair.Value.Date == today).Select(pair => pair.Key).ToArray())
                _counts.Remove(key);
            _counts[Hash("application-budget")] = (today, checked((int)current + 1));
            return SaveLocked();
        }
    }

    public static string Hash(IPAddress address) => Hash(address.ToString());

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private bool SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            var data = new UsageFile
            {
                Entries = _counts.Where(pair => pair.Value.Date >= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1))
                    .Select(pair => (UsageEntry?)new UsageEntry
                    {
                        EgressHash = pair.Key,
                        UtcDate = pair.Value.Date.ToString("yyyy-MM-dd"),
                        Count = pair.Value.Count
                    }).ToList()
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ledgerHealthy = false;
            return false;
        }
    }

    private static bool IsHash(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed class UsageFile
    {
        public List<UsageEntry?>? Entries { get; set; }
    }

    private sealed class UsageEntry
    {
        public string EgressHash { get; set; } = "";
        public string UtcDate { get; set; } = "";
        public int Count { get; set; }
    }
}
