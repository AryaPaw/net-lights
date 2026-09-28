using System.IO.Compression;
using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class StateHistoryAndExportTests
{
    [Fact]
    public async Task AvailabilityHistoryStore_AsyncSaveUsesStableSnapshot()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-state-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset t = DateTimeOffset.UtcNow;
            var history = new AvailabilityHistory();
            history.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Unknown, false), t);
            AvailabilityHistorySnapshot snapshot = AvailabilityHistoryStore.Snapshot(history);
            history.Observe(Snapshot(GroupAvailability.Offline, GroupAvailability.Unknown, false), t.AddMinutes(1));
            AvailabilityHistorySnapshot newerSnapshot = AvailabilityHistoryStore.Snapshot(history);

            await AvailabilityHistoryStore.SaveAsync(newerSnapshot);
            await AvailabilityHistoryStore.SaveAsync(snapshot);

            Assert.Equal(3, AvailabilityHistoryStore.Load().Spans.Count);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void AvailabilityHistoryStore_RestoresBackupWhenPrimaryFileIsMissing()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-state-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var original = new AvailabilityHistory();
            original.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Offline, false), now);
            AvailabilityHistoryStore.Save(original);

            var newer = new AvailabilityHistory(original.Spans, original.LastObservedUtc);
            newer.Observe(Snapshot(GroupAvailability.Limited, GroupAvailability.Offline, false), now.AddSeconds(5));
            AvailabilityHistoryStore.Save(newer);
            File.Delete(AvailabilityHistoryStore.FilePath);

            AvailabilityHistory recovered = AvailabilityHistoryStore.Load();

            Assert.Equal(original.Spans, recovered.Spans);
            Assert.True(File.Exists(AvailabilityHistoryStore.FilePath));
            Assert.Equal(original.Spans, AvailabilityHistoryStore.Load().Spans);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void AvailabilityHistoryStore_RestoresBackupWhenPrimaryIsCorruptAndPreservesCorruptCopy()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-state-corrupt-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var original = new AvailabilityHistory();
            original.Observe(Snapshot(GroupAvailability.Online, GroupAvailability.Offline, false), now);
            AvailabilityHistoryStore.Save(original);

            var newer = new AvailabilityHistory(original.Spans, original.LastObservedUtc);
            newer.Observe(Snapshot(GroupAvailability.Limited, GroupAvailability.Offline, false), now.AddSeconds(5));
            AvailabilityHistoryStore.Save(newer);
            const string corrupt = "{ broken json";
            File.WriteAllText(AvailabilityHistoryStore.FilePath, corrupt);

            AvailabilityHistory recovered = AvailabilityHistoryStore.Load();

            Assert.Equal(original.Spans, recovered.Spans);
            Assert.Contains(Directory.GetFiles(temp, "availability-history.json.corrupt-*.json"), path => File.ReadAllText(path) == corrupt);
            Assert.Equal(original.Spans, AvailabilityHistoryStore.Load().Spans);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Load_MigratesLegacyStateHistoryAndPreservesOriginalFile()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            DateTimeOffset first = DateTimeOffset.UtcNow.AddHours(-2);
            DateTimeOffset second = first.AddHours(1);
            string legacyPath = Path.Combine(temp, "state-history.jsonl");
            File.WriteAllLines(legacyPath,
            [
                $"{{\"utc\":\"{first:O}\",\"ru\":\"Online\",\"world\":\"Offline\"}}",
                $"{{\"utc\":\"{second:O}\",\"ru\":\"Limited\",\"world\":\"Offline\"}}",
                $"{{\"utc\":\"{second.AddMinutes(1):O}\",\"ru\":\"Limited\",\"world\":\"Online\"}}"
            ]);

            AvailabilityHistory history = AvailabilityHistoryStore.Load();

            Assert.Equal(4, history.Spans.Count);
            Assert.Equal(first, history.Spans.Single(s => s.Group == EndpointGroup.Ru && s.State == GroupAvailability.Online).StartedUtc);
            Assert.Equal(second, history.Spans.Single(s => s.Group == EndpointGroup.Ru && s.State == GroupAvailability.Online).EndedUtc);
            Assert.Contains(history.Spans, s => s.Group == EndpointGroup.World && s.State == GroupAvailability.Offline && s.EndedUtc == second.AddMinutes(1));
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(AvailabilityHistoryStore.FilePath));
            Assert.Equal(history.Spans, AvailabilityHistoryStore.Load().Spans);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Load_LegacyFallbackDoesNotOverwriteUnreadableAvailabilityHistory()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-state-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            string currentPath = AvailabilityHistoryStore.FilePath;
            File.WriteAllText(currentPath, "keep this unreadable file");
            DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-1);
            File.WriteAllText(
                Path.Combine(temp, "state-history.jsonl"),
                $"{{\"utc\":\"{now:O}\",\"ru\":\"Online\",\"world\":\"Unknown\"}}\n");

            AvailabilityHistory history = AvailabilityHistoryStore.Load();

            Assert.Equal(2, history.Spans.Count);
            Assert.Equal("keep this unreadable file", File.ReadAllText(currentPath));
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Export_DoesNotCopyStateHistoryEvenIfLeftoverExists()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-exp-" + Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dest);
        try
        {
            SettingsStore.RootDirectory = root;
            File.WriteAllText(Path.Combine(root, "state-history.jsonl"), "{\"utc\":\"2026-01-01T00:00:00Z\",\"ru\":\"Online\",\"world\":\"Online\"}\n");
            string zip = DiagnosticExport.Export(EmptySnapshot(), new BoundedEventLog(), null, dest);
            Assert.True(File.Exists(zip));
            using ZipArchive archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, e => e.Name == "snapshot.json");
            Assert.Contains(archive.Entries, e => e.Name == "events.json");
            Assert.DoesNotContain(archive.Entries, e => e.Name.Equals("state-history.jsonl", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Export_StripsQuerySecretsFromSnapshotJson()
    {
        string dest = Path.Combine(Path.GetTempPath(), "nl-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            var endpoint = new EndpointView(
                "ru-secret",
                EndpointGroup.Ru,
                "infra",
                new Uri("https://example.test/path?token=REVIEW_FAKE_SECRET"),
                ProbeOutcome.Reachable,
                200,
                null,
                TimeSpan.FromMilliseconds(10),
                DateTimeOffset.UtcNow,
                true,
                false,
                null,
                false,
                EndpointStats.Empty);
            var snapshot = new MonitorSnapshot(
                1,
                DateTimeOffset.UtcNow,
                1,
                new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Online, "", null, false, [endpoint]),
                new GroupSnapshot(EndpointGroup.World, GroupAvailability.Unknown, "", null, false, []),
                false,
                null,
                false,
                null,
                false);
            string zip = DiagnosticExport.Export(snapshot, new BoundedEventLog(), null, dest);
            using ZipArchive archive = ZipFile.OpenRead(zip);
            ZipArchiveEntry json = Assert.Single(archive.Entries, e => e.Name == "snapshot.json");
            using Stream stream = json.Open();
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("REVIEW_FAKE_SECRET", text, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    private static MonitorSnapshot EmptySnapshot()
    {
        return new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Unknown, "", null, false, []),
            new GroupSnapshot(EndpointGroup.World, GroupAvailability.Unknown, "", null, false, []),
            true,
            null,
            false,
            null,
            false);
    }

    private static MonitorSnapshot Snapshot(GroupAvailability ru, GroupAvailability world, bool paused)
        => new(1, DateTimeOffset.UtcNow, 1, new GroupSnapshot(EndpointGroup.Ru, ru, "", null, false, []), new GroupSnapshot(EndpointGroup.World, world, "", null, false, []), true, null, false, null, paused);
}
