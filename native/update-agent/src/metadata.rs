use crate::win::wide;
use anyhow::{Context, Result, ensure};
use netlights_core::update::{PRODUCT_KEY, bounded_file, ensure_unlinked};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};
use windows::{
    Win32::{
        Foundation::*,
        Storage::FileSystem::WIN32_FIND_DATAW,
        System::{Com::*, Registry::*},
        UI::Shell::*,
    },
    core::{Interface, PCWSTR, PWSTR},
};

struct Key(HKEY);
impl Drop for Key {
    fn drop(&mut self) {
        let _ = unsafe { RegCloseKey(self.0) };
    }
}
#[derive(Serialize, Deserialize, Clone)]
struct Value {
    kind: u32,
    bytes: Vec<u8>,
}
#[derive(Serialize, Deserialize)]
pub struct Metadata {
    executable: PathBuf,
    key_path: String,
    values: Option<BTreeMap<String, Value>>,
    shortcut_path: Option<PathBuf>,
    shortcut: Option<Vec<u8>>,
}
impl Metadata {
    pub fn capture(executable: &Path) -> Result<Self> {
        Self::capture_key(executable, PRODUCT_KEY, None)
    }
    fn capture_key(executable: &Path, key_path: &str, programs: Option<PathBuf>) -> Result<Self> {
        let mut state = Self {
            executable: fs::canonicalize(executable)?,
            key_path: key_path.into(),
            values: None,
            shortcut_path: None,
            shortcut: None,
        };
        let Some(key) = open(key_path, KEY_READ)? else {
            return Ok(state);
        };
        if !owned(key.0, executable)? {
            return Ok(state);
        }
        let values = values(key.0)?;
        let group =
            string_value(&values, "Inno Setup: Icon Group").unwrap_or_else(|| "Net Lights".into());
        ensure!(
            !group.is_empty()
                && !Path::new(&group).is_absolute()
                && !group.contains(':')
                && group.split(['\\', '/']).all(|s| !s.is_empty()
                    && s != "."
                    && s != ".."
                    && !s.ends_with(['.', ' '])
                    && !s.contains(['<', '>', '"', '|', '?', '*', '\0'])),
            "Invalid Start Menu group"
        );
        let programs = programs.map(Ok).unwrap_or_else(programs_directory)?;
        ensure_unlinked(&programs)?;
        let shortcut = programs.join(group).join("Net Lights.lnk");
        ensure_unlinked(&shortcut)?;
        if shortcut.exists() {
            ensure!(
                same_path(&shortcut_target(&shortcut)?, executable),
                "Shortcut belongs to another installation"
            );
            state.shortcut = Some(bounded_file(&shortcut, 256 * 1024)?);
        }
        state.shortcut_path = Some(shortcut);
        state.values = Some(values);
        Ok(state)
    }
    pub fn recovery_copy(&self, directory: &Path) -> Result<()> {
        if self.values.is_some() {
            let file = directory.join("installation-metadata.json");
            netlights_core::update::atomic_write(&file, &serde_json::to_vec(self)?)?;
        }
        Ok(())
    }
    pub fn restore(&self) -> Result<()> {
        let Some(saved) = &self.values else {
            return Ok(());
        };
        if let Some(key) = open(&self.key_path, KEY_READ)? {
            ensure!(
                owned(key.0, &self.executable)?,
                "Uninstall registration was reassigned"
            );
        }
        if let Some(shortcut) = &self.shortcut_path {
            ensure_unlinked(shortcut)?;
            if shortcut.exists() {
                ensure!(
                    same_path(&shortcut_target(shortcut)?, &self.executable),
                    "Shortcut was reassigned"
                );
            }
        }
        let (key, created) = match open(&self.key_path, KEY_READ | KEY_WRITE)? {
            Some(key) => (key, false),
            None => {
                let mut key = HKEY::default();
                let mut disposition = REG_CREATE_KEY_DISPOSITION::default();
                unsafe {
                    RegCreateKeyExW(
                        HKEY_CURRENT_USER,
                        PCWSTR(wide(&self.key_path).as_ptr()),
                        Some(0),
                        PCWSTR::null(),
                        REG_OPTION_NON_VOLATILE,
                        KEY_READ | KEY_WRITE,
                        None,
                        &mut key,
                        Some(&mut disposition),
                    )
                    .ok()?;
                }
                (Key(key), disposition == REG_CREATED_NEW_KEY)
            }
        };
        // Recheck the writable handle after COM: it may have changed ownership.
        let current = values(key.0)?;
        ensure!(
            if created {
                current.is_empty()
            } else {
                owned(key.0, &self.executable)?
            },
            "Uninstall registration ownership changed"
        );
        for name in current
            .keys()
            .filter(|name| !saved.keys().any(|old| old.eq_ignore_ascii_case(name)))
        {
            unsafe {
                RegDeleteValueW(key.0, PCWSTR(wide(name).as_ptr())).ok()?;
            }
        }
        for (name, value) in saved {
            unsafe {
                RegSetValueExW(
                    key.0,
                    PCWSTR(wide(name).as_ptr()),
                    None,
                    REG_VALUE_TYPE(value.kind),
                    Some(&value.bytes),
                )
                .ok()?;
            }
        }
        if let Some(shortcut) = &self.shortcut_path {
            ensure_unlinked(shortcut)?;
            if let Some(bytes) = &self.shortcut {
                fs::create_dir_all(shortcut.parent().context("Missing shortcut directory")?)?;
                ensure_unlinked(shortcut)?;
                netlights_core::update::atomic_write(shortcut, bytes)?;
            } else if shortcut.exists() {
                fs::remove_file(shortcut)?;
            }
        }
        Ok(())
    }
}
fn open(path: &str, access: REG_SAM_FLAGS) -> Result<Option<Key>> {
    let mut key = HKEY::default();
    let status = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(wide(path).as_ptr()),
            Some(0),
            access,
            &mut key,
        )
    };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(None);
    }
    status.ok()?;
    Ok(Some(Key(key)))
}
fn values(key: HKEY) -> Result<BTreeMap<String, Value>> {
    let mut count = 0;
    let mut subkeys = 0;
    unsafe {
        RegQueryInfoKeyW(
            key,
            None,
            None,
            None,
            Some(&mut subkeys),
            None,
            None,
            Some(&mut count),
            None,
            None,
            None,
            None,
        )
        .ok()?;
    }
    ensure!(
        count <= 64 && subkeys == 0,
        "Uninstall registration exceeds its limits"
    );
    let mut entries = BTreeMap::new();
    let mut total = 0usize;
    for index in 0..count {
        let mut name = [0u16; 257];
        let mut name_size = 257u32;
        let mut bytes = vec![0u8; 65536];
        let mut size = bytes.len() as u32;
        let mut kind = 0;
        unsafe {
            RegEnumValueW(
                key,
                index,
                Some(PWSTR(name.as_mut_ptr())),
                &mut name_size,
                None,
                Some(&mut kind),
                Some(bytes.as_mut_ptr()),
                Some(&mut size),
            )
            .ok()?;
        }
        ensure!(
            name_size <= 256 && size <= 65536,
            "Uninstall value exceeds its limits"
        );
        bytes.truncate(size as usize);
        match REG_VALUE_TYPE(kind) {
            REG_SZ | REG_EXPAND_SZ => {
                ensure!(
                    bytes.len() <= 16386 && bytes.len().is_multiple_of(2),
                    "Uninstall text is invalid"
                );
                let _ = utf16(&bytes)?;
            }
            REG_DWORD => ensure!(bytes.len() == 4, "Uninstall DWORD is invalid"),
            REG_QWORD => ensure!(bytes.len() == 8, "Uninstall QWORD is invalid"),
            REG_BINARY => {}
            REG_MULTI_SZ => {
                let text = utf16(&bytes)?;
                ensure!(
                    text.split('\0').count() <= 66 && text.split('\0').all(|s| s.len() <= 8192),
                    "Uninstall string list exceeds its limits"
                );
            }
            _ => anyhow::bail!("Unsupported uninstall value type"),
        }
        total = total
            .checked_add(bytes.len() + name_size as usize * 2)
            .context("Uninstall state overflow")?;
        ensure!(
            total <= 128 * 1024,
            "Uninstall registration exceeds its size limit"
        );
        entries.insert(
            String::from_utf16(&name[..name_size as usize])?,
            Value { kind, bytes },
        );
    }
    Ok(entries)
}
fn utf16(bytes: &[u8]) -> Result<String> {
    ensure!(bytes.len().is_multiple_of(2), "Uninstall text is invalid");
    Ok(String::from_utf16(
        &bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect::<Vec<_>>(),
    )?)
}
fn string_value(values: &BTreeMap<String, Value>, name: &str) -> Option<String> {
    let value = values
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))?
        .1;
    if value.kind != REG_SZ.0 && value.kind != REG_EXPAND_SZ.0 {
        return None;
    }
    utf16(&value.bytes)
        .ok()
        .map(|s| s.trim_end_matches('\0').to_owned())
}
fn same_path(left: &Path, right: &Path) -> bool {
    fs::canonicalize(left)
        .ok()
        .zip(fs::canonicalize(right).ok())
        .is_some_and(|(a, b)| crate::win::ordinal_paths_equal(&a, &b))
}
fn owned(key: HKEY, executable: &Path) -> Result<bool> {
    let all = values(key)?;
    let Some(location) = string_value(&all, "InstallLocation") else {
        return Ok(false);
    };
    let Some(command) = string_value(&all, "UninstallString") else {
        return Ok(false);
    };
    let uninstaller = PathBuf::from(command.trim().trim_matches('"'));
    Ok(executable
        .parent()
        .is_some_and(|p| same_path(Path::new(&location), p))
        && uninstaller
            .parent()
            .is_some_and(|p| same_path(p, Path::new(&location)))
        && uninstaller.file_name().is_some_and(|s| {
            s.to_string_lossy()
                .to_ascii_lowercase()
                .starts_with("unins")
        })
        && uninstaller
            .extension()
            .is_some_and(|s| s.eq_ignore_ascii_case("exe")))
}
fn programs_directory() -> Result<PathBuf> {
    let pointer = unsafe { SHGetKnownFolderPath(&FOLDERID_Programs, KF_FLAG_DEFAULT, None)? };
    let result = unsafe { pointer.to_string() };
    unsafe {
        CoTaskMemFree(Some(pointer.0 as _));
    }
    Ok(PathBuf::from(result?))
}
fn shortcut_target(path: &Path) -> Result<PathBuf> {
    let link: IShellLinkW = unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)? };
    let persistent: IPersistFile = link.cast()?;
    unsafe {
        persistent.Load(PCWSTR(wide(path).as_ptr()), STGM_READ)?;
    }
    let mut target = [0u16; 32768];
    let mut data = WIN32_FIND_DATAW::default();
    unsafe {
        link.GetPath(&mut target, &mut data, SLGP_RAWPATH.0 as u32)?;
    }
    let end = target
        .iter()
        .position(|c| *c == 0)
        .context("Shortcut target exceeds its size limit")?;
    Ok(PathBuf::from(String::from_utf16(&target[..end])?))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn write_string(key: HKEY, name: &str, text: &str) {
        let bytes = wide(text)
            .into_iter()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>();
        unsafe {
            RegSetValueExW(key, PCWSTR(wide(name).as_ptr()), None, REG_SZ, Some(&bytes))
                .ok()
                .unwrap();
        }
    }
    struct OwnedFixture {
        root: PathBuf,
        key: String,
        executable: PathBuf,
    }
    impl OwnedFixture {
        fn new() -> Self {
            let root = std::env::temp_dir().join(format!(
                "NetLightsMetadataOwned-{}",
                netlights_core::update::unique_name()
            ));
            fs::create_dir(&root).unwrap();
            let executable = root.join("NetLights.exe");
            fs::write(&executable, b"application").unwrap();
            fs::write(root.join("unins000.exe"), b"uninstaller").unwrap();
            let key = format!(
                "Software\\NetLights.Tests\\{}",
                netlights_core::update::unique_name()
            );
            let mut handle = HKEY::default();
            unsafe {
                RegCreateKeyExW(
                    HKEY_CURRENT_USER,
                    PCWSTR(wide(&key).as_ptr()),
                    Some(0),
                    PCWSTR::null(),
                    REG_OPTION_NON_VOLATILE,
                    KEY_ALL_ACCESS,
                    None,
                    &mut handle,
                    None,
                )
                .ok()
                .unwrap();
            }
            let handle = Key(handle);
            write_string(handle.0, "InstallLocation", root.to_str().unwrap());
            write_string(
                handle.0,
                "UninstallString",
                &format!("\"{}\"", root.join("unins000.exe").display()),
            );
            write_string(handle.0, "DisplayVersion", "1.5.0");
            Self {
                root,
                key,
                executable,
            }
        }
        fn snapshot(&self) -> Metadata {
            Metadata::capture_key(
                &self.executable,
                &self.key,
                Some(self.root.join("MissingPrograms")),
            )
            .unwrap()
        }
        fn delete_key(&self) {
            assert!(self.key.starts_with("Software\\NetLights.Tests\\"));
            unsafe {
                RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(wide(&self.key).as_ptr()))
                    .ok()
                    .unwrap();
            }
        }
    }
    impl Drop for OwnedFixture {
        fn drop(&mut self) {
            let _ = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(wide(&self.key).as_ptr())) };
            let _ = fs::remove_dir_all(&self.root);
        }
    }
    #[test]
    fn owned_registration_restores_values_and_missing_shortcut_directory_is_not_an_error() {
        let fixture = OwnedFixture::new();
        let snapshot = fixture.snapshot();
        assert!(snapshot.values.is_some());
        {
            let key = open(&fixture.key, KEY_WRITE).unwrap().unwrap();
            write_string(key.0, "DisplayVersion", "9.0.0");
            write_string(key.0, "AddedByInstaller", "added");
        }
        snapshot.restore().unwrap();
        let key = open(&fixture.key, KEY_READ).unwrap().unwrap();
        let restored = values(key.0).unwrap();
        assert_eq!(string_value(&restored, "DisplayVersion").unwrap(), "1.5.0");
        assert!(!restored.contains_key("AddedByInstaller"));
        assert!(!fixture.root.join("MissingPrograms").exists());
    }
    #[test]
    fn deleted_owned_registration_is_recreated_but_foreign_reassignment_is_never_overwritten() {
        let fixture = OwnedFixture::new();
        let snapshot = fixture.snapshot();
        fixture.delete_key();
        snapshot.restore().unwrap();
        {
            let key = open(&fixture.key, KEY_READ | KEY_WRITE).unwrap().unwrap();
            assert_eq!(
                string_value(&values(key.0).unwrap(), "DisplayVersion").unwrap(),
                "1.5.0"
            );
            write_string(key.0, "InstallLocation", "C:\\different-installation");
            write_string(key.0, "DisplayVersion", "9.0.0");
        }
        assert!(snapshot.restore().is_err());
        let key = open(&fixture.key, KEY_READ).unwrap().unwrap();
        let current = values(key.0).unwrap();
        assert_eq!(
            string_value(&current, "InstallLocation").unwrap(),
            "C:\\different-installation"
        );
        assert_eq!(string_value(&current, "DisplayVersion").unwrap(), "9.0.0");
    }
    #[test]
    fn absent_registration_does_not_acquire_ownership_or_create_it_on_restore() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsMetadata-{}",
            netlights_core::update::unique_name()
        ));
        fs::create_dir(&root).unwrap();
        let executable = root.join("NetLights.exe");
        fs::write(&executable, b"fixture").unwrap();
        let key = format!(
            "Software\\NetLights.Tests\\{}",
            netlights_core::update::unique_name()
        );
        let snapshot =
            Metadata::capture_key(&executable, &key, Some(root.join("Programs"))).unwrap();
        assert!(snapshot.values.is_none());
        snapshot.restore().unwrap();
        assert!(open(&key, KEY_READ).unwrap().is_none());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn ownership_does_not_merge_distinct_unicode_installations() {
        let root = std::env::temp_dir().join(format!(
            "NetLightsUnicodeOwner-{}",
            netlights_core::update::unique_name()
        ));
        let first = root.join("Straße");
        let other = root.join("Strasse");
        fs::create_dir_all(&first).unwrap();
        fs::create_dir_all(&other).unwrap();
        let longs = root.join("ſ");
        let ascii = root.join("S");
        fs::create_dir(&longs).unwrap();
        fs::create_dir(&ascii).unwrap();
        assert!(!same_path(&first, &other));
        assert!(!same_path(&longs, &ascii));
        fs::remove_dir_all(root).unwrap();
    }
}
