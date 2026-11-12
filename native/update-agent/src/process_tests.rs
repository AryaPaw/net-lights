//! These run real disposable Rust processes, not the user's installation or profile.
use super::*;
use windows::Win32::System::Com::*;

struct Fixture {
    root: PathBuf,
    application: PathBuf,
    installer: PathBuf,
    updates: PathBuf,
    old: Vec<u8>,
    other: Option<win::JobProcess>,
}
impl Fixture {
    fn new(payload: &Path, mode: &str, app_mode: &str, expected_version: &str) -> Self {
        let root = std::env::temp_dir().join(format!("NetLightsRustAgent-{}", unique_name()));
        let directory = root.join("installation");
        let updates = root.join("updates");
        let staging = updates.join(unique_name());
        fs::create_dir_all(&directory).unwrap();
        fs::create_dir_all(&staging).unwrap();
        let application = directory.join("NetLights.exe");
        fs::copy(payload, &application).unwrap();
        fs::write(directory.join("unins000.exe"), b"fixture uninstaller").unwrap();
        fs::write(directory.join("fixture-app.txt"), b"healthy").unwrap();
        let old = fs::read(&application).unwrap();
        let replacement = root.join("new-payload.exe");
        let mut new = old.clone();
        new.extend_from_slice(b"NEW-OVERLAY");
        fs::write(&replacement, new).unwrap();
        let installer = staging.join(format!("{ASSET_PREFIX}{expected_version}.exe"));
        fs::copy(payload, &installer).unwrap();
        fs::write(
            staging.join("scenario.txt"),
            format!(
                "{}\n{}\n{mode}\n{app_mode}",
                application.display(),
                replacement.display()
            ),
        )
        .unwrap();
        let unrelated = root.join("unrelated");
        fs::create_dir(&unrelated).unwrap();
        let other_executable = unrelated.join("NetLights.exe");
        fs::copy(payload, &other_executable).unwrap();
        let other = Some(win::JobProcess::spawn(&other_executable, &[], &unrelated).unwrap());
        Self {
            root,
            application,
            installer,
            updates,
            old,
            other,
        }
    }
    fn arguments(&self) -> Arguments {
        Arguments {
            restart: self.application.clone(),
            parent: 0,
            installer: self.installer.clone(),
            sha256: file_hash(&self.installer).unwrap(),
            ready: None,
            timings: Timings {
                parent: Duration::from_millis(100),
                installer: Duration::from_secs(3),
                health: Duration::from_secs(2),
                liveness: Duration::from_millis(200),
            },
        }
    }
    fn assert_rollback(&self) {
        assert_eq!(fs::read(&self.application).unwrap(), self.old);
        assert!(
            !self
                .application
                .parent()
                .unwrap()
                .join("added-by-installer.txt")
                .exists()
        );
        assert_eq!(
            self.other
                .as_ref()
                .unwrap()
                .wait(Duration::from_millis(30))
                .unwrap(),
            None,
            "A different installation was stopped"
        );
        let value: serde_json::Value =
            serde_json::from_slice(&fs::read(self.updates.join("result.json")).unwrap()).unwrap();
        assert_eq!(value["success"], false);
        assert!(self.updates.join("failed-candidate.json").is_file());
        assert!(
            !fs::read_dir(&self.updates)
                .unwrap()
                .filter_map(Result::ok)
                .any(|entry| entry.file_name().to_string_lossy().starts_with("active-"))
        );
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = win::stop_installation_processes(&self.application);
        self.other.take();
        if ensure_unlinked(&self.root).is_ok() {
            let _ = fs::remove_dir_all(&self.root);
        }
    }
}
fn compile_fixture(directory: &Path) -> PathBuf {
    let compiler = std::env::split_paths(&std::env::var_os("PATH").unwrap_or_default())
        .map(|path| path.join("rustc.exe"))
        .find(|path| path.is_file())
        .expect("Rust toolchain must be on PATH");
    let output = directory.join("fixture.exe");
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("examples/fixture_process.rs");
    let resource = Path::new(env!("OUT_DIR")).join("main_res.res");
    assert!(resource.is_file());
    let args = vec![
        "--crate-name".into(),
        "netlights_update_fixture".into(),
        "--edition=2024".into(),
        source.to_string_lossy().into_owned(),
        "-o".into(),
        output.to_string_lossy().into_owned(),
        "-C".into(),
        "debuginfo=0".into(),
        "-C".into(),
        format!("link-arg={}", resource.display()),
    ];
    let mut compiler = win::JobProcess::spawn(&compiler, &args, directory).unwrap();
    let code = compiler.wait(Duration::from_secs(120)).unwrap();
    compiler.stop_tree().unwrap();
    assert_eq!(code, Some(0), "Rust process fixture failed to compile");
    assert_eq!(
        win::product_version(&output).unwrap(),
        version(env!("CARGO_PKG_VERSION")).unwrap()
    );
    output
}

#[test]
fn actual_process_success_failure_timeout_health_and_owned_recovery_matrix() {
    let compiler_root =
        std::env::temp_dir().join(format!("NetLightsRustFixture-{}", unique_name()));
    fs::create_dir(&compiler_root).unwrap();
    struct Cleanup(PathBuf);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    let _cleanup = Cleanup(compiler_root.clone());
    let payload = compile_fixture(&compiler_root);
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok().unwrap();
    }
    struct Com;
    impl Drop for Com {
        fn drop(&mut self) {
            unsafe { CoUninitialize() };
        }
    }
    let _com = Com;
    {
        let fixture = Fixture::new(&payload, "success", "healthy", env!("CARGO_PKG_VERSION"));
        assert_eq!(execute(fixture.arguments(), &fixture.updates).unwrap(), 0);
        assert_ne!(fs::read(&fixture.application).unwrap(), fixture.old);
        let result: serde_json::Value =
            serde_json::from_slice(&fs::read(fixture.updates.join("result.json")).unwrap())
                .unwrap();
        assert_eq!(result["success"], true);
        assert!(
            fs::read_dir(fixture.updates.join("rollback"))
                .unwrap()
                .next()
                .is_none()
        );
    }
    let current = env!("CARGO_PKG_VERSION");
    let mismatch = if current == "2.0.0" { "3.0.0" } else { "2.0.0" };
    for (mode, app_mode, expected, code) in [
        ("error", "healthy", current, 17),
        ("delete-target", "healthy", current, 17),
        ("delete-directory", "healthy", current, 17),
        ("success", "healthy", mismatch, 8),
        ("success", "early-exit", current, 8),
        ("success", "no-health", current, 8),
        ("hang", "healthy", current, 7),
        ("child-writer", "healthy", current, 7),
    ] {
        let fixture = Fixture::new(&payload, mode, app_mode, expected);
        assert_eq!(
            execute(fixture.arguments(), &fixture.updates).unwrap(),
            code,
            "mode={mode}, app={app_mode}, version={expected}"
        );
        fixture.assert_rollback();
        std::thread::sleep(Duration::from_millis(150));
        assert_eq!(
            fs::read(&fixture.application).unwrap(),
            fixture.old,
            "A stopped installer child rewrote restored bytes"
        );
    }
    {
        let fixture = Fixture::new(&payload, "success", "healthy", current);
        let mut args = fixture.arguments();
        args.sha256 = "0".repeat(64);
        assert_eq!(execute(args, &fixture.updates).unwrap(), 5);
        fixture.assert_rollback();
    }
    {
        let fixture = Fixture::new(&payload, "success", "healthy", current);
        let mut args = fixture.arguments();
        args.parent = std::process::id() as i32;
        assert_eq!(execute(args, &fixture.updates).unwrap(), 6);
        assert_eq!(fs::read(&fixture.application).unwrap(), fixture.old);
    }
}

#[test]
fn concurrent_transaction_and_torn_marker_refuse_shared_state_mutation() {
    let root = std::env::temp_dir().join(format!("NetLightsRustGuard-{}", unique_name()));
    fs::create_dir_all(&root).unwrap();
    let application = root.join("NetLights.exe");
    fs::write(&application, b"fixture").unwrap();
    let updates = root.join("updates");
    fs::create_dir(&updates).unwrap();
    let result = updates.join("result.json");
    fs::write(&result, b"preserve").unwrap();
    let (_, identity) = win::identity(&application).unwrap();
    let args = || Arguments {
        restart: application.clone(),
        parent: 0,
        installer: updates.join("missing.exe"),
        sha256: "a".repeat(64),
        ready: None,
        timings: Timings::default(),
    };
    let guard = match win::Guard::acquire(&identity).unwrap() {
        win::Acquisition::Owned(guard) => guard,
        _ => panic!("Fixture lock busy"),
    };
    // Windows mutexes are recursive on the same thread. A second owner must use another thread.
    let updates_copy = updates.clone();
    let arguments = args();
    assert_eq!(
        std::thread::spawn(move || execute(arguments, &updates_copy).unwrap())
            .join()
            .unwrap(),
        10
    );
    assert_eq!(fs::read(&result).unwrap(), b"preserve");
    drop(guard);
    let marker = updates.join(format!("active-{identity}.json"));
    fs::write(&marker, b"{torn").unwrap();
    assert_eq!(execute(args(), &updates).unwrap(), 11);
    assert_eq!(fs::read(&result).unwrap(), b"preserve");
    assert_eq!(fs::read(&marker).unwrap(), b"{torn");
    let valid = marker::create(&application).unwrap();
    assert!(!valid.is_empty());
    fs::remove_dir_all(root).unwrap();
}
