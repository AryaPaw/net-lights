using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NetLights.Core;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class LayeredGeoCountrySourceTests
{
    [Fact]
    public async Task PrimaryRetryAfter_IsRespectedWhileFallbackSucceeds()
    {
        var time = new FakeTimeProvider();
        using var context = TestContext.Create("DE", "TR", "AE", time: time);
        context.CountryIs.Result = new GeoCountryLookupResult(false, null, TimeSpan.FromHours(1));
        Assert.Equal("ipwho.is", (await context.Source.GetCurrentAsync(CancellationToken.None)).ResolutionSource);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("ipwho.is", (await context.Source.GetCurrentAsync(CancellationToken.None)).ResolutionSource);
        Assert.Equal(1, context.CountryIs.LookupCount);
        Assert.Equal(2, context.IpWho.LookupCount);
        time.Advance(TimeSpan.FromHours(1));
        context.CountryIs.Result = new GeoCountryLookupResult(true, "JP", null, TestContext.TestIp);
        Assert.Equal("JP", (await context.Source.GetCurrentAsync(CancellationToken.None)).CountryCode);
        Assert.Equal(2, context.CountryIs.LookupCount);
    }

    [Fact]
    public async Task CountryIsWinsEvenWhenLocalDatabaseDisagrees_AndDoesNotCallIpWho()
    {
        using var context = TestContext.Create("DE", "TR", "DE");

        GeoCountryLookupResult result = await context.Source.GetCurrentAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("TR", result.CountryCode);
        Assert.True(result.Conflicted);
        Assert.Equal(0, context.IpWho.LookupCount);
        Assert.Equal(1, context.CountryIs.LookupCount);
    }

    [Fact]
    public async Task RepeatedSameIp_DoesNotCallFallbackWhenPrimaryIsValid()
    {
        using var context = TestContext.Create("DE", "TR", "DE");

        GeoCountryLookupResult first = await context.Source.GetCurrentAsync(CancellationToken.None);
        GeoCountryLookupResult second = await context.Source.GetCurrentAsync(CancellationToken.None);

        Assert.True(first.Ok && second.Ok);
        Assert.Equal("TR", second.CountryCode);
        Assert.Equal(0, context.IpWho.LookupCount);
    }

    [Fact]
    public async Task IpWhoIsUsedOnlyWhenCountryIsCannotProvideAnAnswer()
    {
        using var context = TestContext.Create("DE", "TR", "AE");
        context.CountryIs.Result = new GeoCountryLookupResult(false, null, null);

        GeoCountryLookupResult result = await context.Source.GetCurrentAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("AE", result.CountryCode);
        Assert.Equal("ipwho.is", result.ResolutionSource);
        Assert.Equal(1, context.IpWho.LookupCount);
        Assert.Null(result.OnlineCountry);
        Assert.Equal("AE", result.ThirdCountry);
    }

    [Fact]
    public async Task PrimaryWithoutIp_DoesNotReuseFallbackFromAnEarlierRoute()
    {
        using var context = TestContext.Create("DE", "TR", "DE");
        context.CountryIs.Result = new GeoCountryLookupResult(false, null, null);

        GeoCountryLookupResult first = await context.Source.GetCurrentAsync(CancellationToken.None);
        context.IpWho.Result = new GeoCountryLookupResult(true, "JP", null, "198.51.100.8");
        GeoCountryLookupResult second = await context.Source.GetCurrentAsync(CancellationToken.None);

        Assert.Equal("DE", first.CountryCode);
        Assert.Equal("JP", second.CountryCode);
        Assert.Equal(2, context.IpWho.LookupCount);
    }

    [Fact]
    public async Task DifferentThirdSourceIp_IsNotCachedForExpectedIp()
    {
        using var context = TestContext.Create("DE", "TR", "DE", thirdIp: "198.51.100.8");
        context.CountryIs.Result = new GeoCountryLookupResult(false, null, null, TestContext.TestIp);

        GeoCountryLookupResult first = await context.Source.GetCurrentAsync(CancellationToken.None);
        context.IpWho.Result = new GeoCountryLookupResult(true, "JP", null, TestContext.TestIp);
        GeoCountryLookupResult second = await context.Source.GetCurrentAsync(CancellationToken.None);

        Assert.False(first.Ok);
        Assert.True(first.DifferentIp);
        Assert.True(second.Ok);
        Assert.Equal(TestContext.TestIp, second.PublicIp);
        Assert.Equal("JP", second.CountryCode);
        Assert.Equal(2, context.IpWho.LookupCount);
    }

    [Fact]
    public void UsageLedger_IsCappedPerInstallationAndDoesNotPersistRawIp()
    {
        using var context = TestContext.Create("DE", "DE", "DE");
        IPAddress egress = IPAddress.Parse(TestContext.TestIp);

        for (int i = 0; i < IpWhoUsageLedger.DailyLimit; i++)
            Assert.True(context.Ledger.TryConsume(i % 2 == 0 ? egress : IPAddress.Parse("203.0.113.16")));
        Assert.False(context.Ledger.TryConsume(IPAddress.Parse("203.0.113.17")));
        string json = File.ReadAllText(context.LedgerPath);
        Assert.DoesNotContain(TestContext.TestIp, json, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageLedger_FailsClosedWhenPersistedDataIsCorrupt()
    {
        string root = Path.Combine(Path.GetTempPath(), "NetLightsGeoTests", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "GeoIP");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "ipwho-usage.json"), "{broken");
            var ledger = new IpWhoUsageLedger(root);

            Assert.False(ledger.TryConsume(IPAddress.Parse(TestContext.TestIp)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void UsageLedger_FailsClosedBeforeReadingOversizedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "NetLightsGeoTests", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "GeoIP");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "ipwho-usage.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(1024 * 1024 + 1);

            var ledger = new IpWhoUsageLedger(root);

            Assert.False(ledger.TryConsume(IPAddress.Parse(TestContext.TestIp)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void UsageLedger_FailsClosedWhenEntryCountExceedsLimit()
    {
        string root = Path.Combine(Path.GetTempPath(), "NetLightsGeoTests", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "GeoIP");
        Directory.CreateDirectory(directory);
        try
        {
            const string entry = "{\"EgressHash\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"UtcDate\":\"2026-10-03\",\"Count\":1}";
            string json = "{\"Entries\":[" + string.Join(',', Enumerable.Repeat(entry, 2049)) + "]}";
            File.WriteAllText(Path.Combine(directory, "ipwho-usage.json"), json);

            var ledger = new IpWhoUsageLedger(root);

            Assert.False(ledger.TryConsume(IPAddress.Parse(TestContext.TestIp)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TestContext : IDisposable
    {
        public const string TestIp = "203.0.113.15";
        private TestContext(string root, FakeSource online, FakeSource third, IpWhoUsageLedger ledger,
            LayeredGeoCountrySource source)
        {
            Root = root;
            CountryIs = online;
            IpWho = third;
            Ledger = ledger;
            Source = source;
        }

        public string Root { get; }
        public string LedgerPath => Path.Combine(Root, "GeoIP", "ipwho-usage.json");
        public FakeSource CountryIs { get; }
        public FakeSource IpWho { get; }
        public IpWhoUsageLedger Ledger { get; }
        public LayeredGeoCountrySource Source { get; }

        public static TestContext Create(string local, string online, string third, string thirdIp = TestIp,
            TimeProvider? time = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "NetLightsGeoTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var onlineSource = new FakeSource(new GeoCountryLookupResult(true, online, null, TestIp));
            var thirdSource = new FakeSource(new GeoCountryLookupResult(true, third, null, thirdIp));
            var database = new FakeDatabase(V2FlyGeoIpDatabase.Parse(Database(local)));
            var ledger = new IpWhoUsageLedger(root);
            var source = new LayeredGeoCountrySource(onlineSource, thirdSource, database, ledger, time);
            return new TestContext(root, onlineSource, thirdSource, ledger, source);
        }

        public void Dispose()
        {
            Source.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static byte[] Database(string country)
            => Message(0x0a, Entry(country));

        private static byte[] Entry(string country)
            => Message(0x0a, Encoding.ASCII.GetBytes(country))
                .Concat(Message(0x12, Message(0x0a, [203, 0, 113, 0]).Concat(Varint(0x10)).Concat(Varint(24)).ToArray()))
                .ToArray();

        private static byte[] Message(byte tag, byte[] value) => [tag, .. Varint((ulong)value.Length), .. value];

        private static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            while (value >= 0x80)
            {
                bytes.Add((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }
            bytes.Add((byte)value);
            return bytes.ToArray();
        }

        private sealed class FakeDatabase(V2FlyGeoIpDatabase database) : IGeoIpDatabaseProvider
        {
            public V2FlyGeoIpDatabase? Current => database;
            public string? Version => "test-r1";
            public void Start() { }
            public void CheckForUpdate() { }
            public void Dispose() { }
        }

        public sealed class FakeSource(GeoCountryLookupResult result) : IGeoCountrySource
        {
            public GeoCountryLookupResult Result { get; set; } = result;
            public int LookupCount { get; private set; }
            public Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
            {
                LookupCount++;
                return Task.FromResult(Result);
            }
        }
    }
}
