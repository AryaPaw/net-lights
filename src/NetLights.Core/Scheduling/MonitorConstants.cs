namespace NetLights.Core;

public static class MonitorConstants
{
    public static readonly TimeSpan GroupInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan GroupOffset = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MinEndpointInterval = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ConfirmationTriggerSilence = TimeSpan.Zero;
    public const int MinUnreachableInfraForPartialOffline = 3;
    public static readonly TimeSpan ConfirmationCooldown = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan EpisodeDuration = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan NetworkDebounce = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ManualDiagnosticsDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SnapshotDeliveryBudget = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ExpiredRetryAfterPause = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UiHeartbeat = TimeSpan.FromSeconds(1);

    public const int EndpointsPerGroup = 7;
    public const int MinInfrastructuresPerGroup = 3;
    public const int MaxPhysicalInflight = 4;
    public const int MaxGroupInflight = 2;
    public const int MaxStartsPerSecond = 4;
    public const int MaxStartsPerMinute = 120;
    public const int MaxConsecutiveFailureMarks = 32;
    public const int MaxEpisodeNewProbes = 7;
    public const int MaxLogEntries = 2000;
    public const int MaxLogBytes = 2 * 1024 * 1024;
    public const int MaxResponseHeadersKiB = 32;
    public const int MaxConnectionsPerServer = 1;
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(48);
}
