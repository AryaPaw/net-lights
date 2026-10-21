using NetLights.App;
using NetLights.Updates;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class SilentUpdateRuntimeTests
{
    [Fact]
    public async Task PortableCopyExitsUpdateLoopBeforeNetworkOrUserSettingsAreQueried()
    {
        string previous = SettingsStore.RootDirectory;
        string directory = Path.Combine(Path.GetTempPath(), "NetLightsPortableUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var gate = new SemaphoreSlim(1, 1);
        Task? loop = null;
        int settingsReads = 0;
        bool exit = false;
        try
        {
            SettingsStore.RootDirectory = directory;
            loop = SilentUpdateRuntime.Loop(() => { settingsReads++; return true; }, "1.5.1",
                Path.Combine(directory, "NetLights.exe"), () => exit = true, gate, cancellation.Token);
            await loop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, settingsReads);
            Assert.False(exit);
            Assert.False(Directory.Exists(Path.Combine(directory, "updates")));
            string journal = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(directory, "diagnostics"), "runs-*.jsonl")));
            Assert.Contains("update-loop-ineligible", journal);
        }
        finally
        {
            cancellation.Cancel();
            if (loop is not null)
            {
                try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) { }
            }
            SettingsStore.RootDirectory = previous;
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(SilentUpdateOutcome.Skipped, 43200)]
    [InlineData(SilentUpdateOutcome.NoUpdate, 43200)]
    [InlineData(SilentUpdateOutcome.Busy, 120)]
    [InlineData(SilentUpdateOutcome.Failed, 120)]
    public void ContinuingOutcomesUseTheExpectedRetryDelay(SilentUpdateOutcome outcome, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), SilentUpdateRuntime.DelayAfter(outcome));

    [Fact]
    public void OfflineKeepsTheExistingNetworkRetryPolicy()
        => Assert.Equal(NetworkWaitPolicy.OfflineRetry, SilentUpdateRuntime.DelayAfter(SilentUpdateOutcome.Offline));
}
