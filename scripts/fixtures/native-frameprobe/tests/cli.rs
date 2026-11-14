use std::{
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant},
};

#[test]
fn both_capture_commands_enter_the_guarded_capture_path_without_any_hosted_environment() {
    for name in ["--capture-roi", "--capture-dxgi-roi"] {
        let mut command = Command::new(env!("CARGO_BIN_EXE_netlights-native-frameprobe"));
        command
            .args([name, "1", "1", "0", "0", "16", "1"])
            .env_remove("GITHUB_ACTIONS")
            .env_remove("RUNNER_ENVIRONMENT")
            .env_remove("CI")
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            command.creation_flags(0x08000000);
        }
        let mut child = command.spawn().unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while child.try_wait().unwrap().is_none() {
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                panic!("The guarded capture command didn't exit before its deadline");
            }
            thread::sleep(Duration::from_millis(10));
        }
        let output = child.wait_with_output().unwrap();
        assert_eq!(output.status.code(), Some(3));
        assert!(output.stderr.is_empty());
        assert_eq!(
            std::str::from_utf8(&output.stdout).unwrap().trim(),
            "{\"kind\":\"error\",\"code\":\"hosted_guard\",\"hresult\":0}"
        );
    }
}
