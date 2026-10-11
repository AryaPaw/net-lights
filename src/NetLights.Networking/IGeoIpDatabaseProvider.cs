namespace NetLights.Networking;

internal interface IGeoIpDatabaseProvider : IDisposable
{
    V2FlyGeoIpDatabase? Current { get; }
    string? Version { get; }
    void Start();
    void CheckForUpdate();
}
