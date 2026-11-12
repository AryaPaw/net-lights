use super::{CountryDatabase, IPLOCATE_MAX_BYTES, V2FLY_MAX_BYTES};
use crate::{
    persistence::{read_bounded, write_atomic},
    traffic::{Category, Traffic},
    transport::Transport,
};
use anyhow::{Context, Result, ensure};
use chrono::{Duration as ChronoDuration, Utc};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::{
        Arc, RwLock,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};
use tokio::{sync::watch, task::JoinHandle};
use url::Url;

const RELEASE_API: &str = "https://api.github.com/repos/v2fly/geoip/releases/latest";
const POINTER_URL: &str = "https://raw.githubusercontent.com/iplocate/ip-address-databases/main/ip-to-country/ip-to-country.csv.zip";
const MEDIA_URL: &str = "https://media.githubusercontent.com/media/iplocate/ip-address-databases/main/ip-to-country/ip-to-country.csv.zip";

pub struct DatabaseSnapshot {
    pub database: CountryDatabase,
    pub version: String,
    pub hash: String,
}
#[derive(Clone, Default)]
pub struct DatabaseSnapshots {
    pub v2fly: Option<Arc<DatabaseSnapshot>>,
    pub iplocate: Option<Arc<DatabaseSnapshot>>,
    pub v2fly_failure: Option<&'static str>,
    pub iplocate_failure: Option<&'static str>,
}
/// One background owner loads and refreshes the two databases sequentially. Readers
/// clone immutable snapshots; parsing and disk writes never run on the UI thread.
pub struct DatabaseWorker {
    state: Arc<RwLock<DatabaseSnapshots>>,
    cancel: Arc<AtomicBool>,
    stop: watch::Sender<bool>,
    task: Option<JoinHandle<()>>,
}
impl DatabaseWorker {
    pub fn start(root: PathBuf, traffic: Arc<Traffic>) -> Result<Self> {
        let state = Arc::new(RwLock::new(DatabaseSnapshots::default()));
        let cancel = Arc::new(AtomicBool::new(false));
        let v2 = Transport::new(traffic.clone(), Category::V2Fly)?;
        let ip = Transport::new(traffic, Category::IPLocate)?;
        let job_state = state.clone();
        let job_cancel = cancel.clone();
        let (stop, stopped) = watch::channel(false);
        let task = tokio::spawn(async move {
            run(root, v2, ip, job_state, job_cancel, stopped).await;
        });
        Ok(Self {
            state,
            cancel,
            stop,
            task: Some(task),
        })
    }
    pub fn snapshots(&self) -> DatabaseSnapshots {
        self.state.read().unwrap_or_else(|e| e.into_inner()).clone()
    }
    #[cfg(test)]
    pub(crate) fn empty_for_tests() -> Self {
        Self {
            state: Arc::new(RwLock::new(DatabaseSnapshots::default())),
            cancel: Arc::new(AtomicBool::new(false)),
            stop: watch::channel(false).0,
            task: None,
        }
    }
    pub async fn shutdown(&mut self) {
        self.cancel.store(true, Ordering::Release);
        self.stop.send_replace(true);
        // The owner must join its blocking parser or disk commit before another
        // service can acquire this profile. Dropping that JoinHandle detaches it.
        if let Some(task) = self.task.take() {
            let _ = task.await;
        }
    }
}
impl Drop for DatabaseWorker {
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Release);
        self.stop.send_replace(true);
    }
}

async fn cancellable<T>(
    future: impl std::future::Future<Output = Result<T>>,
    stop: &mut watch::Receiver<bool>,
) -> Result<T> {
    if *stop.borrow() {
        anyhow::bail!("Database operation cancelled");
    }
    tokio::select! { biased;
        _ = stop.wait_for(|stopped| *stopped) => anyhow::bail!("Database operation cancelled"),
        result = future => result,
    }
}
async fn run(
    root: PathBuf,
    v2: Transport,
    ip: Transport,
    state: Arc<RwLock<DatabaseSnapshots>>,
    cancel: Arc<AtomicBool>,
    mut stop: watch::Receiver<bool>,
) {
    let load_root = root.clone();
    let load_cancel = cancel.clone();
    let load_state = state.clone();
    // Publish each local snapshot as soon as it's loaded, before any network wait.
    let loaded = tokio::task::spawn_blocking(move || {
        let v2_snapshot = load_v2(&load_root, &load_cancel);
        let mut guard = load_state.write().unwrap_or_else(|e| e.into_inner());
        match v2_snapshot {
            Ok(value) => guard.v2fly = value.map(Arc::new),
            Err(_) => guard.v2fly_failure = Some("v2fly-load-failed"),
        }
        drop(guard);
        if load_cancel.load(Ordering::Acquire) {
            return;
        }
        let ip_snapshot = load_ip(&load_root, &load_cancel);
        let mut guard = load_state.write().unwrap_or_else(|e| e.into_inner());
        match ip_snapshot {
            Ok(value) => guard.iplocate = value.map(Arc::new),
            Err(_) => guard.iplocate_failure = Some("iplocate-load-failed"),
        }
    })
    .await;
    if loaded.is_err() {
        let mut guard = state.write().unwrap_or_else(|e| e.into_inner());
        guard.v2fly_failure = Some("database-worker-failed");
        guard.iplocate_failure = Some("database-worker-failed");
    }
    let mut v2_next = Utc::now();
    let mut ip_next = Utc::now();
    let mut v2_retry = ValidationRetry::default();
    let mut ip_retry = ValidationRetry::default();
    loop {
        if cancel.load(Ordering::Acquire) {
            return;
        }
        if Utc::now() >= v2_next {
            let current = state
                .read()
                .unwrap_or_else(|e| e.into_inner())
                .v2fly
                .clone();
            let updated = refresh_v2(
                &root,
                &v2,
                current.as_deref(),
                cancel.clone(),
                &mut stop,
                &mut v2_retry,
            )
            .await;
            if cancel.load(Ordering::Acquire) {
                return;
            }
            let mut guard = state.write().unwrap_or_else(|e| e.into_inner());
            match updated {
                Ok(snapshot) => {
                    if let Some(snapshot) = snapshot {
                        guard.v2fly = Some(Arc::new(snapshot));
                    }
                    guard.v2fly_failure = None;
                    v2_next = Utc::now() + ChronoDuration::days(1);
                }
                Err(_) => {
                    guard.v2fly_failure = Some("v2fly-refresh-failed");
                    v2_next = Utc::now() + ChronoDuration::minutes(5);
                }
            }
        }
        if cancel.load(Ordering::Acquire) {
            return;
        }
        if Utc::now() >= ip_next {
            let current = state
                .read()
                .unwrap_or_else(|e| e.into_inner())
                .iplocate
                .clone();
            let updated = refresh_ip(
                &root,
                &ip,
                current.as_deref(),
                cancel.clone(),
                &mut stop,
                &mut ip_retry,
            )
            .await;
            if cancel.load(Ordering::Acquire) {
                return;
            }
            let mut guard = state.write().unwrap_or_else(|e| e.into_inner());
            match updated {
                Ok(snapshot) => {
                    if let Some(snapshot) = snapshot {
                        guard.iplocate = Some(Arc::new(snapshot));
                    }
                    guard.iplocate_failure = None;
                    ip_next = Utc::now() + ChronoDuration::days(1);
                }
                Err(_) => {
                    guard.iplocate_failure = Some("iplocate-refresh-failed");
                    ip_next = Utc::now() + ChronoDuration::hours(1);
                }
            }
        }
        tokio::select! { _ = tokio::time::sleep(Duration::from_secs(60)) => {}, _ = stop.wait_for(|stopped| *stopped) => return }
    }
}

#[derive(Serialize, Deserialize)]
struct Generation {
    #[serde(rename = "Version", alias = "version")]
    version: String,
    #[serde(rename = "Hash", alias = "hash")]
    hash: String,
}
fn generation(path: &Path) -> Result<Generation> {
    let value: Generation = serde_json::from_slice(&read_bounded(path, 4096)?)?;
    ensure!(
        !value.version.is_empty() && value.version.len() <= 64 && is_hash(&value.hash),
        "Invalid database manifest"
    );
    Ok(value)
}
fn data_path(root: &Path, hash: &str) -> PathBuf {
    root.join(format!("geoip-{}.dat", hash.to_ascii_lowercase()))
}
fn load_v2(root: &Path, cancel: &AtomicBool) -> Result<Option<DatabaseSnapshot>> {
    ensure!(!cancel.load(Ordering::Acquire), "Database load cancelled");
    let directory = root.join("GeoIP");
    let manifest = directory.join("current.json");
    let backup = directory.join("current.json.bak");
    for path in [&manifest, &backup] {
        if let Ok(manifest) = generation(path) {
            let loaded = (|| {
                let bytes = read_bounded(
                    &data_path(&directory, &manifest.hash),
                    V2FLY_MAX_BYTES as u64,
                )?;
                ensure!(
                    hash(&bytes).eq_ignore_ascii_case(&manifest.hash),
                    "Database generation checksum mismatch"
                );
                let database = CountryDatabase::parse_v2fly(&bytes, cancel)?;
                Ok::<_, anyhow::Error>(DatabaseSnapshot {
                    database,
                    version: manifest.version,
                    hash: manifest.hash.to_ascii_lowercase(),
                })
            })();
            if let Ok(snapshot) = loaded {
                return Ok(Some(snapshot));
            }
        }
    }
    if manifest.exists() || backup.exists() {
        anyhow::bail!("No valid database generation");
    }
    let legacy = directory.join("geoip.dat");
    if !legacy.exists() {
        return Ok(None);
    }
    let bytes = read_bounded(&legacy, V2FLY_MAX_BYTES as u64)?;
    let database = CountryDatabase::parse_v2fly(&bytes, cancel)?;
    let snapshot = DatabaseSnapshot {
        database,
        version: "legacy-unverified".into(),
        hash: hash(&bytes),
    };
    // Loading a legacy file doesn't fabricate a verified historical release version.
    Ok(Some(snapshot))
}
fn load_ip(root: &Path, cancel: &AtomicBool) -> Result<Option<DatabaseSnapshot>> {
    ensure!(!cancel.load(Ordering::Acquire), "Database load cancelled");
    let path = root.join("GeoIP-Compare/IPLocate-country.csv.zip");
    if !path.exists() {
        return Ok(None);
    }
    let bytes = read_bounded(&path, IPLOCATE_MAX_BYTES as u64)?;
    let hash = hash(&bytes);
    let database = CountryDatabase::parse_iplocate(&bytes, 1_000_000, 10_000, cancel)?;
    Ok(Some(DatabaseSnapshot {
        database,
        version: format!("Git LFS SHA-256 {}", &hash[..12]),
        hash,
    }))
}
fn save_generation(root: &Path, bytes: &[u8], version: &str, cancel: &AtomicBool) -> Result<()> {
    ensure!(!cancel.load(Ordering::Acquire), "Database update cancelled");
    let directory = root.join("GeoIP");
    fs::create_dir_all(&directory)?;
    let hash = hash(bytes);
    let path = data_path(&directory, &hash);
    let existed = path.exists();
    write_atomic(&path, bytes, false)?;
    let manifest = directory.join("current.json");
    let result = (|| {
        if let Ok(previous) = generation(&manifest) {
            let previous_path = data_path(&directory, &previous.hash);
            if let Ok(mut file) = fs::File::open(previous_path)
                && file.metadata()?.len() <= V2FLY_MAX_BYTES as u64
            {
                let mut hasher = Sha256::new();
                let mut bounded = std::io::Read::take(&mut file, V2FLY_MAX_BYTES as u64 + 1);
                ensure!(
                    std::io::copy(&mut bounded, &mut HashWriter(&mut hasher))?
                        <= V2FLY_MAX_BYTES as u64,
                    "Previous database grew beyond its size limit"
                );
                if format!("{:x}", hasher.finalize()).eq_ignore_ascii_case(&previous.hash) {
                    write_atomic(
                        &directory.join("current.json.bak"),
                        &serde_json::to_vec(&previous)?,
                        false,
                    )?;
                }
            }
        }
        ensure!(!cancel.load(Ordering::Acquire), "Database update cancelled");
        write_atomic(
            &manifest,
            &serde_json::to_vec(&Generation {
                version: version.into(),
                hash: hash.clone(),
            })?,
            false,
        )
    })();
    if result.is_err() && !existed {
        let _ = fs::remove_file(&path);
    }
    if result.is_ok() {
        prune_generations(&directory, &hash);
    }
    result
}
struct HashWriter<'a>(&'a mut Sha256);
impl std::io::Write for HashWriter<'_> {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.0.update(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
fn prune_generations(directory: &Path, active: &str) {
    let mut keep = vec![active.to_ascii_lowercase()];
    if let Ok(previous) = generation(&directory.join("current.json.bak")) {
        keep.push(previous.hash.to_ascii_lowercase());
    }
    if let Ok(files) = fs::read_dir(directory) {
        for file in files.flatten() {
            let name = file.file_name();
            let name = name.to_string_lossy();
            if let Some(value) = name
                .strip_prefix("geoip-")
                .and_then(|s| s.strip_suffix(".dat"))
                && is_hash(value)
                && !keep.iter().any(|keep| keep.eq_ignore_ascii_case(value))
            {
                let _ = fs::remove_file(file.path());
            }
        }
    }
}

#[derive(Default)]
struct ValidationRetry {
    rejected: Option<(String, chrono::DateTime<Utc>)>,
}
async fn validated_candidate<T, D, V, F>(
    retry: &mut ValidationRetry,
    identity: &str,
    now: chrono::DateTime<Utc>,
    download: D,
    validate: V,
) -> Result<T>
where
    D: std::future::Future<Output = Result<Vec<u8>>>,
    V: FnOnce(Vec<u8>) -> F,
    F: std::future::Future<Output = Result<T>>,
{
    ensure!(
        !retry
            .rejected
            .as_ref()
            .is_some_and(|(old, until)| old == identity && *until > now),
        "Rejected database generation is waiting for its next daily check"
    );
    // A transport failure remains retryable. A fully received but invalid or
    // unwritable generation must not consume tens of megabytes every five minutes.
    let bytes = download.await?;
    let result = validate(bytes).await;
    if result.is_err() {
        retry.rejected = Some((identity.into(), now + ChronoDuration::days(1)));
    }
    result
}
async fn refresh_v2(
    root: &Path,
    transport: &Transport,
    current: Option<&DatabaseSnapshot>,
    cancel: Arc<AtomicBool>,
    stop: &mut watch::Receiver<bool>,
    retry: &mut ValidationRetry,
) -> Result<Option<DatabaseSnapshot>> {
    let response = cancellable(
        transport.get(
            &Url::parse(RELEASE_API)?,
            512 * 1024,
            Duration::from_secs(30),
        ),
        stop,
    )
    .await?;
    ensure!(response.status == 200, "GeoIP release query failed");
    let release: serde_json::Value = serde_json::from_slice(&response.body)?;
    let version = release["tag_name"]
        .as_str()
        .context("GeoIP release has no version")?;
    ensure!(
        !version.is_empty() && version.len() <= 64,
        "Invalid GeoIP release version"
    );
    if current.is_some_and(|s| s.version == version) {
        return Ok(None);
    }
    let assets = release["assets"]
        .as_array()
        .context("GeoIP release has no assets")?;
    let asset = |name: &str| -> Result<&serde_json::Value> {
        let mut matching = assets
            .iter()
            .filter(|asset| asset["name"].as_str() == Some(name));
        let result = matching.next().context("GeoIP release asset missing")?;
        ensure!(matching.next().is_none(), "Ambiguous GeoIP release asset");
        Ok(result)
    };
    let database = asset("geoip.dat")?;
    let checksum = asset("geoip.dat.sha256sum")?;
    let size = database["size"]
        .as_u64()
        .context("GeoIP asset has no size")?;
    ensure!(
        size > 0 && size <= V2FLY_MAX_BYTES as u64,
        "Invalid GeoIP release size"
    );
    let expected = fetch_asset(
        transport,
        checksum["url"].as_str().context("Missing checksum URL")?,
        512,
        stop,
    )
    .await?;
    let expected = std::str::from_utf8(&expected)?
        .split_ascii_whitespace()
        .next()
        .context("Empty GeoIP checksum")?;
    ensure!(is_hash(expected), "Invalid GeoIP checksum");
    let identity = format!("{version}:{expected}");
    let expected = expected.to_owned();
    let root = root.to_owned();
    let version = version.to_owned();
    let url = database["url"]
        .as_str()
        .context("Missing GeoIP asset URL")?;
    validated_candidate(
        retry,
        &identity,
        Utc::now(),
        fetch_asset(transport, url, V2FLY_MAX_BYTES, stop),
        move |bytes| async move {
            ensure!(
                bytes.len() as u64 == size && hash(&bytes).eq_ignore_ascii_case(&expected),
                "GeoIP payload checksum or size mismatch"
            );
            tokio::task::spawn_blocking(move || {
                let database = CountryDatabase::parse_v2fly(&bytes, &cancel)?;
                save_generation(&root, &bytes, &version, &cancel)?;
                Ok::<_, anyhow::Error>(DatabaseSnapshot {
                    database,
                    version,
                    hash: hash(&bytes),
                })
            })
            .await?
        },
    )
    .await
    .map(Some)
}
async fn fetch_asset(
    transport: &Transport,
    url: &str,
    maximum: usize,
    stop: &mut watch::Receiver<bool>,
) -> Result<Vec<u8>> {
    let mut url = Url::parse(url)?;
    ensure!(
        url.scheme() == "https"
            && url.host_str() == Some("api.github.com")
            && url
                .path()
                .starts_with("/repos/v2fly/geoip/releases/assets/"),
        "Unapproved GeoIP asset URL"
    );
    for _ in 0..4 {
        ensure!(
            url.scheme() == "https"
                && url.username().is_empty()
                && url.password().is_none()
                && url.port_or_known_default() == Some(443),
            "Unsafe GeoIP asset redirect"
        );
        let response = cancellable(
            transport.get_accept(
                &url,
                maximum,
                Duration::from_secs(30),
                "application/octet-stream",
            ),
            stop,
        )
        .await?;
        if (200..300).contains(&response.status) {
            return Ok(response.body);
        }
        ensure!(
            matches!(response.status, 301 | 302 | 303 | 307 | 308),
            "GeoIP asset download failed"
        );
        url = url.join(
            response
                .headers
                .get("location")
                .context("GeoIP asset redirect has no location")?,
        )?;
        ensure!(
            matches!(
                url.host_str(),
                Some("release-assets.githubusercontent.com" | "objects.githubusercontent.com")
            ) || url.host_str() == Some("api.github.com")
                && url
                    .path()
                    .starts_with("/repos/v2fly/geoip/releases/assets/"),
            "Unapproved GeoIP asset redirect host"
        );
    }
    anyhow::bail!("Too many GeoIP asset redirects")
}
async fn refresh_ip(
    root: &Path,
    transport: &Transport,
    current: Option<&DatabaseSnapshot>,
    cancel: Arc<AtomicBool>,
    stop: &mut watch::Receiver<bool>,
    retry: &mut ValidationRetry,
) -> Result<Option<DatabaseSnapshot>> {
    let pointer = cancellable(
        transport.get(&Url::parse(POINTER_URL)?, 512, Duration::from_secs(30)),
        stop,
    )
    .await?;
    ensure!(pointer.status == 200, "IPLocate pointer download failed");
    let (expected_hash, expected_size) = parse_pointer(&pointer.body)?;
    if current.is_some_and(|s| s.hash == expected_hash) {
        return Ok(None);
    }
    let url = Url::parse(MEDIA_URL)?;
    let download = async {
        let archive = cancellable(
            transport.get(&url, IPLOCATE_MAX_BYTES, Duration::from_secs(120)),
            stop,
        )
        .await?;
        ensure!(archive.status == 200, "IPLocate archive download failed");
        Ok(archive.body)
    };
    let root = root.to_owned();
    let identity = expected_hash.clone();
    validated_candidate(
        retry,
        &identity,
        Utc::now(),
        download,
        move |bytes| async move {
            ensure!(
                bytes.len() as u64 == expected_size && hash(&bytes) == expected_hash,
                "IPLocate archive checksum or size mismatch"
            );
            tokio::task::spawn_blocking(move || {
                let database = CountryDatabase::parse_iplocate(&bytes, 1_000_000, 10_000, &cancel)?;
                ensure!(!cancel.load(Ordering::Acquire), "IPLocate update cancelled");
                write_atomic(
                    &root.join("GeoIP-Compare/IPLocate-country.csv.zip"),
                    &bytes,
                    false,
                )?;
                Ok::<_, anyhow::Error>(DatabaseSnapshot {
                    database,
                    version: format!("Git LFS SHA-256 {}", &expected_hash[..12]),
                    hash: expected_hash,
                })
            })
            .await?
        },
    )
    .await
    .map(Some)
}
fn parse_pointer(bytes: &[u8]) -> Result<(String, u64)> {
    let text = std::str::from_utf8(bytes)?;
    let mut lines = text.lines();
    ensure!(
        lines.next() == Some("version https://git-lfs.github.com/spec/v1"),
        "Invalid Git LFS pointer"
    );
    let mut hash_value = None;
    let mut size_value = None;
    for line in lines {
        if let Some(value) = line.strip_prefix("oid sha256:") {
            ensure!(
                hash_value.is_none() && is_hash(value),
                "Invalid pointer hash"
            );
            hash_value = Some(value.to_ascii_lowercase());
        }
        if let Some(value) = line.strip_prefix("size ") {
            ensure!(size_value.is_none(), "Duplicate pointer size");
            size_value = Some(value.parse::<u64>()?);
        }
    }
    let size = size_value.context("Missing pointer size")?;
    ensure!(
        size > 0 && size <= IPLOCATE_MAX_BYTES as u64,
        "Invalid pointer size"
    );
    Ok((hash_value.context("Missing pointer hash")?, size))
}
pub(super) fn hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
pub(super) fn is_hash(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_hexdigit())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn rejected_generation_does_not_download_again_until_daily_retry_but_new_identity_can() {
        use std::sync::atomic::AtomicUsize;
        let mut retry = ValidationRetry::default();
        let downloads = AtomicUsize::new(0);
        let now = Utc::now();
        let download = || async {
            downloads.fetch_add(1, Ordering::Relaxed);
            Ok(vec![1])
        };
        let result = validated_candidate(
            &mut retry,
            "version-one:hash-one",
            now,
            download(),
            |_| async { Err::<(), _>(anyhow::anyhow!("Invalid or unwritable database")) },
        )
        .await;
        assert!(result.is_err());
        assert_eq!(downloads.load(Ordering::Relaxed), 1);
        assert!(
            validated_candidate(
                &mut retry,
                "version-one:hash-one",
                now + ChronoDuration::minutes(5),
                download(),
                |_| async { Ok(()) }
            )
            .await
            .is_err()
        );
        assert_eq!(downloads.load(Ordering::Relaxed), 1);
        validated_candidate(
            &mut retry,
            "version-two:hash-two",
            now + ChronoDuration::minutes(5),
            download(),
            |_| async { Ok(()) },
        )
        .await
        .unwrap();
        assert_eq!(downloads.load(Ordering::Relaxed), 2);
        validated_candidate(
            &mut retry,
            "version-one:hash-one",
            now + ChronoDuration::days(1),
            download(),
            |_| async { Ok(()) },
        )
        .await
        .unwrap();
        assert_eq!(downloads.load(Ordering::Relaxed), 3);
    }
    #[tokio::test]
    async fn transport_failure_does_not_blacklist_a_database_that_was_never_received() {
        let mut retry = ValidationRetry::default();
        let now = Utc::now();
        assert!(
            validated_candidate(
                &mut retry,
                "generation",
                now,
                async { Err(anyhow::anyhow!("Network unavailable")) },
                |_| async { Ok(()) }
            )
            .await
            .is_err()
        );
        assert!(retry.rejected.is_none());
        validated_candidate(
            &mut retry,
            "generation",
            now,
            async { Ok(vec![1]) },
            |_| async { Ok(()) },
        )
        .await
        .unwrap();
    }
    #[tokio::test]
    async fn shutdown_waits_for_the_blocking_commit_before_a_replacement_owner_runs() {
        let root = fixture();
        let marker = root.join("generation");
        let (entered, entered_rx) = tokio::sync::oneshot::channel();
        let (release, release_rx) = std::sync::mpsc::channel();
        let mut worker = DatabaseWorker::empty_for_tests();
        let first_marker = marker.clone();
        worker.task = Some(tokio::spawn(async move {
            tokio::task::spawn_blocking(move || {
                let _ = entered.send(());
                release_rx.recv_timeout(Duration::from_secs(2)).unwrap();
                write_atomic(&first_marker, b"first", false).unwrap();
            })
            .await
            .unwrap();
        }));
        entered_rx.await.unwrap();
        let stopped = tokio::spawn(async move {
            worker.shutdown().await;
        });
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert!(
            !stopped.is_finished(),
            "Shutdown detached the current generation writer"
        );
        release.send(()).unwrap();
        tokio::time::timeout(Duration::from_secs(2), stopped)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(fs::read(&marker).unwrap(), b"first");
        // A re-enabled service may now commit, with no previous writer left to overwrite it.
        write_atomic(&marker, b"replacement", false).unwrap();
        tokio::time::sleep(Duration::from_millis(30)).await;
        assert_eq!(fs::read(&marker).unwrap(), b"replacement");
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn shutdown_cancels_network_wait_without_waiting_for_its_download_deadline() {
        let mut worker = DatabaseWorker::empty_for_tests();
        let mut stopped = worker.stop.subscribe();
        worker.task = Some(tokio::spawn(async move {
            let result = cancellable(std::future::pending::<Result<()>>(), &mut stopped).await;
            assert!(result.is_err());
        }));
        tokio::time::timeout(Duration::from_secs(1), worker.shutdown())
            .await
            .unwrap();
    }
    fn fixture() -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "NetLightsGeoStore-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }
    fn payload(country: &str, address: u8) -> Vec<u8> {
        // GeoIPList.entry(country_code, cidr(ipv4, prefix)).
        vec![
            10,
            14,
            10,
            2,
            country.as_bytes()[0],
            country.as_bytes()[1],
            18,
            8,
            10,
            4,
            address,
            0,
            0,
            0,
            16,
            8,
        ]
    }
    #[test]
    fn pointer_requires_one_bounded_hash_and_size() {
        let text = format!(
            "version https://git-lfs.github.com/spec/v1\noid sha256:{}\nsize 123\n",
            "a".repeat(64)
        );
        assert_eq!(
            parse_pointer(text.as_bytes()).unwrap(),
            ("a".repeat(64), 123)
        );
        for text in [
            "garbage".to_owned(),
            text.clone() + "size 123\n",
            text.replace("size 123", "size 0"),
            text.replace("size 123", "size 999999999"),
        ] {
            assert!(parse_pointer(text.as_bytes()).is_err());
        }
    }
    #[test]
    fn corrupt_manifest_does_not_relabel_legacy_database() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsGeoManifest-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        fs::create_dir_all(root.join("GeoIP")).unwrap();
        fs::write(root.join("GeoIP/current.json"), b"broken").unwrap();
        fs::write(root.join("GeoIP/geoip.dat"), b"old payload").unwrap();
        assert!(load_v2(&root, &AtomicBool::new(false)).is_err());
        assert_eq!(
            fs::read(root.join("GeoIP/geoip.dat")).unwrap(),
            b"old payload"
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn generations_restore_previous_hash_and_keep_snapshot_versions_immutable() {
        let root = fixture();
        let cancel = AtomicBool::new(false);
        let first = payload("DE", 1);
        let second = payload("JP", 2);
        save_generation(&root, &first, "release-one", &cancel).unwrap();
        let old = load_v2(&root, &cancel).unwrap().unwrap();
        save_generation(&root, &second, "release-two", &cancel).unwrap();
        let current = load_v2(&root, &cancel).unwrap().unwrap();
        assert_eq!(old.version, "release-one");
        assert_eq!(old.database.lookup("1.2.3.4".parse().unwrap()), Some("DE"));
        assert_eq!(current.version, "release-two");
        assert_eq!(
            current.database.lookup("2.2.3.4".parse().unwrap()),
            Some("JP")
        );
        let current_path = data_path(&root.join("GeoIP"), &current.hash);
        fs::write(current_path, b"corrupt").unwrap();
        let restored = load_v2(&root, &cancel).unwrap().unwrap();
        assert_eq!(restored.version, "release-one");
        assert_eq!(restored.hash, hash(&first));
        assert_eq!(
            restored.database.lookup("1.2.3.4".parse().unwrap()),
            Some("DE")
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn cancellation_and_failed_manifest_write_leave_previous_generation_usable() {
        let root = fixture();
        let old = payload("DE", 1);
        let new = payload("JP", 2);
        save_generation(&root, &old, "old", &AtomicBool::new(false)).unwrap();
        assert!(save_generation(&root, &new, "new", &AtomicBool::new(true)).is_err());
        assert_eq!(
            load_v2(&root, &AtomicBool::new(false))
                .unwrap()
                .unwrap()
                .version,
            "old"
        );
        let marker = root.join("GeoIP/current.json.bak");
        fs::create_dir(&marker).unwrap();
        assert!(save_generation(&root, &new, "new", &AtomicBool::new(false)).is_err());
        assert_eq!(
            load_v2(&root, &AtomicBool::new(false))
                .unwrap()
                .unwrap()
                .version,
            "old"
        );
        assert!(!data_path(&root.join("GeoIP"), &hash(&new)).exists());
        fs::remove_dir_all(root).unwrap();
    }
}
