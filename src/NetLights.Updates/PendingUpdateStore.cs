using System.Text.Json;

namespace NetLights.Updates;

public sealed record PendingUpdate(
    string Version,
    string InstallerPath,
    string Sha256,
    DateTimeOffset DownloadedUtc);

public sealed class PendingUpdateStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;

    public PendingUpdateStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetLights",
            "updates");
    }

    public string Root => _root;
    public string PendingPath => Path.Combine(_root, "pending.json");
    public string ResultPath => Path.Combine(_root, "result.json");
    public string PhasePath => Path.Combine(_root, "phases.jsonl");

    public void WritePhase(string phase)
    {
        try
        {
            Directory.CreateDirectory(_root);
            if (File.Exists(PhasePath) && new FileInfo(PhasePath).Length > 2 * 1024 * 1024)
                File.Delete(PhasePath);
            File.AppendAllText(PhasePath, JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                pid = Environment.ProcessId,
                phase
            }) + Environment.NewLine);
        }
        catch (Exception)
        {
            // Diagnostics cannot break the recovery path.
        }
    }

    public void WritePending(PendingUpdate pending)
    {
        Directory.CreateDirectory(_root);
        string tmp = PendingPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(pending, Options));
        File.Move(tmp, PendingPath, true);
    }

    public PendingUpdate? ReadPending()
    {
        if (!File.Exists(PendingPath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(PendingPath).Length > 64 * 1024) return null;
            return JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath), Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void ClearPending()
    {
        if (File.Exists(PendingPath))
        {
            File.Delete(PendingPath);
        }
    }

    public void WriteResult(string version, bool success, string message)
    {
        Directory.CreateDirectory(_root);
        var payload = new { version, success, message, utc = DateTimeOffset.UtcNow };
        string tmp = ResultPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Options));
        File.Move(tmp, ResultPath, true);
    }

    public bool TryReadLastFailure(out string? message)
    {
        message = null;
        if (!File.Exists(ResultPath))
        {
            return false;
        }

        try
        {
            if (new FileInfo(ResultPath).Length > 64 * 1024) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ResultPath));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("success", out JsonElement success) && success.GetBoolean())
            {
                return false;
            }

            message = root.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : "Обновление не установлено.";
            if (message?.Length > 2048) message = "Ошибка обновления: сообщение слишком длинное.";
            return !string.IsNullOrWhiteSpace(message);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
