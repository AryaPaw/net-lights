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

/// Preserve only a source-file basename and numeric position, never a panic
/// payload (which can contain addresses, URLs or user data).
fn panic_location(file: &str, line: u32, column: u32) -> String {
    let file = file.rsplit(['/', '\\']).next().unwrap_or("unknown");
    let basename: String = file
        .chars()
        .take(32)
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect();
    format!("panic-at-{basename}-{line}-{column}")
}
#[cfg(not(test))]
pub(crate) fn install_panic_hook(journal: Journal) {
    let previous = std::panic::take_hook();
    let writing = Mutex::new(());
    std::panic::set_hook(Box::new(move |info| {
        // A simultaneous or recursive panic mustn't wait for a logger lock.
        if let Ok(_guard) = writing.try_lock() {
            journal.panic_event("native-panic");
            if let Some(location) = info.location() {
                journal.panic_event(&panic_location(
                    location.file(),
                    location.line(),
                    location.column(),
                ));
            }
            let message = info
                .payload()
                .downcast_ref::<String>()
                .map(String::as_str)
                .or_else(|| info.payload().downcast_ref::<&str>().copied())
                .unwrap_or("");
            if message.contains("already borrowed") || message.contains("BorrowMutError") {
                journal.panic_event("panic-reentrant-borrow");
            }
        }
        previous(info);
    }));
}
#[derive(Clone)]
pub(crate) struct Journal {
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
        self.write_event(code);
    }
    fn panic_event(&self, code: &str) {
        if let Ok(_guard) = self.gate.try_lock() {
            self.write_event(code);
        }
    }
    fn write_event(&self, code: &str) {
        if code.len() > 80 || !code.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-') {
            return;
        }
        let _ = (|| -> Result<()> {
            let parent = self
                .path
                .parent()
                .ok_or_else(|| anyhow::anyhow!("Missing log directory"))?;
            fs::create_dir_all(parent)?;
            if fs::metadata(&self.path).is_ok_and(|m| m.len() >= 1024 * 1024) {
                let previous = self.path.with_extension("previous.log");
                match fs::symlink_metadata(&previous) {
                    Ok(metadata) => {
                        use std::os::windows::fs::MetadataExt;
                        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
                            return Ok(());
                        }
                        fs::remove_file(&previous)?;
                    }
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                    Err(error) => return Err(error.into()),
                }
                fs::rename(&self.path, previous)?;
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
        .and_then(|name| {
            name.strip_suffix(".previous.log")
                .or_else(|| name.strip_suffix(".log"))
        })
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
    fn a_full_journal_rotates_and_continues_recording_recovery() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsJournalFull-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let journal = Journal::new(&root);
        journal.event("runtime-start");
        let file = OpenOptions::new().write(true).open(&journal.path).unwrap();
        file.set_len(1024 * 1024).unwrap();
        drop(file);
        journal.event("geo-primary-request-ok-http-200");
        let rotated = journal.path.with_extension("previous.log");
        assert_eq!(fs::metadata(rotated).unwrap().len(), 1024 * 1024);
        let current = fs::read_to_string(&journal.path).unwrap();
        assert!(current.contains("geo-primary-request-ok-http-200"));
        assert!(current.len() < 1024);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn panic_location_excludes_private_paths_and_bounds_untrusted_names() {
        assert_eq!(
            panic_location(r"C:\private\token-secret\app.rs", 12, 4),
            "panic-at-app-rs-12-4"
        );
        let code = panic_location(
            &format!("/private/{}", "я?secret".repeat(100)),
            u32::MAX,
            u32::MAX,
        );
        assert!(code.len() <= 80);
        assert!(code.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-'));
        assert!(!code.contains("private"));
    }

    #[test]
    fn panic_logging_never_waits_for_the_regular_writer_and_clones_serialize_lines() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsJournalConcurrency-{}-{}",
            std::process::id(),
            Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let journal = Journal::new(&root);
        let held = journal.gate.lock().unwrap();
        let copy = journal.clone();
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let worker = std::thread::spawn(move || {
            copy.panic_event("panic-at-test-1-1");
            sender.send(()).unwrap();
        });
        let completed = receiver
            .recv_timeout(std::time::Duration::from_secs(2))
            .is_ok();
        drop(held);
        worker.join().unwrap();
        assert!(completed, "Panic hook waited for the ordinary log writer");
        let workers: Vec<_> = (0..4)
            .map(|n| {
                let copy = journal.clone();
                std::thread::spawn(move || {
                    for i in 0..12 {
                        copy.event(&format!("shell-{n}-{i}"));
                    }
                })
            })
            .collect();
        for worker in workers {
            worker.join().unwrap();
        }
        let text = fs::read_to_string(&journal.path).unwrap();
        assert_eq!(text.lines().count(), 48);
        for line in text.lines() {
            let (time, code) = line.split_once(' ').unwrap();
            assert!(chrono::DateTime::parse_from_rfc3339(time).is_ok());
            assert!(code.starts_with("shell-") && code.split('-').count() == 3);
        }
        fs::remove_dir_all(root).unwrap();
    }

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
