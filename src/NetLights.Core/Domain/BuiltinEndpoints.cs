namespace NetLights.Core;

public static class BuiltinEndpoints
{
    public static IReadOnlyList<EndpointDefinition> All { get; } =
    [
        Ru("ru-yandex", "https://yandex.ru/robots.txt", "yandex"),
        Ru("ru-mail", "https://www.mail.ru/robots.txt", "vk"),
        Ru("ru-rostelecom", "https://www.rt.ru/robots.txt", "rostelecom"),
        Ru("ru-selectel", "https://selectel.ru/robots.txt", "selectel"),
        Ru("ru-beeline", "https://www.beeline.ru/robots.txt", "vimpelcom"),
        Ru("ru-megafon", "https://moscow.megafon.ru/robots.txt", "megafon"),
        Ru("ru-timeweb", "https://timeweb.com/robots.txt", "hll"),
        Vpn("vpn-cloudflare", "https://cp.cloudflare.com/generate_204", "cloudflare"),
        Vpn("vpn-github", "https://github.com/robots.txt", "github"),
        Vpn("vpn-wikimedia", "https://en.wikipedia.org/robots.txt", "wikimedia"),
        Vpn("vpn-google", "https://go.dev/robots.txt", "google"),
        Vpn("vpn-duckduckgo", "https://duckduckgo.com/robots.txt", "duckduckgo"),
        Vpn("vpn-apache", "https://www.apache.org/robots.txt", "fastly"),
        Vpn("vpn-arch", "https://archlinux.org/", "haproxy")
    ];

    public static MonitorConfiguration CreateDefault()
    {
        return new MonitorConfiguration
        {
            Endpoints = All,
            UsingBuiltinPool = true
        };
    }

    private static EndpointDefinition Ru(string id, string url, string infra)
        => new(id, EndpointGroup.Ru, new Uri(url), infra);

    private static EndpointDefinition Vpn(string id, string url, string infra)
        => new(id, EndpointGroup.Vpn, new Uri(url), infra);
}
