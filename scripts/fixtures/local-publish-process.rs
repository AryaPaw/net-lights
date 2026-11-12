//! Disposable native processes used only by the local publication regression.
use std::{env, fs, path::PathBuf};
#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateEventW(security: *const (), manual: i32, initial: i32, name: *const u16) -> isize;
    fn OpenEventW(access: u32, inherit: i32, name: *const u16) -> isize;
    fn SetEvent(event: isize) -> i32;
    fn WaitForSingleObject(event: isize, milliseconds: u32) -> u32;
    fn CloseHandle(handle: isize) -> i32;
}
fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain([0]).collect()
}
fn main() {
    let args: Vec<_> = env::args().collect();
    let value = |flag: &str| args.iter().position(|arg| arg == flag).and_then(|i| args.get(i + 1));
    let profile = PathBuf::from(value("--profile-directory").expect("isolated fixture profile"));
    let directory = env::current_exe().unwrap().parent().unwrap().to_owned();
    let mode = fs::read_to_string(directory.join("fixture-mode.txt")).unwrap();
    let name = fs::read_to_string(profile.join("fixture-exit-event.txt")).unwrap();
    if args.iter().any(|arg| arg == "--exit-local") {
        if mode == "no-ready-ignore-exit" {
            return;
        }
        unsafe {
            let event = OpenEventW(2, 0, wide(&name).as_ptr());
            if event != 0 {
                SetEvent(event);
                CloseHandle(event);
            }
        }
        return;
    }
    fs::write(profile.join("fixture-last-pid.txt"), std::process::id().to_string()).unwrap();
    if mode == "crash" {
        std::process::exit(23);
    }
    unsafe {
        let exit = CreateEventW(std::ptr::null(), 1, 0, wide(&name).as_ptr());
        assert_ne!(exit, 0);
        if mode == "healthy" {
            if let Some(name) = value("--update-health-event") {
                let ready = OpenEventW(2, 0, wide(name).as_ptr());
                assert_ne!(ready, 0);
                assert_ne!(SetEvent(ready), 0);
                CloseHandle(ready);
            }
        }
        assert_eq!(WaitForSingleObject(exit, 120_000), 0);
        CloseHandle(exit);
    }
}
