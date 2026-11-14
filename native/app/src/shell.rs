//! Shell callbacks may pump Windows messages. Never call them with GPUI's App
//! borrowed on the UI thread. Only one request can be pending per window.
use std::{os::windows::ffi::OsStrExt, path::PathBuf, sync::mpsc};
use windows::{
    Win32::{
        System::Com::{
            COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE, CoInitializeEx, CoTaskMemFree,
            CoUninitialize,
        },
        UI::Shell::{Common::ITEMIDLIST, SHOpenFolderAndSelectItems, SHParseDisplayName},
    },
    core::PCWSTR,
};

#[derive(Clone)]
pub enum Target {
    Directory(PathBuf),
    LicenseArchive(PathBuf),
}
impl Target {
    pub fn failure_message(&self) -> &'static str {
        match self {
            Self::Directory(_) => "Не удалось открыть папку настроек.",
            Self::LicenseArchive(_) => "Не удалось показать файлы лицензий.",
        }
    }
}
pub struct Job {
    receiver: mpsc::Receiver<bool>,
    pub failure_message: &'static str,
}
impl Job {
    pub fn start(
        target: Target,
        journal: crate::backend::journal::Journal,
    ) -> std::io::Result<Self> {
        Self::start_with(target.failure_message(), move || {
            journal.event("shell-start");
            let result = execute(target);
            journal.event(if result {
                "shell-completed"
            } else {
                "shell-failed"
            });
            result
        })
    }
    fn start_with(
        failure_message: &'static str,
        task: impl FnOnce() -> bool + Send + 'static,
    ) -> std::io::Result<Self> {
        let (sender, receiver) = mpsc::sync_channel(1);
        std::thread::Builder::new()
            .name("netlights-shell".into())
            .spawn(move || {
                // An ordinary Rust panic must become an action error, not take down
                // the monitor. Windows callback panics still need the persistent hook.
                let result =
                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(task)).unwrap_or(false);
                let _ = sender.send(result);
            })?;
        Ok(Self {
            receiver,
            failure_message,
        })
    }
    pub fn poll(&self) -> Option<bool> {
        match self.receiver.try_recv() {
            Ok(success) => Some(success),
            Err(mpsc::TryRecvError::Empty) => None,
            Err(mpsc::TryRecvError::Disconnected) => Some(false),
        }
    }
    pub fn apply_notice(&self, notice: &mut String, success: bool) {
        if !success {
            *notice = self.failure_message.into();
        } else if notice == self.failure_message {
            notice.clear();
        }
    }
}

fn execute(target: Target) -> bool {
    struct Apartment;
    impl Drop for Apartment {
        fn drop(&mut self) {
            unsafe { CoUninitialize() };
        }
    }
    if unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) }.is_err() {
        return false;
    }
    let _apartment = Apartment;
    match target {
        Target::Directory(path) => netlights_core::platform::open_settings(&path).is_ok(),
        Target::LicenseArchive(path) => reveal_archive(&path),
    }
}
fn reveal_archive(path: &std::path::Path) -> bool {
    if !path.is_file() {
        return false;
    }
    let mut name: Vec<u16> = path.as_os_str().encode_wide().collect();
    if name.contains(&0) {
        return false;
    }
    name.push(0);
    struct Pidl(*mut ITEMIDLIST);
    impl Drop for Pidl {
        fn drop(&mut self) {
            unsafe { CoTaskMemFree(Some(self.0.cast())) };
        }
    }
    let mut pidl = Pidl(std::ptr::null_mut());
    if unsafe { SHParseDisplayName(PCWSTR(name.as_ptr()), None, &mut pidl.0, 0, None) }.is_err()
        || pidl.0.is_null()
    {
        return false;
    }
    // With zero children the full PIDL names the item to select. Passing a
    // desktop-relative file PIDL as a child of a parent folder is invalid.
    unsafe { SHOpenFolderAndSelectItems(pidl.0, None, 0) }.is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shell_completion_failure_and_disconnected_worker_are_reported_without_ui_calls() {
        for expected in [true, false] {
            let job = Job::start_with("failure", move || expected).unwrap();
            assert_eq!(
                job.receiver
                    .recv_timeout(std::time::Duration::from_secs(2))
                    .unwrap(),
                expected
            );
        }
        let job = Job::start_with("failure", || panic!("simulated shell task failure")).unwrap();
        assert!(
            !job.receiver
                .recv_timeout(std::time::Duration::from_secs(2))
                .unwrap()
        );
        let (sender, receiver) = mpsc::sync_channel(1);
        let job = Job {
            receiver,
            failure_message: "failure",
        };
        assert_eq!(job.poll(), None);
        drop(sender);
        assert_eq!(job.poll(), Some(false));
        let mut notice = "failure".to_string();
        job.apply_notice(&mut notice, true);
        assert!(notice.is_empty());
        notice = "unrelated monitor error".into();
        job.apply_notice(&mut notice, true);
        assert_eq!(notice, "unrelated monitor error");
    }
    #[test]
    fn missing_shell_targets_return_errors_without_opening_an_application() {
        let absent = std::env::temp_dir()
            .join(format!("NetLightsMissing-{}", std::process::id()))
            .join("absent");
        assert!(!absent.exists());
        assert!(!execute(Target::Directory(absent.clone())));
        assert!(!execute(Target::LicenseArchive(absent)));
    }
}
