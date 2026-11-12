use crate::metadata::Metadata;
use anyhow::{Context, Result, ensure};
use netlights_core::update::{ensure_unlinked, unique_name};
use serde::Serialize;
use std::{
    collections::BTreeSet,
    fs,
    path::{Path, PathBuf},
};
const PAYLOAD_DIRECTORY: &str = "payload";

pub struct Snapshot {
    installation: PathBuf,
    directory: PathBuf,
    files: Vec<PathBuf>,
    directories: BTreeSet<PathBuf>,
    metadata: Metadata,
    pub files_restored: bool,
    keep: bool,
}
impl Snapshot {
    pub fn create(executable: &Path, updates: &Path) -> Result<Self> {
        Self::create_using(executable, updates, Metadata::capture)
    }
    fn create_using(
        executable: &Path,
        updates: &Path,
        capture_metadata: impl FnOnce(&Path) -> Result<Metadata>,
    ) -> Result<Self> {
        ensure_unlinked(executable)?;
        ensure_unlinked(updates)?;
        let executable = fs::canonicalize(executable)?;
        let installation = executable
            .parent()
            .context("Missing installation directory")?
            .to_path_buf();
        ensure!(
            installation.join("unins000.exe").is_file(),
            "An installed copy is required"
        );
        let metadata = capture_metadata(&executable)?;
        let (files, directories) = enumerate(&installation, 100, 1000)?;
        let total = files.iter().try_fold(0u64, |sum, path| -> Result<u64> {
            sum.checked_add(fs::metadata(installation.join(path))?.len())
                .context("Installation size overflow")
        })?;
        ensure!(
            !files.is_empty() && total <= 256 * 1024 * 1024,
            "Installation exceeds snapshot limits"
        );
        fs::create_dir_all(updates)?;
        let updates = fs::canonicalize(updates)?;
        ensure!(
            !updates.starts_with(&installation),
            "Rollback cannot be inside the installation"
        );
        let directory = updates.join("rollback").join(unique_name());
        ensure_unlinked(&directory)?;
        fs::create_dir_all(&directory)?;
        let copied = (|| -> Result<()> {
            for file in &files {
                let source = installation.join(file);
                ensure_unlinked(&source)?;
                let target = directory.join(PAYLOAD_DIRECTORY).join(file);
                fs::create_dir_all(target.parent().unwrap())?;
                fs::copy(source, target)?;
            }
            metadata.recovery_copy(&directory)?;
            netlights_core::update::atomic_write(
                &directory.join("snapshot.json"),
                &serde_json::to_vec(&Recovery {
                    version: 2,
                    payload_directory: PAYLOAD_DIRECTORY,
                    installation: &installation,
                    files: &files,
                    directories: &directories,
                })?,
            )?;
            Ok(())
        })();
        if let Err(error) = copied {
            let _ = remove_owned(&directory);
            return Err(error);
        }
        Ok(Self {
            installation,
            directory,
            files,
            directories,
            metadata,
            files_restored: false,
            keep: true,
        })
    }
    pub fn restore(&mut self) -> Result<()> {
        self.files_restored = false;
        self.keep = true;
        ensure_unlinked(&self.installation)?;
        ensure_unlinked(&self.directory)?;
        fs::create_dir_all(&self.installation)?;
        ensure_unlinked(&self.installation)?;
        let (now, directories) = enumerate(&self.installation, 200, 1000)?;
        for file in now.iter().filter(|file| !self.files.contains(file)) {
            let path = self.installation.join(file);
            ensure_unlinked(&path)?;
            fs::remove_file(path)?;
        }
        let mut extras = directories
            .difference(&self.directories)
            .cloned()
            .collect::<Vec<_>>();
        extras.sort_by_key(|p| std::cmp::Reverse(p.components().count()));
        for directory in extras {
            let path = self.installation.join(directory);
            ensure_unlinked(&path)?;
            if fs::read_dir(&path)?.next().is_none() {
                fs::remove_dir(path)?;
            }
        }
        for directory in &self.directories {
            let path = self.installation.join(directory);
            ensure_unlinked(&path)?;
            fs::create_dir_all(&path)?;
            ensure_unlinked(&path)?;
        }
        // A partial installer may replace an old file with a directory. Remove
        // its now-empty, newly created directory before copying the old file.
        for file in &self.files {
            let source = self.directory.join(PAYLOAD_DIRECTORY).join(file);
            let destination = self.installation.join(file);
            ensure_unlinked(&source)?;
            ensure_unlinked(&destination)?;
            fs::create_dir_all(destination.parent().unwrap())?;
            fs::copy(source, destination)?;
        }
        self.files_restored = true;
        self.metadata.restore()?;
        self.keep = false;
        Ok(())
    }
    pub fn retain(&mut self) {
        self.keep = true;
    }
    pub fn commit(&mut self) {
        self.keep = false;
    }
}
#[derive(Serialize)]
struct Recovery<'a> {
    version: u32,
    payload_directory: &'static str,
    installation: &'a Path,
    files: &'a [PathBuf],
    directories: &'a BTreeSet<PathBuf>,
}
fn enumerate(
    root: &Path,
    maximum: usize,
    maximum_directories: usize,
) -> Result<(Vec<PathBuf>, BTreeSet<PathBuf>)> {
    ensure_unlinked(root)?;
    let mut pending = vec![root.to_path_buf()];
    let mut files = Vec::new();
    let mut directories = BTreeSet::new();
    while let Some(directory) = pending.pop() {
        ensure_unlinked(&directory)?;
        for entry in fs::read_dir(directory)? {
            let entry = entry?;
            let path = entry.path();
            ensure_unlinked(&path)?;
            let relative = path.strip_prefix(root)?.to_path_buf();
            if entry.file_type()?.is_dir() {
                ensure!(
                    directories.len() < maximum_directories,
                    "Too many installation directories"
                );
                directories.insert(relative);
                pending.push(path);
            } else {
                files.push(relative);
                ensure!(files.len() <= maximum, "Too many installation files");
            }
        }
    }
    Ok((files, directories))
}
fn remove_owned(directory: &Path) -> Result<()> {
    ensure_unlinked(directory)?;
    // The backup adds exactly one payload namespace around at most 1000
    // original directories and two control files around at most 100 files.
    let _ = enumerate(directory, 102, 1001)?;
    ensure!(
        directory.file_name().is_some_and(
            |n| n.len() == 32 && n.to_string_lossy().bytes().all(|b| b.is_ascii_hexdigit())
        ) && directory
            .parent()
            .and_then(|p| p.file_name())
            .is_some_and(|n| n == "rollback"),
        "Rollback directory isn't owned"
    );
    fs::remove_dir_all(directory)?;
    Ok(())
}
impl Drop for Snapshot {
    fn drop(&mut self) {
        if !self.keep {
            let _ = remove_owned(&self.directory);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn unregistered(executable: &Path) -> Metadata {
        serde_json::from_value(serde_json::json!({
            "executable":executable,"key_path":"unused-test-key","values":null,
            "shortcut_path":null,"shortcut":null
        }))
        .unwrap()
    }
    #[test]
    fn snapshot_control_names_cannot_overwrite_captured_installation_files() {
        let root =
            std::env::temp_dir().join(format!("NetLightsSnapshotNamespace-{}", unique_name()));
        let installation = root.join("installation");
        fs::create_dir_all(installation.join("payload")).unwrap();
        fs::create_dir_all(installation.join("empty/nested")).unwrap();
        let originals = [
            ("NetLights.exe", b"old application".as_slice()),
            ("unins000.exe", b"old uninstaller".as_slice()),
            ("snapshot.json", b"original snapshot name".as_slice()),
            (
                "installation-metadata.json",
                b"original metadata name".as_slice(),
            ),
            ("payload/keep.txt", b"original payload directory".as_slice()),
        ];
        for (name, bytes) in originals {
            fs::write(installation.join(name), bytes).unwrap();
        }
        let executable = installation.join("NetLights.exe");
        let mut snapshot = Snapshot::create_using(&executable, &root.join("updates"), |path| {
            // Exercise creation of both real control files without reading a registry key.
            Ok(serde_json::from_value(serde_json::json!({
                "executable":path,"key_path":"unused-test-key","values":{},
                "shortcut_path":null,"shortcut":null
            }))
            .unwrap())
        })
        .unwrap();
        for (name, _) in originals {
            fs::write(installation.join(name), b"installer replacement").unwrap();
        }
        fs::remove_dir_all(installation.join("empty")).unwrap();
        // Registry restoration is tested separately; this is a filesystem collision regression.
        snapshot.metadata = unregistered(&executable);
        snapshot.restore().unwrap();
        assert!(
            installation.join("empty/nested").is_dir(),
            "Captured empty installation directories weren't restored"
        );
        for (name, bytes) in originals {
            assert_eq!(
                fs::read(installation.join(name)).unwrap(),
                bytes,
                "Original {name} was overwritten by snapshot control metadata"
            );
        }
        drop(snapshot);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn restore_replaces_new_directory_colliding_with_an_original_file_before_copying() {
        for relative in ["LICENSE", "assets/icon.png"] {
            let root =
                std::env::temp_dir().join(format!("NetLightsSnapshotConflict-{}", unique_name()));
            let installation = root.join("installation");
            let backup = root.join("updates/rollback").join(unique_name());
            let file = PathBuf::from(relative);
            fs::create_dir_all(installation.join(&file)).unwrap();
            fs::write(installation.join(&file).join("added-notice.txt"), b"new").unwrap();
            fs::create_dir_all(backup.join(PAYLOAD_DIRECTORY).join(&file).parent().unwrap())
                .unwrap();
            fs::write(backup.join(PAYLOAD_DIRECTORY).join(&file), b"original").unwrap();
            let mut original_directories = BTreeSet::new();
            if let Some(parent) = file.parent().filter(|path| !path.as_os_str().is_empty()) {
                original_directories.insert(parent.to_path_buf());
            }
            // No registry access: this snapshot explicitly captured no owned metadata.
            let metadata: Metadata = serde_json::from_value(serde_json::json!({
                "executable": installation.join("NetLights.exe"),
                "key_path":"unused-test-key","values":null,
                "shortcut_path":null,"shortcut":null
            }))
            .unwrap();
            let mut snapshot = Snapshot {
                installation: installation.clone(),
                directory: backup,
                files: vec![file.clone()],
                directories: original_directories,
                metadata,
                files_restored: false,
                keep: true,
            };
            snapshot.restore().expect(
                "An installer-created directory mustn't prevent restoration of the original file",
            );
            assert!(snapshot.files_restored);
            assert_eq!(fs::read(installation.join(&file)).unwrap(), b"original");
            drop(snapshot);
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn restore_preserves_old_bytes_removes_new_files_and_does_not_touch_other_directory() {
        let root = std::env::temp_dir().join(format!("NetLightsSnapshot-{}", unique_name()));
        let installation = root.join("installation");
        fs::create_dir_all(&installation).unwrap();
        let executable = installation.join("NetLights.exe");
        fs::write(&executable, b"old").unwrap();
        fs::write(installation.join("unins000.exe"), b"uninstaller").unwrap();
        let other = root.join("other");
        fs::create_dir(&other).unwrap();
        fs::write(other.join("NetLights.exe"), b"unrelated").unwrap();
        let mut snapshot = Snapshot::create(&executable, &root.join("updates")).unwrap();
        fs::write(&executable, b"new").unwrap();
        fs::write(installation.join("new-file"), b"new").unwrap();
        snapshot.restore().unwrap();
        assert!(snapshot.files_restored);
        assert_eq!(fs::read(&executable).unwrap(), b"old");
        assert!(!installation.join("new-file").exists());
        assert_eq!(fs::read(other.join("NetLights.exe")).unwrap(), b"unrelated");
        drop(snapshot);
        fs::remove_dir_all(root).unwrap();
    }
}
