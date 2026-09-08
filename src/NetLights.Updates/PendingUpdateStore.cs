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
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ResultPath));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("success", out JsonElement success) && success.GetBoolean())
            {
                return false;
            }

            message = root.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : "Обновление не установлено.";
            return !string.IsNullOrWhiteSpace(message);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
