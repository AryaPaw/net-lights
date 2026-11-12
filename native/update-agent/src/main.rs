#![cfg_attr(not(test), windows_subsystem = "windows")]
#[cfg(windows)]
mod marker;
#[cfg(windows)]
mod metadata;
#[cfg(all(test, windows))]
mod process_tests;
#[cfg(windows)]
mod snapshot;
#[cfg(windows)]
mod win;

#[cfg(windows)]
use anyhow::{Context, Result, ensure};
#[cfg(windows)]
use netlights_core::update::*;
#[cfg(windows)]
use std::{
    collections::BTreeMap,
    fs::{self, OpenOptions},
    io::{Read, Write},
    os::windows::fs::OpenOptionsExt,
    path::{Path, PathBuf},
    time::Duration,
};

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    {
        std::process::ExitCode::from(run(std::env::args().skip(1).collect()) as u8)
    }
    #[cfg(not(windows))]
    {
        std::process::ExitCode::from(2)
    }
}
#[cfg(windows)]
fn parse(args: &[String]) -> Result<BTreeMap<String, String>> {
    ensure!(
        args.len().is_multiple_of(2),
        "Each updater argument needs a value"
    );
    let mut map = BTreeMap::new();
    for pair in args.as_chunks::<2>().0 {
        let name = pair[0]
            .strip_prefix("--")
            .context("Invalid updater argument")?
            .to_ascii_lowercase();
        ensure!(
            ["restart", "parent", "pending", "sha256", "ready"].contains(&name.as_str()),
            "Unknown updater argument"
        );
        ensure!(
            map.insert(name, pair[1].clone()).is_none(),
            "Duplicate updater argument"
        );
    }
    Ok(map)
}
#[cfg(windows)]
fn run(args: Vec<String>) -> u32 {
    let com = unsafe {
        windows::Win32::System::Com::CoInitializeEx(
            None,
            windows::Win32::System::Com::COINIT_APARTMENTTHREADED,
        )
    };
    if com.is_err() {
        return 1;
    }
    let result = run_initialized(args);
    unsafe { windows::Win32::System::Com::CoUninitialize() };
    result
}
#[cfg(windows)]
fn run_initialized(args: Vec<String>) -> u32 {
    let root = match std::env::var_os("LOCALAPPDATA") {
        Some(root) => PathBuf::from(root).join("NetLights/updates"),
        None => return 2,
    };
    if args
        .first()
        .is_some_and(|arg| arg == "--complete-manual-install")
    {
        return complete_manual(&args[1..], &root).unwrap_or(11);
    }
    let parsed = match Arguments::parse(&args) {
        Ok(value) => value,
        Err(_) => return 2,
    };
    execute(parsed, &root).unwrap_or(11)
}
#[cfg(windows)]
struct Arguments {
    restart: PathBuf,
    parent: i32,
    installer: PathBuf,
    sha256: String,
    ready: Option<String>,
    timings: Timings,
}
#[cfg(windows)]
#[derive(Clone, Copy)]
struct Timings {
    parent: Duration,
    installer: Duration,
    health: Duration,
    liveness: Duration,
}
#[cfg(windows)]
impl Default for Timings {
    fn default() -> Self {
        Self {
            parent: Duration::from_secs(60),
            installer: Duration::from_secs(600),
            health: Duration::from_secs(20),
            liveness: Duration::from_secs(3),
        }
    }
}
#[cfg(windows)]
impl Arguments {
    fn parse(args: &[String]) -> Result<Self> {
        let map = parse(args)?;
        let restart = PathBuf::from(map.get("restart").context("Restart target is missing")?);
        ensure!(
            restart.is_absolute()
                && restart.is_file()
                && restart
                    .file_name()
                    .is_some_and(|name| name.eq_ignore_ascii_case("NetLights.exe")),
            "Invalid restart target"
        );
        ensure_unlinked(&restart)?;
        let installer = PathBuf::from(map.get("pending").context("Pending installer is missing")?);
        let sha256 = map
            .get("sha256")
            .context("Installer digest is missing")?
            .to_ascii_lowercase();
        ensure!(valid_hash(&sha256), "Invalid installer digest");
        let parent = map
            .get("parent")
            .context("Parent identity is missing")?
            .parse()?;
        let ready = map.get("ready").cloned();
        if let Some(name) = &ready {
            ensure!(
                win::signal_name_valid(name, "Local\\NetLights.UpdateAgentReady."),
                "Invalid handoff signal name"
            );
        }
        Ok(Self {
            restart,
            parent,
            installer,
            sha256,
            ready,
            timings: Timings::default(),
        })
    }
    fn candidate(&self) -> Option<String> {
        self.installer
            .file_name()
            .and_then(|s| s.to_str())
            .filter(|name| safe_filename(name))
            .map(|name| name[ASSET_PREFIX.len()..name.len() - 4].to_owned())
    }
}
#[cfg(windows)]
struct Transaction {
    root: PathBuf,
    marker: PathBuf,
    _guard: win::Guard,
    backup: Option<snapshot::Snapshot>,
    installer: Option<win::JobProcess>,
    installer_started: bool,
    installer_stopped: bool,
    committed: bool,
    parent_stopped: bool,
    may_remove_marker: bool,
}
#[cfg(windows)]
impl Drop for Transaction {
    fn drop(&mut self) {
        if self.may_remove_marker {
            let _ = fs::remove_file(&self.marker);
        }
        self.installer.take();
        self.backup.take();
    }
}
#[cfg(windows)]
fn execute(arguments: Arguments, root: &Path) -> Result<u32> {
    let (_, identity) = win::identity(&arguments.restart)?;
    let guard = match win::Guard::acquire(&identity)? {
        win::Acquisition::Owned(guard) => guard,
        win::Acquisition::Busy => return Ok(10),
        win::Acquisition::Interrupted => return Ok(11),
    };
    ensure_unlinked(root)?;
    fs::create_dir_all(root)?;
    let marker = root.join(format!("active-{identity}.json"));
    ensure_unlinked(&marker)?;
    let marker_bytes = marker::create(&arguments.restart)?;
    {
        let mut output = match OpenOptions::new()
            .write(true)
            .create_new(true)
            .share_mode(1)
            .open(&marker)
        {
            Ok(file) => file,
            Err(_) => return Ok(11),
        };
        output.write_all(&marker_bytes)?;
        output.sync_all()?;
    }
    let mut tx = Transaction {
        root: root.into(),
        marker,
        _guard: guard,
        backup: None,
        installer: None,
        installer_started: false,
        installer_stopped: true,
        committed: false,
        parent_stopped: false,
        may_remove_marker: true,
    };
    let outcome = install(&mut tx, &arguments);
    match outcome {
        Ok(0) => {
            if let Some(backup) = &mut tx.backup {
                backup.commit();
            }
            let _ = write_result(
                root,
                &arguments.candidate().unwrap_or_else(|| "unknown".into()),
                true,
                "Установлено, мониторинг запущен.",
            );
            if let Err(error) = fs::remove_file(root.join("pending.json"))
                && error.kind() != std::io::ErrorKind::NotFound
            {
                phase(root, "pending-cleanup-failed");
            }
            cleanup_installer(&arguments.installer, root);
            Ok(0)
        }
        Ok(code) => {
            if tx.parent_stopped {
                recover(&mut tx, &arguments);
            } else {
                let _ = write_result(
                    root,
                    "unknown",
                    false,
                    "Обновление не началось. Установленная программа не заменена.",
                );
            }
            if tx.parent_stopped {
                cleanup_installer(&arguments.installer, root);
            }
            Ok(code)
        }
        Err(_) => {
            phase(root, "agent-transaction-failed");
            if !tx.committed {
                recover(&mut tx, &arguments);
            } else {
                phase(root, "post-success-cleanup-failed");
            }
            if tx.parent_stopped {
                cleanup_installer(&arguments.installer, root);
            }
            Ok(if tx.committed { 0 } else { 1 })
        }
    }
}
#[cfg(windows)]
fn install(tx: &mut Transaction, args: &Arguments) -> Result<u32> {
    phase(&tx.root, "agent-start");
    let root = fs::canonicalize(&tx.root)?;
    if !args.installer.is_file() {
        return Ok(3);
    }
    ensure_unlinked(&args.installer)?;
    let installer = fs::canonicalize(&args.installer)?;
    if !installer.starts_with(&root)
        || !installer
            .file_name()
            .is_some_and(|n| safe_filename(&n.to_string_lossy()))
    {
        return Ok(4);
    }
    let size = fs::metadata(&installer)?.len();
    if size == 0 || size > MAX_INSTALLER {
        return Ok(9);
    }
    if let Some(name) = &args.ready {
        win::Signal::open_and_set(name)?;
    }
    if !win::wait_parent(args.parent, args.timings.parent)? {
        phase(&tx.root, "parent-timeout");
        return Ok(6);
    }
    tx.parent_stopped = true;
    let mut read_lock = OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(&installer)?;
    let digest = locked_installer_hash(&mut read_lock)?;
    if !digest.eq_ignore_ascii_case(&args.sha256) {
        phase(&tx.root, "installer-hash-failed");
        return Ok(5);
    }
    tx.backup = Some(snapshot::Snapshot::create(&args.restart, &tx.root)?);
    phase(&tx.root, "installed-files-backed-up");
    let mut command = [
        "/VERYSILENT",
        "/NORESTART",
        "/SUPPRESSMSGBOXES",
        "/NETLIGHTSUPDATE",
    ]
    .map(String::from)
    .to_vec();
    command.push(format!(
        "/DIR={}",
        args.restart
            .parent()
            .context("Installation directory is missing")?
            .display()
    ));
    tx.installer = Some(win::JobProcess::spawn(
        &installer,
        &command,
        installer.parent().unwrap(),
    )?);
    tx.installer_started = true;
    tx.installer_stopped = false;
    tx.may_remove_marker = false;
    phase(&tx.root, "installer-started");
    let process = tx.installer.as_mut().unwrap();
    let exit = process.wait(args.timings.installer)?;
    process.stop_tree()?;
    tx.installer_stopped = true;
    tx.installer.take();
    drop(read_lock);
    let Some(exit) = exit else {
        phase(&tx.root, "installer-timeout");
        return Ok(7);
    };
    if exit != 0 {
        phase(&tx.root, "installer-failed");
        return Ok(exit.clamp(1, 255));
    }
    let candidate = args
        .candidate()
        .and_then(|value| version(&value))
        .context("Installer version is invalid")?;
    if win::product_version(&args.restart)? != candidate {
        phase(&tx.root, "version-not-confirmed");
        return Ok(8);
    }
    phase(&tx.root, "version-confirmed");
    if !win::restart(
        &args.restart,
        true,
        false,
        args.timings.health,
        args.timings.liveness,
    )? {
        phase(&tx.root, "process-restart-failed");
        return Ok(8);
    }
    tx.committed = true;
    tx.may_remove_marker = true;
    phase(&tx.root, "process-restarted");
    Ok(0)
}
#[cfg(windows)]
fn locked_installer_hash(file: &mut fs::File) -> Result<String> {
    use sha2::Digest;
    // The payload can change while the agent waits for its parent. Verify the
    // locked handle again and cap reads rather than trusting the earlier path stat.
    let size = file.metadata()?.len();
    ensure!(
        size > 0 && size <= MAX_INSTALLER,
        "Locked installer exceeds its size limit"
    );
    let mut input = file.take(MAX_INSTALLER + 1);
    let mut hash = sha2::Sha256::new();
    let mut buffer = [0u8; 32768];
    let mut total = 0u64;
    loop {
        let read = input.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        total += read as u64;
        ensure!(
            total <= MAX_INSTALLER,
            "Installer grew beyond its size limit"
        );
        hash.update(&buffer[..read]);
    }
    ensure!(
        total == size,
        "Installer changed while its checksum was read"
    );
    Ok(format!("{:x}", hash.finalize()))
}
#[cfg(windows)]
fn recover(tx: &mut Transaction, args: &Arguments) {
    if let Some(installer) = &mut tx.installer {
        tx.installer_stopped = installer.stop_tree().is_ok();
        if tx.installer_stopped {
            tx.installer.take();
        }
    }
    let mut restored = tx.backup.is_none() && !tx.installer_started;
    let mut files_restored = restored;
    if let Some(backup) = &mut tx.backup {
        if tx.installer_stopped {
            restored = win::stop_installation_processes(&args.restart)
                .and_then(|_| backup.restore())
                .is_ok();
            files_restored = backup.files_restored;
        } else {
            backup.retain();
        }
    }
    tx.may_remove_marker = tx.installer_stopped && (restored || files_restored);
    phase(
        &tx.root,
        if restored {
            "rollback-complete"
        } else {
            "rollback-failed"
        },
    );
    let guarded = args
        .candidate()
        .is_some_and(|candidate| record_failure(&tx.root, &candidate, &args.sha256).is_ok());
    if !guarded {
        phase(&tx.root, "retry-guard-write-failed");
    }
    let restoration = if tx.backup.is_none() {
        "Установка не изменялась."
    } else if restored {
        "Предыдущая установка восстановлена."
    } else if files_restored {
        "Файлы восстановлены, но регистрация установки или ярлык восстановлены не полностью."
    } else {
        "Восстановление предыдущей установки не подтверждено."
    };
    let _ = write_result(
        &tx.root,
        &args.candidate().unwrap_or_else(|| "unknown".into()),
        false,
        &format!("Обновление не установлено. {restoration}"),
    );
    if tx.parent_stopped && files_restored {
        let restarted = win::restart(
            &args.restart,
            false,
            !guarded,
            args.timings.health,
            args.timings.liveness,
        )
        .unwrap_or(false);
        phase(
            &tx.root,
            if restarted {
                "previous-process-restarted"
            } else {
                "previous-process-restart-failed"
            },
        );
    }
}
#[cfg(windows)]
fn cleanup_installer(installer: &Path, root: &Path) {
    let result = (|| -> Result<()> {
        ensure_unlinked(installer)?;
        let parent = installer.parent().context("Installer parent is missing")?;
        let canonical_root = fs::canonicalize(root)?;
        let canonical_parent = fs::canonicalize(parent)?;
        ensure!(
            canonical_parent.starts_with(canonical_root)
                && installer
                    .file_name()
                    .is_some_and(|n| safe_filename(&n.to_string_lossy())),
            "Installer cleanup isn't owned"
        );
        if installer.is_file() {
            fs::remove_file(installer)?;
        }
        if parent != root {
            let _ = fs::remove_dir(parent);
        }
        Ok(())
    })();
    let _ = result;
}
#[cfg(windows)]
fn complete_manual(args: &[String], root: &Path) -> Result<u32> {
    let parsed = match parse(args) {
        Ok(parsed) if parsed.len() == 1 => parsed,
        _ => return Ok(2),
    };
    let Some(target) = parsed.get("restart") else {
        return Ok(2);
    };
    let executable = PathBuf::from(target);
    if !executable.is_file()
        || !executable
            .file_name()
            .is_some_and(|s| s.eq_ignore_ascii_case("NetLights.exe"))
    {
        return Ok(2);
    }
    let directory = executable
        .parent()
        .context("Manual target has no directory")?;
    for path in [
        directory.to_path_buf(),
        executable.clone(),
        directory.join("unins000.exe"),
        directory.join("NetLights.UpdateAgent.exe"),
        root.into(),
    ] {
        ensure_unlinked(&path)?;
    }
    if !directory.join("unins000.exe").is_file() {
        return Ok(2);
    }
    if win::canonical(&std::env::current_exe()?)?
        != win::canonical(&directory.join("NetLights.UpdateAgent.exe"))?
    {
        return Ok(2);
    }
    let (_, identity) = win::identity(&executable)?;
    let _guard = match win::Guard::acquire(&identity)? {
        win::Acquisition::Owned(guard) => guard,
        win::Acquisition::Busy => return Ok(10),
        win::Acquisition::Interrupted => return Ok(11),
    };
    let marker = root.join(format!("active-{identity}.json"));
    ensure_unlinked(&marker)?;
    if !marker.exists() {
        return Ok(0);
    }
    let bytes = bounded_file(&marker, 4096)?;
    marker::require_owner(&bytes, &executable)?;
    ensure_unlinked(root)?;
    ensure_unlinked(&marker)?;
    fs::remove_file(marker)?;
    Ok(0)
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    #[test]
    fn missing_or_duplicate_arguments_are_rejected_before_any_updates_io() {
        assert!(Arguments::parse(&[]).is_err());
        assert!(
            parse(&[
                "--restart".into(),
                "a".into(),
                "--restart".into(),
                "b".into()
            ])
            .is_err()
        );
        assert!(parse(&["--unknown".into(), "value".into()]).is_err());
    }
    #[test]
    fn checksum_rechecks_size_on_the_locked_payload_after_parent_wait() {
        use std::io::Seek;
        let path = std::env::temp_dir().join(format!("NetLightsLockedInstaller-{}", unique_name()));
        fs::write(&path, b"small staged payload").unwrap();
        assert!(fs::metadata(&path).unwrap().len() <= MAX_INSTALLER);
        // Simulate replacement or growth after the first preflight stat.
        {
            let file = OpenOptions::new().write(true).open(&path).unwrap();
            file.set_len(MAX_INSTALLER + 1).unwrap();
        }
        let mut locked = OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(&path)
            .unwrap();
        assert!(locked_installer_hash(&mut locked).is_err());
        assert_eq!(locked.stream_position().unwrap(), 0);
        drop(locked);
        fs::remove_file(&path).unwrap();
    }
}
