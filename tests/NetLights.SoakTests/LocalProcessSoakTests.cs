using System.Security.Cryptography.X509Certificates;
using NetLights.Core;
using NetLights.Networking;
using NetLights.TestSupport;
using Xunit;

namespace NetLights.SoakTests;

public sealed class LocalProcessSoakTests
{
    [Fact]
    public async Task T57_ShortLocalhostProcess()
    {
        X509Certificate2 cert = LocalHttpsFixture.CreateCertificate(
            "localhost",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(7));
        await using var fx = new LocalHttpsFixture(FixtureMode.Ok, cert);
        using var probe = HttpsProbeFactory.CreateWithCustomTrust(TimeProvider.System, "1.0.0", cert, "localhost");
        var endpoints = BuiltinEndpoints.All
            .Select(e => e with { Uri = new Uri($"https://{fx.Uri.IdnHost}:{fx.Uri.Port}/{e.Id}") })
            .ToList();
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = endpoints, UsingBuiltinPool = false }, TimeProvider.System);
        await using var host = new MonitorHost(kernel, probe, TimeProvider.System);
        int seconds = 20;
        if (int.TryParse(Environment.GetEnvironmentVariable("NETLIGHTS_SOAK_SECONDS"), out int configured)
            && configured >= 20
            && configured <= 7200)
        {
            seconds = configured;
        }

        host.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Assert.Contains(host.Snapshot.Ru.Endpoints, e => e.Outcome == ProbeOutcome.Reachable);
        Assert.NotEqual(GroupAvailability.Unknown, host.Snapshot.Ru.Availability);
        Assert.InRange(host.Kernel.PhysicalInflight, 0, 4);
    }
}
