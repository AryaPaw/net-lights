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

    [Fact]
    public void BoundedEventLog_SnapshotDropsEntriesOlderThanRetention()
    {
        var log = new BoundedEventLog();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        log.Add(now.AddHours(-49), "old", "expired");
        log.Add(now, "new", "fresh");
        IReadOnlyList<LogEntry> snap = log.Snapshot();
        Assert.DoesNotContain(snap, e => e.Code == "old");
        Assert.Contains(snap, e => e.Code == "new");
    }
}
