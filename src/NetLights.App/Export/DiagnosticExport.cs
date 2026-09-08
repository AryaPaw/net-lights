using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using NetLights.Core;

namespace NetLights.App;

internal static class DiagnosticExport
{
    public static string Export(MonitorSnapshot snapshot, BoundedEventLog log, string? configWarning)
    {
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string dir = Path.Combine(Path.GetTempPath(), "NetLights-export-" + Guid.NewGuid().ToString("N"));
        string zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"net-lights-export-{stamp}.zip");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "snapshot.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(dir, "events.json"), JsonSerializer.Serialize(log.Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
            var versions = new
            {
                product = ProductInfo.Version,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = Environment.OSVersion.VersionString,
                configWarning
            };
            File.WriteAllText(Path.Combine(dir, "versions.json"), JsonSerializer.Serialize(versions, new JsonSerializerOptions { WriteIndented = true }));
            string history = StateHistoryStore.FilePath;
            if (File.Exists(history))
            {
                File.Copy(history, Path.Combine(dir, "state-history.jsonl"), true);
            }

            string tmpZip = zip + ".partial";
            if (File.Exists(tmpZip))
            {
                File.Delete(tmpZip);
            }

            ZipFile.CreateFromDirectory(dir, tmpZip);
            if (!File.Exists(tmpZip) || new FileInfo(tmpZip).Length == 0)
            {
                throw new IOException("Архив не записан.");
            }

            File.Move(tmpZip, zip, true);
            return zip;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup of the temporary export directory
            }
        }
    }

    public static string FormatTooltip(MonitorSnapshot snapshot)
    {
        string text = $"Провайдер: {Label(snapshot.Ru.Availability)} | VPN: {Label(snapshot.Vpn.Availability)}";
        if (snapshot.Paused)
        {
            text += " | пауза";
        }

        return Truncate(text);
    }

    public static string Label(GroupAvailability availability)
    {
        return availability switch
        {
            GroupAvailability.Online => "доступно",
            GroupAvailability.Limited => "ограничено",
            GroupAvailability.Offline => "нет ответа",
            GroupAvailability.Unknown => "нет свежих данных",
            _ => throw new InvalidOperationException($"Unknown availability {availability}.")
        };
    }

    public static string FailureLabel(StructuredFailure failure)
    {
        return failure.Kind switch
        {
            StructuredFailureKind.DnsFailure => "DNS",
            StructuredFailureKind.TcpRefused => "TCP отказ",
            StructuredFailureKind.TcpReset => "TCP сброс",
            StructuredFailureKind.TransportTimeout => "таймаут",
            StructuredFailureKind.TlsProtocol => "ошибка TLS",
            StructuredFailureKind.TlsHandshake => "TLS handshake",
            StructuredFailureKind.CertificateTrust => "сертификат: доверие",
            StructuredFailureKind.CertificateName => "сертификат: имя",
            StructuredFailureKind.CertificateExpired => "сертификат: срок",
            StructuredFailureKind.InvalidHeaders => "некорректные заголовки",
            StructuredFailureKind.OversizedHeaders => "слишком большие заголовки",
            StructuredFailureKind.LocalResource => "локальная ошибка",
            StructuredFailureKind.Cancelled => "отмена",
            StructuredFailureKind.DeadlineExceeded => "таймаут",
            StructuredFailureKind.MonitorError => "ошибка монитора",
            StructuredFailureKind.NetworkUnavailable => "нет сетевого подключения",
            StructuredFailureKind.None => "",
            _ => failure.SafeDetail
        };
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];
}
