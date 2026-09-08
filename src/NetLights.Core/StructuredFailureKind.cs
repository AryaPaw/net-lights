namespace NetLights.Core;

public enum StructuredFailureKind
{
    None,
    DnsFailure,
    TcpRefused,
    TcpReset,
    TransportTimeout,
    TlsProtocol,
    TlsHandshake,
    CertificateTrust,
    CertificateName,
    CertificateExpired,
    InvalidHeaders,
    OversizedHeaders,
    LocalResource,
    Cancelled,
    DeadlineExceeded,
    MonitorError,
    NetworkUnavailable
}
