using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NetLights.Core;

namespace NetLights.Networking;

public static class HttpsProbeFactory
{
    public static HttpsProbe CreateProduction(TimeProvider time, string version)
        => new(time, version);

    public static HttpsProbe CreateWithCustomTrust(TimeProvider time, string version, X509Certificate2 root, string hostName)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        policy.CustomTrustStore.Add(root);
        var ssl = new SslClientAuthenticationOptions
        {
            CertificateChainPolicy = policy
        };
        return new HttpsProbe(time, version, ssl);
    }
}
