//! Windows integration shared by the native host. This module never mutates
//! network configuration or accepts a linked live profile as an isolated one.
use anyhow::{Context, Result};
use sha2::{Digest, Sha256};
use std::{
    ffi::OsStr,
    fs,
    marker::PhantomData,
    os::windows::{ffi::OsStrExt, fs::MetadataExt},
    path::{Path, PathBuf},
    rc::Rc,
};
use windows::{
    Win32::{
        Foundation::{
            CloseHandle, ERROR_FILE_NOT_FOUND, HANDLE, RPC_E_CHANGED_MODE, WAIT_ABANDONED,
            WAIT_OBJECT_0, WAIT_TIMEOUT,
        },
        Storage::FileSystem::{
            FILE_ATTRIBUTE_REPARSE_POINT, GetDriveTypeW, GetFullPathNameW, GetLongPathNameW,
            QueryDosDeviceW,
        },
        System::{
            Com::{
                CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize, IPersistFile, STGM_READ,
            },
            Registry::{
                self, HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_READ, KEY_SET_VALUE,
                REG_OPTION_NON_VOLATILE, REG_SZ, REG_VALUE_TYPE,
            },
            Threading::{
                CreateEventW, CreateMutexW, EVENT_MODIFY_STATE, OpenEventW, ReleaseMutex,
                ResetEvent, SetEvent, WaitForSingleObject,
            },
        },
        UI::{
            Shell::{
                FOLDERID_LocalAppData, FOLDERID_Startup, IShellLinkW, KF_FLAG_DEFAULT,
                SHGetKnownFolderPath, SLGP_RAWPATH, ShellExecuteW, ShellLink,
            },
            WindowsAndMessaging::SW_SHOWNORMAL,
        },
    },
    core::{Interface, PCWSTR, w},
};

const INSTANCE_MUTEX: &str = "Local\\NetLights.SingleInstance";
const AUTOSTART_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
const AUTOSTART_VALUE: &str = "NetLights";

fn wide(value: impl AsRef<OsStr>) -> Vec<u16> {
    value
        .as_ref()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}
fn hash24(value: &str) -> String {
    format!("{:X}", Sha256::digest(value.as_bytes()))[..24].into()
}
fn upper(value: &str) -> String {
    crate::update::legacy_invariant_upper(value)
}
fn ordinal_ignore_case(left: &str, right: &str) -> bool {
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
    let left: Vec<_> = left.encode_utf16().collect();
    let right: Vec<_> = right.encode_utf16().collect();
    if left.len() > i32::MAX as usize || right.len() > i32::MAX as usize {
        return false;
    }
    // Legacy invariant-uppercase hashing is a compatibility protocol, not proof
    // that two Windows installation paths have the same owner (for example ſ/S).
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
fn full_path(path: &Path) -> Result<PathBuf> {
    let text = wide(path);
    anyhow::ensure!(
        !text[..text.len() - 1].contains(&0),
        "A path cannot contain a null character"
    );
    let mut output = vec![0; 32768];
    let len = unsafe { GetFullPathNameW(PCWSTR(text.as_ptr()), Some(&mut output), None) } as usize;
    anyhow::ensure!(
        len > 0 && len < output.len(),
        "Cannot normalize the local path"
    );
    let mut normalized = String::from_utf16(&output[..len])?;
    while normalized.len() > 3 && normalized.ends_with(['\\', '/']) {
        normalized.pop();
    }
    Ok(PathBuf::from(normalized))
}
fn ensure_unlinked(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) => anyhow::ensure!(
                metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT.0 == 0,
                "A linked directory isn't an isolated profile"
            ),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
    }
    Ok(())
}
fn expand_existing(path: &Path) -> Result<PathBuf> {
    let existing = path
        .ancestors()
        .find(|candidate| candidate.is_dir())
        .context("An accessible directory ancestor is required")?;
    let text = wide(existing);
    let mut expanded = vec![0; 32768];
    let length = unsafe { GetLongPathNameW(PCWSTR(text.as_ptr()), Some(&mut expanded)) } as usize;
    anyhow::ensure!(
        length > 0 && length < expanded.len(),
        "Cannot expand existing directory names"
    );
    let base = PathBuf::from(String::from_utf16(&expanded[..length])?);
    full_path(&base.join(path.strip_prefix(existing)?))
}
pub fn default_profile() -> Result<PathBuf> {
    Ok(known_folder(&FOLDERID_LocalAppData)?.join("NetLights"))
}
fn known_folder(id: &windows::core::GUID) -> Result<PathBuf> {
    let pointer = unsafe { SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None)? };
    let result = unsafe { pointer.to_string() };
    unsafe {
        CoTaskMemFree(Some(pointer.0.cast()));
    }
    Ok(PathBuf::from(result?))
}
fn local_profile_drive(kind: u32) -> bool {
    // GetDriveTypeW's documented DRIVE_REMOVABLE, DRIVE_FIXED and DRIVE_RAMDISK.
    // Reject remote, unknown, absent and read-only optical roots before any writes.
    matches!(kind, 2 | 3 | 6)
}
pub fn isolated_profile(path: &Path) -> Result<PathBuf> {
    let raw = path
        .to_str()
        .context("A valid Unicode profile path is required")?;
    let bytes = raw.as_bytes();
    anyhow::ensure!(
        path.is_absolute()
            && bytes.len() >= 3
            && bytes[0].is_ascii_alphabetic()
            && bytes[1] == b':'
            && matches!(bytes[2], b'/' | b'\\')
            && !raw.starts_with("\\\\"),
        "An absolute regular local drive path is required"
    );
    anyhow::ensure!(
        !raw[2..]
            .split(['/', '\\'])
            .any(|part| part.ends_with('.') || part.ends_with(' ') || part.contains(':')),
        "Device and filesystem aliases aren't accepted for isolation"
    );
    let drive_root = wide(format!("{}\\", &raw[..2]));
    anyhow::ensure!(
        local_profile_drive(unsafe { GetDriveTypeW(PCWSTR(drive_root.as_ptr())) }),
        "An isolated profile requires a known local writable drive; mapped network drives aren't accepted"
    );
    let drive = wide(&raw[..2]);
    let mut target = vec![0; 32768];
    let length = unsafe { QueryDosDeviceW(PCWSTR(drive.as_ptr()), Some(&mut target)) } as usize;
    anyhow::ensure!(
        length > 0 && length < target.len(),
        "Cannot inspect the profile drive"
    );
    let target = String::from_utf16_lossy(&target[..length]);
    anyhow::ensure!(
        !target.starts_with("\\??\\"),
        "A substituted drive isn't an isolated profile"
    );
    let candidate = full_path(path)?;
    ensure_unlinked(&candidate)?;
    anyhow::ensure!(!candidate.is_file(), "Profile directory points at a file");
    let candidate = expand_existing(&candidate)?;
    ensure_unlinked(&candidate)?;
    let live = full_path(&default_profile()?)?;
    // Long-name expansion handles DOS aliases, but doesn't resolve junctions.
    // If the live root itself is linked, its true identity cannot be proved by
    // this guard; refuse isolated writes rather than comparing different names.
    ensure_unlinked(&live)?;
    let live = expand_existing(&live)?;
    let key = upper(&candidate.to_string_lossy());
    let live_key = upper(&live.to_string_lossy());
    anyhow::ensure!(
        key != live_key && !key.starts_with(&(live_key + "\\")),
        "An isolated profile cannot use the live profile or its children"
    );
    Ok(candidate)
}

struct OwnedHandle(HANDLE);
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
pub struct Instance {
    mutex: OwnedHandle,
    exit: OwnedHandle,
    show: OwnedHandle,
    // A Windows mutex is released by its owning OS thread, not a runtime worker.
    _main_thread: PhantomData<Rc<()>>,
}
pub enum Startup {
    Owner(Instance),
    Forwarded,
}
impl Instance {
    pub fn take_exit_request(&self) -> bool {
        unsafe { WaitForSingleObject(self.exit.0, 0) == WAIT_OBJECT_0 }
    }
    pub fn take_show_request(&self) -> bool {
        unsafe { WaitForSingleObject(self.show.0, 0) == WAIT_OBJECT_0 }
    }
}
impl Drop for Instance {
    fn drop(&mut self) {
        unsafe {
            let _ = ReleaseMutex(self.mutex.0);
        }
    }
}
fn signal_existing(name: &str) -> Result<bool> {
    let name = wide(name);
    let handle = match unsafe { OpenEventW(EVENT_MODIFY_STATE, false, PCWSTR(name.as_ptr())) } {
        Ok(handle) => OwnedHandle(handle),
        Err(error) if error.code().0 as u32 == 0x80070002 => return Ok(false),
        Err(error) => return Err(error.into()),
    };
    unsafe {
        SetEvent(handle.0)?;
    }
    Ok(true)
}
pub fn acquire(profile: Option<&Path>, show: bool, exit: bool) -> Result<Startup> {
    let profile = profile.map(isolated_profile).transpose()?;
    let exe = full_path(&std::env::current_exe()?)?;
    let mut identity = exe.to_string_lossy().into_owned();
    if let Some(profile) = &profile {
        identity.push('|');
        identity.push_str(&upper(&profile.to_string_lossy()));
    }
    let suffix = hash24(&identity);
    let exit_name = format!("Local\\NetLights.Exit.{suffix}");
    let show_name = format!("Local\\NetLights.Show.{suffix}");
    if exit {
        let _ = signal_existing(&exit_name)?;
        return Ok(Startup::Forwarded);
    }
    let mutex_name = if let Some(profile) = &profile {
        format!(
            "{INSTANCE_MUTEX}.{}",
            hash24(&upper(&profile.to_string_lossy()))
        )
    } else {
        INSTANCE_MUTEX.into()
    };
    let mutex_name = wide(mutex_name);
    let mutex = OwnedHandle(unsafe { CreateMutexW(None, false, PCWSTR(mutex_name.as_ptr()))? });
    let status = unsafe { WaitForSingleObject(mutex.0, 0) };
    if status == WAIT_TIMEOUT {
        if show {
            let _ = signal_existing(&show_name)?;
        }
        return Ok(Startup::Forwarded);
    }
    anyhow::ensure!(
        status == WAIT_OBJECT_0 || status == WAIT_ABANDONED,
        "Cannot acquire the application instance"
    );
    let instance = (|| {
        let exit_name = wide(exit_name);
        let show_name = wide(show_name);
        let exit =
            OwnedHandle(unsafe { CreateEventW(None, false, false, PCWSTR(exit_name.as_ptr()))? });
        let show =
            OwnedHandle(unsafe { CreateEventW(None, false, false, PCWSTR(show_name.as_ptr()))? });
        unsafe {
            ResetEvent(exit.0)?;
            ResetEvent(show.0)?;
        }
        Ok::<_, anyhow::Error>((exit, show))
    })();
    match instance {
        Ok((exit, show)) => Ok(Startup::Owner(Instance {
            mutex,
            exit,
            show,
            _main_thread: PhantomData,
        })),
        Err(error) => {
            unsafe {
                let _ = ReleaseMutex(mutex.0);
            }
            Err(error)
        }
    }
}
pub fn signal_health(name: &str) -> Result<()> {
    let suffix = name
        .strip_prefix("Local\\NetLights.UpdateHealth.")
        .context("Invalid update health event name")?;
    anyhow::ensure!(
        suffix.len() == 32 && suffix.bytes().all(|b| b.is_ascii_hexdigit()),
        "Invalid update health event name"
    );
    anyhow::ensure!(
        signal_existing(name)?,
        "Update health event no longer exists"
    );
    Ok(())
}

struct OwnedKey(HKEY);
impl Drop for OwnedKey {
    fn drop(&mut self) {
        unsafe {
            let _ = Registry::RegCloseKey(self.0);
        }
    }
}
fn read_command(key: HKEY, name: &str) -> Result<Option<String>> {
    let name = wide(name);
    let mut value_type = REG_VALUE_TYPE::default();
    let mut size = 0u32;
    let status = unsafe {
        Registry::RegQueryValueExW(
            key,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut value_type),
            None,
            Some(&mut size),
        )
    };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(None);
    }
    status.ok()?;
    anyhow::ensure!(
        value_type == REG_SZ && size <= 64 * 1024 && size.is_multiple_of(2),
        "Autostart command has an unsupported value type or length"
    );
    let mut bytes = vec![0u8; size as usize];
    unsafe {
        Registry::RegQueryValueExW(
            key,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut value_type),
            Some(bytes.as_mut_ptr()),
            Some(&mut size),
        )
        .ok()?;
    }
    anyhow::ensure!(
        value_type == REG_SZ && size as usize <= bytes.len() && size.is_multiple_of(2),
        "Autostart command changed while reading"
    );
    let units: Vec<u16> = bytes[..size as usize]
        .as_chunks::<2>()
        .0
        .iter()
        .map(|b| u16::from_le_bytes([b[0], b[1]]))
        .take_while(|c| *c != 0)
        .collect();
    Ok(Some(String::from_utf16(&units)?))
}
fn owns_command(command: &str, exe: &Path) -> bool {
    let path = exe.to_string_lossy();
    ordinal_ignore_case(command, &path) || ordinal_ignore_case(command, &format!("\"{path}\""))
}
fn set_auto_start_at(enabled: bool, exe: &Path, subkey: &str) -> Result<()> {
    let exe = full_path(exe)?;
    let path = exe.to_string_lossy();
    anyhow::ensure!(!path.contains('"'), "Invalid executable path");
    let subkey = wide(subkey);
    let mut key = HKEY::default();
    unsafe {
        Registry::RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey.as_ptr()),
            None,
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_QUERY_VALUE | KEY_SET_VALUE,
            None,
            &mut key,
            None,
        )
        .ok()?;
    }
    let key = OwnedKey(key);
    let current = read_command(key.0, AUTOSTART_VALUE)?;
    if let Some(command) = &current
        && !owns_command(command, &exe)
    {
        anyhow::ensure!(!enabled, "Autostart belongs to another installation");
        return Ok(());
    }
    let value_name = wide(AUTOSTART_VALUE);
    if enabled {
        let command = wide(format!("\"{path}\""));
        let bytes: Vec<u8> = command.into_iter().flat_map(u16::to_le_bytes).collect();
        unsafe {
            Registry::RegSetValueExW(
                key.0,
                PCWSTR(value_name.as_ptr()),
                None,
                REG_SZ,
                Some(&bytes),
            )
            .ok()?;
        }
    } else if current.is_some() {
        let status = unsafe { Registry::RegDeleteValueW(key.0, PCWSTR(value_name.as_ptr())) };
        if status != ERROR_FILE_NOT_FOUND {
            status.ok()?;
        }
    }
    Ok(())
}
pub fn set_auto_start(enabled: bool, exe: &Path) -> Result<()> {
    set_auto_start_at(enabled, exe, AUTOSTART_KEY)?;
    // Older installed versions used a Startup shortcut. Remove only a shortcut
    // whose resolved target is this executable; a foreign shortcut is preserved.
    if let Ok(directory) = known_folder(&FOLDERID_Startup) {
        let _ = remove_owned_shortcut(&directory.join("Net Lights.lnk"), exe);
    }
    Ok(())
}
struct ComApartment(bool);
impl Drop for ComApartment {
    fn drop(&mut self) {
        if self.0 {
            unsafe {
                CoUninitialize();
            }
        }
    }
}
fn remove_owned_shortcut(shortcut: &Path, exe: &Path) -> Result<()> {
    if !shortcut.is_file() {
        return Ok(());
    }
    let initialized = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    if initialized != RPC_E_CHANGED_MODE {
        initialized.ok()?;
    }
    let _apartment = ComApartment(initialized.is_ok());
    let link: IShellLinkW = unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)? };
    let persist: IPersistFile = link.cast()?;
    let name = wide(shortcut);
    unsafe {
        persist.Load(PCWSTR(name.as_ptr()), STGM_READ)?;
    }
    let mut target = vec![0u16; 32768];
    unsafe {
        link.GetPath(&mut target, std::ptr::null_mut(), SLGP_RAWPATH.0 as u32)?;
    }
    let length = target
        .iter()
        .position(|c| *c == 0)
        .context("The shortcut target is too long")?;
    let target = PathBuf::from(String::from_utf16(&target[..length])?);
    if !target.as_os_str().is_empty()
        && ordinal_ignore_case(
            &full_path(&target)?.to_string_lossy(),
            &full_path(exe)?.to_string_lossy(),
        )
    {
        drop(persist);
        drop(link);
        fs::remove_file(shortcut)?;
    }
    Ok(())
}
pub fn auto_start_enabled(exe: &Path) -> Result<bool> {
    let mut key = HKEY::default();
    let subkey = wide(AUTOSTART_KEY);
    let status = unsafe {
        Registry::RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey.as_ptr()),
            None,
            KEY_READ,
            &mut key,
        )
    };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(false);
    }
    status.ok()?;
    let key = OwnedKey(key);
    Ok(read_command(key.0, AUTOSTART_VALUE)?.is_some_and(|command| owns_command(&command, exe)))
}
pub fn open_settings(root: &Path) -> Result<()> {
    anyhow::ensure!(root.is_dir(), "The settings directory doesn't exist");
    let directory = wide(root);
    let result = unsafe {
        ShellExecuteW(
            None,
            w!("open"),
            PCWSTR(directory.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };
    anyhow::ensure!(
        result.0 as usize > 32,
        "Windows couldn't open the settings directory"
    );
    Ok(())
}

fn eligible_adapter(operational_status: i32, interface_type: u32) -> bool {
    operational_status == 1 && !matches!(interface_type, 24 | 131)
}
/// Matches NetworkInterface.GetIsNetworkAvailable(): an Up interface other
/// than software loopback or tunnel. This doesn't claim Internet connectivity.
pub fn network_available() -> bool {
    // A local inspection failure mustn't fabricate an explicit network outage.
    // Normal HTTPS evidence remains available in that conservative case.
    try_network_available().unwrap_or(true)
}
pub fn try_network_available() -> Result<bool> {
    use windows::Win32::NetworkManagement::IpHelper::{FreeMibTable, GetIfTable2, MIB_IF_TABLE2};
    struct Table(*mut MIB_IF_TABLE2);
    impl Drop for Table {
        fn drop(&mut self) {
            unsafe {
                FreeMibTable(self.0.cast());
            }
        }
    }
    let mut pointer = std::ptr::null_mut();
    unsafe {
        GetIfTable2(&mut pointer).ok()?;
    }
    anyhow::ensure!(
        !pointer.is_null(),
        "Windows didn't return the interface table"
    );
    let table = Table(pointer);
    let count = unsafe { (*table.0).NumEntries } as usize;
    anyhow::ensure!(
        count <= 4096,
        "The Windows interface table exceeds its bound"
    );
    let rows = unsafe { std::slice::from_raw_parts((*table.0).Table.as_ptr(), count) };
    Ok(rows
        .iter()
        .any(|row| eligible_adapter(row.OperStatus.0, row.Type)))
}

#[cfg(test)]
mod tests {
    use super::*;
    struct RegistryFixture(String);
    impl Drop for RegistryFixture {
        fn drop(&mut self) {
            assert!(
                self.0
                    .starts_with("Software\\NetLights.Tests\\native-autostart-")
            );
            let name = wide(&self.0);
            unsafe {
                let _ = Registry::RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(name.as_ptr()));
            }
        }
    }
    #[test]
    fn isolated_instance_forwards_requests_and_releases_ownership() {
        let profile = std::env::current_dir().unwrap().join(format!(
            "artifacts/platform-profile-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().unwrap()
        ));
        let owner = match acquire(Some(&profile), false, false).unwrap() {
            Startup::Owner(owner) => owner,
            Startup::Forwarded => panic!("A new isolated profile must own its mutex"),
        };
        let second_profile = profile.clone();
        std::thread::spawn(move || {
            assert!(matches!(
                acquire(Some(&second_profile), true, false).unwrap(),
                Startup::Forwarded
            ))
        })
        .join()
        .unwrap();
        assert!(owner.take_show_request());
        assert!(!owner.take_show_request());
        let second_profile = profile.clone();
        std::thread::spawn(move || {
            assert!(matches!(
                acquire(Some(&second_profile), false, true).unwrap(),
                Startup::Forwarded
            ))
        })
        .join()
        .unwrap();
        assert!(owner.take_exit_request());
        assert!(!owner.take_exit_request());
        drop(owner);
        assert!(matches!(
            acquire(Some(&profile), false, false).unwrap(),
            Startup::Owner(_)
        ));
        assert!(
            !profile.exists(),
            "Instance arbitration mustn't create or save a profile"
        );
    }
    #[test]
    fn live_profile_and_filesystem_aliases_are_rejected() {
        let live = default_profile().unwrap();
        assert!(isolated_profile(&live).is_err());
        assert!(isolated_profile(&live.join("child")).is_err());
        for text in [
            "relative",
            "C:\\profile.",
            "C:\\profile ",
            "C:\\profile:stream",
            "\\\\?\\C:\\profile",
            "\\\\localhost\\C$\\profile",
        ] {
            assert!(isolated_profile(Path::new(text)).is_err(), "{text}");
        }
    }
    #[test]
    fn isolated_profiles_reject_remote_unknown_and_optical_drive_types() {
        for kind in [0, 1, 4, 5, u32::MAX] {
            assert!(
                !local_profile_drive(kind),
                "Drive type {kind} cannot prove local isolation"
            );
        }
        for kind in [2, 3, 6] {
            assert!(local_profile_drive(kind));
        }
        // Use the existing local workspace without creating a drive, a mapping or a profile.
        let candidate = std::env::current_dir().unwrap().join(format!(
            "artifacts/local-drive-proof-{}",
            std::process::id()
        ));
        assert!(isolated_profile(&candidate).is_ok());
        assert!(!candidate.exists());
    }
    #[test]
    fn autostart_never_overwrites_another_installation() {
        let subkey = format!(
            "Software\\NetLights.Tests\\native-autostart-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().unwrap_or_default()
        );
        let _cleanup = RegistryFixture(subkey.clone());
        let ours = PathBuf::from("C:\\NetLightsTest\\ours.exe");
        let foreign = PathBuf::from("C:\\NetLightsTest\\foreign.exe");
        set_auto_start_at(true, &foreign, &subkey).unwrap();
        assert!(set_auto_start_at(true, &ours, &subkey).is_err());
        set_auto_start_at(false, &ours, &subkey).unwrap();
        let mut key = HKEY::default();
        let wide_key = wide(&subkey);
        unsafe {
            Registry::RegOpenKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(wide_key.as_ptr()),
                None,
                KEY_READ,
                &mut key,
            )
            .ok()
            .unwrap();
        }
        let key = OwnedKey(key);
        assert!(
            read_command(key.0, AUTOSTART_VALUE)
                .unwrap()
                .is_some_and(|s| owns_command(&s, &foreign))
        );
        drop(key);
        set_auto_start_at(false, &foreign, &subkey).unwrap();
        assert!(subkey.starts_with("Software\\NetLights.Tests\\native-autostart-"));
        unsafe {
            Registry::RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(wide_key.as_ptr()))
                .ok()
                .unwrap();
        }
    }
    #[test]
    fn update_health_name_is_narrow() {
        assert!(signal_health("Global\\arbitrary").is_err());
        assert!(signal_health("Local\\NetLights.UpdateHealth.not-a-guid").is_err());
    }
    #[test]
    fn unicode_distinct_installations_cannot_own_each_others_autostart_or_shortcut() {
        let identity = format!(
            "{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().unwrap()
        );
        let directory = std::env::temp_dir().join(format!("NetLightsOrdinalOwnership-{identity}"));
        let ours = directory.join("ſ").join("NetLights.exe");
        let foreign = directory.join("S").join("NetLights.exe");
        for path in [&ours, &foreign] {
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, b"owned fixture").unwrap();
        }
        assert_eq!(
            upper(&ours.to_string_lossy()),
            upper(&foreign.to_string_lossy()),
            "The fixture must collide under legacy hash casing"
        );
        assert!(!ordinal_ignore_case(
            &ours.to_string_lossy(),
            &foreign.to_string_lossy()
        ));
        assert!(ordinal_ignore_case(
            "C:\\NetLights\\NetLights.exe",
            "c:\\netlights\\netlights.EXE"
        ));
        let subkey = format!("Software\\NetLights.Tests\\native-autostart-{identity}");
        let _registry = RegistryFixture(subkey.clone());
        set_auto_start_at(true, &foreign, &subkey).unwrap();
        assert!(set_auto_start_at(true, &ours, &subkey).is_err());
        set_auto_start_at(false, &ours, &subkey).unwrap();
        let mut handle = HKEY::default();
        unsafe {
            Registry::RegOpenKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(wide(&subkey).as_ptr()),
                None,
                KEY_READ,
                &mut handle,
            )
            .ok()
            .unwrap();
        }
        let key = OwnedKey(handle);
        let command = read_command(key.0, AUTOSTART_VALUE).unwrap().unwrap();
        assert!(owns_command(&command, &foreign));
        assert!(!owns_command(&command, &ours));
        drop(key);
        let initialized = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
        if initialized != RPC_E_CHANGED_MODE {
            initialized.ok().unwrap();
        }
        let _apartment = ComApartment(initialized.is_ok());
        let shortcut = directory.join("Net Lights.lnk");
        {
            let link: IShellLinkW =
                unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).unwrap() };
            unsafe {
                link.SetPath(PCWSTR(wide(&foreign).as_ptr())).unwrap();
            }
            let persist: IPersistFile = link.cast().unwrap();
            unsafe {
                persist
                    .Save(PCWSTR(wide(&shortcut).as_ptr()), true)
                    .unwrap();
            }
        }
        let original = fs::read(&shortcut).unwrap();
        remove_owned_shortcut(&shortcut, &ours).unwrap();
        assert_eq!(fs::read(&shortcut).unwrap(), original);
        remove_owned_shortcut(&shortcut, &foreign).unwrap();
        assert!(!shortcut.exists());
        set_auto_start_at(false, &foreign, &subkey).unwrap();
        fs::remove_dir_all(directory).unwrap();
    }
    #[test]
    fn profile_identity_preserves_dotnet_invariant_casing_contract() {
        assert_eq!(upper("ßſıΐᾀ"), "ßSıΐᾈ");
        assert_ne!(upper("Straße"), upper("Strasse"));
    }
    #[test]
    fn network_filter_ignores_down_loopback_and_tunnel_interfaces() {
        assert!(!eligible_adapter(1, 24));
        assert!(!eligible_adapter(1, 131));
        assert!(!eligible_adapter(2, 6));
        assert!(!eligible_adapter(7, 71));
        assert!(eligible_adapter(1, 6));
        assert!(eligible_adapter(1, 71));
        assert!(
            eligible_adapter(1, 53),
            "An Up virtual interface is eligible under the .NET contract"
        );
    }
}
