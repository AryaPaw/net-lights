use crate::win;
use anyhow::{Context, Result, ensure};
use serde::{Deserialize, Serialize};
use std::path::Path;

/// The filename and mutex keep the legacy identity protocol. Ownership must
/// retain the canonical spelling: invariant uppercase merges distinct paths.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct ActiveMarker {
    version: u32,
    installed_executable: String,
}

pub fn create(executable: &Path) -> Result<Vec<u8>> {
    let canonical = win::canonical(executable)?;
    let bytes = serde_json::to_vec(&ActiveMarker {
        version: 2,
        installed_executable: canonical
            .to_str()
            .context("Installation path must be valid Unicode")?
            .into(),
    })?;
    ensure!(
        bytes.len() <= 4096,
        "Transaction marker exceeds its size limit"
    );
    Ok(bytes)
}

pub fn require_owner(bytes: &[u8], executable: &Path) -> Result<()> {
    ensure!(
        bytes.len() <= 4096,
        "Transaction marker exceeds its size limit"
    );
    let marker: ActiveMarker = serde_json::from_slice(bytes)?;
    // V1 and torn bytes contain no trustworthy raw owner. A manual install in
    // another directory with the same legacy hash mustn't clear that state.
    ensure!(
        marker.version == 2,
        "Legacy or unsupported marker ownership cannot be proved"
    );
    let current = win::canonical(executable)?;
    ensure!(
        win::ordinal_paths_equal(Path::new(&marker.installed_executable), &current),
        "Transaction marker belongs to another installation"
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use netlights_core::update::unique_name;
    use std::fs;

    #[test]
    fn v2_marker_preserves_raw_owner_and_rejects_foreign_legacy_torn_and_duplicate_fields() {
        let root = std::env::temp_dir().join(format!("NetLightsMarkerOwner-{}", unique_name()));
        let own = root.join("ſ/NetLights.exe");
        let other = root.join("S/NetLights.exe");
        fs::create_dir_all(own.parent().unwrap()).unwrap();
        fs::create_dir_all(other.parent().unwrap()).unwrap();
        fs::write(&own, b"own").unwrap();
        fs::write(&other, b"other").unwrap();
        assert_eq!(fs::read(&own).unwrap(), b"own");
        let bytes = create(&own).unwrap();
        let parsed: ActiveMarker = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(parsed.version, 2);
        assert_eq!(
            parsed.installed_executable,
            win::canonical(&own).unwrap().to_str().unwrap()
        );
        require_owner(&bytes, &own).unwrap();
        assert!(require_owner(&bytes, &other).is_err());
        for invalid in [
            Vec::new(),
            b"{torn".to_vec(),
            b"null".to_vec(),
            serde_json::to_vec(
                &serde_json::json!({"Version":1,"InstalledExecutable":parsed.installed_executable}),
            )
            .unwrap(),
            br#"{"Version":2,"Version":2,"InstalledExecutable":"TARGET"}"#.to_vec(),
            format!("{}0{}", "[".repeat(150), "]".repeat(150)).into_bytes(),
        ] {
            assert!(require_owner(&invalid, &own).is_err());
        }
        fs::remove_dir_all(root).unwrap();
    }
}
