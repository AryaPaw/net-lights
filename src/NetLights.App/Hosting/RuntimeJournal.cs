using System.Text.Json;

namespace NetLights.App;

internal static class RuntimeJournal
{
    private static readonly object Gate = new();
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private const long MaxTotalBytes = 8L * 1024 * 1024;
    private const long MaxSegmentBytes = 1024 * 1024;

    internal static void Write(string code, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                string directory = Path.Combine(SettingsStore.RootDirectory, "diagnostics");
                Directory.CreateDirectory(directory);
                string stem = "runs-" + DateTime.UtcNow.ToString("yyyyMMdd");
                int segment = 0;
                string path;
                do
                {
                    path = Path.Combine(directory, stem + "-" + segment.ToString("D3") + ".jsonl");
                    if (!File.Exists(path) || new FileInfo(path).Length < MaxSegmentBytes) break;
                    segment++;
                } while (segment < 1000);
                string entry = JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow,
                    run = RunId,
                    pid = Environment.ProcessId,
                    version = ProductInfo.Version,
                    code,
                    errorType = exception?.GetType().FullName,
                    errorSite = exception?.TargetSite is { } site
                        ? site.DeclaringType?.FullName + "." + site.Name : null,
                    hresult = exception?.HResult
                });
                File.AppendAllText(path, entry + Environment.NewLine);
                Prune(directory);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never stop monitoring or prevent the application from exiting.
        }
    }

    private static void Prune(string directory)
    {
        FileInfo[] files = new DirectoryInfo(directory).GetFiles("runs-*.jsonl")
            .OrderBy(file => file.CreationTimeUtc).ToArray();
        long total = files.Sum(file => file.Length);
        DateTime cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (FileInfo file in files)
        {
            if (file.LastWriteTimeUtc >= cutoff && total <= MaxTotalBytes) break;
            total -= file.Length;
            file.Delete();
        }
    }
}
