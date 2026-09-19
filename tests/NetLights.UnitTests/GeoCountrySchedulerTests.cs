using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountrySchedulerTests
{
    [Fact]
    public void IpWatch_IsTenSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(10), GeoCountryPolicy.IpWatchInterval);

    [Fact]
    public async Task ConfirmedIso_WhenSourcesAgree()
    {
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE");
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();
        GeoCountryDisplay display = await WaitDisplay(scheduler, letters: "DE");
        Assert.Equal("Германия / Germany", display.Tooltip);
        Assert.Equal(1, source.SelfCount);
        Assert.Equal(1, source.ConfirmCount);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), source.LastConfirmIp);
    }

    [Fact]
    public async Task ShowsSelfCountry_WhenSourcesDiffer()
    {
        var source = new FakeCountrySource("8.8.8.8", "PL", "DE");
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();
        GeoCountryDisplay display = await WaitDisplay(scheduler, letters: "PL", minConfirms: 1);
        Assert.Equal("Польша / Poland", display.Tooltip);
    }

    [Fact]
    public async Task ConfirmUsesSameIpAsSelf()
    {
        var source = new FakeCountrySource("2001:4860:4860::8888", "US", "US");
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();
        await WaitDisplay(scheduler, "US");
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), source.LastConfirmIp);
    }

    [Fact]
    public async Task OneInflight_SkipsOverlappingTicks()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE")
        {
            SelfGate = new TaskCompletionSource<GeoCountrySelfResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.SelfCount == 1);
        time.Advance(GeoCountryPolicy.IpWatchInterval);
        time.Advance(GeoCountryPolicy.IpWatchInterval);
        Assert.Equal(1, source.SelfCount);
        Assert.Equal(0, source.ConfirmCount);
        source.SelfGate.SetResult(source.SelfValue);
        await WaitDisplay(scheduler, "DE");
        Assert.Equal(1, source.ConfirmCount);
    }

    [Fact]
    public async Task StableIp_DoesNotReconfirmBeforeFifteenMinutes()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        time.Advance(GeoCountryPolicy.IpWatchInterval);
        await WaitUntil(() => source.SelfCount == 2);
        Assert.Equal(1, source.ConfirmCount);
        Assert.Equal("DE", scheduler.Current.Letters);
        time.Advance(GeoCountryPolicy.ConfirmMaxAge);
        await WaitUntil(() => source.ConfirmCount == 2);
        Assert.Equal("DE", scheduler.Current.Letters);
    }

    [Fact]
    public async Task NewIp_KeepsPreviousLettersUntilNewCountryArrives()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE");
        var seen = new List<string>();
        using var scheduler = new GeoCountryScheduler(source, time, display => seen.Add(display.Letters));
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        int unconfirmedAfterStart = seen.Count(letter => letter == "??");
        source.Ip = IPAddress.Parse("1.1.1.1");
        source.SelfCountry = "NL";
        source.ConfirmCountry = "NL";
        time.Advance(GeoCountryPolicy.IpWatchInterval);
        await WaitDisplay(scheduler, "NL");
        Assert.Equal(unconfirmedAfterStart, seen.Count(letter => letter == "??"));
        Assert.Equal(2, source.ConfirmCount);
    }

    [Fact]
    public async Task UsesSelfCountry_WhenConfirmFails()
    {
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE") { ConfirmOk = false };
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        Assert.Equal("Германия / Germany", scheduler.Current.Tooltip);
    }

    [Fact]
    public async Task NetworkChange_KeepsLettersAndReconfirms()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE");
        var seen = new List<string>();
        using var scheduler = new GeoCountryScheduler(source, time, display => seen.Add(display.Letters));
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        int confirms = source.ConfirmCount;
        scheduler.NotifyNetworkOrResume();
        Assert.Equal("DE", scheduler.Current.Letters);
        Assert.DoesNotContain("??", seen);
        await WaitUntil(() => source.ConfirmCount > confirms);
        Assert.Equal("DE", scheduler.Current.Letters);
    }

    [Fact]
    public async Task FailureBackoff_ThenReturnsToIpWatch()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE") { SelfOk = false };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.SelfCount == 1);
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(1, source.SelfCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => source.SelfCount == 2);
        time.Advance(TimeSpan.FromSeconds(45));
        await WaitUntil(() => source.SelfCount == 3);
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntil(() => source.SelfCount == 4);
        time.Advance(GeoCountryPolicy.IpWatchInterval);
        await WaitUntil(() => source.SelfCount == 5);
        Assert.Equal(0, source.ConfirmCount);
    }

    [Fact]
    public async Task Stop_CancelsSchedule()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        scheduler.Stop();
        int selves = source.SelfCount;
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(selves, source.SelfCount);
        Assert.False(scheduler.IsEnabled);
        Assert.Equal(GeoCountryDisplay.Disabled, scheduler.Current);
    }

    [Fact]
    public async Task RetryAfter_IsHonoredOn429()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("8.8.8.8", "DE", "DE")
        {
            SelfOk = false,
            SelfRetryAfter = TimeSpan.FromSeconds(90)
        };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.SelfCount == 1);
        time.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(1, source.SelfCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => source.SelfCount == 2);
    }

    private static async Task<GeoCountryDisplay> WaitDisplay(GeoCountryScheduler scheduler, string letters, int minConfirms = 0)
    {
        await WaitUntil(() => scheduler.Current.Letters == letters);
        return scheduler.Current;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class FakeCountrySource : IGeoCountrySource
    {
        public FakeCountrySource(string ip, string selfCountry, string confirmCountry)
        {
            Ip = IPAddress.Parse(ip);
            SelfCountry = selfCountry;
            ConfirmCountry = confirmCountry;
        }

        public IPAddress Ip { get; set; }
        public string SelfCountry { get; set; }
        public string ConfirmCountry { get; set; }
        public bool SelfOk { get; set; } = true;
        public bool ConfirmOk { get; set; } = true;
        public TimeSpan? SelfRetryAfter { get; set; }
        public int SelfCount { get; private set; }
        public int ConfirmCount { get; private set; }
        public IPAddress? LastConfirmIp { get; private set; }
        public TaskCompletionSource<GeoCountrySelfResult>? SelfGate { get; init; }

        public GeoCountrySelfResult SelfValue
            => new(SelfOk, SelfOk ? Ip : null, SelfOk ? SelfCountry : null, SelfRetryAfter);

        public async Task<GeoCountrySelfResult> GetSelfAsync(CancellationToken cancellationToken)
        {
            SelfCount++;
            if (SelfGate is not null && SelfCount == 1)
            {
                return await SelfGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return SelfValue;
        }

        public Task<GeoCountryConfirmResult> ConfirmAsync(IPAddress ip, CancellationToken cancellationToken)
        {
            ConfirmCount++;
            LastConfirmIp = ip;
            return Task.FromResult(new GeoCountryConfirmResult(ConfirmOk, ConfirmOk ? ConfirmCountry : null, null));
        }
    }
}
