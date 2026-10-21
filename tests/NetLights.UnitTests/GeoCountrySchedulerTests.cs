using Microsoft.Extensions.Time.Testing;
using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class GeoCountrySchedulerTests
{
    [Fact]
    public async Task ConfiguredIntervalControlsCountryPollingAndRetryAfterSurvivesSettingChange()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("DE");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.SetPollingInterval(30);
        scheduler.Start();
        await WaitDisplay(scheduler, "DE");
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, source.LookupCount);
        source.LookupOk = false;
        source.RetryAfter = TimeSpan.FromHours(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => source.LookupCount == 2);
        // Wait for the serialized cycle to finish publishing before changing its timer.
        await WaitUntil(() => !scheduler.Current.Fresh);
        scheduler.SetPollingInterval(10);
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(2, source.LookupCount);
        time.Advance(TimeSpan.FromMinutes(30));
        await WaitUntil(() => source.LookupCount == 3);
    }
    [Fact]
    public void RefreshPolicy_IsTenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), GeoCountryPolicy.RefreshInterval);
        Assert.Equal(8_640, TimeSpan.FromDays(1).Ticks / GeoCountryPolicy.RefreshInterval.Ticks);
        Assert.Equal(500, NetLights.Networking.IpWhoUsageLedger.DailyLimit);
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
    public async Task CandidateDatabaseObservation_IsRecordedButCannotChooseMainCountry()
    {
        var source = new MutableSource(new GeoCountryLookupResult(true, "JP", null, "203.0.113.9",
            LocalCountry: "JP", OnlineCountry: "JP", IplocateCountry: "FR", IplocateDatabaseVersion: "mirror"));
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System, seen.Add);

        scheduler.Start();
        GeoCountryDisplay result = await WaitDisplay(scheduler, "JP");

        Assert.Equal("JP", result.Letters);
        Assert.Equal("FR", Assert.Single(seen).Observation?.IplocateCountry);
    }

    [Fact]
    public async Task UnchangedCountry_PublishesCompactablePollHeartbeat()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("AE");
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, time, seen.Add);
        scheduler.Start();
        await WaitDisplay(scheduler, "AE");

        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => source.LookupCount == 2);
        await WaitUntil(() => seen.Count == 2);

        Assert.Equal("AE", seen[1].Letters);
        Assert.Equal(time.GetUtcNow(), seen[1].Observation?.ObservedUntilUtc);
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
    public async Task PublicIpChangeWithinSameCountry_IsRecordedWithoutSplittingCountryStay()
    {
        var source = new MutableSource(new GeoCountryLookupResult(true, "DE", null,
            "203.0.113.1", LocalCountry: "DE", OnlineCountry: "DE", DatabaseVersion: "r1"));
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System, seen.Add);
        scheduler.Start();
        await WaitUntil(() => seen.Count > 0);

        source.Result = source.Result with { PublicIp = "203.0.113.2" };
        scheduler.NotifyNetworkOrResume();
        await WaitUntil(() => seen.Any(display => display.Observation?.PublicIpChanged == true));

        Assert.Equal("DE", scheduler.Current.Letters);
        Assert.Contains(seen, display => display.Observation?.PublicIpChanged == true);
    }

    [Fact]
    public async Task MissingSourceResult_IsRecordedAsAnObservation()
    {
        var source = new MutableSource(new GeoCountryLookupResult(true, "DE", null,
            "203.0.113.1", LocalCountry: "DE", OnlineCountry: "DE", DatabaseVersion: "r1"));
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System, seen.Add);
        scheduler.Start();
        await WaitUntil(() => seen.Any(display => display.Fresh));

        source.Result = new GeoCountryLookupResult(false, null, TimeSpan.FromSeconds(10),
            "203.0.113.1", Conflicted: true, LocalCountry: "DE", OnlineCountry: null, DatabaseVersion: "r1");
        scheduler.NotifyNetworkOrResume();
        await WaitUntil(() => seen.Any(display => display.Observation is { Confirmed: false, OnlineCountry: null }));

        Assert.Contains(seen, display => display.Observation is { Confirmed: false, LocalCountry: "DE", OnlineCountry: null });
    }

    [Fact]
    public async Task LocalEstimate_IsDisplayedButDoesNotExtendConfirmedCountry()
    {
        var time = new FakeTimeProvider();
        var source = new MutableSource(new GeoCountryLookupResult(true, "DE", null,
            "203.0.113.1", LocalCountry: "DE", OnlineCountry: "DE", ResolutionSource: "country.is"));
        var seen = new List<GeoCountryDisplay>();
        using var scheduler = new GeoCountryScheduler(source, time, seen.Add);
        scheduler.Start();
        await WaitUntil(() => seen.Any(display => display.Fresh));
        DateTimeOffset confirmedAt = time.GetUtcNow();

        source.Result = new GeoCountryLookupResult(false, null, TimeSpan.FromSeconds(10),
            "203.0.113.1", LocalCountry: "FR", DatabaseVersion: "r1", ResolutionSource: "local-estimate");
        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitUntil(() => seen.Any(display => display.ResolutionSource == "local-estimate"));

        GeoCountryDisplay estimate = seen.Last(display => display.ResolutionSource == "local-estimate");
        Assert.Equal("FR", estimate.Letters);
        Assert.False(estimate.Fresh);
        Assert.Equal(confirmedAt, estimate.ConfirmedThroughUtc);
        Assert.Null(estimate.Observation?.CountryCode);
        Assert.Equal("local-estimate", estimate.Observation?.ResolutionSource);
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
        await WaitUntil(() => !scheduler.Current.Fresh);
        Assert.Equal("AE", scheduler.Current.Letters);
        Assert.Contains("устарело", scheduler.Current.Tooltip, StringComparison.OrdinalIgnoreCase);
        time.Advance(TimeSpan.FromMinutes(16));

        await WaitDisplay(scheduler, "??");
        Assert.False(scheduler.Current.Fresh);
    }

    [Fact]
    public async Task ConflictingSources_DoNotKeepPriorCountryVisible()
    {
        var time = new FakeTimeProvider();
        var source = new FakeCountrySource("TR");
        using var scheduler = new GeoCountryScheduler(source, time);
        scheduler.Start();
        await WaitDisplay(scheduler, "TR");
        source.LookupOk = false;
        source.Conflicted = true;
        time.Advance(GeoCountryPolicy.RefreshInterval);
        await WaitDisplay(scheduler, "??");
        Assert.Contains("не подтверждена", scheduler.Current.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentExitIps_ShowUnstableRoute()
    {
        var source = new FakeCountrySource("DE") { LookupOk = false, DifferentIp = true };
        using var scheduler = new GeoCountryScheduler(source, TimeProvider.System);
        scheduler.Start();
        await WaitUntil(() => scheduler.Current.Letters == "??"
            && scheduler.Current.Tooltip.Contains("IP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("IP", scheduler.Current.Tooltip, StringComparison.OrdinalIgnoreCase);
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
        public bool Conflicted { get; set; }
        public bool DifferentIp { get; set; }
        public TimeSpan? RetryAfter { get; set; }
        public int LookupCount { get; private set; }
        public TaskCompletionSource<GeoCountryLookupResult>? LookupGate { get; init; }

        public GeoCountryLookupResult LookupValue
            => new(LookupOk, LookupOk ? Country : null, RetryAfter, Conflicted: Conflicted, DifferentIp: DifferentIp);

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

    private sealed class MutableSource(GeoCountryLookupResult result) : IGeoCountrySource
    {
        public GeoCountryLookupResult Result { get; set; } = result;
        public Task<GeoCountryLookupResult> GetCurrentAsync(CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }
}
