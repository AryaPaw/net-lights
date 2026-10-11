using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class SettingsRecoveryTests
{
    [Fact]
    public void RuntimeJournal_OmitsExceptionMessage()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            RuntimeJournal.Write("failure", new IOException("https://example.test/?token=REVIEW_FAKE_SECRET"));
            string file = Assert.Single(Directory.GetFiles(Path.Combine(root, "diagnostics"), "runs-*.jsonl"));
            string content = File.ReadAllText(file);
            Assert.Contains("failure", content);
            Assert.DoesNotContain("REVIEW_FAKE_SECRET", content);
            Assert.DoesNotContain("example.test", content);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OversizedSettings_AreRejectedBeforeDeserialization()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            const string validSettings = "{\"settingsVersion\":8}";
            File.WriteAllText(SettingsStore.SettingsPath, validSettings + new string(' ', 1024 * 1024 - validSettings.Length));
            Assert.Equal(SettingsLoadStatus.Loaded, SettingsStore.LoadDetailed().Status);
            File.AppendAllText(SettingsStore.SettingsPath, " ");
            Assert.Equal(SettingsLoadStatus.Corrupt, SettingsStore.LoadDetailed().Status);
            Assert.Throws<IOException>(() => SettingsStore.Save(new AppSettings()));
            Assert.Equal(1024 * 1024 + 1, new FileInfo(SettingsStore.SettingsPath).Length);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CorruptPrimary_UsesBackupAndPreservesOriginal()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            SettingsStore.Save(new AppSettings { WindowWidth = 1100 });
            SettingsStore.Save(new AppSettings { WindowWidth = 1200 });
            File.WriteAllText(SettingsStore.SettingsPath, "{broken");

            (AppSettings restored, SettingsLoadStatus status) = SettingsStore.LoadDetailed();
            Assert.Equal(SettingsLoadStatus.Recovered, status);
            Assert.Equal(1100, restored.WindowWidth);
            SettingsStore.Save(restored);
            Assert.Contains(Directory.GetFiles(root, "settings.json.corrupt-*.json"),
                file => File.ReadAllText(file) == "{broken");
            Assert.Equal(1100, SettingsStore.Load().WindowWidth);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CorruptOnlyCopy_IsNotOverwrittenWithDefaults()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            File.WriteAllText(SettingsStore.SettingsPath, "{broken");
            Assert.Equal(SettingsLoadStatus.Corrupt, SettingsStore.LoadDetailed().Status);
            Assert.Throws<IOException>(() => SettingsStore.Save(new AppSettings()));
            Assert.Equal("{broken", File.ReadAllText(SettingsStore.SettingsPath));
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OversizedEndpointPool_IsRejectedBeforeDeserializationAndPreserved()
    {
        string previous = SettingsStore.RootDirectory;
        string root = Path.Combine(Path.GetTempPath(), "nl-endpoints-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SettingsStore.RootDirectory = root;
            File.WriteAllText(SettingsStore.EndpointsPath, "[]" + new string(' ', 1024 * 1024 - 1));

            long originalSize = new FileInfo(SettingsStore.EndpointsPath).Length;
            (var config, string? warning) = SettingsStore.LoadPool();

            Assert.NotNull(warning);
            Assert.Contains("превышает", warning);
            Assert.True(config.UsingBuiltinPool);
            Assert.Equal(14, config.Endpoints.Count);
            Assert.Equal(originalSize, new FileInfo(SettingsStore.EndpointsPath).Length);
        }
        finally
        {
            SettingsStore.RootDirectory = previous;
            Directory.Delete(root, true);
        }
    }
}
