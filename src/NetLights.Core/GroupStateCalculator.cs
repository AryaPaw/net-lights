namespace NetLights.Core;

public readonly record struct EndpointEvidence(
    string EndpointId,
    string InfrastructureId,
    ProbeOutcome? Outcome,
    long CompletedTimestamp,
    bool Fresh);

public static class GroupStateCalculator
{
    public static GroupAvailability Evaluate(
        IReadOnlyList<EndpointDefinition> configured,
        IReadOnlyList<EndpointEvidence> evidence,
        out string reason,
        out int reachableInfrastructures,
        bool allowPartialOffline = false)
    {
        Dictionary<string, EndpointEvidence> byId = evidence
            .GroupBy(e => e.EndpointId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        HashSet<string> reachable = new(StringComparer.Ordinal);
        int freshUnreachable = 0;
        int missingOrIndeterminate = 0;
        HashSet<string> unreachableInfra = new(StringComparer.Ordinal);

        foreach (EndpointDefinition endpoint in configured)
        {
            if (!byId.TryGetValue(endpoint.Id, out EndpointEvidence item) || !item.Fresh || item.Outcome is null)
            {
                missingOrIndeterminate++;
                continue;
            }

            switch (item.Outcome.Value)
            {
                case ProbeOutcome.Reachable:
                    reachable.Add(item.InfrastructureId);
                    break;
                case ProbeOutcome.Unreachable:
                    freshUnreachable++;
                    unreachableInfra.Add(item.InfrastructureId);
                    break;
                case ProbeOutcome.Indeterminate:
                    missingOrIndeterminate++;
                    break;
                case ProbeOutcome.Cancelled:
                    missingOrIndeterminate++;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown outcome {item.Outcome.Value}.");
            }
        }

        reachableInfrastructures = reachable.Count;
        if (reachable.Count >= 2)
        {
            reason = "Не менее двух независимых HTTPS-подтверждений.";
            return GroupAvailability.Online;
        }

        if (reachable.Count == 1)
        {
            reason = "Есть только одно независимое HTTPS-подтверждение.";
            return GroupAvailability.Limited;
        }

        if (reachable.Count == 0
            && unreachableInfra.Count >= MonitorConstants.MinInfrastructuresPerGroup
            && freshUnreachable >= MonitorConstants.MinUnreachableInfraForPartialOffline
            && (allowPartialOffline || (freshUnreachable == configured.Count && missingOrIndeterminate == 0)))
        {
            reason = allowPartialOffline && freshUnreachable < configured.Count
                ? "Несколько независимых HTTPS-направлений недоступны."
                : "Все контрольные адреса свежо недоступны.";
            return GroupAvailability.Offline;
        }

        reason = missingOrIndeterminate > 0
            ? "Недостаточно свежих доказательств."
            : "Нет подтвержденной доступности.";
        return GroupAvailability.Unknown;
    }
}
