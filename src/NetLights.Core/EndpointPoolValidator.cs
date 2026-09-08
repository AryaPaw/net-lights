namespace NetLights.Core;

public sealed record EndpointPoolValidation(
    bool IsValid,
    string? Error,
    IReadOnlyList<EndpointDefinition> Endpoints);

public static class EndpointPoolValidator
{
    public static EndpointPoolValidation Validate(IReadOnlyList<EndpointDefinition> endpoints)
    {
        if (endpoints.Count != MonitorConstants.EndpointsPerGroup * 2)
        {
            return Fail("Нужно ровно семь адресов в каждой группе.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (EndpointDefinition endpoint in endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Id))
            {
                return Fail("Идентификатор адреса пуст.");
            }

            if (!ids.Add(endpoint.Id))
            {
                return Fail($"Дублируется идентификатор {endpoint.Id}.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.InfrastructureId))
            {
                return Fail($"Пустой infrastructureId у {endpoint.Id}.");
            }

            if (!endpoint.Uri.IsAbsoluteUri || endpoint.Uri.Scheme != Uri.UriSchemeHttps)
            {
                return Fail($"Адрес {endpoint.Id} должен использовать https.");
            }

            if (!string.IsNullOrEmpty(endpoint.Uri.UserInfo))
            {
                return Fail($"Адрес {endpoint.Id} содержит credentials.");
            }
        }

        if (!ValidateGroup(endpoints, EndpointGroup.Ru, out string? ruError))
        {
            return Fail(ruError!);
        }

        if (!ValidateGroup(endpoints, EndpointGroup.Vpn, out string? vpnError))
        {
            return Fail(vpnError!);
        }

        return new EndpointPoolValidation(true, null, endpoints);
    }

    private static bool ValidateGroup(
        IReadOnlyList<EndpointDefinition> endpoints,
        EndpointGroup group,
        out string? error)
    {
        List<EndpointDefinition> ofGroup = endpoints.Where(e => e.Group == group).ToList();
        if (ofGroup.Count != MonitorConstants.EndpointsPerGroup)
        {
            error = $"Группа {group} должна содержать семь адресов.";
            return false;
        }

        int infra = ofGroup.Select(e => e.InfrastructureId).Distinct(StringComparer.Ordinal).Count();
        if (infra < MonitorConstants.MinInfrastructuresPerGroup)
        {
            error = $"Группа {group} должна содержать не менее трех infrastructureId.";
            return false;
        }

        error = null;
        return true;
    }

    private static EndpointPoolValidation Fail(string error)
        => new(false, error, BuiltinEndpoints.All);
}
