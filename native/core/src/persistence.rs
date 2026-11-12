use crate::{history::*, locale::LocalizedText, model::*};
use anyhow::{Context, Result};
use chrono::Utc;
use serde::{Serialize, de::DeserializeOwned};
use std::{
    fs::{self, File, OpenOptions},
    io::{BufWriter, Read, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};
static WRITE_SEQUENCE: AtomicU64 = AtomicU64::new(0);
const SETTINGS_FILE_LIMIT: u64 = 1024 * 1024;
const AVAILABILITY_FILE_LIMIT: u64 = 16 * 1024 * 1024;
const LOCATION_FILE_LIMIT: u64 = 64 * 1024 * 1024;

fn store_file_limit(path: &Path) -> u64 {
    match path.file_name().and_then(|name| name.to_str()) {
        Some("settings.json" | "endpoints.json") => SETTINGS_FILE_LIMIT,
        Some("availability-history.json") => AVAILABILITY_FILE_LIMIT,
        _ => LOCATION_FILE_LIMIT,
    }
}

/// Validate the previous JSON without allocating a Value tree for every saved
/// observation. Scalar parsing and the depth limit remain those of serde_json.
struct ValidJson;
impl<'de> serde::Deserialize<'de> for ValidJson {
    fn deserialize<D: serde::Deserializer<'de>>(
        deserializer: D,
    ) -> std::result::Result<Self, D::Error> {
        struct Check;
        impl<'de> serde::de::Visitor<'de> for Check {
            type Value = ValidJson;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("valid JSON")
            }
            fn visit_bool<E: serde::de::Error>(self, _: bool) -> std::result::Result<ValidJson, E> {
                Ok(ValidJson)
            }
            fn visit_i64<E: serde::de::Error>(self, _: i64) -> std::result::Result<ValidJson, E> {
                Ok(ValidJson)
            }
            fn visit_u64<E: serde::de::Error>(self, _: u64) -> std::result::Result<ValidJson, E> {
                Ok(ValidJson)
            }
            fn visit_f64<E: serde::de::Error>(
                self,
                value: f64,
            ) -> std::result::Result<ValidJson, E> {
                if value.is_finite() {
                    Ok(ValidJson)
                } else {
                    Err(E::custom("Nonfinite JSON number"))
                }
            }
            fn visit_str<E: serde::de::Error>(self, _: &str) -> std::result::Result<ValidJson, E> {
                Ok(ValidJson)
            }
            fn visit_unit<E: serde::de::Error>(self) -> std::result::Result<ValidJson, E> {
                Ok(ValidJson)
            }
            fn visit_seq<A: serde::de::SeqAccess<'de>>(
                self,
                mut seq: A,
            ) -> std::result::Result<ValidJson, A::Error> {
                while seq.next_element::<ValidJson>()?.is_some() {}
                Ok(ValidJson)
            }
            fn visit_map<A: serde::de::MapAccess<'de>>(
                self,
                mut map: A,
            ) -> std::result::Result<ValidJson, A::Error> {
                while map.next_key::<serde::de::IgnoredAny>()?.is_some() {
                    let _: ValidJson = map.next_value()?;
                }
                Ok(ValidJson)
            }
        }
        deserializer.deserialize_any(Check)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LoadStatus {
    Absent,
    Loaded,
    Recovered,
    Blocked,
}
pub struct Loaded<T> {
    pub value: T,
    pub status: LoadStatus,
}
#[derive(Clone)]
pub struct Profile {
    pub root: PathBuf,
    pub settings: Settings,
    pub availability: AvailabilityHistory,
    pub locations: LocationHistory,
    pub endpoints: Vec<Endpoint>,
    pub warnings: Vec<LocalizedText>,
    settings_writable: bool,
    availability_writable: bool,
    locations_writable: bool,
}
/// An immutable write request contains only changed stores. The runtime admits
/// one writer; unchanged country history isn't cloned for an availability save.
pub struct SaveSnapshot {
    root: PathBuf,
    settings: Option<Settings>,
    availability: Option<AvailabilityHistory>,
    locations: Option<LocationHistory>,
}
impl SaveSnapshot {
    pub fn write(self) -> Vec<&'static str> {
        let mut failures = Vec::new();
        if let Some(value) = self.settings
            && save_json(&self.root.join("settings.json"), &value).is_err()
        {
            failures.push("settings-save-failed");
        }
        if let Some(value) = self.availability
            && save_json(&self.root.join("availability-history.json"), &value).is_err()
        {
            failures.push("availability-save-failed");
        }
        if let Some(value) = self.locations
            && save_json(&self.root.join("location-history.json"), &value).is_err()
        {
            failures.push("location-save-failed");
        }
        failures
    }
}
pub fn read_bounded(path: &Path, maximum: u64) -> Result<Vec<u8>> {
    let mut input = File::open(path)?;
    anyhow::ensure!(
        input.metadata()?.len() <= maximum,
        "File exceeds its size limit"
    );
    let mut bytes = Vec::new();
    Read::by_ref(&mut input)
        .take(maximum + 1)
        .read_to_end(&mut bytes)?;
    anyhow::ensure!(
        bytes.len() as u64 <= maximum,
        "File grew beyond its size limit"
    );
    Ok(bytes)
}
pub fn read_json<T: DeserializeOwned>(path: &Path, maximum: u64) -> Result<T> {
    let bytes = read_bounded(path, maximum)?;
    let bytes = bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes);
    Ok(serde_json::from_slice(bytes)?)
}
pub fn load<T: DeserializeOwned + Default>(
    path: &Path,
    maximum: u64,
    valid: impl Fn(&T) -> bool,
) -> Loaded<T> {
    if let Ok(value) = read_json(path, maximum)
        && valid(&value)
    {
        return Loaded {
            value,
            status: LoadStatus::Loaded,
        };
    }
    if let Ok(value) = read_json(&path.with_extension("json.bak"), maximum)
        && valid(&value)
    {
        return Loaded {
            value,
            status: LoadStatus::Recovered,
        };
    }
    Loaded {
        value: T::default(),
        status: if path.exists() || path.with_extension("json.bak").exists() {
            LoadStatus::Blocked
        } else {
            LoadStatus::Absent
        },
    }
}
/// The caller is the single profile owner. Atomic publication never truncates a live file.
pub fn write_atomic(path: &Path, bytes: &[u8], backup: bool) -> Result<()> {
    write_atomic_with(path, backup, |output| Ok(output.write_all(bytes)?))
}
fn write_atomic_with(
    path: &Path,
    backup: bool,
    write: impl FnOnce(&mut File) -> Result<()>,
) -> Result<()> {
    let parent = path.parent().context("File needs a parent directory")?;
    fs::create_dir_all(parent)?;
    let name = path
        .file_name()
        .context("File needs a name")?
        .to_string_lossy();
    // A file left by an interrupted process mustn't block a later process whose
    // PID Windows happens to reuse. Every owned request has a distinct name.
    let nonce = format!(
        "{}.{}.{}",
        std::process::id(),
        Utc::now().timestamp_nanos_opt().unwrap_or_default(),
        WRITE_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    );
    let temporary = parent.join(format!(".{name}.{nonce}.tmp"));
    let result = (|| {
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        write(&mut output)?;
        output.sync_all()?;
        drop(output);
        if backup && path.is_file() {
            let bak = path.with_extension("json.bak");
            let bak_temp = parent.join(format!(".{name}.bak.{nonce}.tmp"));
            let backup_result = (|| {
                let committed = read_bounded(path, LOCATION_FILE_LIMIT)?;
                // Never replace the sole valid backup with a corrupt primary.
                let json = committed
                    .strip_prefix(&[0xef, 0xbb, 0xbf])
                    .unwrap_or(&committed);
                serde_json::from_slice::<ValidJson>(json)?;
                let mut output = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&bak_temp)?;
                output.write_all(&committed)?;
                output.sync_all()?;
                drop(output);
                fs::rename(&bak_temp, &bak)?;
                Ok::<(), anyhow::Error>(())
            })();
            let _ = fs::remove_file(&bak_temp);
            backup_result?;
        }
        fs::rename(&temporary, path)?;
        Ok(())
    })();
    let _ = fs::remove_file(&temporary);
    result
}
pub fn save_json(path: &Path, value: &impl Serialize) -> Result<()> {
    write_atomic_with(path, true, |output| {
        // Serialize directly into the owned temporary file. A maximum history
        // no longer needs an additional complete JSON allocation at every save.
        {
            let mut buffered = BufWriter::with_capacity(64 * 1024, &mut *output);
            serde_json::to_writer_pretty(&mut buffered, value)?;
            buffered.flush()?;
        }
        anyhow::ensure!(
            output.metadata()?.len() <= store_file_limit(path),
            "Serialized store exceeds its read limit; previous files are preserved"
        );
        Ok(())
    })
}

impl Profile {
    pub fn open(root: PathBuf, isolated: bool) -> Result<Self> {
        fs::create_dir_all(&root)?;
        let loaded: Loaded<Settings> = load(
            &root.join("settings.json"),
            SETTINGS_FILE_LIMIT,
            |s: &Settings| s.settings_version <= 8,
        );
        let writable = loaded.status != LoadStatus::Blocked;
        let mut settings = loaded.value;
        settings.normalize();
        if isolated {
            settings.auto_start = false;
            settings.auto_update_enabled = false;
        }
        let mut warnings = Vec::new();
        for (status, name) in [(loaded.status, "настроек")] {
            match status {
                LoadStatus::Recovered => warnings.push(LocalizedText::new(format!("Восстановлена резервная копия {name}."), "The settings backup was restored.")),
                LoadStatus::Blocked => warnings.push(LocalizedText::new(format!("Файл {name} повреждён или имеет неподдерживаемую версию. Запись заблокирована; исходные данные сохранены."), "The settings file is damaged or has an unsupported version. Writes are blocked; the original data was preserved.")),
                _ => {},
            }
        }
        let states = load(
            &root.join("availability-history.json"),
            AVAILABILITY_FILE_LIMIT,
            |h: &AvailabilityHistory| h.spans.len() <= 10_002,
        );
        let countries = load(
            &root.join("location-history.json"),
            LOCATION_FILE_LIMIT,
            |h: &LocationHistory| {
                h.version <= 3
                    && h.observations.len() <= LOCATION_OBSERVATION_LIMIT
                    && h.stays.len() <= LOCATION_STAY_LIMIT
            },
        );
        for (status, filename) in [
            (states.status, "availability-history.json"),
            (countries.status, "location-history.json"),
        ] {
            if status == LoadStatus::Blocked {
                warnings.push(LocalizedText::new(format!("Не удалось прочитать {filename}. Исходный файл сохранён; запись этой истории заблокирована."), format!("Couldn't read {filename}. The original file was preserved; writes to this history are blocked.")));
            }
        }
        let mut availability = states.value;
        let mut locations = countries.value;
        locations.stays.sort_by_key(|s| s.started_utc);
        locations.observations.sort_by_key(|o| o.observed_utc);
        // A new process never claims that the previous session monitored its downtime,
        // even when a crash/restart took less than the ordinary freshness tolerance.
        let frontier = availability
            .last_observed_utc
            .or_else(|| availability.spans.iter().map(|s| s.started_utc).max())
            .unwrap_or_else(Utc::now);
        availability.close(frontier);
        availability.prune(Utc::now());
        let frontier = locations
            .last_observed_utc
            .or_else(|| locations.observations.last().map(|o| o.until()))
            .or_else(|| locations.stays.last().map(|s| s.started_utc))
            .unwrap_or_else(Utc::now);
        locations.close(frontier);
        locations.prune(Utc::now());
        let mut endpoints = builtins();
        let pool_path = root.join("endpoints.json");
        if pool_path.exists() {
            match read_json::<Vec<Endpoint>>(&pool_path, 1024 * 1024) {
                Ok(custom) if validate_endpoints(&custom).is_ok() => endpoints = custom,
                _ => warnings.push(LocalizedText::new(
                    "Список адресов некорректен. Используется встроенный список.",
                    "The endpoint list is invalid. The built-in list is being used.",
                )),
            }
        }
        for (status, filename) in [
            (loaded.status, "settings.json"),
            (states.status, "availability-history.json"),
            (countries.status, "location-history.json"),
        ] {
            if status == LoadStatus::Recovered && root.join(filename).is_file() {
                let corrupt = root.join(format!(
                    "{filename}.corrupt-{}-{}",
                    Utc::now().timestamp_nanos_opt().unwrap_or_default(),
                    std::process::id()
                ));
                if fs::rename(root.join(filename), corrupt).is_err() {
                    warnings.push(LocalizedText::new(format!("Исходный {filename} не удалось сохранить отдельно. Новые записи могут быть отложены."), format!("Couldn't preserve the original {filename} separately. New writes may be delayed.")));
                }
            }
        }
        Ok(Self {
            root,
            settings,
            availability,
            locations,
            endpoints,
            warnings,
            settings_writable: writable,
            availability_writable: states.status != LoadStatus::Blocked,
            locations_writable: countries.status != LoadStatus::Blocked,
        })
    }
    pub fn save_settings(&self) -> Result<()> {
        anyhow::ensure!(
            self.settings_writable,
            "Запись повреждённых настроек заблокирована."
        );
        save_json(&self.root.join("settings.json"), &self.settings)
    }
    pub fn settings_writable(&self) -> bool {
        self.settings_writable
    }
    pub fn snapshot(&self, settings: bool, availability: bool, locations: bool) -> SaveSnapshot {
        SaveSnapshot {
            root: self.root.clone(),
            settings: (settings && self.settings_writable).then(|| self.settings.clone()),
            availability: (availability && self.availability_writable)
                .then(|| self.availability.clone()),
            locations: (locations && self.locations_writable).then(|| self.locations.clone()),
        }
    }
    pub fn save_histories(&self) -> Vec<&'static str> {
        let mut failures = Vec::new();
        if self.availability_writable
            && save_json(
                &self.root.join("availability-history.json"),
                &self.availability,
            )
            .is_err()
        {
            failures.push("availability-save-failed");
        }
        if self.locations_writable
            && save_json(&self.root.join("location-history.json"), &self.locations).is_err()
        {
            failures.push("location-save-failed");
        }
        failures
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "NetLightsRust-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        fs::create_dir_all(&p).unwrap();
        p
    }
    #[test]
    fn a_country_transition_at_the_import_limit_survives_two_saves_and_restart() {
        let root = fixture();
        let now = Utc::now();
        let history = LocationHistory {
            stays: (0..100_000)
                .map(|i| LocationStay {
                    iso: if i % 2 == 0 { "PL" } else { "DE" }.into(),
                    started_utc: now - chrono::Duration::seconds(100_000 - i),
                    ended_utc: Some(now - chrono::Duration::seconds(99_999 - i)),
                })
                .collect(),
            last_observed_utc: Some(now - chrono::Duration::seconds(1)),
            ..Default::default()
        };
        save_json(&root.join("location-history.json"), &history).unwrap();
        let mut profile = Profile::open(root.clone(), true).unwrap();
        profile.locations.observe(
            serde_json::from_value(serde_json::json!({
                "observedUtc":now,"countryCode":"JP","confirmed":true,
                "publicIp":"192.0.2.1","resolutionSource":"country.is"
            }))
            .unwrap(),
        );
        assert!(profile.save_histories().is_empty());
        assert!(profile.save_histories().is_empty());
        let reopened = Profile::open(root.clone(), true).unwrap();
        assert!(
            !reopened
                .warnings
                .iter()
                .any(|w| w.contains("заблокирована"))
        );
        assert_eq!(
            reopened.locations.stays.last().map(|s| s.iso.as_str()),
            Some("JP")
        );
        assert_eq!(reopened.locations.stays.len(), 100_000);
        assert_eq!(
            reopened.locations.capacity_truncated_through_utc,
            Some(now - chrono::Duration::seconds(99_999))
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn serialization_failure_cannot_replace_primary_backup_or_leave_a_temporary_file() {
        struct Broken;
        impl Serialize for Broken {
            fn serialize<S: serde::Serializer>(
                &self,
                serializer: S,
            ) -> std::result::Result<S::Ok, S::Error> {
                use serde::ser::SerializeSeq;
                let mut seq = serializer.serialize_seq(Some(2))?;
                seq.serialize_element(&"partial output")?;
                Err(serde::ser::Error::custom("Injected serialization failure"))
            }
        }
        let root = fixture();
        let path = root.join("settings.json");
        let first = Settings::default();
        save_json(&path, &first).unwrap();
        assert_eq!(
            fs::read(&path).unwrap(),
            serde_json::to_vec_pretty(&first).unwrap()
        );
        save_json(
            &path,
            &Settings {
                window_width: 1234,
                ..first
            },
        )
        .unwrap();
        let primary = fs::read(&path).unwrap();
        let backup = fs::read(path.with_extension("json.bak")).unwrap();
        assert!(save_json(&path, &Broken).is_err());
        assert_eq!(fs::read(&path).unwrap(), primary);
        assert_eq!(fs::read(path.with_extension("json.bak")).unwrap(), backup);
        assert!(save_json(&path, &"x".repeat(SETTINGS_FILE_LIMIT as usize)).is_err());
        assert_eq!(fs::read(&path).unwrap(), primary);
        assert_eq!(fs::read(path.with_extension("json.bak")).unwrap(), backup);
        assert_eq!(fs::read_dir(&root).unwrap().count(), 2);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn quick_profile_restart_preserves_the_same_country_gap_in_stays_and_source_observations() {
        let root = fixture();
        let origin = Utc::now() - chrono::Duration::seconds(30);
        let last_before_exit = origin + chrono::Duration::seconds(5);
        let resumed = origin + chrono::Duration::seconds(10);
        let next_check = origin + chrono::Duration::seconds(15);
        let path = root.join("location-history.json");
        let observation = serde_json::json!({
            "observedUtc":origin,"observedUntilUtc":last_before_exit,
            "countryCode":"DE","onlineCountry":"DE","localCountry":"DE",
            "iplocateCountry":"DE","confirmed":true,"publicIp":"192.0.2.1",
            "databaseVersion":"v2fly-v1","iplocateDatabaseVersion":"iplocate-v1",
            "resolutionSource":"country.is"
        });
        save_json(
            &path,
            &serde_json::json!({
                "version":3,"lastObservedUtc":last_before_exit,
                "stays":[{"iso":"DE","startedUtc":origin,"endedUtc":null}],
                "observations":[observation.clone()]
            }),
        )
        .unwrap();
        let mut profile = Profile::open(root.clone(), true).unwrap();
        assert_eq!(profile.locations.stays[0].ended_utc, Some(last_before_exit));
        assert_eq!(profile.locations.observations[0].until(), last_before_exit);

        for at in [resumed, next_check] {
            let mut current = observation.clone();
            current["observedUtc"] = serde_json::to_value(at).unwrap();
            current["observedUntilUtc"] = serde_json::Value::Null;
            profile
                .locations
                .observe(serde_json::from_value(current).unwrap());
        }
        profile.locations.close(next_check);
        assert!(profile.save_histories().is_empty());
        let persisted: serde_json::Value = read_json(&path, 1024 * 1024).unwrap();
        assert!(persisted.get("observationBoundary").is_none());
        let reopened = Profile::open(root.clone(), true).unwrap();
        let history = &reopened.locations;
        assert_eq!(history.stays.len(), 2);
        assert_eq!(history.stays[0].ended_utc, Some(last_before_exit));
        assert_eq!(history.stays[1].started_utc, resumed);
        assert_eq!(history.stays[1].ended_utc, Some(next_check));
        assert_eq!(history.observations.len(), 2);
        assert_eq!(history.observations[0].observed_utc, origin);
        assert_eq!(history.observations[0].until(), last_before_exit);
        assert_eq!(history.observations[1].observed_utc, resumed);
        assert_eq!(history.observations[1].until(), next_check);
        assert_eq!(
            history.observations[0].public_ip.as_deref(),
            Some("192.0.2.1")
        );
        assert_eq!(
            history.observations[1].public_ip.as_deref(),
            Some("192.0.2.1")
        );
        let buckets =
            crate::history::frequency(history, origin, origin + chrono::Duration::seconds(20), 20);
        assert!(buckets[0..5].iter().all(|bucket| bucket.observed));
        assert!(buckets[5..10].iter().all(|bucket| !bucket.observed));
        assert!(buckets[10..15].iter().all(|bucket| bucket.observed));
        assert_eq!(
            buckets.iter().map(|bucket| bucket.changes).sum::<usize>(),
            0
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn streaming_backup_validation_preserves_json_numeric_and_syntax_checks() {
        assert!(
            serde_json::from_str::<ValidJson>(
                r#"{"values":[true,false,null,-1,18446744073709551615,1.25e100,"text",{},[]]}"#
            )
            .is_ok()
        );
        for input in [
            r#"{"text":"escaped\\\" string","numbers":[0,-1,18446744073709551615,1.25e100],"values":[true,false,null,{},[]]}"#,
            "{broken}",
            "{} trailing",
            r#"{"number":1e400}"#,
            "[1,]",
            r#"{"unterminated":"#,
        ] {
            assert_eq!(
                serde_json::from_str::<ValidJson>(input).is_ok(),
                serde_json::from_str::<serde_json::Value>(input).is_ok(),
                "{input}"
            );
        }
        let root = fixture();
        let path = root.join("settings.json");
        save_json(&path, &Settings::default()).unwrap();
        save_json(&path, &Settings::default()).unwrap();
        let backup = fs::read(path.with_extension("json.bak")).unwrap();
        fs::write(&path, br#"{"number":1e400}"#).unwrap();
        assert!(save_json(&path, &Settings::default()).is_err());
        assert_eq!(fs::read(path.with_extension("json.bak")).unwrap(), backup);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn accepted_legacy_bom_file_can_be_saved_and_backed_up_without_losing_bytes() {
        let root = fixture();
        let path = root.join("settings.json");
        let mut original = vec![0xef, 0xbb, 0xbf];
        original.extend(serde_json::to_vec(&Settings::default()).unwrap());
        fs::write(&path, &original).unwrap();
        let mut profile = Profile::open(root.clone(), true).unwrap();
        profile.settings.window_width = 1234;
        profile.save_settings().unwrap();
        assert_eq!(
            read_json::<Settings>(&path, 1024 * 1024)
                .unwrap()
                .window_width,
            1234
        );
        assert_eq!(fs::read(path.with_extension("json.bak")).unwrap(), original);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn interrupted_temporary_files_cannot_block_a_reused_process_id() {
        let root = fixture();
        let path = root.join("settings.json");
        let stale = root.join(format!(".settings.json.{}.tmp", std::process::id()));
        fs::write(&stale, b"interrupted").unwrap();
        save_json(&path, &Settings::default()).unwrap();
        save_json(&path, &Settings::default()).unwrap();
        assert_eq!(fs::read(&stale).unwrap(), b"interrupted");
        assert!(path.with_extension("json.bak").is_file());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn saving_one_store_never_creates_or_replaces_another() {
        let root = fixture();
        let profile = Profile::open(root.clone(), true).unwrap();
        fs::write(root.join("location-history.json"), b"keep-the-original").unwrap();
        assert!(profile.snapshot(false, true, false).write().is_empty());
        assert!(root.join("availability-history.json").is_file());
        assert!(!root.join("settings.json").exists());
        assert_eq!(
            fs::read(root.join("location-history.json")).unwrap(),
            b"keep-the-original"
        );
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn corrupt_settings_are_not_replaced_on_exit() {
        let root = fixture();
        fs::write(root.join("settings.json"), b"broken").unwrap();
        let p = Profile::open(root.clone(), true).unwrap();
        assert!(p.save_settings().is_err());
        assert_eq!(fs::read(root.join("settings.json")).unwrap(), b"broken");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn backup_recovery_keeps_last_complete_settings() {
        let root = fixture();
        let path = root.join("settings.json");
        save_json(&path, &Settings::default()).unwrap();
        let s = Settings {
            window_width: 1234,
            ..Default::default()
        };
        save_json(&path, &s).unwrap();
        fs::write(&path, b"torn").unwrap();
        let loaded: Loaded<Settings> = load(&path, 1024 * 1024, |_| true);
        assert_eq!(loaded.status, LoadStatus::Recovered);
        assert_eq!(loaded.value.window_width, 1450);
        assert_eq!(fs::read(&path).unwrap(), b"torn");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn large_files_are_rejected_before_reading() {
        let root = fixture();
        let path = root.join("large.json");
        let f = File::create(&path).unwrap();
        f.set_len(1024 * 1024 + 1).unwrap();
        assert!(read_bounded(&path, 1024 * 1024).is_err());
        drop(f);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn empty_primary_cannot_hide_a_valid_nonempty_history_backup() {
        let root = fixture();
        let path = root.join("location-history.json");
        let mut history = LocationHistory::default();
        history.stays.push(LocationStay {
            iso: "DE".into(),
            started_utc: Utc::now(),
            ended_utc: None,
        });
        fs::write(
            path.with_extension("json.bak"),
            serde_json::to_vec(&history).unwrap(),
        )
        .unwrap();
        fs::write(&path, b"{}").unwrap();
        let loaded: Loaded<LocationHistory> = load(&path, 1024 * 1024, |_| true);
        assert_eq!(loaded.status, LoadStatus::Recovered);
        assert_eq!(loaded.value.stays[0].iso, "DE");
        assert!(serde_json::from_str::<AvailabilityHistory>("{}").is_err());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn even_a_short_restart_leaves_the_previous_session_gap_unknown() {
        let root = fixture();
        let observed = Utc::now() - chrono::Duration::seconds(10);
        let history = AvailabilityHistory {
            last_observed_utc: Some(observed),
            spans: vec![AvailabilitySpan {
                group: Group::Ru,
                state: Availability::Online,
                paused: false,
                started_utc: observed - chrono::Duration::hours(1),
                ended_utc: None,
            }],
            ..Default::default()
        };
        let locations = LocationHistory {
            last_observed_utc: Some(observed),
            stays: vec![LocationStay {
                iso: "DE".into(),
                started_utc: observed - chrono::Duration::hours(1),
                ended_utc: None,
            }],
            ..Default::default()
        };
        save_json(&root.join("availability-history.json"), &history).unwrap();
        save_json(&root.join("location-history.json"), &locations).unwrap();
        let loaded = Profile::open(root.clone(), true).unwrap();
        assert_eq!(loaded.availability.spans[0].ended_utc, Some(observed));
        assert_eq!(loaded.locations.stays[0].ended_utc, Some(observed));
        fs::remove_dir_all(root).unwrap();
    }
}
