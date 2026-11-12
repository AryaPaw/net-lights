use anyhow::Result;
use chrono::Utc;
use std::{
    collections::VecDeque,
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

type JournalEntries = Arc<Mutex<VecDeque<(chrono::DateTime<Utc>, String)>>>;
#[derive(Clone)]
pub(super) struct Journal {
    pub(super) path: PathBuf,
    gate: Arc<Mutex<()>>,
    pub(super) events: JournalEntries,
}
impl Journal {
    pub fn new(root: &Path) -> Self {
        Self {
            path: root.join("logs").join(format!(
                "native-{}-{}.log",
                Utc::now().format("%Y%m%d"),
                std::process::id()
            )),
            gate: Arc::new(Mutex::new(())),
            events: Arc::new(Mutex::new(VecDeque::new())),
        }
    }
    pub fn event(&self, code: &str) {
        if code.len() > 80 || !code.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-') {
            return;
        }
        {
            let mut events = self.events.lock().unwrap_or_else(|e| e.into_inner());
            events.push_back((Utc::now(), code.into()));
            if events.len() > 512 {
                events.pop_front();
            }
        }
        let _guard = self.gate.lock().unwrap_or_else(|e| e.into_inner());
        let _ = (|| -> Result<()> {
            let parent = self
                .path
                .parent()
                .ok_or_else(|| anyhow::anyhow!("Missing log directory"))?;
            fs::create_dir_all(parent)?;
            if fs::metadata(&self.path).is_ok_and(|m| m.len() >= 1024 * 1024) {
                return Ok(());
            }
            let mut output = OpenOptions::new()
                .create(true)
                .append(true)
                .open(&self.path)?;
            writeln!(output, "{} {code}", Utc::now().to_rfc3339())?;
            let mut old: Vec<_> = fs::read_dir(parent)?
                .filter_map(Result::ok)
                .filter(|e| {
                    owned_log_name(&e.file_name())
                        && e.file_type().is_ok_and(|kind| kind.is_file())
                        && e.path() != self.path
                })
                .collect();
            old.sort_by_cached_key(|entry| {
                (
                    entry
                        .metadata()
                        .and_then(|metadata| metadata.modified())
                        .unwrap_or(std::time::SystemTime::UNIX_EPOCH),
                    entry.file_name(),
                )
            });
            while old.len() > 7 {
                let path = old.remove(0).path();
                let _ = fs::remove_file(path);
            }
            Ok(())
        })();
    }
}

fn owned_log_name(name: &std::ffi::OsStr) -> bool {
    let Some(value) = name
        .to_str()
        .and_then(|name| name.strip_prefix("native-"))
        .and_then(|name| name.strip_suffix(".log"))
    else {
        return false;
    };
    let Some((date, pid)) = value.split_once('-') else {
        return false;
    };
    date.len() == 8
        && date.bytes().all(|byte| byte.is_ascii_digit())
        && chrono::NaiveDate::parse_from_str(date, "%Y%m%d").is_ok()
        && pid
            .parse::<u32>()
            .is_ok_and(|value| value > 0 && value.to_string() == pid)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn journal_rotation_preserves_foreign_files_and_keeps_only_eight_owned_logs() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsJournalOwnership-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let logs = root.join("logs");
        fs::create_dir_all(&logs).unwrap();
        let foreign = [
            "native-000-notes.txt",
            "native-support.zip",
            "native-20200101-99.log.bak",
        ];
        for name in foreign {
            fs::write(logs.join(name), b"preserved user bytes").unwrap();
        }
        let foreign_directory = logs.join("native-20200101-999.log");
        fs::create_dir(&foreign_directory).unwrap();
        for index in 1..=8 {
            fs::write(
                logs.join(format!("native-20200101-{index}.log")),
                b"owned old log",
            )
            .unwrap();
        }
        let journal = Journal::new(&root);
        journal.event("runtime-start");
        let retained = foreign.iter().all(|name| {
            fs::read(logs.join(name)).is_ok_and(|data| data == b"preserved user bytes")
        }) && foreign_directory.is_dir();
        let owned = fs::read_dir(&logs)
            .unwrap()
            .flatten()
            .filter(|entry| {
                owned_log_name(&entry.file_name())
                    && entry.file_type().is_ok_and(|kind| kind.is_file())
            })
            .count();
        let active = journal.path.is_file();
        fs::remove_dir_all(root).unwrap();
        assert!(retained);
        assert!(active);
        assert_eq!(owned, 8);
        for invalid in [
            "native-20260230-123.log",
            "native-20261112-0.log",
            "native-20261112-001.log",
            "native-20261112--1.log",
            "native-20261112-1.log.bak",
        ] {
            assert!(!owned_log_name(invalid.as_ref()));
        }
    }

    #[test]
    fn journal_rotation_retains_the_newest_crash_log_when_pid_order_differs() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsJournalTime-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let logs = root.join("logs");
        fs::create_dir_all(&logs).unwrap();
        for pid in 1..=9 {
            let path = logs.join(format!("native-20200101-{pid}.log"));
            fs::write(&path, b"owned log").unwrap();
            let age = if pid == 1 { 100 } else { pid };
            fs::OpenOptions::new()
                .write(true)
                .open(path)
                .unwrap()
                .set_times(fs::FileTimes::new().set_modified(
                    std::time::SystemTime::UNIX_EPOCH
                        + std::time::Duration::from_secs(1_000_000 + age),
                ))
                .unwrap();
        }
        Journal::new(&root).event("runtime-start");
        let newest_kept = logs.join("native-20200101-1.log").is_file();
        let oldest_removed = !logs.join("native-20200101-2.log").exists()
            && !logs.join("native-20200101-3.log").exists();
        fs::remove_dir_all(root).unwrap();
        assert!(newest_kept);
        assert!(oldest_removed);
    }
}
