using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class HistoryLoadBoundsTests
{
    [Fact]
    public void LocationHistory_RejectsExcessiveRecordCountWithoutReplacingFile()
    {
        WithIsolatedRoot(() =>
        {
            string path = LocationHistoryStore.FilePath;
            File.WriteAllText(path, "{\"stays\":[],\"observations\":[" + string.Join(',', Enumerable.Repeat("null", 70_001)) + "]}");
            long originalSize = new FileInfo(path).Length;
            Assert.Empty(LocationHistoryStore.Load().Observations);
            Assert.Equal(HistoryLoadStatus.Corrupt, LocationHistoryStore.LastLoadStatus);
            Assert.Equal(originalSize, new FileInfo(path).Length);
        });
    }

    [Fact]
    public void AvailabilityHistory_RejectsExcessiveRecordCountWithoutReplacingFile()
    {
        WithIsolatedRoot(() =>
        {
            string path = AvailabilityHistoryStore.FilePath;
            File.WriteAllText(path, "{\"spans\":[" + string.Join(',', Enumerable.Repeat("null", 20_001)) + "]}");
            long originalSize = new FileInfo(path).Length;
            Assert.Empty(AvailabilityHistoryStore.Load().Spans);
            Assert.Equal(HistoryLoadStatus.Corrupt, AvailabilityHistoryStore.LastLoadStatus);
            Assert.Equal(originalSize, new FileInfo(path).Length);
        });
    }

    private static void WithIsolatedRoot(Action action)
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            action();
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }
}
