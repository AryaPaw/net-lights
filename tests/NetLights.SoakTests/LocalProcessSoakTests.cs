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
        var endpoints = BuiltinEndpoints.All.Select(e => e with { Uri = fx.Uri }).ToList();
        var kernel = new MonitorKernel(new MonitorConfiguration { Endpoints = endpoints, UsingBuiltinPool = false }, TimeProvider.System);
        await using var host = new MonitorHost(kernel, probe, TimeProvider.System);
        host.Start();
        await Task.Delay(TimeSpan.FromSeconds(20));
        Assert.NotEqual(GroupAvailability.Offline, host.Snapshot.Ru.Availability);
        Assert.InRange(host.Kernel.PhysicalInflight, 0, 4);
    }
}
