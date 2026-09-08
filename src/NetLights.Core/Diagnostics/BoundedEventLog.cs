using System.Text;

namespace NetLights.Core;

public sealed class BoundedEventLog
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _gate = new();
    private int _bytes;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Add(DateTimeOffset utc, string code, string message)
    {
        string safe = Truncate(Sanitize(message), 240);
        var entry = new LogEntry(utc, code, safe);
        int size = Encoding.UTF8.GetByteCount(entry.Code) + Encoding.UTF8.GetByteCount(entry.Message) + 32;
        lock (_gate)
        {
            _entries.Enqueue(entry);
            _bytes += size;
            while (_entries.Count > 0)
            {
                LogEntry oldest = _entries.Peek();
                bool tooOld = utc - oldest.Utc > MonitorConstants.HistoryRetention;
                bool tooMany = _entries.Count > MonitorConstants.MaxLogEntries || _bytes > MonitorConstants.MaxLogBytes;
                if (!tooOld && !tooMany)
                {
                    break;
                }

                LogEntry old = _entries.Dequeue();
                _bytes -= Encoding.UTF8.GetByteCount(old.Code) + Encoding.UTF8.GetByteCount(old.Message) + 32;
                if (_bytes < 0)
                {
                    _bytes = 0;
                }
            }
        }
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string trimmed = value.Replace('\r', ' ').Replace('\n', ' ');
        if (trimmed.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("vless", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("authorization", StringComparison.OrdinalIgnoreCase))
        {
            return "redacted";
        }

        return trimmed;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}

public sealed record LogEntry(DateTimeOffset Utc, string Code, string Message);
