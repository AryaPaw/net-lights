use super::State;
use anyhow::Result;
use chrono::Utc;
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};

pub(super) fn write(state: &State, root: &Path) -> Result<PathBuf> {
    let directory = root.join("exports");
    fs::create_dir_all(&directory)?;
    let path = directory.join(format!(
        "NetLights-diagnostic-{}.zip",
        Utc::now().format("%Y%m%d-%H%M%S")
    ));
    let file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)?;
    struct Incomplete(PathBuf, bool);
    impl Drop for Incomplete {
        fn drop(&mut self) {
            if !self.1 {
                let _ = fs::remove_file(&self.0);
            }
        }
    }
    let mut owned = Incomplete(path.clone(), false);
    let mut archive = zip::ZipWriter::new(file);
    let groups: Vec<_> = state.monitor.groups.iter().map(|g| serde_json::json!({"group": g.group, "availability":g.availability,
        "endpoints":g.endpoints.iter().enumerate().map(|(index,e)| serde_json::json!({"index":index,"outcome":e.outcome,"httpStatus":e.http_status,"fresh":e.fresh,"elapsedMs":e.elapsed_ms})).collect::<Vec<_>>()})).collect();
    for (name, data) in [
        (
            "snapshot.json",
            serde_json::json!({"groups":groups,"paused":state.monitor.paused}),
        ),
        (
            "versions.json",
            serde_json::json!({"version":netlights_core::VERSION,"runtime":"Rust"}),
        ),
        ("events.json", serde_json::json!(state.events)),
    ] {
        archive.start_file(name, zip::write::SimpleFileOptions::default())?;
        archive.write_all(&serde_json::to_vec_pretty(&data)?)?;
    }
    archive.finish()?.sync_all()?;
    owned.1 = true;
    Ok(path)
}
