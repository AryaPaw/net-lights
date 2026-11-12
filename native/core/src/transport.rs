use crate::{
    model::{Outcome, ProbeResult},
    monitor::ProbeWork,
    traffic::*,
};
use bytes::Bytes;
use chrono::{DateTime, Utc};
use http_body_util::{BodyExt, Empty};
use hyper::{Method, Request, Uri, Version};
use hyper_util::{
    client::legacy::{
        Client,
        connect::{
            Connected, Connection, HttpConnector,
            dns::{GaiAddrs, GaiResolver, Name},
        },
    },
    rt::{TokioExecutor, TokioIo, TokioTimer},
};
use rustls_platform_verifier::BuilderVerifierExt;
use std::{
    collections::{BTreeMap, HashMap},
    error::Error,
    fmt,
    future::Future,
    io,
    net::SocketAddr,
    path::Path,
    pin::Pin,
    sync::{Arc, Mutex, atomic::Ordering},
    task::{Context, Poll},
    time::{Duration, Instant},
};
use tokio::{
    io::{AsyncRead, AsyncWrite, AsyncWriteExt, ReadBuf},
    net::TcpStream,
    sync::Semaphore,
};
use tokio_rustls::{TlsConnector, client::TlsStream};
use tower_service::Service;
use url::Url;

pub struct TrackedIo<T> {
    inner: T,
    counter: Arc<Mutex<Arc<Counter>>>,
    _physical: Option<tokio::sync::OwnedSemaphorePermit>,
}
impl<T: AsyncRead + Unpin> AsyncRead for TrackedIo<T> {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let this = self.get_mut();
        let before = buffer.filled().len();
        let result = Pin::new(&mut this.inner).poll_read(cx, buffer);
        if let Poll::Ready(Ok(())) = &result {
            this.counter
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .received
                .fetch_add((buffer.filled().len() - before) as u64, Ordering::Relaxed);
        }
        result
    }
}
impl<T: AsyncWrite + Unpin> AsyncWrite for TrackedIo<T> {
    fn poll_write(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        bytes: &[u8],
    ) -> Poll<io::Result<usize>> {
        let this = self.get_mut();
        let result = Pin::new(&mut this.inner).poll_write(cx, bytes);
        if let Poll::Ready(Ok(n)) = &result {
            this.counter
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .sent
                .fetch_add(*n as u64, Ordering::Relaxed);
        }
        result
    }
    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().inner).poll_flush(cx)
    }
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().inner).poll_shutdown(cx)
    }
}
pub struct SecureConnection(TlsStream<TrackedIo<TcpStream>>);
impl AsyncRead for SecureConnection {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().0).poll_read(cx, b)
    }
}
impl AsyncWrite for SecureConnection {
    fn poll_write(self: Pin<&mut Self>, cx: &mut Context<'_>, b: &[u8]) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.get_mut().0).poll_write(cx, b)
    }
    fn poll_flush(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().0).poll_flush(cx)
    }
    fn poll_shutdown(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.get_mut().0).poll_shutdown(cx)
    }
}
impl Connection for SecureConnection {
    fn connected(&self) -> Connected {
        Connected::new()
    }
}

#[derive(Debug)]
enum ConnectFailure {
    Target,
    Deadline,
    Network(Box<dyn Error + Send + Sync>),
    Tls(io::Error),
}
impl fmt::Display for ConnectFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "HTTPS connection failed")
    }
}
impl Error for ConnectFailure {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Tls(e) => Some(e),
            Self::Network(e) => Some(e.as_ref()),
            _ => None,
        }
    }
}
#[derive(Debug)]
struct DnsFailure(io::Error);
impl fmt::Display for DnsFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("DNS resolution failed")
    }
}
impl Error for DnsFailure {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        Some(&self.0)
    }
}
#[derive(Clone)]
struct Resolver<R = GaiResolver>(R);
impl<R> Service<Name> for Resolver<R>
where
    R: Service<Name, Response = GaiAddrs, Error = io::Error>,
    R::Future: Send + 'static,
{
    type Response = GaiAddrs;
    type Error = DnsFailure;
    type Future = Pin<Box<dyn Future<Output = Result<GaiAddrs, DnsFailure>> + Send>>;
    fn poll_ready(&mut self, cx: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
        self.0.poll_ready(cx).map_err(DnsFailure)
    }
    fn call(&mut self, name: Name) -> Self::Future {
        let future = self.0.call(name);
        Box::pin(async move { future.await.map_err(DnsFailure) })
    }
}
struct Origin {
    request: Arc<Semaphore>,
    physical: Arc<Semaphore>,
    counter: Arc<Mutex<Arc<Counter>>>,
    observed: Mutex<Option<ObservedAddress>>,
}
struct Admission {
    origins: Mutex<HashMap<String, Arc<Origin>>>,
    connecting: Arc<Semaphore>,
}
impl Admission {
    fn origin(&self, key: &str) -> anyhow::Result<Arc<Origin>> {
        let mut origins = self.origins.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(origin) = origins.get(key) {
            return Ok(origin.clone());
        }
        anyhow::ensure!(origins.len() < 64, "Origin limit reached");
        let origin = Arc::new(Origin {
            request: Arc::new(Semaphore::new(1)),
            physical: Arc::new(Semaphore::new(1)),
            counter: Arc::new(Mutex::new(Arc::new(Counter::default()))),
            observed: Mutex::new(None),
        });
        origins.insert(key.into(), origin.clone());
        Ok(origin)
    }
}
#[derive(Clone)]
pub struct Connector {
    admission: Arc<Admission>,
    tls: TlsConnector,
    deadline: Duration,
}
impl Service<Uri> for Connector {
    type Response = TokioIo<SecureConnection>;
    type Error = Box<dyn Error + Send + Sync>;
    type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;
    fn poll_ready(&mut self, _: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
        Poll::Ready(Ok(()))
    }
    fn call(&mut self, uri: Uri) -> Self::Future {
        let this = self.clone();
        Box::pin(async move {
            if uri.scheme_str() != Some("https") {
                return Err(Box::new(ConnectFailure::Target) as Self::Error);
            }
            // hyper-util may detach a started connection when an idle checkout
            // wins the race. This timeout belongs to the connector itself, and
            // its socket permit survives until the actual stream is dropped.
            tokio::time::timeout(this.deadline, async {
                let host = uri
                    .host()
                    .ok_or(ConnectFailure::Target)?
                    .trim_start_matches('[')
                    .trim_end_matches(']')
                    .to_owned();
                let authority = uri.authority().ok_or(ConnectFailure::Target)?;
                let origin = this.admission.origin(&format!(
                    "https://{}",
                    authority.as_str().to_ascii_lowercase()
                ))?;
                let physical = origin.physical.clone().acquire_owned().await?;
                let _connecting = this.admission.connecting.clone().acquire_owned().await?;
                let mut tcp = HttpConnector::new_with_resolver(Resolver(GaiResolver::new()));
                tcp.enforce_http(false);
                tcp.set_nodelay(true);
                let stream = tcp
                    .call(uri)
                    .await
                    .map_err(|e| ConnectFailure::Network(Box::new(e)))?
                    .into_inner();
                if let Ok(address) = stream.peer_addr() {
                    *origin.observed.lock().unwrap_or_else(|e| e.into_inner()) =
                        Some(ObservedAddress {
                            address,
                            observed_utc: Utc::now(),
                        });
                }
                let stream = TrackedIo {
                    inner: stream,
                    counter: origin.counter.clone(),
                    _physical: Some(physical),
                };
                let name = rustls::pki_types::ServerName::try_from(host)
                    .map_err(|_| ConnectFailure::Target)?;
                let secure = this
                    .tls
                    .connect(name, stream)
                    .await
                    .map_err(ConnectFailure::Tls)?;
                Ok::<_, Self::Error>(TokioIo::new(SecureConnection(secure)))
            })
            .await
            .map_err(|_| Box::new(ConnectFailure::Deadline) as Self::Error)?
        })
    }
}
type HttpClient = Client<Connector, Empty<Bytes>>;
struct Pool {
    client: HttpClient,
    created: Instant,
}
#[derive(Clone)]
pub struct Transport {
    pool: Arc<Mutex<Pool>>,
    connector: Connector,
    traffic: Arc<Traffic>,
    category: Category,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ObservedAddress {
    pub address: SocketAddr,
    pub observed_utc: DateTime<Utc>,
}
#[derive(Debug)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: BTreeMap<String, String>,
    pub body: Vec<u8>,
}
#[derive(Debug)]
pub enum DownloadStep {
    Redirect(Url),
    Complete { bytes: u64, sha256: String },
}
impl Transport {
    pub fn new(traffic: Arc<Traffic>, category: Category) -> anyhow::Result<Self> {
        let config = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_safe_default_protocol_versions()?
        .with_platform_verifier()?
        .with_no_client_auth();
        Self::configured(traffic, category, config, Duration::from_secs(10))
    }
    fn configured(
        traffic: Arc<Traffic>,
        category: Category,
        config: rustls::ClientConfig,
        deadline: Duration,
    ) -> anyhow::Result<Self> {
        let connector = Connector {
            admission: Arc::new(Admission {
                origins: Mutex::new(HashMap::new()),
                connecting: Arc::new(Semaphore::new(4)),
            }),
            tls: TlsConnector::from(Arc::new(config)),
            deadline,
        };
        Ok(Self {
            pool: Arc::new(Mutex::new(Pool {
                client: build_client(connector.clone()),
                created: Instant::now(),
            })),
            connector,
            traffic,
            category,
        })
    }
    /// Related regular sources must explicitly share a pool and its physical
    /// admission. Byte accounting follows the current request, including TLS.
    pub fn for_category(&self, category: Category) -> Self {
        Self {
            category,
            ..self.clone()
        }
    }
    /// This is the last actual HTTPS peer, never a second DNS lookup. A reused
    /// connection refreshes its observation time only after receiving headers.
    pub fn observed_address(&self, url: &Url) -> Option<ObservedAddress> {
        let origin = self
            .connector
            .admission
            .origins
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .get(&url.origin().ascii_serialization())
            .cloned()?;
        *origin.observed.lock().unwrap_or_else(|e| e.into_inner())
    }
    pub fn recycle(&self) {
        let mut pool = self.pool.lock().unwrap_or_else(|e| e.into_inner());
        *pool = Pool {
            client: build_client(self.connector.clone()),
            created: Instant::now(),
        };
    }
    async fn response(
        &self,
        method: Method,
        url: &Url,
        accept: &str,
    ) -> anyhow::Result<(
        hyper::Response<hyper::body::Incoming>,
        tokio::sync::OwnedSemaphorePermit,
    )> {
        anyhow::ensure!(
            url.scheme() == "https" && url.username().is_empty() && url.password().is_none(),
            "HTTPS target required"
        );
        let key = url.origin().ascii_serialization();
        let origin = self.connector.admission.origin(&key)?;
        let permit = origin.request.clone().acquire_owned().await?;
        *origin.counter.lock().unwrap_or_else(|e| e.into_inner()) = self
            .traffic
            .counter(self.category, url.host_str().unwrap_or_default());
        let client = {
            let mut pool = self.pool.lock().unwrap_or_else(|e| e.into_inner());
            if pool.created.elapsed() >= Duration::from_secs(60) {
                *pool = Pool {
                    client: build_client(self.connector.clone()),
                    created: Instant::now(),
                };
            }
            pool.client.clone()
        };
        let request = Request::builder()
            .method(method)
            .uri(url.as_str())
            .version(Version::HTTP_11)
            .header(
                "User-Agent",
                concat!("NetLights/", env!("CARGO_PKG_VERSION")),
            )
            .header("Accept", accept)
            .body(Empty::new())?;
        let response = client.request(request).await?;
        if let Some(address) = origin
            .observed
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_mut()
        {
            address.observed_utc = Utc::now();
        }
        Ok((response, permit))
    }
    pub async fn probe(&self, work: &ProbeWork) -> ProbeResult {
        let start = Instant::now();
        let result = tokio::time::timeout(
            Duration::from_millis(work.deadline_ms),
            self.response(Method::HEAD, &work.endpoint.uri, "*/*"),
        )
        .await;
        let mut probe = match result {
            Ok(Ok((response, _permit))) if (200..=599).contains(&response.status().as_u16()) => {
                let status = response.status().as_u16();
                ProbeResult {
                    outcome: Outcome::Reachable,
                    status: Some(status),
                    failure: None,
                    elapsed_ms: 0,
                    retry_after_ms: retry_after(
                        status,
                        response
                            .headers()
                            .get("retry-after")
                            .and_then(|v| v.to_str().ok()),
                        Utc::now(),
                    ),
                }
            }
            Ok(Ok(_)) => ProbeResult {
                outcome: Outcome::Indeterminate,
                status: None,
                failure: Some("invalid-http"),
                elapsed_ms: 0,
                retry_after_ms: 0,
            },
            Ok(Err(error)) => classify_error(&error),
            Err(_) => ProbeResult {
                outcome: Outcome::Unreachable,
                status: None,
                failure: Some("deadline"),
                elapsed_ms: 0,
                retry_after_ms: 0,
            },
        };
        probe.elapsed_ms = start.elapsed().as_millis().min(u64::MAX as u128) as u64;
        probe
    }
    pub async fn get(
        &self,
        url: &Url,
        maximum: usize,
        deadline: Duration,
    ) -> anyhow::Result<HttpResponse> {
        self.get_accept(url, maximum, deadline, "*/*").await
    }
    pub async fn get_accept(
        &self,
        url: &Url,
        maximum: usize,
        deadline: Duration,
        accept: &str,
    ) -> anyhow::Result<HttpResponse> {
        tokio::time::timeout(deadline, async {
            let (response, _permit) = self.response(Method::GET, url, accept).await?;
            let status = response.status().as_u16();
            let headers = response
                .headers()
                .iter()
                .filter_map(|(k, v)| {
                    v.to_str()
                        .ok()
                        .map(|v| (k.as_str().to_owned(), v.to_owned()))
                })
                .collect();
            if let Some(length) = response
                .headers()
                .get("content-length")
                .and_then(|v| v.to_str().ok())
                .and_then(|v| v.parse::<u64>().ok())
            {
                anyhow::ensure!(length <= maximum as u64, "Response exceeds its size limit");
            }
            let mut body = response.into_body();
            let mut bytes = Vec::new();
            while let Some(frame) = body.frame().await {
                if let Ok(data) = frame?.into_data() {
                    anyhow::ensure!(
                        bytes.len().saturating_add(data.len()) <= maximum,
                        "Response exceeds its size limit"
                    );
                    bytes.extend_from_slice(&data);
                }
            }
            Ok(HttpResponse {
                status,
                headers,
                body: bytes,
            })
        })
        .await?
    }
    pub async fn download(
        &self,
        url: &Url,
        path: &Path,
        maximum: u64,
        deadline: Duration,
    ) -> anyhow::Result<(u64, String)> {
        match self.download_step(url, path, maximum, deadline).await? {
            DownloadStep::Complete { bytes, sha256 } => Ok((bytes, sha256)),
            DownloadStep::Redirect(_) => {
                anyhow::bail!("Download redirect requires explicit validation")
            }
        }
    }
    pub async fn download_step(
        &self,
        url: &Url,
        path: &Path,
        maximum: u64,
        deadline: Duration,
    ) -> anyhow::Result<DownloadStep> {
        use sha2::{Digest, Sha256};
        tokio::time::timeout(deadline, async {
            let (response, _permit) = self
                .response(Method::GET, url, "application/octet-stream")
                .await?;
            if response.status().is_redirection() {
                let location = response
                    .headers()
                    .get("location")
                    .and_then(|s| s.to_str().ok())
                    .ok_or_else(|| anyhow::anyhow!("Redirect has no target"))?;
                return Ok(DownloadStep::Redirect(url.join(location)?));
            }
            anyhow::ensure!(response.status().is_success(), "Download did not succeed");
            if let Some(length) = response
                .headers()
                .get("content-length")
                .and_then(|v| v.to_str().ok())
                .and_then(|v| v.parse::<u64>().ok())
            {
                anyhow::ensure!(length <= maximum, "Download exceeds its size limit");
            }
            let mut output = tokio::fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)
                .await?;
            let mut body = response.into_body();
            let mut count = 0u64;
            let mut hash = Sha256::new();
            while let Some(frame) = body.frame().await {
                if let Ok(data) = frame?.into_data() {
                    count = count.saturating_add(data.len() as u64);
                    anyhow::ensure!(count <= maximum, "Download exceeds its size limit");
                    hash.update(&data);
                    output.write_all(&data).await?;
                }
            }
            output.sync_all().await?;
            Ok(DownloadStep::Complete {
                bytes: count,
                sha256: format!("{:x}", hash.finalize()),
            })
        })
        .await?
    }
}
fn build_client(connector: Connector) -> HttpClient {
    Client::builder(TokioExecutor::new())
        .pool_timer(TokioTimer::new())
        .pool_idle_timeout(Duration::from_secs(30))
        .pool_max_idle_per_host(1)
        .http1_max_buf_size(32 * 1024)
        .build(connector)
}
fn classify_error(error: &anyhow::Error) -> ProbeResult {
    let mut outcome = Outcome::Indeterminate;
    let mut failure = "local-resource";
    for cause in error.chain() {
        if let Some(connection) = cause.downcast_ref::<ConnectFailure>() {
            match connection {
                ConnectFailure::Target => {
                    outcome = Outcome::Indeterminate;
                    failure = "invalid-target";
                }
                ConnectFailure::Deadline => {
                    outcome = Outcome::Unreachable;
                    failure = "deadline";
                }
                ConnectFailure::Network(_) => {
                    outcome = Outcome::Unreachable;
                    failure = "transport";
                }
                ConnectFailure::Tls(io) => {
                    match io.get_ref().and_then(|e| e.downcast_ref::<rustls::Error>()) {
                        Some(rustls::Error::InvalidCertificate(_)) => {
                            outcome = Outcome::Indeterminate;
                            failure = "certificate-trust";
                        }
                        Some(_) => {
                            outcome = Outcome::Unreachable;
                            failure = "tls-protocol";
                        }
                        None => {
                            outcome = Outcome::Indeterminate;
                            failure = "tls-indeterminate";
                        }
                    }
                }
            }
        }
        if cause.downcast_ref::<DnsFailure>().is_some() {
            outcome = Outcome::Unreachable;
            failure = "dns";
        }
        if let Some(hyper_error) = cause.downcast_ref::<hyper::Error>() {
            if hyper_error.is_parse() || hyper_error.is_incomplete_message() {
                outcome = Outcome::Indeterminate;
                failure = "invalid-http";
            } else if hyper_error.is_user() || hyper_error.is_canceled() {
                outcome = Outcome::Indeterminate;
                failure = "local-resource";
            } else if hyper_error.is_closed()
                || hyper_error.is_timeout()
                || hyper_error.is_body_write_aborted()
            {
                outcome = Outcome::Unreachable;
                failure = "transport";
            }
        }
        if let Some(io) = cause.downcast_ref::<io::Error>() {
            match io.kind() {
                io::ErrorKind::PermissionDenied
                | io::ErrorKind::OutOfMemory
                | io::ErrorKind::AddrNotAvailable => {
                    outcome = Outcome::Indeterminate;
                    failure = "local-resource";
                }
                io::ErrorKind::ConnectionRefused
                | io::ErrorKind::ConnectionReset
                | io::ErrorKind::ConnectionAborted
                | io::ErrorKind::NotConnected
                | io::ErrorKind::TimedOut => {
                    outcome = Outcome::Unreachable;
                    if failure != "dns" {
                        failure = "transport";
                    }
                }
                _ => {}
            }
        }
    }
    ProbeResult {
        outcome,
        status: None,
        failure: Some(failure),
        elapsed_ms: 0,
        retry_after_ms: 0,
    }
}
pub fn retry_after(status: u16, header: Option<&str>, now: DateTime<Utc>) -> u64 {
    let Some(header) = header.filter(|s| !s.trim().is_empty()) else {
        return if matches!(status, 403 | 429) {
            900_000
        } else {
            0
        };
    };
    if let Ok(seconds) = header.trim().parse::<u64>() {
        return if seconds == 0 {
            60_000
        } else {
            seconds.saturating_mul(1000).min(86_400_000)
        };
    }
    if let Ok(date) = DateTime::parse_from_rfc2822(header) {
        let remaining = (date.with_timezone(&Utc) - now).num_milliseconds();
        return if remaining <= 0 {
            60_000
        } else {
            (remaining as u64).min(86_400_000)
        };
    }
    if matches!(status, 403 | 429 | 503) {
        900_000
    } else {
        0
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Endpoint, Group};
    use std::sync::atomic::AtomicUsize;
    use tokio::{io::AsyncReadExt, net::TcpListener, task::JoinSet};

    struct Fixture {
        url: Url,
        config: rustls::ClientConfig,
        task: tokio::task::JoinHandle<()>,
        accepted: Arc<AtomicUsize>,
        maximum: Arc<AtomicUsize>,
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            self.task.abort();
        }
    }
    struct ConnectionCount(Arc<AtomicUsize>);
    impl Drop for ConnectionCount {
        fn drop(&mut self) {
            self.0.fetch_sub(1, Ordering::AcqRel);
        }
    }
    async fn tls_fixture(partial: bool, response_delay: Duration) -> Fixture {
        tls_fixture_named(
            partial,
            response_delay,
            vec!["localhost".into(), "127.0.0.1".into(), "::1".into()],
        )
        .await
    }
    async fn tls_fixture_named(
        partial: bool,
        response_delay: Duration,
        names: Vec<String>,
    ) -> Fixture {
        tls_fixture_response(
            partial,
            response_delay,
            names,
            b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n".to_vec(),
        )
        .await
    }
    async fn response_fixture(response: &[u8]) -> Fixture {
        tls_fixture_response(
            false,
            Duration::ZERO,
            vec!["localhost".into(), "127.0.0.1".into(), "::1".into()],
            response.to_vec(),
        )
        .await
    }
    async fn tls_fixture_response(
        partial: bool,
        response_delay: Duration,
        names: Vec<String>,
        response: Vec<u8>,
    ) -> Fixture {
        let rcgen::CertifiedKey { cert, signing_key } =
            rcgen::generate_simple_self_signed(names).unwrap();
        let mut roots = rustls::RootCertStore::empty();
        roots.add(cert.der().clone()).unwrap();
        let config = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_root_certificates(roots)
        .with_no_client_auth();
        let server = rustls::ServerConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_no_client_auth()
        .with_single_cert(
            vec![cert.der().clone()],
            rustls::pki_types::PrivatePkcs8KeyDer::from(signing_key.serialize_der()).into(),
        )
        .unwrap();
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let address = listener.local_addr().unwrap();
        let accepted = Arc::new(AtomicUsize::new(0));
        let maximum = Arc::new(AtomicUsize::new(0));
        let active = Arc::new(AtomicUsize::new(0));
        let all = accepted.clone();
        let peak = maximum.clone();
        let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(server));
        let response: Arc<[u8]> = response.into();
        let task = tokio::spawn(async move {
            let mut children = JoinSet::new();
            loop {
                tokio::select! {
                    connection = listener.accept() => {
                        let Ok((connection, _)) = connection else { break; };
                        all.fetch_add(1, Ordering::AcqRel); let current = active.fetch_add(1, Ordering::AcqRel) + 1; peak.fetch_max(current, Ordering::AcqRel);
                        let guard = ConnectionCount(active.clone()); let acceptor = acceptor.clone(); let response = response.clone();
                        children.spawn(async move {
                            let _guard = guard; let Ok(mut stream) = acceptor.accept(connection).await else { return; };
                            loop {
                                let mut headers = Vec::new(); let mut byte = [0];
                                loop {
                                    match stream.read(&mut byte).await { Ok(0) | Err(_) => return, Ok(_) => headers.push(byte[0]) }
                                    if headers.ends_with(b"\r\n\r\n") { break; }
                                    if headers.len() > 32768 { return; }
                                }
                                tokio::time::sleep(response_delay).await;
                                if partial { let _ = stream.write_all(b"HTTP/1.1 200 OK\r\nX-Incomplete: value").await; let _ = stream.shutdown().await; return; }
                                if stream.write_all(&response).await.is_err() { return; }
                                if response.windows(b"connection: close\r\n".len()).any(|value| value.eq_ignore_ascii_case(b"connection: close\r\n")) { let _ = stream.shutdown().await; return; }
                            }
                        });
                    },
                    _ = children.join_next(), if !children.is_empty() => {},
                }
            }
        });
        Fixture {
            url: Url::parse(&format!("https://{address}/probe")).unwrap(),
            config,
            task,
            accepted,
            maximum,
        }
    }
    fn work(url: Url, group: Group) -> ProbeWork {
        ProbeWork {
            endpoint: Endpoint {
                id: format!("fixture-{group:?}"),
                group,
                uri: url,
                infrastructure_id: "fixture".into(),
            },
            epoch: 1,
            attempt: 1,
            started: 0,
            deadline_ms: 3000,
            manual: false,
        }
    }
    #[tokio::test]
    async fn bounded_get_returns_real_tls_body_and_rejects_declared_or_chunked_oversize() {
        let traffic = Arc::new(Traffic::default());
        let fixture = response_fixture(b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nX-Source: fixture\r\nConnection: close\r\n\r\nhello").await;
        let transport = Transport::configured(
            traffic.clone(),
            Category::Country,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let result = transport
            .get_accept(&fixture.url, 5, Duration::from_secs(2), "application/json")
            .await
            .unwrap();
        assert_eq!(result.status, 200);
        assert_eq!(result.body, b"hello");
        assert_eq!(
            result.headers.get("x-source").map(String::as_str),
            Some("fixture")
        );
        assert_eq!(fixture.accepted.load(Ordering::Acquire), 1);
        assert!(
            traffic
                .snapshot()
                .iter()
                .any(|row| row.sent_bytes > 0 && row.received_bytes > 5)
        );
        for response in [
            b"HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\n123456".as_slice(),
            b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n3\r\n123\r\n3\r\n456\r\n0\r\n\r\n".as_slice(),
        ] {
            let fixture = response_fixture(response).await;
            let transport = Transport::configured(Arc::new(Traffic::default()), Category::Country, fixture.config.clone(), Duration::from_secs(2)).unwrap();
            let error = transport.get(&fixture.url, 5, Duration::from_secs(2)).await.unwrap_err();
            assert!(error.to_string().contains("Response exceeds its size limit"), "{error:#}");
        }
    }
    #[tokio::test]
    async fn streamed_download_verifies_payload_digest_and_never_overwrites_existing_files() {
        use sha2::Digest;
        let root = std::env::temp_dir().join(format!(
            "NetLightsDownload-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        std::fs::create_dir(&root).unwrap();
        let target = root.join("installer.exe");
        let fixture = response_fixture(
            b"HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\ninstaller",
        )
        .await;
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Updates,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let result = transport
            .download_step(&fixture.url, &target, 9, Duration::from_secs(2))
            .await
            .unwrap();
        let DownloadStep::Complete { bytes, sha256 } = result else {
            panic!("A successful payload was treated as a redirect")
        };
        assert_eq!(bytes, 9);
        assert_eq!(sha256, format!("{:x}", sha2::Sha256::digest(b"installer")));
        assert_eq!(std::fs::read(&target).unwrap(), b"installer");
        assert!(
            transport
                .download_step(&fixture.url, &target, 9, Duration::from_secs(2))
                .await
                .is_err()
        );
        assert_eq!(std::fs::read(&target).unwrap(), b"installer");
        std::fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn redirect_and_oversized_downloads_do_not_create_a_payload_or_follow_another_host() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsDownloadRejected-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        std::fs::create_dir(&root).unwrap();
        let target = root.join("installer.exe");
        let fixture = response_fixture(b"HTTP/1.1 302 Found\r\nLocation: https://untrusted.invalid/other.exe\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").await;
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Updates,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let result = transport
            .download_step(&fixture.url, &target, 9, Duration::from_secs(2))
            .await
            .unwrap();
        let DownloadStep::Redirect(url) = result else {
            panic!("The redirect was accepted as installer bytes")
        };
        assert_eq!(url.as_str(), "https://untrusted.invalid/other.exe");
        assert!(!target.exists());
        assert_eq!(fixture.accepted.load(Ordering::Acquire), 1);
        let fixture = response_fixture(
            b"HTTP/1.1 200 OK\r\nContent-Length: 10\r\nConnection: close\r\n\r\n0123456789",
        )
        .await;
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Updates,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let error = transport
            .download_step(&fixture.url, &target, 9, Duration::from_secs(2))
            .await
            .unwrap_err();
        assert!(
            error
                .to_string()
                .contains("Download exceeds its size limit")
        );
        assert!(!target.exists());
        std::fs::remove_dir(root).unwrap();
    }
    #[test]
    fn retry_after_dates_and_seconds_apply_the_same_bounded_backoff_policy() {
        let now = DateTime::parse_from_rfc3339("2026-11-11T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        for seconds in [1u64, 60, 86_400, 30 * 86_400] {
            let header = (now + chrono::Duration::seconds(seconds as i64)).to_rfc2822();
            assert_eq!(
                retry_after(429, Some(&header), now),
                retry_after(429, Some(&seconds.to_string()), now),
                "HTTP-date and delay-seconds disagree for {seconds} seconds"
            );
        }
        assert_eq!(retry_after(429, Some(&now.to_rfc2822()), now), 60_000);
        assert_eq!(retry_after(429, Some("invalid date"), now), 900_000);
    }
    #[tokio::test]
    async fn production_platform_verifier_rejects_untrusted_tls_without_an_offline_or_http_vote() {
        let fixture = tls_fixture(false, Duration::ZERO).await;
        // The private self-signed certificate is never added to an OS trust
        // store. Exercise the same platform verifier configuration as the app.
        let transport = Transport::new(Arc::new(Traffic::default()), Category::Provider).unwrap();
        let result = transport.probe(&work(fixture.url.clone(), Group::Ru)).await;
        assert_eq!(result.outcome, Outcome::Indeterminate, "{result:?}");
        assert_eq!(result.failure, Some("certificate-trust"), "{result:?}");
        assert_eq!(
            result.status, None,
            "An untrusted TLS peer mustn't supply an HTTP availability vote"
        );
        assert_eq!(
            fixture.accepted.load(Ordering::Acquire),
            1,
            "The negative must attempt an actual local TLS connection"
        );
    }
    #[tokio::test]
    async fn trusted_certificate_with_wrong_hostname_is_rejected_without_an_offline_or_http_vote() {
        let fixture =
            tls_fixture_named(false, Duration::ZERO, vec!["wrong-host.invalid".into()]).await;
        // Trust this exact fixture certificate, but retain hostname validation.
        // Its SAN doesn't cover the actual 127.0.0.1 connection target.
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Provider,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let result = transport.probe(&work(fixture.url.clone(), Group::Ru)).await;
        assert_eq!(result.outcome, Outcome::Indeterminate, "{result:?}");
        assert_eq!(result.failure, Some("certificate-trust"), "{result:?}");
        assert_eq!(
            result.status, None,
            "A mismatched TLS peer mustn't supply an HTTP availability vote"
        );
        assert_eq!(
            fixture.accepted.load(Ordering::Acquire),
            1,
            "The negative must attempt an actual local TLS connection"
        );
    }
    #[tokio::test]
    async fn truncated_https_headers_are_indeterminate_not_an_offline_vote() {
        let fixture = tls_fixture(true, Duration::ZERO).await;
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Provider,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let result = transport.probe(&work(fixture.url.clone(), Group::Ru)).await;
        assert_eq!(result.outcome, Outcome::Indeterminate);
        assert_eq!(result.failure, Some("invalid-http"));
        assert_eq!(
            fixture.accepted.load(Ordering::Acquire),
            1,
            "The test must complete a real TLS connection"
        );
    }
    #[tokio::test]
    async fn related_categories_share_one_socket_and_attribute_reused_bytes() {
        let fixture = tls_fixture(false, Duration::from_millis(30)).await;
        let traffic = Arc::new(Traffic::default());
        let provider = Transport::configured(
            traffic.clone(),
            Category::Provider,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let world = provider.for_category(Category::World);
        assert!(
            provider.observed_address(&fixture.url).is_none(),
            "A configured target isn't an observed address"
        );
        let a = work(fixture.url.clone(), Group::Ru);
        let mut other = fixture.url.clone();
        other.set_path("/world");
        let b = work(other, Group::World);
        let (a, b) = tokio::join!(provider.probe(&a), world.probe(&b));
        assert_eq!(a.outcome, Outcome::Reachable);
        assert_eq!(b.outcome, Outcome::Reachable);
        assert_eq!(fixture.accepted.load(Ordering::Acquire), 1);
        assert_eq!(fixture.maximum.load(Ordering::Acquire), 1);
        for category in [Category::Provider, Category::World] {
            let rows = traffic.snapshot();
            let row = rows
                .iter()
                .find(|r| r.category == category.label())
                .expect("Each request category must own actual counted bytes");
            assert!(row.sent_bytes > 0 && row.received_bytes > 0);
        }
        let rows = traffic.snapshot();
        let provider = rows
            .iter()
            .find(|r| r.category == Category::Provider.label())
            .unwrap();
        let world = rows
            .iter()
            .find(|r| r.category == Category::World.label())
            .unwrap();
        assert!(
            provider.sent_bytes > world.sent_bytes,
            "TLS opening bytes must belong to the first category, while reuse still counts the second request"
        );
    }
    #[tokio::test]
    async fn actual_https_peer_and_reused_connection_timestamp_are_retained() {
        let fixture = tls_fixture(false, Duration::ZERO).await;
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Provider,
            fixture.config.clone(),
            Duration::from_secs(2),
        )
        .unwrap();
        let work = work(fixture.url.clone(), Group::Ru);
        assert_eq!(transport.probe(&work).await.outcome, Outcome::Reachable);
        let first = transport.observed_address(&fixture.url).unwrap();
        assert!(first.address.ip().is_loopback());
        assert_eq!(
            first.address.port(),
            fixture.url.port_or_known_default().unwrap()
        );
        let before_reuse = Utc::now();
        assert_eq!(transport.probe(&work).await.outcome, Outcome::Reachable);
        let second = transport.observed_address(&fixture.url).unwrap();
        assert_eq!(second.address, first.address);
        assert!(second.observed_utc >= before_reuse);
        assert!(second.observed_utc >= first.observed_utc);
        assert_eq!(
            fixture.accepted.load(Ordering::Acquire),
            1,
            "The second observation must come from socket reuse"
        );
    }
    #[tokio::test]
    async fn connector_itself_bounds_stalled_tls_and_releases_actual_socket_permit() {
        let fixture = tls_fixture(false, Duration::ZERO).await;
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0))
            .await
            .unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut count = 0usize;
            let mut data = [0; 4096];
            loop {
                let n = socket.read(&mut data).await.unwrap();
                if n == 0 {
                    break;
                }
                count += n;
            }
            count
        });
        let transport = Transport::configured(
            Arc::new(Traffic::default()),
            Category::Provider,
            fixture.config.clone(),
            Duration::from_millis(250),
        )
        .unwrap();
        let uri: Uri = format!("https://{address}/").parse().unwrap();
        let mut connector = transport.connector.clone();
        // Deliberately bypass response()/probe(): a detached connector has no
        // outer request future left to enforce that future's deadline.
        let result = tokio::time::timeout(Duration::from_secs(3), connector.call(uri))
            .await
            .expect("The connector needs its own deadline")
            .err()
            .expect("A stalled TLS peer must fail");
        assert!(matches!(
            result.downcast_ref::<ConnectFailure>(),
            Some(ConnectFailure::Deadline)
        ));
        assert_eq!(
            classify_error(&anyhow::Error::new(ConnectFailure::Network(result))).failure,
            Some("deadline")
        );
        assert!(
            tokio::time::timeout(Duration::from_secs(2), server)
                .await
                .unwrap()
                .unwrap()
                > 0,
            "TLS bytes must be sent and the socket must actually close"
        );
        let origin = transport
            .connector
            .admission
            .origin(&format!("https://{address}"))
            .unwrap();
        assert_eq!(origin.physical.available_permits(), 1);
        assert_eq!(
            transport.connector.admission.connecting.available_permits(),
            4
        );
    }
    #[tokio::test]
    async fn a_dns_fixture_preserves_typed_resolution_failure_through_http_connector() {
        #[derive(Clone)]
        struct BrokenDns;
        impl Service<Name> for BrokenDns {
            type Response = GaiAddrs;
            type Error = io::Error;
            type Future = std::future::Ready<io::Result<GaiAddrs>>;
            fn poll_ready(&mut self, _: &mut Context<'_>) -> Poll<io::Result<()>> {
                Poll::Ready(Ok(()))
            }
            fn call(&mut self, _: Name) -> Self::Future {
                std::future::ready(Err(io::Error::new(
                    io::ErrorKind::NotFound,
                    "Fixture DNS failure",
                )))
            }
        }
        let mut connector = HttpConnector::new_with_resolver(Resolver(BrokenDns));
        connector.enforce_http(false);
        let error = connector
            .call("https://netlights-fixture.invalid/".parse().unwrap())
            .await
            .expect_err("The resolver must fail before any connection");
        let result = classify_error(&anyhow::Error::new(ConnectFailure::Network(Box::new(
            error,
        ))));
        assert_eq!(result.outcome, Outcome::Unreachable);
        assert_eq!(result.failure, Some("dns"));
    }
    #[test]
    fn server_backoff_is_not_a_connectivity_failure() {
        assert_eq!(retry_after(429, None, Utc::now()), 900_000);
        assert_eq!(retry_after(503, None, Utc::now()), 0);
        assert_eq!(retry_after(429, Some("0"), Utc::now()), 60_000);
    }
    #[tokio::test]
    async fn tracked_io_counts_actual_partial_writes_and_reads() {
        use tokio::io::AsyncReadExt;
        let (a, mut b) = tokio::io::duplex(4);
        let counter = Arc::new(Counter::default());
        let mut tracked = TrackedIo {
            inner: a,
            counter: Arc::new(Mutex::new(counter.clone())),
            _physical: None,
        };
        let task = tokio::spawn(async move {
            let mut bytes = [0; 4];
            b.read_exact(&mut bytes).await.unwrap();
            b.write_all(b"ok").await.unwrap();
        });
        tracked.write_all(b"test").await.unwrap();
        let mut bytes = [0; 2];
        tracked.read_exact(&mut bytes).await.unwrap();
        task.await.unwrap();
        assert_eq!(counter.sent.load(Ordering::Relaxed), 4);
        assert_eq!(counter.received.load(Ordering::Relaxed), 2);
    }
}
