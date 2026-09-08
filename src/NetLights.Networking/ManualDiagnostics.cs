using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetLights.Core;

namespace NetLights.Networking;

public sealed record ManualDiagnosticResult(
    EndpointObservation Https,
    PingReply? Icmp,
    bool? Tcp,
    IPAddress? Address,
    string Note);

public static class ManualDiagnostics
{
    public static async Task<ManualDiagnosticResult> RunAsync(
        HttpsProbe probe,
        EndpointDefinition endpoint,
        TimeProvider time,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        EndpointObservation https = await probe.ProbeAsync(endpoint, 0, 0, deadline, cancellationToken).ConfigureAwait(false);
        probe.TryGetObservedAddress(endpoint.Uri, out IPAddress? address);
        PingReply? ping = null;
        bool? tcp = null;
        if (address is null)
        {
            return new ManualDiagnosticResult(https, null, null, address, "IP неизвестен, ICMP/TCP не выполнялись.");
        }

        try
        {
            using var pinger = new Ping();
            ping = await pinger.SendPingAsync(address, deadline).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ping = null;
        }

        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(deadline);
            await socket.ConnectAsync(new IPEndPoint(address, 443), cts.Token).ConfigureAwait(false);
            tcp = true;
        }
        catch (Exception)
        {
            tcp = false;
        }

        return new ManualDiagnosticResult(
            https,
            ping,
            tcp,
            address,
            "ICMP и TCP сравниваются с HTTPS отдельно; маршруты могут различаться.");
    }
}
