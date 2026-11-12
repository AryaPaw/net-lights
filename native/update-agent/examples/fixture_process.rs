//! Disposable process payload for update-agent tests. Never shipped or invoked by the app.
#![cfg_attr(windows, windows_subsystem = "windows")]
#[cfg(windows)]
use std::os::windows::process::CommandExt;
use std::{fs, path::PathBuf, thread, time::Duration};
fn main() {
    let arguments = std::env::args().collect::<Vec<_>>();
    if arguments.get(1).is_some_and(|arg| arg == "--writer") {
        let path = PathBuf::from(arguments.get(2).unwrap());
        loop {
            let _ = fs::write(&path, b"child-writer");
            thread::sleep(Duration::from_millis(20));
        }
    }
    let executable = std::env::current_exe().unwrap();
    let directory = executable.parent().unwrap();
    if executable
        .file_name()
        .unwrap()
        .to_string_lossy()
        .starts_with("NetLights-Setup-")
    {
        let config = fs::read_to_string(directory.join("scenario.txt")).unwrap();
        let config = config.lines().collect::<Vec<_>>();
        let target = PathBuf::from(config[0]);
        fs::copy(config[1], &target).unwrap();
        fs::write(
            target.parent().unwrap().join("added-by-installer.txt"),
            b"added",
        )
        .unwrap();
        let app_mode = config.get(3).copied().unwrap_or("healthy");
        fs::write(target.parent().unwrap().join("fixture-app.txt"), app_mode).unwrap();
        match config[2] {
            "error" => std::process::exit(17),
            "delete-target" => {
                fs::remove_file(&target).unwrap();
                std::process::exit(17);
            }
            "delete-directory" => {
                fs::remove_dir_all(target.parent().unwrap()).unwrap();
                std::process::exit(17);
            }
            "child-writer" => {
                let mut command = std::process::Command::new(&executable);
                command
                    .arg("--writer")
                    .arg(target.parent().unwrap().join("NetLights.exe"));
                #[cfg(windows)]
                command.creation_flags(0x08000000);
                let mut child = command.spawn().unwrap();
                fs::write(directory.join("writer.pid"), child.id().to_string()).unwrap();
                let _ = child.wait();
            }
            "hang" => loop {
                thread::sleep(Duration::from_millis(50));
            },
            _ => {}
        }
        return;
    }
    let mode =
        fs::read_to_string(directory.join("fixture-app.txt")).unwrap_or_else(|_| "healthy".into());
    if mode == "early-exit" {
        return;
    }
    if mode != "no-health"
        && let Some(index) = arguments
            .iter()
            .position(|arg| arg == "--update-health-event")
        && let Some(name) = arguments.get(index + 1)
    {
        #[cfg(windows)]
        {
            #[link(name = "kernel32")]
            unsafe extern "system" {
                fn OpenEventW(access: u32, inherit: i32, name: *const u16)
                -> *mut std::ffi::c_void;
                fn SetEvent(event: *mut std::ffi::c_void) -> i32;
                fn CloseHandle(event: *mut std::ffi::c_void) -> i32;
            }
            let wide = name.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
            let event = unsafe { OpenEventW(2, 0, wide.as_ptr()) };
            if !event.is_null() {
                let _ = unsafe { SetEvent(event) };
                let _ = unsafe { CloseHandle(event) };
            }
        }
    }
    loop {
        thread::sleep(Duration::from_millis(100));
    }
}
