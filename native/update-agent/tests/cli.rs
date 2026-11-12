#![cfg(windows)]
use netlights_core::update::{legacy_invariant_upper, unique_name};
use sha2::{Digest, Sha256};
use std::{
    fs,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::Command,
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::CloseHandle,
        System::Threading::{CreateMutexW, ReleaseMutex, WaitForSingleObject},
    },
    core::PCWSTR,
};

struct Fixture {
    root: PathBuf,
    app: PathBuf,
    agent: PathBuf,
    updates: PathBuf,
    marker: PathBuf,
    target: String,
    identity: String,
}
impl Fixture {
    fn new() -> Self {
        Self::new_at("install")
    }
    fn new_at(name: &str) -> Self {
        let root = std::env::temp_dir().join(format!("NetLightsRustAgentCLI-{}", unique_name()));
        let installation = root.join(name);
        fs::create_dir_all(&installation).unwrap();
        let app = installation.join("NetLights.exe");
        fs::write(&app, b"fixture application").unwrap();
        fs::write(installation.join("unins000.exe"), b"fixture uninstaller").unwrap();
        let agent = installation.join("NetLights.UpdateAgent.exe");
        fs::copy(env!("CARGO_BIN_EXE_netlights-update-agent"), &agent).unwrap();
        let updates = root.join("profile/NetLights/updates");
        fs::create_dir_all(&updates).unwrap();
        let target = legacy_invariant_upper(fs::canonicalize(&app).unwrap().to_str().unwrap());
        let identity = format!("{:X}", Sha256::digest(target.as_bytes()));
        let marker = updates.join(format!("active-{identity}.json"));
        Self {
            root,
            app,
            agent,
            updates,
            marker,
            target,
            identity,
        }
    }
    fn valid(&self) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "Version":2,
            "InstalledExecutable":fs::canonicalize(&self.app).unwrap().to_str().unwrap()
        }))
        .unwrap()
    }
    fn legacy(&self) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({"Version":1,"InstalledExecutable":self.target}))
            .unwrap()
    }
    fn run(&self, binary: &Path, args: &[&str]) -> i32 {
        let mut command = Command::new(binary);
        command
            .args(args)
            .env("LOCALAPPDATA", self.root.join("profile"))
            .creation_flags(0x08000000);
        let mut process = command.spawn().unwrap();
        let clock = Instant::now();
        loop {
            if let Some(code) = process.try_wait().unwrap() {
                return code.code().unwrap();
            }
            if clock.elapsed() > Duration::from_secs(10) {
                let _ = process.kill();
                let _ = process.wait();
                panic!("Scoped agent CLI exceeded ten seconds");
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }
    fn complete(&self) -> i32 {
        self.run(
            &self.agent,
            &[
                "--complete-manual-install",
                "--restart",
                self.app.to_str().unwrap(),
            ],
        )
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.root);
    }
}

#[test]
fn manual_repair_cannot_claim_a_legacy_marker_from_a_distinct_unicode_installation() {
    let fixture = Fixture::new_at("ſ");
    let other = fixture.root.join("S");
    fs::create_dir(&other).unwrap();
    let other_app = other.join("NetLights.exe");
    let other_agent = other.join("NetLights.UpdateAgent.exe");
    fs::write(&other_app, b"distinct application").unwrap();
    fs::write(other.join("unins000.exe"), b"uninstaller").unwrap();
    fs::copy(&fixture.agent, &other_agent).unwrap();
    assert_eq!(fs::read(&fixture.app).unwrap(), b"fixture application");
    let other_raw = fs::canonicalize(&other_app).unwrap();
    assert_ne!(fs::canonicalize(&fixture.app).unwrap(), other_raw);
    assert_eq!(
        fixture.target,
        legacy_invariant_upper(other_raw.to_str().unwrap())
    );
    for original in [fixture.legacy(), fixture.valid()] {
        fs::write(&fixture.marker, &original).unwrap();
        assert_eq!(
            fixture.run(
                &other_agent,
                &[
                    "--complete-manual-install",
                    "--restart",
                    other_app.to_str().unwrap(),
                ]
            ),
            11,
            "Lossy legacy uppercase isn't proof that this marker belongs to the current installation"
        );
        assert_eq!(fs::read(&fixture.marker).unwrap(), original);
    }
}

#[test]
fn published_agent_rejects_missing_arguments_without_creating_update_state() {
    let fixture = Fixture::new();
    fs::remove_dir_all(&fixture.updates).unwrap();
    assert_eq!(fixture.run(&fixture.agent, &[]), 2);
    assert!(!fixture.updates.exists());
}
#[test]
fn manual_repair_removes_only_a_proven_own_v2_marker() {
    let fixture = Fixture::new();
    fs::write(&fixture.marker, fixture.valid()).unwrap();
    fs::write(fixture.updates.join("result.json"), b"preserve").unwrap();
    assert_eq!(fixture.complete(), 0);
    assert!(!fixture.marker.exists());
    assert_eq!(
        fs::read(fixture.updates.join("result.json")).unwrap(),
        b"preserve"
    );
}
#[test]
fn manual_repair_preserves_foreign_unsupported_oversize_and_deep_markers() {
    for marker in [
        Vec::new(),
        b"{torn".to_vec(),
        b"null".to_vec(),
        serde_json::to_vec(&serde_json::json!({"Version":1,"InstalledExecutable":"FOREIGN"}))
            .unwrap(),
        vec![b' '; 4097],
        format!("{}0{}", "[".repeat(150), "]".repeat(150)).into_bytes(),
    ] {
        let fixture = Fixture::new();
        fs::write(&fixture.marker, &marker).unwrap();
        assert_eq!(fixture.complete(), 11);
        assert_eq!(fs::read(&fixture.marker).unwrap(), marker);
    }
    let fixture = Fixture::new();
    let legacy = fixture.legacy();
    fs::write(&fixture.marker, &legacy).unwrap();
    assert_eq!(fixture.complete(), 11);
    assert_eq!(fs::read(&fixture.marker).unwrap(), legacy);
}
#[test]
fn manual_repair_cannot_break_an_active_transaction_or_run_from_another_installation() {
    let fixture = Fixture::new();
    fs::write(&fixture.marker, fixture.valid()).unwrap();
    let name = format!("Global\\NetLights.UpdateInstallation.{}", fixture.identity)
        .encode_utf16()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let mutex = unsafe { CreateMutexW(None, false, PCWSTR(name.as_ptr())).unwrap() };
    assert_eq!(unsafe { WaitForSingleObject(mutex, 0) }.0, 0);
    assert_eq!(fixture.complete(), 10);
    assert_eq!(fs::read(&fixture.marker).unwrap(), fixture.valid());
    let _ = unsafe { ReleaseMutex(mutex) };
    let _ = unsafe { CloseHandle(mutex) };
    assert_eq!(
        fixture.run(
            Path::new(env!("CARGO_BIN_EXE_netlights-update-agent")),
            &[
                "--complete-manual-install",
                "--restart",
                fixture.app.to_str().unwrap()
            ]
        ),
        2
    );
    assert_eq!(fs::read(&fixture.marker).unwrap(), fixture.valid());
}
