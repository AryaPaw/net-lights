using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class LocationSourceHistoryLayoutTests
{
    [Fact]
    public void CandidateDatabaseHistory_IsIndependentFromMainLocalAndOnlineResults()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        LocationObservation observation = Observation(now, "DE", "JP") with
        {
            IplocateCountry = "FR",
            IplocateDatabaseVersion = "mirror · 2026-09-29"
        };

        LocationSourceSpan span = Assert.Single(LocationSourceHistoryLayout.Build([observation], now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        LocationSourceCountryRun candidate = Assert.Single(LocationSourceHistoryLayout.CountryRuns([span], LocationSourceKind.IplocateDatabase));
        LocationSourceCountryRun local = Assert.Single(LocationSourceHistoryLayout.CountryRuns([span], LocationSourceKind.LocalDatabase));
        LocationSourceCountryRun online = Assert.Single(LocationSourceHistoryLayout.CountryRuns([span], LocationSourceKind.Online));

        Assert.Equal("FR", candidate.Country);
        Assert.Equal("DE", local.Country);
        Assert.Equal("JP", online.Country);
        Assert.Equal("FR", span.IplocateCountry);
        Assert.Equal("mirror · 2026-09-29", span.IplocateDatabaseVersion);
    }

    [Fact]
    public void CountryRuns_MergesIpChangesWithoutInventingCountryChanges()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        LocationSourceSpan[] spans =
        [
            new(start, start.AddSeconds(20), "PL", "PL", false, PublicIpChanged: true),
            new(start.AddSeconds(20), start.AddSeconds(40), "PL", "PL", false, PublicIpChanged: true),
            new(start.AddSeconds(40), start.AddMinutes(1), "PL", "DE", true),
            new(start.AddMinutes(1), start.AddMinutes(2), null, "DE", false),
            new(start.AddMinutes(2), start.AddMinutes(3), "PL", "PL", false)
        ];

        IReadOnlyList<LocationSourceCountryRun> local = LocationSourceHistoryLayout.CountryRuns(spans, LocationSourceKind.LocalDatabase);
        IReadOnlyList<LocationSourceCountryRun> online = LocationSourceHistoryLayout.CountryRuns(spans, LocationSourceKind.Online);

        Assert.Equal(2, local.Count);
        Assert.Equal(new LocationSourceCountryRun(start, start.AddMinutes(1), "PL"), local[0]);
        Assert.Equal(start.AddMinutes(2), local[1].StartedUtc);
        Assert.Equal(3, online.Count);
        Assert.Equal(new LocationSourceCountryRun(start, start.AddSeconds(40), "PL"), online[0]);
        Assert.Equal("DE", online[1].Country);
        Assert.Equal(start.AddMinutes(2), online[1].EndedUtc);
    }

    [Fact]
    public void Build_ExposesSourceConflictAndClearsItOnAgreement()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-29T10:00:00Z");
        LocationObservation[] observations =
        [
            Observation(start, "DE", "DE"),
            Observation(start.AddMinutes(1), "DE", "TR"),
            Observation(start.AddMinutes(2), "DE", "DE")
        ];

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(observations, start.AddMinutes(3), TimeSpan.FromMinutes(3));

        Assert.Equal(3, spans.Count);
        Assert.False(spans[0].Conflict);
        Assert.True(spans[1].Conflict);
        Assert.Equal("DE", spans[1].LocalCountry);
        Assert.Equal("TR", spans[1].OnlineCountry);
        Assert.False(spans[2].Conflict);
    }

    [Fact]
    public void Build_LeavesAreaBeforeFirstObservationAsNoData()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-29T10:00:00Z");
        LocationObservation[] observations = [Observation(start.AddMinutes(1), "DE", "DE")];

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(observations, start.AddMinutes(2), TimeSpan.FromMinutes(2));

        Assert.Equal(2, spans.Count);
        Assert.Null(spans[0].LocalCountry);
        Assert.Equal(start, spans[0].StartedUtc);
        Assert.Equal("DE", spans[1].LocalCountry);
        Assert.Equal(start.AddMinutes(1), spans[1].StartedUtc);
    }

    [Fact]
    public void Build_DoesNotShowOldObservationAsLiveDuringLongGap()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-29T10:00:00Z");
        LocationObservation[] observations = [Observation(start, "DE", "DE")];
        DateTimeOffset now = start.AddMinutes(10);

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(observations, now, TimeSpan.FromMinutes(5));

        LocationSourceSpan span = Assert.Single(spans);
        Assert.Equal(now - TimeSpan.FromMinutes(5), span.StartedUtc);
        Assert.Equal(now, span.EndedUtc);
        Assert.Null(span.LocalCountry);
        Assert.Null(span.OnlineCountry);
    }

    [Fact]
    public void Build_MarksComputerOffGapBetweenObservationsAsNoData()
    {
        DateTimeOffset beforeShutdown = DateTimeOffset.Parse("2026-09-29T21:43:00Z");
        DateTimeOffset afterStartup = beforeShutdown.AddHours(11);
        LocationObservation[] observations =
        [
            Observation(beforeShutdown, "DE", "DE"),
            Observation(afterStartup, "DE", "TR")
        ];

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(
            observations, afterStartup.AddMinutes(1), TimeSpan.FromHours(12));

        Assert.Contains(spans, span => span.StartedUtc == beforeShutdown.AddMinutes(2)
            && span.EndedUtc == afterStartup && span.LocalCountry is null && span.OnlineCountry is null);
        Assert.DoesNotContain(spans, span => span.LocalCountry is not null
            && span.StartedUtc < afterStartup && span.EndedUtc > beforeShutdown.AddMinutes(2));
    }

    [Fact]
    public void Build_RetainsThirdSourceVoteForConflictTooltip()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-29T10:01:00Z");
        LocationObservation observation = Observation(now.AddMinutes(-1), "DE", "TR") with { ThirdCountry = "DE" };

        LocationSourceSpan span = Assert.Single(LocationSourceHistoryLayout.Build([observation], now, TimeSpan.FromMinutes(1)));

        Assert.True(span.Conflict);
        Assert.Equal("DE", span.ThirdCountry);
    }

    [Fact]
    public void Build_ExtendsUnchangedSourcesOnlyAcrossConfirmedCountryStay()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        LocationObservation[] observations =
        [
            Observation(start, "PL", "PL") with { PublicIp = "203.0.113.8" },
            Observation(start.AddMinutes(30), "PL", "PL")
        ];
        LocationStay[] stays =
        [
            new("PL", start, start.AddMinutes(20)),
            new("PL", start.AddMinutes(30), null)
        ];

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(
            observations, start.AddMinutes(40), TimeSpan.FromMinutes(40), stays);

        Assert.Contains(spans, span => span.StartedUtc == start && span.EndedUtc == start.AddMinutes(20)
            && span.LocalCountry == "PL" && span.OnlineCountry == "PL" && span.PublicIp == "203.0.113.8");
        Assert.Contains(spans, span => span.StartedUtc == start.AddMinutes(20)
            && span.EndedUtc == start.AddMinutes(30) && span.LocalCountry is null);
        Assert.Contains(spans, span => span.StartedUtc == start.AddMinutes(30)
            && span.EndedUtc == start.AddMinutes(40) && span.OnlineCountry == "PL");
    }

    [Fact]
    public void Build_NewPollCoverageStopsAfterChecksStopEvenIfCountryStayIsOpen()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-30T10:00:00Z");
        LocationObservation observation = Observation(start, "PL", "PL") with
        {
            ObservedUntilUtc = start.AddMinutes(10)
        };

        IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(
            [observation], start.AddMinutes(30), TimeSpan.FromMinutes(30), [new LocationStay("PL", start, null)]);

        Assert.Contains(spans, span => span.StartedUtc == start && span.EndedUtc == start.AddMinutes(12)
            && span.LocalCountry == "PL" && !span.InferredFromStay);
        Assert.Contains(spans, span => span.StartedUtc == start.AddMinutes(12)
            && span.EndedUtc == start.AddMinutes(30) && span.LocalCountry is null);
    }

    [Fact]
    public void Build_KeepsTheSameKnownAndUnknownIntervalsAcrossAllChartPeriods()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-30T15:00:00Z");
        DateTimeOffset first = now.AddMinutes(-80);
        DateTimeOffset second = now.AddMinutes(-40);
        LocationObservation[] observations =
        [
            Observation(first, "PL", "PL") with { ObservedUntilUtc = first.AddMinutes(10) },
            Observation(second, "PL", "PL") with { ObservedUntilUtc = now }
        ];
        LocationStay[] stays =
        [
            new("PL", first, first.AddMinutes(12)),
            new("PL", second, null)
        ];

        foreach (TimeSpan period in LocationChartWindows.All)
        {
            IReadOnlyList<LocationSourceSpan> spans = LocationSourceHistoryLayout.Build(observations, now, period, stays);
            DateTimeOffset visibleStart = now - period;
            DateTimeOffset gapStart = first.AddMinutes(12) < visibleStart ? visibleStart : first.AddMinutes(12);
            Assert.Contains(spans, span => span.StartedUtc == gapStart
                && span.EndedUtc == second && span.LocalCountry is null && span.OnlineCountry is null);
            Assert.Contains(spans, span => span.StartedUtc == second
                && span.EndedUtc == now && span.LocalCountry == "PL" && span.OnlineCountry == "PL");
        }
    }

    private static LocationObservation Observation(DateTimeOffset at, string? local, string? online)
        => new(at, null, local == online ? local : null, local, online, null, "test", false, false, false, local == online);
}
