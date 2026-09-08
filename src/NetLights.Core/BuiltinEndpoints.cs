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
        World("world-cloudflare", "https://cp.cloudflare.com/generate_204", "cloudflare"),
        World("world-mozilla", "https://firefox-portal-detection.com/generate_204", "fastly"),
        World("world-fedora", "https://fedoraproject.org/static/hotspot.txt", "unc"),
        World("world-debian", "https://www.debian.org/", "utwente"),
        World("world-arch", "https://archlinux.org/", "haproxy"),
        World("world-github", "https://github.com/robots.txt", "github"),
        World("world-wikimedia", "https://en.wikipedia.org/robots.txt", "wikimedia")
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

    private static EndpointDefinition World(string id, string url, string infra)
        => new(id, EndpointGroup.World, new Uri(url), infra);
}
