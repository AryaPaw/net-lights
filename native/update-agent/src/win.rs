use anyhow::{Context, Result, bail, ensure};
use netlights_core::update::{ensure_unlinked, unique_name};
use std::{
    ffi::OsStr,
    fs,
    os::windows::ffi::OsStrExt,
    path::{Path, PathBuf},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Foundation::*,
        Storage::FileSystem::*,
        System::{Diagnostics::ToolHelp::*, JobObjects::*, Threading::*},
    },
    core::{HRESULT, PCWSTR, PWSTR},
};

pub fn wide(value: impl AsRef<OsStr>) -> Vec<u16> {
    value.as_ref().encode_wide().chain(Some(0)).collect()
}
pub struct Handle(pub HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            let _ = unsafe { CloseHandle(self.0) };
        }
    }
}
pub fn canonical(executable: &Path) -> Result<PathBuf> {
    ensure_unlinked(executable)?;
    Ok(fs::canonicalize(executable)?)
}
fn canonical_location(path: &Path) -> Result<PathBuf> {
    ensure_unlinked(path)?;
    let mut existing = path;
    let mut suffix = Vec::new();
    while !existing.exists() {
        suffix.push(
            existing
                .file_name()
                .context("Location has no existing ancestor")?
                .to_os_string(),
        );
        existing = existing.parent().context("Location has no parent")?;
    }
    let mut location = fs::canonicalize(existing)?;
    for part in suffix.iter().rev() {
        location.push(part);
    }
    Ok(location)
}
pub fn ordinal_paths_equal(left: &Path, right: &Path) -> bool {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        #[link_name = "CompareStringOrdinal"]
        fn compare_string_ordinal(
            left: *const u16,
            left_len: i32,
            right: *const u16,
            right_len: i32,
            ignore_case: i32,
        ) -> i32;
    }
    let left = left.as_os_str().encode_wide().collect::<Vec<_>>();
    let right = right.as_os_str().encode_wide().collect::<Vec<_>>();
    if left.len() > i32::MAX as usize || right.len() > i32::MAX as usize {
        return false;
    }
    unsafe {
        compare_string_ordinal(
            left.as_ptr(),
            left.len() as i32,
            right.as_ptr(),
            right.len() as i32,
            1,
        ) == 2
    }
}
pub fn identity(executable: &Path) -> Result<(String, String)> {
    netlights_core::update::installation_identity(executable)
}
pub struct Guard {
    handle: Handle,
    owned: bool,
}
pub enum Acquisition {
    Owned(Guard),
    Busy,
    Interrupted,
}
impl Guard {
    pub fn acquire(identity: &str) -> Result<Acquisition> {
        let name = wide(format!("Global\\NetLights.UpdateInstallation.{identity}"));
        let handle = Handle(unsafe { CreateMutexW(None, false, PCWSTR(name.as_ptr()))? });
        match unsafe { WaitForSingleObject(handle.0, 0) } {
            WAIT_OBJECT_0 => Ok(Acquisition::Owned(Self {
                handle,
                owned: true,
            })),
            WAIT_ABANDONED => {
                let _ = unsafe { ReleaseMutex(handle.0) };
                Ok(Acquisition::Interrupted)
            }
            WAIT_TIMEOUT => Ok(Acquisition::Busy),
            _ => bail!("Installation lock failed"),
        }
    }
}
impl Drop for Guard {
    fn drop(&mut self) {
        if self.owned {
            let _ = unsafe { ReleaseMutex(self.handle.0) };
        }
    }
}
pub struct Signal(Handle);
impl Signal {
    pub fn create(name: &str) -> Result<Self> {
        let name = wide(name);
        Ok(Self(Handle(unsafe {
            CreateEventW(None, true, false, PCWSTR(name.as_ptr()))?
        })))
    }
    pub fn open_and_set(name: &str) -> Result<()> {
        let name = wide(name);
        let event =
            Handle(unsafe { OpenEventW(EVENT_MODIFY_STATE, false, PCWSTR(name.as_ptr()))? });
        unsafe { SetEvent(event.0)? };
        Ok(())
    }
    pub fn wait(&self, timeout: Duration) -> bool {
        unsafe { WaitForSingleObject(self.0.0, milliseconds(timeout)) == WAIT_OBJECT_0 }
    }
}
pub fn signal_name_valid(name: &str, prefix: &str) -> bool {
    name.strip_prefix(prefix)
        .is_some_and(|value| value.len() == 32 && value.bytes().all(|b| b.is_ascii_hexdigit()))
}
fn milliseconds(duration: Duration) -> u32 {
    duration.as_millis().min((u32::MAX - 1) as u128) as u32
}
pub fn wait_parent(pid: i32, timeout: Duration) -> Result<bool> {
    if pid <= 0 {
        return Ok(true);
    }
    match unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, pid as u32) } {
        Ok(handle) => {
            let handle = Handle(handle);
            Ok(unsafe { WaitForSingleObject(handle.0, milliseconds(timeout)) == WAIT_OBJECT_0 })
        }
        Err(error) if error.code() == HRESULT::from_win32(ERROR_INVALID_PARAMETER.0) => Ok(true),
        Err(error) => Err(error.into()),
    }
}

/// An installer is assigned while suspended. Its entire tree dies if the agent crashes.
pub struct JobProcess {
    process: Handle,
    job: Handle,
    detached: bool,
}
impl JobProcess {
    pub fn spawn(executable: &Path, arguments: &[String], directory: &Path) -> Result<Self> {
        let job = Handle(unsafe { CreateJobObjectW(None, PCWSTR::null())? });
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as _,
                std::mem::size_of_val(&limits) as u32,
            )?;
        }
        let application = wide(executable);
        let command_line = std::iter::once(executable.to_string_lossy().into_owned())
            .chain(arguments.iter().cloned())
            .map(|arg| quote(&arg))
            .collect::<Vec<_>>()
            .join(" ");
        let mut command_line = wide(command_line);
        let working = wide(directory);
        let startup = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            ..Default::default()
        };
        let mut info = PROCESS_INFORMATION::default();
        unsafe {
            CreateProcessW(
                PCWSTR(application.as_ptr()),
                Some(PWSTR(command_line.as_mut_ptr())),
                None,
                None,
                false,
                CREATE_SUSPENDED | CREATE_NO_WINDOW,
                None,
                PCWSTR(working.as_ptr()),
                &startup,
                &mut info,
            )?;
        }
        let process = Handle(info.hProcess);
        let thread = Handle(info.hThread);
        if let Err(error) = unsafe { AssignProcessToJobObject(job.0, process.0) } {
            let _ = unsafe { TerminateProcess(process.0, 1) };
            return Err(error.into());
        }
        if unsafe { ResumeThread(thread.0) } == u32::MAX {
            let _ = unsafe { TerminateJobObject(job.0, 1) };
            bail!("Process resume failed");
        }
        Ok(Self {
            process,
            job,
            detached: false,
        })
    }
    pub fn wait(&self, timeout: Duration) -> Result<Option<u32>> {
        match unsafe { WaitForSingleObject(self.process.0, milliseconds(timeout)) } {
            WAIT_TIMEOUT => Ok(None),
            WAIT_OBJECT_0 => {
                let mut exit = 0;
                unsafe { GetExitCodeProcess(self.process.0, &mut exit)? };
                Ok(Some(exit))
            }
            _ => bail!("Process wait failed"),
        }
    }
    pub fn stop_tree(&mut self) -> Result<()> {
        unsafe {
            TerminateJobObject(self.job.0, 1)?;
        }
        let clock = Instant::now();
        while clock.elapsed() < Duration::from_secs(5) {
            let mut accounting = JOBOBJECT_BASIC_ACCOUNTING_INFORMATION::default();
            unsafe {
                QueryInformationJobObject(
                    Some(self.job.0),
                    JobObjectBasicAccountingInformation,
                    &mut accounting as *mut _ as _,
                    std::mem::size_of_val(&accounting) as u32,
                    None,
                )?;
            }
            if accounting.ActiveProcesses == 0 {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        bail!("Installer processes didn't stop")
    }
    pub fn detach(mut self) -> Result<()> {
        let limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        unsafe {
            SetInformationJobObject(
                self.job.0,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as _,
                std::mem::size_of_val(&limits) as u32,
            )?;
        }
        self.detached = true;
        Ok(())
    }
}
impl Drop for JobProcess {
    fn drop(&mut self) {
        if !self.detached {
            let _ = unsafe { TerminateJobObject(self.job.0, 1) };
        }
    }
}
fn quote(value: &str) -> String {
    let mut text = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        match c {
            '\\' => slashes += 1,
            '"' => {
                text.extend(std::iter::repeat_n('\\', slashes * 2 + 1));
                text.push('"');
                slashes = 0;
            }
            _ => {
                text.extend(std::iter::repeat_n('\\', slashes));
                slashes = 0;
                text.push(c);
            }
        }
    }
    text.extend(std::iter::repeat_n('\\', slashes * 2));
    text.push('"');
    text
}
pub fn stop_installation_processes(target: &Path) -> Result<()> {
    let expected = canonical_location(target)?;
    let snapshot = Handle(unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)? });
    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    if unsafe { Process32FirstW(snapshot.0, &mut entry) }.is_err() {
        return Ok(());
    }
    loop {
        let length = entry
            .szExeFile
            .iter()
            .position(|c| *c == 0)
            .unwrap_or(entry.szExeFile.len());
        if String::from_utf16_lossy(&entry.szExeFile[..length])
            .eq_ignore_ascii_case("NetLights.exe")
            && let Ok(handle) = unsafe {
                OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | PROCESS_SYNCHRONIZE,
                    false,
                    entry.th32ProcessID,
                )
            }
        {
            let process = Handle(handle);
            let mut buffer = [0u16; 32768];
            let mut size = buffer.len() as u32;
            if unsafe {
                QueryFullProcessImageNameW(
                    process.0,
                    PROCESS_NAME_WIN32,
                    PWSTR(buffer.as_mut_ptr()),
                    &mut size,
                )
            }
            .is_ok()
            {
                let path = PathBuf::from(String::from_utf16_lossy(&buffer[..size as usize]));
                if canonical_location(&path).is_ok_and(|p| ordinal_paths_equal(&p, &expected))
                    && unsafe { WaitForSingleObject(process.0, 0) } != WAIT_OBJECT_0
                {
                    unsafe { TerminateProcess(process.0, 1)? };
                    ensure!(
                        unsafe { WaitForSingleObject(process.0, 5000) } == WAIT_OBJECT_0,
                        "Installation process didn't stop"
                    );
                }
            }
        }
        if unsafe { Process32NextW(snapshot.0, &mut entry) }.is_err() {
            break;
        }
    }
    Ok(())
}
pub fn restart(
    executable: &Path,
    require_monitor: bool,
    suppress_updates: bool,
    health_wait: Duration,
    liveness_wait: Duration,
) -> Result<bool> {
    let name = format!("Local\\NetLights.UpdateHealth.{}", unique_name());
    let signal = if require_monitor {
        Some(Signal::create(&name)?)
    } else {
        None
    };
    let mut args = Vec::new();
    if require_monitor {
        args.extend(["--update-health-event".into(), name]);
    }
    if suppress_updates {
        args.push("--skip-automatic-updates".into());
    }
    let mut next = JobProcess::spawn(
        executable,
        &args,
        executable.parent().context("Executable has no directory")?,
    )?;
    if signal.is_some_and(|signal| !signal.wait(health_wait)) {
        next.stop_tree()?;
        return Ok(false);
    }
    if next.wait(liveness_wait)?.is_some() {
        return Ok(false);
    }
    next.detach()?;
    Ok(true)
}
pub fn product_version(executable: &Path) -> Result<[u32; 4]> {
    let path = wide(executable);
    let size = unsafe { GetFileVersionInfoSizeW(PCWSTR(path.as_ptr()), None) };
    ensure!(
        size > 0 && size <= 1024 * 1024,
        "Executable version resource is missing"
    );
    let mut data = vec![0u8; size as usize];
    unsafe {
        GetFileVersionInfoW(PCWSTR(path.as_ptr()), None, size, data.as_mut_ptr() as _)?;
    }
    let mut value = std::ptr::null_mut();
    let mut length = 0u32;
    ensure!(
        unsafe {
            VerQueryValueW(
                data.as_ptr() as _,
                PCWSTR(wide("\\").as_ptr()),
                &mut value,
                &mut length,
            )
        }
        .as_bool(),
        "Executable version resource is invalid"
    );
    ensure!(
        !value.is_null() && length as usize >= std::mem::size_of::<VS_FIXEDFILEINFO>(),
        "Executable fixed version is missing"
    );
    let version = unsafe { std::ptr::read_unaligned(value as *const VS_FIXEDFILEINFO) };
    ensure!(
        version.dwSignature == 0xfeef04bd,
        "Executable version signature is invalid"
    );
    Ok([
        version.dwProductVersionMS >> 16,
        version.dwProductVersionMS & 0xffff,
        version.dwProductVersionLS >> 16,
        version.dwProductVersionLS & 0xffff,
    ])
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn command_line_quoting_preserves_spaces_quotes_and_trailing_slashes() {
        assert_eq!(
            quote("C:\\Program Files\\NetLights\\"),
            "\"C:\\Program Files\\NetLights\\\\\""
        );
        assert_eq!(quote("a\"b"), "\"a\\\"b\"");
    }
    #[test]
    fn signal_names_are_strict() {
        assert!(signal_name_valid(
            &format!("Local\\NetLights.UpdateHealth.{}", "a".repeat(32)),
            "Local\\NetLights.UpdateHealth."
        ));
        assert!(!signal_name_valid(
            "Local\\Other",
            "Local\\NetLights.UpdateHealth."
        ));
    }
}
