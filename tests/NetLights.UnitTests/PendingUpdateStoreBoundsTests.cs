using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class PendingUpdateStoreBoundsTests
{
    [Fact]
    public void OversizedFiles_AreIgnoredWithoutChangingThem()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-update-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PendingUpdateStore(root);
            store.WritePending(new PendingUpdate("1.5.0", "installer.exe", new string('a', 64), DateTimeOffset.UtcNow));
            store.WriteResult("1.5.0", false, "Installer failed");
            foreach (string path in new[] { store.PendingPath, store.ResultPath })
                File.AppendAllText(path, new string(' ', 64 * 1024 - (int)new FileInfo(path).Length));
            Assert.NotNull(store.ReadPending());
            Assert.True(store.TryReadLastFailure(out _));
            File.AppendAllText(store.PendingPath, " ");
            File.AppendAllText(store.ResultPath, " ");
            Assert.Null(store.ReadPending());
            Assert.False(store.TryReadLastFailure(out _));
            Assert.Equal(64 * 1024 + 1, new FileInfo(store.PendingPath).Length);
            Assert.Equal(64 * 1024 + 1, new FileInfo(store.ResultPath).Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
