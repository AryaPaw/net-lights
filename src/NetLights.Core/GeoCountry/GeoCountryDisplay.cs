namespace NetLights.Core;

public sealed record GeoCountryDisplay(
    string Letters,
    string Tooltip,
    bool Fresh = true,
    LocationObservation? Observation = null,
    string? LocalCountry = null,
    string? OnlineCountry = null,
    string? ThirdCountry = null,
    string? PublicIpHash = null,
    string? ResolutionSource = null,
    DateTimeOffset? ConfirmedThroughUtc = null)
{
    public static GeoCountryDisplay Unconfirmed { get; } = new("??", "??", false);

    public static GeoCountryDisplay Disabled { get; } = new("??", "определение выключено", false);

    public static GeoCountryDisplay Conflicted { get; } = new("??", "источники расходятся", false);

    public static GeoCountryDisplay UnstableRoute { get; } = new("??", "внешний IP меняется", false);

    public static GeoCountryDisplay Confirmed(string iso2)
        => new(iso2, GeoCountryNames.Tooltip(iso2));

    public static GeoCountryDisplay FromConfirmedLookup(GeoCountryLookupResult lookup, LocationObservation? observation)
    {
        string country = lookup.CountryCode ?? "??";
        string details = string.Join(" · ", new[]
        {
            lookup.ResolutionSource == "country.is" ? "источник country.is" : null,
            lookup.ResolutionSource == "ipwho.is" ? "резервный источник ipwho.is" : null,
            lookup.LocalCountry is null ? null : "база " + lookup.LocalCountry,
            lookup.OnlineCountry is null ? null : "сеть " + lookup.OnlineCountry,
            lookup.ThirdCountry is null ? null : "проверка " + lookup.ThirdCountry
        }.Where(value => value is not null));
        string tooltip = details.Length == 0
            ? GeoCountryNames.Tooltip(country)
            : GeoCountryNames.Russian(country) + " · " + details;
        if (tooltip.Length > GeoCountryNames.TrayTooltipMaxChars)
            tooltip = tooltip[..GeoCountryNames.TrayTooltipMaxChars];
        return new(country, tooltip, true, observation, lookup.LocalCountry, lookup.OnlineCountry, lookup.ThirdCountry,
            observation?.PublicIpHash, lookup.ResolutionSource ?? "legacy");
    }

    public static GeoCountryDisplay FromUnconfirmedLookup(GeoCountryLookupResult lookup, LocationObservation? observation)
    {
        string status = lookup.DifferentIp ? "внешний IP меняется" : "страна не подтверждена";
        string details = string.Join(" · ", new[]
        {
            lookup.DifferentIp ? "IP-адреса источников не совпали" : null,
            lookup.LocalCountry is null ? null : "база " + lookup.LocalCountry,
            lookup.OnlineCountry is null ? null : "сеть " + lookup.OnlineCountry,
            lookup.ThirdCountry is null ? null : "проверка " + lookup.ThirdCountry
        }.Where(value => value is not null));
        string tooltip = details.Length == 0 ? status : status + " · " + details;
        if (tooltip.Length > GeoCountryNames.TrayTooltipMaxChars)
            tooltip = tooltip[..GeoCountryNames.TrayTooltipMaxChars];
        return new("??", tooltip, false, observation, lookup.LocalCountry, lookup.OnlineCountry, lookup.ThirdCountry,
            observation?.PublicIpHash, lookup.ResolutionSource ?? "legacy");
    }

    public static GeoCountryDisplay FromLocalEstimate(GeoCountryLookupResult lookup, LocationObservation? observation,
        DateTimeOffset? confirmedThroughUtc)
    {
        string? country = GeoCountryParsers.IsIso3166Alpha2(lookup.LocalCountry)
            ? lookup.LocalCountry
            : GeoCountryParsers.IsIso3166Alpha2(lookup.IplocateCountry) ? lookup.IplocateCountry : null;
        string letters = country ?? "??";
        string tooltip = country is null
            ? "Нет ответа онлайн-сервисов и локальной оценки"
            : "Оценка по локальной базе: " + GeoCountryNames.Tooltip(country) + " · это не подтвержденное текущее местоположение";
        if (tooltip.Length > GeoCountryNames.TrayTooltipMaxChars)
            tooltip = tooltip[..GeoCountryNames.TrayTooltipMaxChars];
        return new(letters, tooltip, false, observation, lookup.LocalCountry, lookup.OnlineCountry,
            lookup.ThirdCountry, observation?.PublicIpHash, "local-estimate", confirmedThroughUtc);
    }

    public static GeoCountryDisplay LastKnown(string iso2)
    {
        string text = GeoCountryNames.Russian(iso2) + " (устарело)";
        if (text.Length > GeoCountryNames.TrayTooltipMaxChars)
        {
            text = text[..GeoCountryNames.TrayTooltipMaxChars];
        }

        return new(iso2, text, false);
    }
}
