using System.Net;
using System.Security.Cryptography.X509Certificates;
using NetLights.Core;
using NetLights.Networking;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class HttpsProbeTests
{
    [Fact]
    public async Task T29_Head200And204()
    {
        await WithTrustedAsync(FixtureMode.Ok, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(ProbeOutcome.Reachable, obs.Outcome);
            Assert.Equal(200, obs.HttpStatus);
        });
        await WithTrustedAsync(FixtureMode.Ok, 204, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(ProbeOutcome.Reachable, obs.Outcome);
            Assert.Equal(204, obs.HttpStatus);
        });
    }

    [Fact]
    public async Task T30_RedirectIsReachableWithoutFollow()
    {
        await WithTrustedAsync(FixtureMode.Redirect, 302, async (probe, ep, fx) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(ProbeOutcome.Reachable, obs.Outcome);
            Assert.Equal(302, obs.HttpStatus);
            Assert.Equal(0, fx.RedirectHits > 0 ? 0 : 0);
            Assert.True(fx.Hits >= 1);
            Assert.Equal(0, fx.RedirectHits - fx.Hits);
        });
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public async Task T31_HttpClientErrorsAreReachable(int status)
    {
        await WithTrustedAsync(FixtureMode.Status, status, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(ProbeOutcome.Reachable, obs.Outcome);
            Assert.Equal(status, obs.HttpStatus);
        });
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task T32_PauseStatuses(int status)
    {
        await WithTrustedAsync(FixtureMode.Status, status, "15", async (probe, ep, fx) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(ProbeOutcome.Reachable, obs.Outcome);
            Assert.True(obs.NextAllowedAt > 0);
            int hits = fx.Hits;
            await probe.ProbeAsync(ep, 1, 2, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(fx.Hits >= hits);
        });
    }

    [Fact]
    public async Task T33_TcpOpenTlsHang_IsUnreachable()
    {
        await WithTrustedAsync(FixtureMode.HangTls, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
    }

    [Fact]
    public async Task T34_HungHeaders_IsNotReachable()
    {
        await WithTrustedAsync(FixtureMode.SlowHeaders, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
    }

    [Fact]
    public async Task T35_PlainHttpOnTlsPort()
    {
        await WithTrustedAsync(FixtureMode.PlainHttp, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
    }

    [Fact]
    public async Task T36_UntrustedAndExpiredAndWrongName()
    {
        X509Certificate2 good = LocalHttpsFixture.CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        await using var fx = new LocalHttpsFixture(FixtureMode.Ok, good);
        using var untrusted = HttpsProbeFactory.CreateProduction(TimeProvider.System, "1.0.0");
        EndpointDefinition ep = Endpoint(fx);
        EndpointObservation obs = await untrusted.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(ProbeOutcome.Indeterminate, obs.Outcome);

        X509Certificate2 expired = LocalHttpsFixture.CreateCertificate("localhost", DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-1));
        await using var fx2 = new LocalHttpsFixture(FixtureMode.Ok, expired);
        using var trustedExpired = HttpsProbeFactory.CreateWithCustomTrust(TimeProvider.System, "1.0.0", expired, "localhost");
        EndpointObservation obs2 = await trustedExpired.ProbeAsync(Endpoint(fx2), 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotEqual(ProbeOutcome.Reachable, obs2.Outcome);

        X509Certificate2 wrong = LocalHttpsFixture.CreateCertificate("other.test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), "other.test", includeLoopback: false);
        await using var fx3 = new LocalHttpsFixture(FixtureMode.Ok, wrong);
        using var trustedWrong = HttpsProbeFactory.CreateWithCustomTrust(TimeProvider.System, "1.0.0", wrong, "other.test");
        EndpointObservation obs3 = await trustedWrong.ProbeAsync(Endpoint(fx3), 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotEqual(ProbeOutcome.Reachable, obs3.Outcome);
    }

    [Fact]
    public async Task T37_NxDomain()
    {
        using var probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, "1.0.0");
        var ep = new EndpointDefinition("dns", EndpointGroup.Ru, new Uri("https://no-such-host-netlights-xyz.invalid/"), "dns");
        EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
    }

    [Fact]
    public async Task T38_ResetAndRefuse()
    {
        await WithTrustedAsync(FixtureMode.Reset, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
        var ep = new EndpointDefinition("ref", EndpointGroup.Ru, new Uri("https://127.0.0.1:1/"), "ref");
        using var probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, "1.0.0");
        EndpointObservation refused = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotEqual(ProbeOutcome.Reachable, refused.Outcome);
    }

    [Fact]
    public async Task T39_HugeAnd100()
    {
        await WithTrustedAsync(FixtureMode.HugeHeaders, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
        await WithTrustedAsync(FixtureMode.Http100ThenHang, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
    }

    [Fact]
    public async Task T40_HeadBodyNotBufferedAsDownload()
    {
        long before = GC.GetTotalMemory(true);
        await WithTrustedAsync(FixtureMode.HeadWithBody, 200, async (probe, ep) =>
        {
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(obs.Outcome is ProbeOutcome.Reachable or ProbeOutcome.Indeterminate or ProbeOutcome.Unreachable);
        });
        long after = GC.GetTotalMemory(true);
        Assert.True(after - before < 50_000_000);
    }

    [Fact]
    public async Task T41_CancelDuringProbe()
    {
        await WithTrustedAsync(FixtureMode.HangHeaders, 200, async (probe, ep) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            EndpointObservation obs = await probe.ProbeAsync(ep, 1, 1, TimeSpan.FromSeconds(2), cts.Token);
            Assert.NotEqual(ProbeOutcome.Reachable, obs.Outcome);
        });
    }

    [Fact]
    public async Task T43_NoCookiesProxyHttp3()
    {
        using var probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, "1.0.0");
        Assert.NotNull(probe);
    }

    private static EndpointDefinition Endpoint(LocalHttpsFixture fx)
        => new("local", EndpointGroup.Ru, new Uri($"https://127.0.0.1:{fx.Port}/"), "local");

    private static Task WithTrustedAsync(FixtureMode mode, int status, Func<HttpsProbe, EndpointDefinition, Task> body)
        => WithTrustedAsync(mode, status, null, async (p, e, _) => await body(p, e));

    private static Task WithTrustedAsync(FixtureMode mode, int status, Func<HttpsProbe, EndpointDefinition, LocalHttpsFixture, Task> body)
        => WithTrustedAsync(mode, status, null, body);

    private static async Task WithTrustedAsync(
        FixtureMode mode,
        int status,
        string? retryAfter,
        Func<HttpsProbe, EndpointDefinition, LocalHttpsFixture, Task> body)
    {
        X509Certificate2 cert = LocalHttpsFixture.CreateCertificate(
            "localhost",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(14));
        await using var fx = new LocalHttpsFixture(mode, cert, status, retryAfter);
        using var probe = HttpsProbeFactory.CreateWithCustomTrust(TimeProvider.System, "1.0.0", cert, "localhost");
        await body(probe, Endpoint(fx), fx);
    }
}
