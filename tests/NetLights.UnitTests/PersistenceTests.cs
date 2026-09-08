using NetLights.Core;
using Xunit;

namespace NetLights.UnitTests;

public sealed class PersistenceTests
{
    [Fact]
    public void BoundedEventLog_SnapshotIsStableWhileMutating()
    {
        var log = new BoundedEventLog();
        log.Add(DateTimeOffset.UtcNow, "a", "one");
        IReadOnlyList<LogEntry> snap = log.Snapshot();
        log.Add(DateTimeOffset.UtcNow, "b", "two");
        Assert.Single(snap);
        Assert.Equal(2, log.Snapshot().Count);
    }
}
