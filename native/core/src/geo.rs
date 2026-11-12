use crate::{
    geoip::{DatabaseSnapshots, DatabaseWorker, normalize_country},
    history::LocationObservation,
    persistence::{read_bounded, write_atomic},
    traffic::{Category, Traffic},
    transport::{HttpResponse, Transport},
};
use anyhow::{Result, ensure};
use chrono::{DateTime, Duration as ChronoDuration, NaiveDate, Utc};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, HashMap},
    net::IpAddr,
    path::{Path, PathBuf},
    sync::Arc,
    time::{Duration, Instant},
};
use url::Url;

const COUNTRY_URL: &str = "https://api.country.is/";
const FALLBACK_URL: &str = "https://ipwho.is/?fields=success,country_code,ip";
const CACHE_LIMIT: usize = 512;

#[derive(Clone, Debug, Default)]
struct Lookup {
    ip: Option<IpAddr>,
    country: Option<String>,
    retry: Option<Duration>,
}
impl Lookup {
    fn usable(&self) -> bool {
        self.ip.is_some() && self.country.is_some()
    }
}
enum SourceClient {
    Network {
        transport: Transport,
        country_is: bool,
    },
    #[cfg(test)]
    Stub {
        replies: std::collections::VecDeque<Lookup>,
        calls: Arc<std::sync::atomic::AtomicUsize>,
    },
    #[cfg(test)]
    Delayed { reply: Lookup, delay: Duration },
}
impl SourceClient {
    async fn fetch(&mut self, now: DateTime<Utc>) -> Lookup {
        let started = Instant::now();
        match self {
            Self::Network {
                transport,
                country_is,
            } => {
                let url = if *country_is {
                    COUNTRY_URL
                } else {
                    FALLBACK_URL
                };
                let Ok(url) = Url::parse(url) else {
                    return Lookup::default();
                };
                match transport.get(&url, 4096, Duration::from_secs(5)).await {
                    Ok(response) => parse_response(response, *country_is, time_after(now, started)),
                    Err(_) => Lookup::default(),
                }
            }
            #[cfg(test)]
            Self::Stub { replies, calls } => {
                calls.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                replies.pop_front().unwrap_or_default()
            }
            #[cfg(test)]
            Self::Delayed { reply, delay } => {
                tokio::time::sleep(*delay).await;
                reply.clone()
            }
        }
    }
}
#[derive(Clone)]
struct CacheEntry {
    lookup: Lookup,
    expires: DateTime<Utc>,
}

/// The runtime owns one service and awaits one observation at a time. Online
/// polling is independent from UI visibility and from background database refresh.
pub struct GeoService {
    primary: SourceClient,
    fallback: SourceClient,
    databases: DatabaseWorker,
    usage: UsageLedger,
    cache: HashMap<String, CacheEntry>,
    primary_retry_until: Option<DateTime<Utc>>,
    fallback_retry_until: Option<DateTime<Utc>>,
    last_public_ip: Option<IpAddr>,
    last_observation: Option<LocationObservation>,
    last_confirmed_country: Option<String>,
    failures: usize,
    all_sources_retry: Option<Duration>,
}
impl GeoService {
    pub fn new(root: PathBuf, traffic: Arc<Traffic>) -> Result<Self> {
        Ok(Self {
            primary: SourceClient::Network {
                transport: Transport::new(traffic.clone(), Category::Country)?,
                country_is: true,
            },
            fallback: SourceClient::Network {
                transport: Transport::new(traffic.clone(), Category::Fallback)?,
                country_is: false,
            },
            databases: DatabaseWorker::start(root.clone(), traffic)?,
            usage: UsageLedger::open(&root),
            cache: HashMap::new(),
            primary_retry_until: None,
            fallback_retry_until: None,
            last_public_ip: None,
            last_observation: None,
            last_confirmed_country: None,
            failures: 0,
            all_sources_retry: None,
        })
    }
    pub async fn observe(&mut self) -> Result<LocationObservation> {
        self.observe_at(Utc::now()).await
    }
    pub fn database_status(&self) -> DatabaseSnapshots {
        self.databases.snapshots()
    }
    pub fn quota_failure(&self) -> Option<&'static str> {
        self.usage.failure
    }
    pub fn network_changed(&mut self) {
        self.failures = 0;
        // Neither cache identity nor a service-wide Retry-After belongs to a network epoch.
        // With no current IP, fallback must make a fresh request rather than using a cache.
    }
    pub fn next_delay(&self, default_seconds: u64) -> Duration {
        let interval = Duration::from_secs(
            if crate::model::COUNTRY_INTERVALS.contains(&default_seconds) {
                default_seconds
            } else {
                10
            },
        );
        if let Some(retry) = self.all_sources_retry {
            return retry.max(interval);
        }
        let backoff = match self.failures {
            0 => 0,
            1 => 15,
            2 => 45,
            3 => 120,
            _ => 0,
        };
        interval.max(Duration::from_secs(backoff))
    }
    pub async fn shutdown(&mut self) {
        self.databases.shutdown().await;
    }
    async fn observe_at(&mut self, now: DateTime<Utc>) -> Result<LocationObservation> {
        let started = Instant::now();
        let primary = if let Some(until) = self.primary_retry_until.filter(|until| *until > now) {
            Lookup {
                retry: Some(duration_until(now, until)),
                ..Lookup::default()
            }
        } else {
            let result = self.primary.fetch(now).await;
            if let Some(retry) = result.retry {
                self.primary_retry_until = Some(time_after(now, started) + chrono_duration(retry));
            }
            result
        };
        let mut ip = primary.ip;
        let mut accepted = primary.country.clone().filter(|_| primary.usable());
        let online = accepted.clone();
        let mut third = None;
        let mut retry = None;
        let mut source = "none";
        let mut different_ip = false;
        if primary.usable() {
            source = "country.is";
        } else {
            let fallback = self
                .fallback_observation(primary.ip, time_after(now, started))
                .await;
            // A service's Retry-After gates only that service. The scheduler can
            // sleep longer only when neither online source is eligible.
            retry = primary.retry.zip(fallback.retry).map(|(a, b)| a.min(b));
            different_ip = primary.ip.zip(fallback.ip).is_some_and(|(a, b)| a != b);
            if fallback.usable() && !different_ip {
                ip = fallback.ip;
                accepted = fallback.country.clone();
                third = fallback.country;
                source = "ipwho.is";
            } else {
                ip = primary.ip.or(self.last_public_ip);
                accepted = None;
            }
        }
        let ip_observed = primary.ip.is_some() || accepted.is_some();
        if ip_observed {
            self.last_public_ip = ip;
        }
        let snapshots = self.databases.snapshots();
        let local = ip.and_then(|ip| {
            snapshots
                .v2fly
                .as_ref()?
                .database
                .lookup(ip)
                .map(str::to_owned)
        });
        let iplocate = ip.and_then(|ip| {
            snapshots
                .iplocate
                .as_ref()?
                .database
                .lookup(ip)
                .map(str::to_owned)
        });
        let database_version = snapshots.v2fly.as_ref().map(|s| s.version.clone());
        let iplocate_database_version = snapshots.iplocate.as_ref().map(|s| s.version.clone());
        let confirmed = accepted.is_some() && ip.is_some();
        if !confirmed && !different_ip && (local.is_some() || iplocate.is_some()) {
            source = "local-estimate";
        }
        let public_ip = ip.map(|ip| ip.to_string());
        let public_ip_hash = public_ip.as_ref().map(|ip| digest(ip.as_bytes()));
        let previous = self.last_observation.as_ref();
        let same_ip = public_ip_hash.is_some()
            && previous.is_some_and(|previous| previous.public_ip_hash == public_ip_hash);
        let observed_utc = time_after(now, started);
        let observation = LocationObservation {
            observed_utc,
            observed_until_utc: Some(observed_utc),
            public_ip_hash: public_ip_hash.clone(),
            public_ip,
            country_code: accepted.clone(),
            local_country: local.clone(),
            online_country: online.clone(),
            third_country: third,
            database_version: database_version.clone(),
            iplocate_country: iplocate.clone(),
            iplocate_database_version: iplocate_database_version.clone(),
            public_ip_changed: ip_observed
                && previous.is_some_and(|previous| {
                    previous.public_ip_hash.is_some() && previous.public_ip_hash != public_ip_hash
                }),
            country_changed: confirmed
                && self
                    .last_confirmed_country
                    .as_ref()
                    .is_some_and(|last| Some(last) != accepted.as_ref()),
            classification_changed: same_ip
                && previous.is_some_and(|previous| {
                    previous.local_country != local
                        || previous.online_country != online
                        || previous.database_version != database_version
                        || previous.iplocate_country != iplocate
                        || previous.iplocate_database_version != iplocate_database_version
                }),
            confirmed,
            resolution_source: Some(source.into()),
        };
        if confirmed {
            self.last_confirmed_country = accepted;
            self.failures = 0;
            self.all_sources_retry = None;
        } else {
            self.failures = self.failures.saturating_add(1).min(4);
            self.all_sources_retry = retry;
        }
        self.last_observation = Some(observation.clone());
        Ok(observation)
    }
    async fn fallback_observation(
        &mut self,
        current_ip: Option<IpAddr>,
        now: DateTime<Utc>,
    ) -> Lookup {
        let started = Instant::now();
        if let Some(until) = self.fallback_retry_until.filter(|until| *until > now) {
            return Lookup {
                retry: Some(duration_until(now, until)),
                ..Lookup::default()
            };
        }
        self.cache.retain(|_, entry| entry.expires > now);
        let key = current_ip.map(|ip| digest(ip.to_string().as_bytes()));
        if let Some(cached) = key.as_ref().and_then(|key| self.cache.get(key)) {
            return cached.lookup.clone();
        }
        if !self.usage.consume(now.date_naive()) {
            // Revisit the persisted daily budget after rollover even if the primary
            // service is currently throttled for a much longer period.
            return Lookup {
                retry: Some(Duration::from_secs(10)),
                ..Lookup::default()
            };
        }
        let result = self.fallback.fetch(now).await;
        let now = time_after(now, started);
        if let Some(retry) = result.retry {
            self.fallback_retry_until = Some(now + chrono_duration(retry));
        }
        if result.usable() {
            if let Some(returned_ip) = result.ip {
                self.cache.insert(
                    digest(returned_ip.to_string().as_bytes()),
                    CacheEntry {
                        lookup: result.clone(),
                        expires: now + ChronoDuration::hours(24),
                    },
                );
            }
        } else if let Some(key) = key {
            let retry = result.retry.unwrap_or(Duration::from_secs(300));
            self.cache.insert(
                key,
                CacheEntry {
                    lookup: result.clone(),
                    expires: now + chrono_duration(retry),
                },
            );
        }
        while self.cache.len() > CACHE_LIMIT {
            let oldest = self
                .cache
                .iter()
                .min_by_key(|(_, entry)| entry.expires)
                .map(|(key, _)| key.clone());
            if let Some(oldest) = oldest {
                self.cache.remove(&oldest);
            } else {
                break;
            }
        }
        result
    }
}
fn time_after(utc: DateTime<Utc>, started: Instant) -> DateTime<Utc> {
    utc + ChronoDuration::from_std(started.elapsed()).unwrap_or_default()
}
fn duration_until(now: DateTime<Utc>, until: DateTime<Utc>) -> Duration {
    (until - now).to_std().unwrap_or_default()
}
fn chrono_duration(duration: Duration) -> ChronoDuration {
    ChronoDuration::milliseconds(duration.as_millis().min(86_400_000) as i64)
}
fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn parse_response(response: HttpResponse, country_is: bool, now: DateTime<Utc>) -> Lookup {
    if response.status == 429 {
        let retry = response
            .headers
            .get("retry-after")
            .and_then(|header| {
                if let Ok(seconds) = header.parse::<u64>() {
                    return (seconds > 0).then_some(Duration::from_secs(seconds.min(86400)));
                }
                DateTime::parse_from_rfc2822(header)
                    .ok()
                    .map(|date| date.with_timezone(&Utc))
                    .filter(|date| *date > now)
                    .map(|date| duration_until(now, date).min(Duration::from_secs(86400)))
            })
            .unwrap_or(Duration::from_secs(60));
        return Lookup {
            retry: Some(retry),
            ..Lookup::default()
        };
    }
    if !(200..300).contains(&response.status) || response.body.len() > 4096 {
        return Lookup::default();
    }
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(&response.body) else {
        return Lookup::default();
    };
    if !country_is && value["success"].as_bool() != Some(true) {
        return Lookup::default();
    }
    let ip = value["ip"]
        .as_str()
        .and_then(|ip| ip.parse::<IpAddr>().ok())
        .filter(|ip| !ip.is_loopback() && !ip.is_unspecified() && !ip.is_multicast());
    let key = if country_is {
        "country"
    } else {
        "country_code"
    };
    let country = value[key].as_str().and_then(normalize_country);
    Lookup {
        ip,
        country,
        retry: None,
    }
}

#[derive(Serialize, Deserialize)]
struct UsageFile {
    #[serde(rename = "Entries")]
    entries: Vec<Option<UsageEntry>>,
}
#[derive(Serialize, Deserialize)]
struct UsageEntry {
    #[serde(rename = "EgressHash")]
    hash: String,
    #[serde(rename = "UtcDate")]
    date: String,
    #[serde(rename = "Count")]
    count: u32,
}
struct UsageLedger {
    path: PathBuf,
    counts: BTreeMap<String, (NaiveDate, u32)>,
    failure: Option<&'static str>,
}
impl UsageLedger {
    fn open(root: &Path) -> Self {
        let mut result = Self {
            path: root.join("GeoIP/ipwho-usage.json"),
            counts: BTreeMap::new(),
            failure: None,
        };
        match result.load() {
            Ok(()) => {}
            Err(_) => {
                result.counts.clear();
                result.failure = Some("ipwho-quota-load-failed");
            }
        }
        result
    }
    fn load(&mut self) -> Result<()> {
        let bytes = match read_bounded(&self.path, 1024 * 1024) {
            Ok(bytes) => bytes,
            Err(error)
                if error
                    .downcast_ref::<std::io::Error>()
                    .is_some_and(|error| error.kind() == std::io::ErrorKind::NotFound) =>
            {
                return Ok(());
            }
            Err(error) => return Err(error),
        };
        ensure!(!bytes.is_empty(), "Quota file is empty");
        let file: UsageFile = serde_json::from_slice(&bytes)?;
        ensure!(file.entries.len() <= 2048, "Too many quota entries");
        for entry in file.entries {
            let entry = entry.ok_or_else(|| anyhow::anyhow!("Null quota entry"))?;
            ensure!(
                entry.hash.len() == 64
                    && entry.hash.bytes().all(|b| b.is_ascii_hexdigit())
                    && entry.count <= 500,
                "Invalid quota entry"
            );
            let date = NaiveDate::parse_from_str(&entry.date, "%Y-%m-%d")?;
            ensure!(
                date.format("%Y-%m-%d").to_string() == entry.date,
                "Invalid quota date"
            );
            ensure!(
                self.counts
                    .insert(entry.hash.to_ascii_lowercase(), (date, entry.count))
                    .is_none(),
                "Duplicate quota key"
            );
        }
        Ok(())
    }
    fn consume(&mut self, today: NaiveDate) -> bool {
        if self.failure.is_some() {
            return false;
        }
        let count: u64 = self
            .counts
            .values()
            .filter(|(date, _)| *date == today)
            .map(|(_, count)| u64::from(*count))
            .sum();
        if count >= 500 {
            return false;
        }
        self.counts
            .retain(|_, (date, _)| *date >= today - ChronoDuration::days(1) && *date != today);
        self.counts
            .insert(digest(b"application-budget"), (today, count as u32 + 1));
        let data = UsageFile {
            entries: self
                .counts
                .iter()
                .map(|(hash, (date, count))| {
                    Some(UsageEntry {
                        hash: hash.clone(),
                        date: date.format("%Y-%m-%d").to_string(),
                        count: *count,
                    })
                })
                .collect(),
        };
        let result = serde_json::to_vec(&data)
            .map_err(anyhow::Error::from)
            .and_then(|bytes| write_atomic(&self.path, &bytes, false));
        if result.is_err() {
            self.failure = Some("ipwho-quota-save-failed");
            return false;
        }
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        fs,
        sync::atomic::{AtomicUsize, Ordering},
    };
    fn root() -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "NetLightsGeo-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }
    fn answer(ip: &str, country: &str) -> Lookup {
        Lookup {
            ip: Some(ip.parse().unwrap()),
            country: Some(country.into()),
            retry: None,
        }
    }
    fn service(
        root: &Path,
        primary: Vec<Lookup>,
        fallback: Vec<Lookup>,
    ) -> (GeoService, Arc<AtomicUsize>, Arc<AtomicUsize>) {
        let a = Arc::new(AtomicUsize::new(0));
        let b = Arc::new(AtomicUsize::new(0));
        (
            GeoService {
                primary: SourceClient::Stub {
                    replies: primary.into(),
                    calls: a.clone(),
                },
                fallback: SourceClient::Stub {
                    replies: fallback.into(),
                    calls: b.clone(),
                },
                databases: DatabaseWorker::empty_for_tests(),
                usage: UsageLedger::open(root),
                cache: HashMap::new(),
                primary_retry_until: None,
                fallback_retry_until: None,
                last_public_ip: None,
                last_observation: None,
                last_confirmed_country: None,
                failures: 0,
                all_sources_retry: None,
            },
            a,
            b,
        )
    }
    #[tokio::test(flavor = "current_thread")]
    async fn newly_created_geo_service_can_stop_before_any_background_network_request() {
        let root = root();
        let traffic = Arc::new(Traffic::default());
        let mut service = GeoService::new(root.clone(), traffic.clone()).unwrap();
        assert!(service.database_status().v2fly.is_none());
        assert!(service.database_status().iplocate.is_none());
        assert_eq!(service.quota_failure(), None);
        // On this current-thread executor the spawned database owner hasn't
        // polled yet. Shutdown must set cancellation before awaiting that owner.
        tokio::time::timeout(Duration::from_secs(2), service.shutdown())
            .await
            .unwrap();
        assert!(
            traffic.snapshot().is_empty(),
            "A disabled service contacted a public source during shutdown"
        );
        tokio::time::timeout(Duration::from_secs(2), service.shutdown())
            .await
            .unwrap();
        assert!(traffic.snapshot().is_empty());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn online_throttling_dates_are_bounded_and_invalid_service_payloads_remain_unconfirmed() {
        let now = DateTime::parse_from_rfc3339("2026-11-11T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc);
        for (header, expected) in [
            ("120".to_owned(), 120),
            ((now + ChronoDuration::seconds(90)).to_rfc2822(), 90),
            ((now + ChronoDuration::days(30)).to_rfc2822(), 86400),
            (now.to_rfc2822(), 60),
            ("0".to_owned(), 60),
        ] {
            let row = parse_response(
                HttpResponse {
                    status: 429,
                    headers: BTreeMap::from([("retry-after".into(), header)]),
                    body: Vec::new(),
                },
                true,
                now,
            );
            assert_eq!(row.retry, Some(Duration::from_secs(expected)));
            assert!(!row.usable());
        }
        for (status, body) in [
            (500, b"{}".to_vec()),
            (200, b"broken-json".to_vec()),
            (200, vec![b' '; 4097]),
        ] {
            let row = parse_response(
                HttpResponse {
                    status,
                    headers: BTreeMap::new(),
                    body,
                },
                true,
                now,
            );
            assert!(!row.usable());
            assert_eq!(row.ip, None);
            assert_eq!(row.country, None);
        }
    }
    #[tokio::test]
    async fn failed_checks_use_the_documented_backoff_but_network_change_only_resets_failures() {
        let root = root();
        let (mut service, _, _) = service(&root, vec![], vec![]);
        let now = Utc::now();
        assert_eq!(service.next_delay(10), Duration::from_secs(10));
        for (index, expected) in [15, 45, 120, 10].into_iter().enumerate() {
            let row = service
                .observe_at(now + ChronoDuration::minutes(index as i64))
                .await
                .unwrap();
            assert!(!row.confirmed);
            assert_eq!(service.next_delay(10), Duration::from_secs(expected));
        }
        service.network_changed();
        assert_eq!(service.next_delay(u64::MAX), Duration::from_secs(10));
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn primary_country_is_accepted_without_spending_fallback_quota() {
        let root = root();
        let (mut service, primary, fallback) =
            service(&root, vec![answer("203.0.113.5", "JP")], vec![]);
        let row = service.observe().await.unwrap();
        assert!(row.confirmed);
        assert_eq!(row.country_code.as_deref(), Some("JP"));
        assert_eq!(row.resolution_source.as_deref(), Some("country.is"));
        assert_eq!(primary.load(Ordering::Relaxed), 1);
        assert_eq!(fallback.load(Ordering::Relaxed), 0);
        assert!(!root.join("GeoIP/ipwho-usage.json").exists());
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn unknown_ip_never_reuses_an_old_fallback_country_after_route_change() {
        let root = root();
        let (mut service, _, calls) = service(
            &root,
            vec![Lookup::default(), Lookup::default()],
            vec![answer("203.0.113.1", "DE"), answer("203.0.113.2", "JP")],
        );
        let first = service.observe().await.unwrap();
        service.network_changed();
        let second = service.observe().await.unwrap();
        assert_eq!(calls.load(Ordering::Relaxed), 2);
        assert_eq!(first.country_code.as_deref(), Some("DE"));
        assert_eq!(second.country_code.as_deref(), Some("JP"));
        assert!(second.public_ip_changed);
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn observation_and_retry_after_start_when_delayed_responses_arrive() {
        let root = root();
        let (mut service, _, _) = service(&root, vec![], vec![]);
        service.primary = SourceClient::Delayed {
            reply: Lookup::default(),
            delay: Duration::from_millis(35),
        };
        service.fallback = SourceClient::Delayed {
            reply: answer("203.0.113.1", "DE"),
            delay: Duration::from_millis(35),
        };
        let started_utc = Utc::now();
        let row = service.observe_at(started_utc).await.unwrap();
        assert!(row.confirmed);
        assert!(row.observed_utc >= started_utc + ChronoDuration::milliseconds(60));
        assert_eq!(row.observed_until_utc, Some(row.observed_utc));
        service.primary = SourceClient::Delayed {
            reply: Lookup {
                retry: Some(Duration::from_secs(60)),
                ..Lookup::default()
            },
            delay: Duration::from_millis(35),
        };
        service.fallback = SourceClient::Delayed {
            reply: Lookup {
                retry: Some(Duration::from_secs(120)),
                ..Lookup::default()
            },
            delay: Duration::from_millis(35),
        };
        let started_utc = Utc::now();
        let row = service.observe_at(started_utc).await.unwrap();
        assert!(!row.confirmed);
        assert!(
            service.primary_retry_until.unwrap()
                >= started_utc + ChronoDuration::seconds(60) + ChronoDuration::milliseconds(30)
        );
        assert!(
            service.fallback_retry_until.unwrap()
                >= started_utc + ChronoDuration::seconds(120) + ChronoDuration::milliseconds(60)
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn current_primary_ip_survives_missing_country_and_fallback_failure() {
        let root = root();
        let partial = Lookup {
            ip: Some("203.0.113.2".parse().unwrap()),
            ..Lookup::default()
        };
        let (mut service, _, fallback) = service(
            &root,
            vec![answer("203.0.113.1", "DE"), partial, Lookup::default()],
            vec![Lookup {
                retry: Some(Duration::from_secs(60)),
                ..Lookup::default()
            }],
        );
        assert!(service.observe().await.unwrap().confirmed);
        let row = service.observe().await.unwrap();
        assert!(!row.confirmed);
        assert_eq!(row.country_code, None);
        assert_eq!(row.public_ip.as_deref(), Some("203.0.113.2"));
        assert!(row.public_ip_changed);
        assert_eq!(
            row.public_ip_hash.as_deref(),
            Some(digest(b"203.0.113.2").as_str())
        );
        let missing = service.observe().await.unwrap();
        assert!(!missing.confirmed);
        assert_eq!(missing.public_ip.as_deref(), Some("203.0.113.2"));
        assert!(!missing.public_ip_changed);
        assert_eq!(fallback.load(Ordering::Relaxed), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn verified_current_ip_can_use_fallback_cache_and_mismatched_ip_is_unknown() {
        let root = root();
        let partial = Lookup {
            ip: Some("203.0.113.1".parse().unwrap()),
            ..Lookup::default()
        };
        let (mut service, _, calls) = service(
            &root,
            vec![partial.clone(), partial],
            vec![answer("203.0.113.1", "DE")],
        );
        assert!(service.observe().await.unwrap().confirmed);
        assert!(service.observe().await.unwrap().confirmed);
        assert_eq!(calls.load(Ordering::Relaxed), 1);
        let (mut mismatch, _, _) = super::tests::service(
            &root,
            vec![Lookup {
                ip: Some("203.0.113.1".parse().unwrap()),
                ..Lookup::default()
            }],
            vec![answer("203.0.113.2", "FR")],
        );
        assert!(!mismatch.observe().await.unwrap().confirmed);
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn fallback_retry_after_survives_network_events_without_throttling_primary() {
        let root = root();
        let throttled = Lookup {
            retry: Some(Duration::from_secs(600)),
            ..Lookup::default()
        };
        let (mut service, primary_calls, calls) = service(
            &root,
            vec![Lookup::default(), Lookup::default()],
            vec![throttled],
        );
        let now = Utc::now();
        assert!(!service.observe_at(now).await.unwrap().confirmed);
        service.network_changed();
        assert!(
            !service
                .observe_at(now + ChronoDuration::seconds(2))
                .await
                .unwrap()
                .confirmed
        );
        assert_eq!(calls.load(Ordering::Relaxed), 1);
        assert_eq!(primary_calls.load(Ordering::Relaxed), 2);
        assert_eq!(service.next_delay(10), Duration::from_secs(15));
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn fallback_throttling_cannot_suspend_a_recovered_primary() {
        let root = root();
        let (mut service, primary_calls, fallback_calls) = service(
            &root,
            vec![Lookup::default(), answer("203.0.113.7", "DE")],
            vec![Lookup {
                retry: Some(Duration::from_secs(86400)),
                ..Lookup::default()
            }],
        );
        let now = Utc::now();
        assert!(!service.observe_at(now).await.unwrap().confirmed);
        let delay = service.next_delay(10);
        assert!(
            service
                .observe_at(now + ChronoDuration::seconds(15))
                .await
                .unwrap()
                .confirmed
        );
        assert_eq!(primary_calls.load(Ordering::Relaxed), 2);
        assert_eq!(fallback_calls.load(Ordering::Relaxed), 1);
        fs::remove_dir_all(root).unwrap();
        assert!(delay <= Duration::from_secs(15));
    }
    #[tokio::test]
    async fn both_throttled_sources_wait_for_the_earliest_deadline_without_bypassing_it() {
        let root = root();
        let throttled = |seconds| Lookup {
            retry: Some(Duration::from_secs(seconds)),
            ..Lookup::default()
        };
        let (mut service, primary_calls, fallback_calls) =
            service(&root, vec![throttled(86400)], vec![throttled(600)]);
        let now = Utc::now();
        assert!(!service.observe_at(now).await.unwrap().confirmed);
        assert_eq!(service.next_delay(10), Duration::from_secs(600));
        service.network_changed();
        assert!(
            !service
                .observe_at(now + ChronoDuration::seconds(2))
                .await
                .unwrap()
                .confirmed
        );
        assert!(service.next_delay(10) >= Duration::from_secs(597));
        assert_eq!(primary_calls.load(Ordering::Relaxed), 1);
        assert_eq!(fallback_calls.load(Ordering::Relaxed), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn exhausted_daily_budget_can_recover_before_primary_retry_after_expires() {
        let root = root();
        fs::create_dir_all(root.join("GeoIP")).unwrap();
        let now = Utc::now();
        let quota = UsageFile {
            entries: vec![Some(UsageEntry {
                hash: digest(b"application-budget"),
                date: now.date_naive().to_string(),
                count: 500,
            })],
        };
        fs::write(
            root.join("GeoIP/ipwho-usage.json"),
            serde_json::to_vec(&quota).unwrap(),
        )
        .unwrap();
        let (mut service, _, calls) = service(
            &root,
            vec![Lookup {
                retry: Some(Duration::from_secs(86400)),
                ..Lookup::default()
            }],
            vec![answer("203.0.113.1", "DE")],
        );
        assert!(!service.observe_at(now).await.unwrap().confirmed);
        assert_eq!(service.next_delay(10), Duration::from_secs(10));
        assert_eq!(calls.load(Ordering::Relaxed), 0);
        let tomorrow = (now.date_naive() + ChronoDuration::days(1))
            .and_hms_opt(0, 0, 1)
            .unwrap()
            .and_utc();
        assert!(service.observe_at(tomorrow).await.unwrap().confirmed);
        assert_eq!(calls.load(Ordering::Relaxed), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn quota_is_global_persistent_and_resets_on_next_utc_day() {
        let root = root();
        let today = Utc::now().date_naive();
        let mut quota = UsageLedger::open(&root);
        for _ in 0..500 {
            assert!(quota.consume(today));
        }
        assert!(!quota.consume(today));
        drop(quota);
        let mut restored = UsageLedger::open(&root);
        assert!(!restored.consume(today));
        assert!(restored.consume(today + ChronoDuration::days(1)));
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn quota_corruption_null_duplicate_and_write_failure_fail_closed() {
        let root = root();
        fs::create_dir_all(root.join("GeoIP")).unwrap();
        let path = root.join("GeoIP/ipwho-usage.json");
        let duplicate = format!(
            r#"{{"Entries":[{{"EgressHash":"{}","UtcDate":"2026-11-11","Count":1}},{{"EgressHash":"{}","UtcDate":"2026-11-11","Count":1}}]}}"#,
            "a".repeat(64),
            "A".repeat(64)
        );
        for value in [
            "null",
            "{\"Entries\":null}",
            "{\"Entries\":[null]}",
            duplicate.as_str(),
            "broken",
        ] {
            fs::write(&path, value).unwrap();
            let mut quota = UsageLedger::open(&root);
            assert!(!quota.consume(Utc::now().date_naive()));
            assert!(quota.failure.is_some());
        }
        fs::remove_file(&path).unwrap();
        fs::create_dir(&path).unwrap();
        let mut quota = UsageLedger::open(&root);
        assert!(!quota.consume(Utc::now().date_naive()));
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn online_parser_rejects_invalid_payloads_and_honors_geo_retry_default() {
        let response = |status, body: &[u8]| HttpResponse {
            status,
            headers: BTreeMap::new(),
            body: body.to_vec(),
        };
        let now = Utc::now();
        assert!(
            !parse_response(
                response(200, br#"{"country":"ZZ","ip":"203.0.113.1"}"#),
                true,
                now
            )
            .usable()
        );
        assert!(
            !parse_response(
                response(
                    200,
                    br#"{"success":false,"country_code":"DE","ip":"203.0.113.1"}"#
                ),
                false,
                now
            )
            .usable()
        );
        assert!(
            !parse_response(
                response(200, br#"{"country":"DE","ip":"127.0.0.1"}"#),
                true,
                now
            )
            .usable()
        );
        assert_eq!(
            parse_response(response(429, b""), true, now).retry,
            Some(Duration::from_secs(60))
        );
        assert!(
            parse_response(
                response(200, br#"{"country":"de","ip":"2001:db8::1"}"#),
                true,
                now
            )
            .usable()
        );
    }
}
