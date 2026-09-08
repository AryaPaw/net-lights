using NetLights.Core;

namespace NetLights.App;

internal static class StatusSnapshotProjector
{
    public static string Fingerprint(MonitorSnapshot snapshot)
        => string.Join("|", Rows(snapshot).Select(r => $"{r.Group}:{r.Id}:{r.Result}:{r.Http}:{r.Delay}:{r.LastCompleted?.UtcTicks}:{r.Reason}"))
           + "|" + snapshot.Ru.Availability + snapshot.Vpn.Availability
           + "|" + snapshot.Ru.ConfirmationActive + snapshot.Vpn.ConfirmationActive;

    public static string Summary(MonitorSnapshot snapshot)
    {
        string summary = $"Провайдер: {DiagnosticExport.Label(snapshot.Ru.Availability)}  |  VPN: {DiagnosticExport.Label(snapshot.Vpn.Availability)}";
        if (snapshot.Ru.ConfirmationActive || snapshot.Vpn.ConfirmationActive)
        {
            summary += "  |  идёт дополнительная проверка";
        }

        return summary;
    }

    public static string Title(MonitorSnapshot snapshot)
        => $"Net Lights - Провайдер: {DiagnosticExport.Label(snapshot.Ru.Availability)}, VPN: {DiagnosticExport.Label(snapshot.Vpn.Availability)}";

    public static List<EndpointRow> Rows(MonitorSnapshot snapshot)
    {
        var rows = new List<EndpointRow>(14);
        AddGroup(rows, "Провайдер", snapshot.Ru);
        AddGroup(rows, "VPN", snapshot.Vpn);
        return rows;
    }

    public static string AgeLabel(DateTimeOffset? utc, DateTimeOffset now)
    {
        if (utc is null)
        {
            return "";
        }

        int seconds = (int)Math.Max(0, Math.Floor((now - utc.Value).TotalSeconds));
        return seconds + " сек назад";
    }

    public static (Color Back, Color Fore) RowStyle(EndpointGroup group, ProbeOutcome? outcome, bool highContrast)
    {
        if (highContrast)
        {
            return outcome switch
            {
                ProbeOutcome.Reachable => (SystemColors.Window, SystemColors.WindowText),
                ProbeOutcome.Unreachable or ProbeOutcome.Cancelled => (SystemColors.ControlDark, SystemColors.HighlightText),
                _ => (SystemColors.Control, SystemColors.ControlText)
            };
        }

        bool down = outcome is ProbeOutcome.Unreachable or ProbeOutcome.Cancelled;
        bool ok = outcome == ProbeOutcome.Reachable;
        if (group == EndpointGroup.Ru)
        {
            if (ok)
            {
                return (Color.FromArgb(214, 234, 247), Color.FromArgb(12, 54, 90));
            }

            if (down)
            {
                return (Color.FromArgb(18, 52, 86), Color.FromArgb(196, 220, 238));
            }

            return (Color.FromArgb(126, 168, 204), Color.FromArgb(14, 40, 64));
        }

        if (ok)
        {
            return (Color.FromArgb(247, 226, 196), Color.FromArgb(92, 48, 10));
        }

        if (down)
        {
            return (Color.FromArgb(90, 44, 12), Color.FromArgb(247, 220, 176));
        }

        return (Color.FromArgb(196, 140, 72), Color.FromArgb(48, 24, 8));
    }

    private static void AddGroup(List<EndpointRow> rows, string groupName, GroupSnapshot group)
    {
        bool highContrast = SystemInformation.HighContrast;
        foreach (EndpointView endpoint in group.Endpoints)
        {
            string result = ResultLabel(endpoint);
            string reason = endpoint.Paused
                ? "пауза сервера"
                : endpoint.Failure is { } failure
                    ? DiagnosticExport.FailureLabel(failure)
                    : endpoint.Outcome == ProbeOutcome.Reachable
                        ? ""
                        : endpoint.Fresh
                            ? ""
                            : "нет свежих данных";
            (Color back, Color fore) = RowStyle(group.Group, endpoint.Outcome, highContrast);
            rows.Add(new EndpointRow(
                groupName,
                endpoint.Id,
                result,
                endpoint.HttpStatus?.ToString() ?? "",
                endpoint.Elapsed is { } elapsed ? $"{elapsed.TotalMilliseconds:0} мс" : "",
                endpoint.LastCompletedUtc,
                reason,
                back,
                fore));
        }
    }

    internal static string ResultLabel(EndpointView endpoint)
    {
        if (endpoint.Outcome is null)
        {
            return endpoint.InFlight ? "проверка" : "нет данных";
        }

        return endpoint.Outcome switch
        {
            ProbeOutcome.Reachable => "доступен",
            ProbeOutcome.Unreachable => "нет ответа",
            ProbeOutcome.Indeterminate => "неясно",
            ProbeOutcome.Cancelled => "отмена",
            _ => endpoint.Outcome.ToString() ?? "нет данных"
        };
    }
}

internal readonly record struct EndpointRow(
    string Group,
    string Id,
    string Result,
    string Http,
    string Delay,
    DateTimeOffset? LastCompleted,
    string Reason,
    Color Back,
    Color Fore);
