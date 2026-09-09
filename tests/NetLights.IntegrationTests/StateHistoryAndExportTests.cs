using System.IO.Compression;
using NetLights.App;
using NetLights.Core;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class StateHistoryAndExportTests
{
    [Fact]
    public void DeleteLegacyStateHistory_RemovesJsonlAndTemp()
    {
        string previous = SettingsStore.RootDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "nl-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SettingsStore.RootDirectory = temp;
            File.WriteAllText(Path.Combine(temp, "state-history.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(temp, "state-history.jsonl.tmp"), "x");
            SettingsStore.DeleteLegacyStateHistory();
            Assert.False(File.Exists(Path.Combine(temp, "state-history.jsonl")));
            Assert.False(File.Exists(Path.Combine(temp, "state-history.jsonl.tmp")));
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
            File.WriteAllText(Path.Combine(root, "state-history.jsonl"), "{\"utc\":\"2026-01-01T00:00:00Z\",\"ru\":\"Online\",\"vpn\":\"Online\"}\n");
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

    private static MonitorSnapshot EmptySnapshot()
    {
        return new MonitorSnapshot(
            1,
            DateTimeOffset.UtcNow,
            1,
            new GroupSnapshot(EndpointGroup.Ru, GroupAvailability.Unknown, "", null, false, []),
            new GroupSnapshot(EndpointGroup.Vpn, GroupAvailability.Unknown, "", null, false, []),
            true,
            null,
            false,
            null,
            false);
    }
}
