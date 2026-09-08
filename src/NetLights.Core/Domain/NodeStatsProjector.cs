namespace NetLights.Core;

public readonly record struct NodeStatRow(
    string Group,
    string Id,
    string Uri,
    int Samples,
    string Success,
    int Restricted,
    string Last,
    string Verdict,
    int ProblemScore);

public static class NodeStatsProjector
{
    public static List<NodeStatRow> Rows(MonitorSnapshot snapshot)
    {
        var rows = new List<NodeStatRow>(14);
        Add(rows, "Провайдер", snapshot.Ru);
        Add(rows, "VPN", snapshot.Vpn);
        return rows
            .OrderByDescending(r => r.ProblemScore)
            .ThenBy(r => r.Group, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static string Verdict(EndpointStats stats)
    {
        if (stats.Samples == 0)
        {
            return "нет данных с запуска";
        }

        if (stats.Restricted > 0 && stats.Restricted * 3 >= stats.Samples)
        {
            return "похоже, ограничивают (HTTP 403/429)";
        }

        if (stats.SuccessPercent >= 80)
        {
            return "стабильно";
        }

        if (stats.DnsFailures * 2 >= stats.Samples)
        {
            return "часто DNS, кандидат на замену";
        }

        if (stats.Timeouts * 2 >= stats.Samples)
        {
            return "часто таймаут";
        }

        if (stats.SuccessPercent <= 30)
        {
            return "часто недоступен, кандидат на замену";
        }

        return "нестабильно";
    }

    private static void Add(List<NodeStatRow> rows, string groupName, GroupSnapshot group)
    {
        foreach (EndpointView endpoint in group.Endpoints)
        {
            EndpointStats stats = endpoint.Stats;
            string last = endpoint.HttpStatus is { } http
                ? "HTTP " + http
                : endpoint.Failure is { } failure
                    ? failure.Kind.ToString()
                    : stats.LastHttp is { } lastHttp
                        ? "HTTP " + lastHttp
                        : stats.LastFailure?.ToString() ?? "";
            rows.Add(new NodeStatRow(
                groupName,
                endpoint.Id,
                endpoint.Uri.Host,
                stats.Samples,
                stats.Samples == 0 ? "—" : stats.SuccessPercent + "%",
                stats.Restricted,
                last,
                Verdict(stats),
                stats.ProblemScore));
        }
    }
}
