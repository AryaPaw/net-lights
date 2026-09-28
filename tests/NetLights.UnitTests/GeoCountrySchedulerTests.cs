using Microsoft.Extensions.Time.Testing;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountrySchedulerTests
{
    [Fact]
    public void RefreshPolicy_StaysWithinFreeProviderDailyQuota()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), GeoCountryPolicy.RefreshInterval);
        Assert.True(TimeSpan.FromDays(1).Ticks / GeoCountryPolicy.RefreshInterval.Ticks < 1000);
    }

    [Fact]
    public async Task InitialLookup_PublishesCountryFromSingleSource()
    {
        var source = new FakeCountrySource("AE");
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();

        GeoCountryDisplay display = await WaitDisplay(scheduler, "AE");

        Assert.Equal("ОАЭ / United Arab Emirates", display.Tooltip);
        Assert.Equal(1, source.LookupCount);
    }

    [Fact]
    public async Task UnchangedCountry_RefreshesWithoutRepublishing()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("AE");
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, time, seen.Add);
        scheduler.Start();
        await WaitDisplay(scheduler, "AE");

        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => source.LookupCount == 2);

        Assert.Single(seen);
        Assert.Equal("AE", scheduler.Current.Letters);
    }

    [Fact]
    public async Task CountryChangeFromSameProvider_IsPublished()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("NL");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "NL");

        source.Country = "AE";
        time.Advance(GeoCountryPolicy.RefreshInterval);

        GeoCountryDisplay display = await WaitDisplay(scheduler, "AE");
        Assert.Equal("ОАЭ / United Arab Emirates", display.Tooltip);
        Assert.Equal(2, source.LookupCount);
    }

    [Fact]
    public async Task OneInflight_SkipsOverlappingTicks()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE")
        {
            LookupGate = new TaskCompletionSource<GeoCountryLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.LookupCount == 1);
        time.Advance(GeoCountryPolicy.RefreshInterval);
        time.Advance(GeoCountryPolicy.RefreshInterval);
        Assert.Equal(1, source.LookupCount);

        source.LookupGate.SetResult(source.LookupValue);
        await WaitDisplay(scheduler, "DE");
        Assert.Equal(1, source.LookupCount);
    }

    [Fact]
    public async Task NetworkChange_RefreshesImmediatelyAndKeepsLastCountryUntilResult()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");

        source.Country = "AE";
        scheduler.NotifyNetworkOrResume();

        GeoCountryDisplay display = await WaitDisplay(scheduler, "AE");
        Assert.Equal(2, source.LookupCount);
        Assert.Equal("AE", display.Letters);
    }

    [Fact]
    public async Task FailureBackoff_ThenReturnsToRefreshInterval()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE") { LookupOk = false };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.LookupCount == 1);

        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(1, source.LookupCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => source.LookupCount == 2);
        time.Advance(TimeSpan.FromSeconds(45));
        await WaitUntil(() => source.LookupCount == 3);
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntil(() => source.LookupCount == 4);
        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => source.LookupCount == 5);
    }

    [Fact]
    public async Task StaleAfterRepeatedFailures_MarksLastCountryStale()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("AE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "AE");
        source.LookupOk = false;

        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => source.LookupCount == 2);
        time.Advance(TimeSpan.FromSeconds(15));
        await WaitUntil(() => source.LookupCount == 3);
        time.Advance(TimeSpan.FromSeconds(45));
        await WaitUntil(() => source.LookupCount == 4);
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntil(() => source.LookupCount == 5);
        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => source.LookupCount == 6);
        Assert.True(scheduler.Current.Fresh);
        time.Advance(GeoCountryPolicy.RefreshInterval);

        await WaitUntil(() => !scheduler.Current.Fresh);
        Assert.Equal("AE", scheduler.Current.Letters);
        Assert.Contains("устарело", scheduler.Current.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_CancelsSchedule()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        scheduler.Stop();
        int calls = source.LookupCount;
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);

        Assert.Equal(calls, source.LookupCount);
        Assert.False(scheduler.IsEnabled);
        Assert.Equal(GeoCountryDisplay.Disabled, scheduler.Current);
    }

    [Fact]
    public async Task RetryAfter_IsHonoredOn429()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE")
        {
            LookupOk = false,
            RetryAfter = TimeSpan.FromSeconds(90)
        };
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitUntil(() => source.LookupCount == 1);

        time.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(1, source.LookupCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => source.LookupCount == 2);
    }

    private static async Task<GeoCountryDisplay> WaitDisplay(GeoCountryScheduler scheduler, string letters)
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

    private sealed class FakeCountrySource(string country) : IGeoCountrySource
    {
        public string Country { get; set; } = country;
        public bool LookupOk { get; set; } = true;
        public TimeSpan? RetryAfter { get; set; }
        public int LookupCount { get; private set; }
        public TaskCompletionSource<GeoCountryLookupResult>? LookupGate { get; init; }

        public GeoCountryLookupResult LookupValue
            => new(LookupOk, LookupOk ? Country : null, RetryAfter);

        public async Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
        {
            LookupCount++;
            if (LookupGate is not null && LookupCount == 1)
            {
                return await LookupGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return LookupValue;
        }
    }
}
