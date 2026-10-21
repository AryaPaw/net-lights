using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class DamagedBackupTests
{
    [Fact]
    public void SettingsWithOnlyDamagedBackupCannotOverwriteTheOnlyOriginal()
    {
        Isolated(() =>
        {
            const string damaged = "{\"settingsVersion\":8,\"windowWidth\":1234,broken";
            File.WriteAllText(SettingsStore.SettingsPath + ".bak", damaged);
            Assert.Equal(SettingsLoadStatus.Corrupt, SettingsStore.LoadDetailed().Status);
            for (int i = 0; i < 2; i++) Assert.Throws<IOException>(() => SettingsStore.Save(new AppSettings()));
            Assert.Equal(damaged, File.ReadAllText(SettingsStore.SettingsPath + ".bak"));
            Assert.False(File.Exists(SettingsStore.SettingsPath));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoryWithOnlyDamagedBackupPreservesItAcrossRepeatedSaves(bool locations)
    {
        Isolated(() =>
        {
            string path = locations ? LocationHistoryStore.FilePath : AvailabilityHistoryStore.FilePath;
            const string damaged = "{\"partialHistory\":true,broken";
            File.WriteAllText(path + ".bak", damaged);
            if (locations)
            {
                var history = LocationHistoryStore.Load();
                Assert.Equal(HistoryLoadStatus.Corrupt, LocationHistoryStore.LastLoadStatus);
                LocationHistoryStore.Save(history);
                LocationHistoryStore.Save(history);
                Assert.Equal(HistoryLoadStatus.Loaded, LoadLocationsStatus());
            }
            else
            {
                var history = AvailabilityHistoryStore.Load();
                Assert.Equal(HistoryLoadStatus.Corrupt, AvailabilityHistoryStore.LastLoadStatus);
                AvailabilityHistoryStore.Save(history);
                AvailabilityHistoryStore.Save(history);
                _ = AvailabilityHistoryStore.Load();
                Assert.Equal(HistoryLoadStatus.Loaded, AvailabilityHistoryStore.LastLoadStatus);
            }
            string original = Assert.Single(Directory.GetFiles(SettingsStore.RootDirectory, Path.GetFileName(path) + ".bak.corrupt-*.json"));
            Assert.Equal(damaged, File.ReadAllText(original));
        });
    }

    private static HistoryLoadStatus LoadLocationsStatus()
    {
        _ = LocationHistoryStore.Load();
        return LocationHistoryStore.LastLoadStatus;
    }

    private static void Isolated(Action action)
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "NetLightsDamagedBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { SettingsStore.RootDirectory = root; action(); }
        finally { SettingsStore.RootDirectory = previous; Directory.Delete(root, true); }
    }
}
