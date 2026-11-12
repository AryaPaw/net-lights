//! Release selection and the installation handoff. Installation itself belongs to the agent.
use crate::transport::{DownloadStep, Transport};
use anyhow::{Context, Result, bail, ensure};
use chrono::{DateTime, Duration as ChronoDuration, Utc};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    time::Duration,
};
use url::Url;

pub const MAX_MANIFEST: usize = 64 * 1024;
pub const MAX_INSTALLER: u64 = 80 * 1024 * 1024;
pub const ASSET_PREFIX: &str = "NetLights-Setup-win-x64-";
pub const PRODUCT_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{7C2B1A9E-4F31-4A6D-9C2E-1B8E5D2A0F11}_is1";
pub const UNRESOLVED_TRANSACTION_NOTICE: &str = "Сохранена отметка незавершённого обновления. Без подтверждения владельца автоматически удалять её небезопасно. Данные не изменены. Повторный запуск установщика не гарантирует восстановление; нужна проверка этой отметки.";

#[derive(Debug)]
pub struct UnresolvedTransaction;
impl std::fmt::Display for UnresolvedTransaction {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(UNRESOLVED_TRANSACTION_NOTICE)
    }
}
impl std::error::Error for UnresolvedTransaction {}

/// Compatibility with the pre-Rust .NET string.ToUpperInvariant identity protocol.
/// Keep this mapping stable: full Unicode uppercase can merge distinct Windows paths.
pub fn legacy_invariant_upper(value: &str) -> String {
    value
        .chars()
        .map(|c| {
            LEGACY_INVARIANT_UPPER
                .binary_search_by_key(&(c as u32), |pair| pair.0)
                .ok()
                .and_then(|i| char::from_u32(LEGACY_INVARIANT_UPPER[i].1))
                .unwrap_or(c)
        })
        .collect()
}
pub fn installation_identity(executable: &Path) -> Result<(String, String)> {
    ensure_unlinked(executable)?;
    let canonical = fs::canonicalize(executable)?;
    let path = legacy_invariant_upper(
        canonical
            .to_str()
            .context("Installation path must be valid Unicode")?,
    );
    let identity = format!("{:X}", Sha256::digest(path.as_bytes()));
    Ok((path, identity))
}

#[derive(Clone, Debug)]
pub struct Release {
    pub version: String,
    pub url: Url,
    pub sha256: String,
}

pub fn version(value: &str) -> Option<[u32; 4]> {
    let value = value
        .trim()
        .trim_start_matches(['v', 'V'])
        .split('+')
        .next()?;
    let parts = value.split('.').collect::<Vec<_>>();
    if !(2..=4).contains(&parts.len()) {
        return None;
    }
    let mut parsed = [0; 4];
    for (i, part) in parts.iter().enumerate() {
        if part.is_empty() || !part.bytes().all(|b| b.is_ascii_digit()) {
            return None;
        }
        parsed[i] = part.parse().ok()?;
    }
    Some(parsed)
}
pub fn safe_filename(name: &str) -> bool {
    name.starts_with(ASSET_PREFIX)
        && name.ends_with(".exe")
        && version(&name[ASSET_PREFIX.len()..name.len() - 4]).is_some()
        && !name.contains(['/', '\\', ':', '\0'])
        && !name.contains("..")
}
pub fn allowed_download(url: &Url, redirect: bool) -> bool {
    if url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some_and(|p| p != 443)
    {
        return false;
    }
    match url.host_str() {
        Some("github.com") => {
            url.path()
                .starts_with("/AryaPaw/net-lights/releases/download/")
                && !url.path().contains("..")
        }
        Some(
            "objects.githubusercontent.com"
            | "release-assets.githubusercontent.com"
            | "github-releases.githubusercontent.com",
        ) => redirect,
        _ => false,
    }
}
pub async fn find_update(transport: &Transport, current: &str) -> Result<Option<Release>> {
    let current = version(current).context("Invalid current version")?;
    let response = transport
        .get(
            &Url::parse("https://api.github.com/repos/AryaPaw/net-lights/releases/latest")?,
            MAX_MANIFEST,
            Duration::from_secs(20),
        )
        .await?;
    if response.status == 404 {
        return Ok(None);
    }
    ensure!(response.status == 200, "Release query failed");
    parse_release(&response.body, current)
}
fn parse_release(bytes: &[u8], current: [u32; 4]) -> Result<Option<Release>> {
    ensure!(
        bytes.len() <= MAX_MANIFEST,
        "Manifest exceeds its size limit"
    );
    let doc: serde_json::Value = serde_json::from_slice(bytes)?;
    if doc
        .get("prerelease")
        .and_then(|v| v.as_bool())
        .unwrap_or(true)
        || doc.get("draft").and_then(|v| v.as_bool()).unwrap_or(false)
    {
        return Ok(None);
    }
    let tag = doc
        .get("tag_name")
        .and_then(|v| v.as_str())
        .context("Release version is missing")?;
    let next = version(tag).context("Invalid release version")?;
    if next <= current {
        return Ok(None);
    }
    let version = tag.trim_start_matches(['v', 'V']).to_owned();
    // A release build suffix isn't part of the package identity.
    ensure!(
        !version.contains('+'),
        "Release tag cannot contain build metadata"
    );
    let expected = format!("{ASSET_PREFIX}{version}.exe");
    let assets = doc
        .get("assets")
        .and_then(|v| v.as_array())
        .context("Release assets are missing")?;
    let matching = assets
        .iter()
        .filter(|a| a.get("name").and_then(|v| v.as_str()) == Some(&expected))
        .collect::<Vec<_>>();
    ensure!(
        matching.len() == 1,
        "Release must contain exactly one matching installer"
    );
    let asset = matching[0];
    let size = asset
        .get("size")
        .and_then(|v| v.as_u64())
        .context("Installer size is missing")?;
    ensure!(
        size > 0 && size <= MAX_INSTALLER,
        "Installer size is invalid"
    );
    let url = Url::parse(
        asset
            .get("browser_download_url")
            .and_then(|v| v.as_str())
            .context("Installer URL is missing")?,
    )?;
    ensure!(allowed_download(&url, false), "Installer URL isn't allowed");
    let hash = asset
        .get("digest")
        .and_then(|v| v.as_str())
        .and_then(|s| s.strip_prefix("sha256:"))
        .context("Installer digest is missing")?;
    ensure!(valid_hash(hash), "Installer digest is invalid");
    Ok(Some(Release {
        version,
        url,
        sha256: hash.to_ascii_lowercase(),
    }))
}

pub fn ensure_unlinked(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        if let Ok(meta) = fs::symlink_metadata(ancestor) {
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                ensure!(
                    meta.file_attributes() & 0x400 == 0,
                    "Linked update path rejected"
                );
            }
            ensure!(
                !meta.file_type().is_symlink(),
                "Linked update path rejected"
            );
        }
    }
    Ok(())
}
pub async fn stage_update(
    transport: &Transport,
    release: &Release,
    updates_root: &Path,
) -> Result<PathBuf> {
    let name = format!("{ASSET_PREFIX}{}.exe", release.version);
    ensure!(
        safe_filename(&name) && valid_hash(&release.sha256),
        "Invalid update identity"
    );
    ensure_unlinked(updates_root)?;
    fs::create_dir_all(updates_root)?;
    let root = fs::canonicalize(updates_root)?;
    let directory = root.join(unique_name());
    fs::create_dir(&directory)?;
    let target = directory.join(name);
    let mut pending = PendingStage {
        directory: directory.clone(),
        armed: true,
    };
    atomic_write(
        &directory.join(".stage.json"),
        &serde_json::to_vec(&StageReceipt {
            schema: 1,
            owner: "NetLights".into(),
            installer: target.file_name().unwrap().to_string_lossy().into_owned(),
        })?,
    )?;
    let result = async {
        let mut url = release.url.clone();
        for hop in 0..=5 {
            ensure!(
                allowed_download(&url, hop > 0),
                "Download redirect isn't allowed"
            );
            match transport
                .download_step(&url, &target, MAX_INSTALLER, Duration::from_secs(120))
                .await?
            {
                DownloadStep::Redirect(next) => {
                    ensure!(hop < 5, "Too many installer redirects");
                    url = next;
                }
                DownloadStep::Complete { bytes, sha256 } => {
                    ensure!(
                        bytes > 0 && bytes <= MAX_INSTALLER,
                        "Installer size is invalid"
                    );
                    ensure!(
                        sha256.eq_ignore_ascii_case(&release.sha256),
                        "Installer checksum mismatch"
                    );
                    return Ok(target.clone());
                }
            }
        }
        bail!("Too many installer redirects")
    }
    .await;
    if result.is_ok() {
        pending.armed = false;
    }
    result
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct StageReceipt {
    schema: u32,
    owner: String,
    installer: String,
}
struct PendingStage {
    directory: PathBuf,
    armed: bool,
}
impl Drop for PendingStage {
    fn drop(&mut self) {
        if self.armed && cleanup_owned_stage(&self.directory).is_err() {
            let _ = fs::remove_dir(&self.directory);
        }
    }
}
fn cleanup_owned_stage(directory: &Path) -> Result<()> {
    ensure_unlinked(directory)?;
    let name = directory
        .file_name()
        .context("Staging name is missing")?
        .to_str()
        .context("Staging name isn't Unicode")?;
    ensure!(
        name.len() == 32 && name.bytes().all(|b| b.is_ascii_hexdigit()),
        "Staging directory isn't owned"
    );
    let receipt_path = directory.join(".stage.json");
    let receipt: StageReceipt = serde_json::from_slice(&bounded_file(&receipt_path, 4096)?)?;
    ensure!(
        receipt.schema == 1 && receipt.owner == "NetLights" && safe_filename(&receipt.installer),
        "Staging receipt isn't supported"
    );
    let mut entries = fs::read_dir(directory)?;
    let mut files = Vec::new();
    for _ in 0..4 {
        let Some(entry) = entries.next() else { break };
        let entry = entry?;
        let path = entry.path();
        ensure_unlinked(&path)?;
        let file = entry.file_name();
        ensure!(
            entry.file_type()?.is_file()
                && (file == ".stage.json"
                    || file == "NetLights.UpdateAgent.exe"
                    || file == receipt.installer.as_str()),
            "Unexpected staging contents are preserved"
        );
        files.push(path);
    }
    ensure!(
        files.len() <= 3 && entries.next().is_none(),
        "Staging contains too many files"
    );
    // Keep the receipt until every payload has gone. A locked running agent is
    // retried on a later sweep instead of losing proof of directory ownership.
    for file in files
        .iter()
        .filter(|path| path.file_name().is_some_and(|n| n != ".stage.json"))
    {
        ensure_unlinked(file)?;
        fs::remove_file(file)?;
    }
    ensure_unlinked(&receipt_path)?;
    fs::remove_file(receipt_path)?;
    fs::remove_dir(directory)?;
    Ok(())
}
pub fn cleanup_staged_update(setup: &Path, updates_root: &Path) -> Result<()> {
    ensure_unlinked(updates_root)?;
    let root = fs::canonicalize(updates_root)?;
    let directory = setup.parent().context("Staged installer has no parent")?;
    let directory = fs::canonicalize(directory)?;
    ensure!(
        directory.parent() == Some(root.as_path()),
        "Staging cleanup isn't directly inside its update root"
    );
    cleanup_owned_stage(&directory)
}
pub fn cleanup_old_staging(updates_root: &Path, minimum_age: Duration) -> Result<usize> {
    if !updates_root.exists() {
        return Ok(0);
    }
    ensure_unlinked(updates_root)?;
    let root = fs::canonicalize(updates_root)?;
    let entries = fs::read_dir(&root)?
        .take(513)
        .collect::<std::io::Result<Vec<_>>>()?;
    if entries.len() > 512 {
        return Ok(0);
    }
    // An active or interrupted transaction owns its files until resolved.
    if entries.iter().any(|entry| {
        entry
            .file_name()
            .to_str()
            .is_some_and(|name| name.starts_with("active-") && name.ends_with(".json"))
    }) {
        return Ok(0);
    }
    let mut removed = 0;
    for entry in entries {
        let path = entry.path();
        if !entry.file_type()?.is_dir() {
            continue;
        }
        let age = std::time::SystemTime::now()
            .duration_since(entry.metadata()?.modified()?)
            .unwrap_or_default();
        if age >= minimum_age && cleanup_owned_stage(&path).is_ok() {
            removed += 1;
        }
    }
    Ok(removed)
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FailedCandidate {
    version: String,
    sha256: String,
    attempts: u8,
    retry_after_utc: DateTime<Utc>,
}
pub fn valid_hash(hash: &str) -> bool {
    hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit())
}
pub fn retry_allowed(root: &Path, release: &Release) -> bool {
    let path = root.join("failed-candidate.json");
    if ensure_unlinked(&path).is_err() {
        return false;
    }
    match path.try_exists() {
        Ok(false) => return true,
        Err(_) => return false,
        Ok(true) => {}
    }
    let Ok(value) = read_retry(&path) else {
        return false;
    };
    version(&value.version) != version(&release.version)
        || !value.sha256.eq_ignore_ascii_case(&release.sha256)
        || Utc::now() >= value.retry_after_utc
}
/// Reject unavailable or interrupted installations before downloading a payload.
/// Only the installation agent may resolve a durable transaction marker.
pub fn preflight(root: &Path, restart: &Path) -> Result<()> {
    ensure!(
        restart.is_absolute()
            && restart.is_file()
            && restart
                .file_name()
                .is_some_and(|name| name.eq_ignore_ascii_case("NetLights.exe")),
        "Invalid installed application"
    );
    let directory = restart
        .parent()
        .context("Installation directory is missing")?;
    for file in [
        restart.to_path_buf(),
        directory.join("unins000.exe"),
        directory.join("NetLights.UpdateAgent.exe"),
    ] {
        ensure_unlinked(&file)?;
        ensure!(
            file.is_file(),
            "Установленная копия неполна. Повторно запустите установщик Net Lights."
        );
    }
    let (_, identity) = installation_identity(restart)?;
    let marker = root.join(format!("active-{identity}.json"));
    ensure_unlinked(&marker)?;
    if marker.try_exists()? {
        return Err(UnresolvedTransaction.into());
    }
    Ok(())
}
fn read_retry(path: &Path) -> Result<FailedCandidate> {
    let value: FailedCandidate = serde_json::from_slice(&bounded_file(path, 4096)?)?;
    ensure!(
        version(&value.version).is_some()
            && valid_hash(&value.sha256)
            && (1..=8).contains(&value.attempts),
        "Invalid update retry state"
    );
    Ok(value)
}
pub fn record_failure(root: &Path, candidate: &str, sha: &str) -> Result<()> {
    ensure!(
        version(candidate).is_some() && valid_hash(sha),
        "Invalid failed update identity"
    );
    let attempts = read_retry(&root.join("failed-candidate.json"))
        .ok()
        .filter(|old| {
            version(&old.version) == version(candidate) && old.sha256.eq_ignore_ascii_case(sha)
        })
        .map_or(1, |old| old.attempts.saturating_add(1).min(8));
    let hours = (12i64 * (1i64 << (attempts - 1))).min(168);
    atomic_write(
        &root.join("failed-candidate.json"),
        &serde_json::to_vec(&FailedCandidate {
            version: candidate.into(),
            sha256: sha.to_ascii_lowercase(),
            attempts,
            retry_after_utc: Utc::now() + ChronoDuration::hours(hours),
        })?,
    )
}
pub fn write_result(root: &Path, candidate: &str, success: bool, message: &str) -> Result<()> {
    atomic_write(
        &root.join("result.json"),
        &serde_json::to_vec(
            &serde_json::json!({"version":candidate,"success":success,"message":message,"utc":Utc::now()}),
        )?,
    )
}
pub fn phase(root: &Path, code: &str) {
    let result = (|| -> Result<()> {
        fs::create_dir_all(root)?;
        let path = root.join("phases.jsonl");
        if path.metadata().is_ok_and(|m| m.len() > 2 * 1024 * 1024) {
            fs::remove_file(&path)?;
        }
        let mut file = OpenOptions::new().append(true).create(true).open(path)?;
        writeln!(
            file,
            "{}",
            serde_json::json!({"utc":Utc::now(),"pid":std::process::id(),"phase":code})
        )?;
        Ok(())
    })();
    let _ = result;
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path.parent().context("Update file needs a directory")?;
    ensure_unlinked(parent)?;
    fs::create_dir_all(parent)?;
    let temporary = parent.join(format!("{}.tmp", unique_name()));
    let result = (|| -> Result<()> {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        ensure_unlinked(path)?;
        fs::rename(&temporary, path)?;
        Ok(())
    })();
    let _ = fs::remove_file(temporary);
    result
}
pub fn bounded_file(path: &Path, limit: u64) -> Result<Vec<u8>> {
    let mut file = File::open(path)?;
    ensure!(
        file.metadata()?.len() <= limit,
        "Update state exceeds its size limit"
    );
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take(limit + 1)
        .read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() as u64 <= limit,
        "Update state grew beyond its size limit"
    );
    Ok(bytes)
}
pub fn file_hash(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut hash = Sha256::new();
    let mut buffer = [0u8; 32768];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
    }
    Ok(format!("{:x}", hash.finalize()))
}
pub fn unique_name() -> String {
    // Random Win32 event identifiers must remain compatible with Guid.TryParseExact("N").
    #[cfg(windows)]
    {
        use windows::Win32::System::Com::CoCreateGuid;
        if let Ok(guid) = unsafe { CoCreateGuid() } {
            return format!("{:032x}", guid.to_u128());
        }
    }
    format!(
        "{:032x}",
        Utc::now().timestamp_nanos_opt().unwrap_or_default() as u128
            ^ ((std::process::id() as u128) << 64)
    )
}

#[cfg(windows)]
pub fn start_agent(setup: &Path, sha: &str, restart: &Path) -> Result<()> {
    use std::os::windows::process::CommandExt;
    use windows::{
        Win32::{
            Foundation::{CloseHandle, WAIT_OBJECT_0},
            System::Threading::{CreateEventW, WaitForSingleObject},
        },
        core::PCWSTR,
    };
    ensure!(
        valid_hash(sha)
            && setup.is_file()
            && restart.is_file()
            && restart
                .file_name()
                .is_some_and(|n| n.eq_ignore_ascii_case("NetLights.exe")),
        "Invalid updater handoff"
    );
    ensure_unlinked(setup)?;
    ensure_unlinked(restart)?;
    ensure!(
        restart
            .parent()
            .is_some_and(|p| p.join("unins000.exe").is_file()),
        "Обновление через установщик доступно только установленной версии."
    );
    let expected_root =
        PathBuf::from(std::env::var_os("LOCALAPPDATA").context("LOCALAPPDATA isn't available")?)
            .join("NetLights/updates");
    preflight(&expected_root, restart)?;
    let root = fs::canonicalize(expected_root)?;
    let setup = fs::canonicalize(setup)?;
    ensure!(
        setup.starts_with(&root) && setup.parent() != Some(root.as_path()),
        "Installer must belong to its staged directory"
    );
    let installed_agent = restart
        .parent()
        .context("Application directory isn't available")?
        .join("NetLights.UpdateAgent.exe");
    let staged_agent = setup.parent().unwrap().join("NetLights.UpdateAgent.exe");
    ensure_unlinked(&installed_agent)?;
    ensure_unlinked(&staged_agent)?;
    fs::copy(installed_agent, &staged_agent)?;
    let event_name = format!("Local\\NetLights.UpdateAgentReady.{}", unique_name());
    let wide: Vec<u16> = event_name.encode_utf16().chain(Some(0)).collect();
    let event = unsafe { CreateEventW(None, true, false, PCWSTR(wide.as_ptr()))? };
    let outcome = (|| -> Result<()> {
        let mut agent = std::process::Command::new(&staged_agent)
            .args(["--parent", &std::process::id().to_string(), "--pending"])
            .arg(&setup)
            .args(["--sha256", sha, "--restart"])
            .arg(restart)
            .args(["--ready", &event_name])
            .creation_flags(0x08000000)
            .spawn()?;
        let ready = unsafe { WaitForSingleObject(event, 5000) } == WAIT_OBJECT_0
            && agent.try_wait()?.is_none();
        if !ready {
            let _ = agent.kill();
            let _ = agent.wait();
            bail!(
                "Обновление не началось: требуется ручная установка или восстановление незавершённой установки."
            );
        }
        Ok(())
    })();
    let _ = unsafe { CloseHandle(event) };
    outcome
}

// Frozen compatibility mapping for the existing .NET invariant-uppercase identity protocol.
const LEGACY_INVARIANT_UPPER: &[(u32, u32)] = &[
    (0x61, 0x41),
    (0x62, 0x42),
    (0x63, 0x43),
    (0x64, 0x44),
    (0x65, 0x45),
    (0x66, 0x46),
    (0x67, 0x47),
    (0x68, 0x48),
    (0x69, 0x49),
    (0x6A, 0x4A),
    (0x6B, 0x4B),
    (0x6C, 0x4C),
    (0x6D, 0x4D),
    (0x6E, 0x4E),
    (0x6F, 0x4F),
    (0x70, 0x50),
    (0x71, 0x51),
    (0x72, 0x52),
    (0x73, 0x53),
    (0x74, 0x54),
    (0x75, 0x55),
    (0x76, 0x56),
    (0x77, 0x57),
    (0x78, 0x58),
    (0x79, 0x59),
    (0x7A, 0x5A),
    (0xB5, 0x39C),
    (0xE0, 0xC0),
    (0xE1, 0xC1),
    (0xE2, 0xC2),
    (0xE3, 0xC3),
    (0xE4, 0xC4),
    (0xE5, 0xC5),
    (0xE6, 0xC6),
    (0xE7, 0xC7),
    (0xE8, 0xC8),
    (0xE9, 0xC9),
    (0xEA, 0xCA),
    (0xEB, 0xCB),
    (0xEC, 0xCC),
    (0xED, 0xCD),
    (0xEE, 0xCE),
    (0xEF, 0xCF),
    (0xF0, 0xD0),
    (0xF1, 0xD1),
    (0xF2, 0xD2),
    (0xF3, 0xD3),
    (0xF4, 0xD4),
    (0xF5, 0xD5),
    (0xF6, 0xD6),
    (0xF8, 0xD8),
    (0xF9, 0xD9),
    (0xFA, 0xDA),
    (0xFB, 0xDB),
    (0xFC, 0xDC),
    (0xFD, 0xDD),
    (0xFE, 0xDE),
    (0xFF, 0x178),
    (0x101, 0x100),
    (0x103, 0x102),
    (0x105, 0x104),
    (0x107, 0x106),
    (0x109, 0x108),
    (0x10B, 0x10A),
    (0x10D, 0x10C),
    (0x10F, 0x10E),
    (0x111, 0x110),
    (0x113, 0x112),
    (0x115, 0x114),
    (0x117, 0x116),
    (0x119, 0x118),
    (0x11B, 0x11A),
    (0x11D, 0x11C),
    (0x11F, 0x11E),
    (0x121, 0x120),
    (0x123, 0x122),
    (0x125, 0x124),
    (0x127, 0x126),
    (0x129, 0x128),
    (0x12B, 0x12A),
    (0x12D, 0x12C),
    (0x12F, 0x12E),
    (0x133, 0x132),
    (0x135, 0x134),
    (0x137, 0x136),
    (0x13A, 0x139),
    (0x13C, 0x13B),
    (0x13E, 0x13D),
    (0x140, 0x13F),
    (0x142, 0x141),
    (0x144, 0x143),
    (0x146, 0x145),
    (0x148, 0x147),
    (0x14B, 0x14A),
    (0x14D, 0x14C),
    (0x14F, 0x14E),
    (0x151, 0x150),
    (0x153, 0x152),
    (0x155, 0x154),
    (0x157, 0x156),
    (0x159, 0x158),
    (0x15B, 0x15A),
    (0x15D, 0x15C),
    (0x15F, 0x15E),
    (0x161, 0x160),
    (0x163, 0x162),
    (0x165, 0x164),
    (0x167, 0x166),
    (0x169, 0x168),
    (0x16B, 0x16A),
    (0x16D, 0x16C),
    (0x16F, 0x16E),
    (0x171, 0x170),
    (0x173, 0x172),
    (0x175, 0x174),
    (0x177, 0x176),
    (0x17A, 0x179),
    (0x17C, 0x17B),
    (0x17E, 0x17D),
    (0x17F, 0x53),
    (0x180, 0x243),
    (0x183, 0x182),
    (0x185, 0x184),
    (0x188, 0x187),
    (0x18C, 0x18B),
    (0x192, 0x191),
    (0x195, 0x1F6),
    (0x199, 0x198),
    (0x19A, 0x23D),
    (0x19E, 0x220),
    (0x1A1, 0x1A0),
    (0x1A3, 0x1A2),
    (0x1A5, 0x1A4),
    (0x1A8, 0x1A7),
    (0x1AD, 0x1AC),
    (0x1B0, 0x1AF),
    (0x1B4, 0x1B3),
    (0x1B6, 0x1B5),
    (0x1B9, 0x1B8),
    (0x1BD, 0x1BC),
    (0x1BF, 0x1F7),
    (0x1C5, 0x1C4),
    (0x1C6, 0x1C4),
    (0x1C8, 0x1C7),
    (0x1C9, 0x1C7),
    (0x1CB, 0x1CA),
    (0x1CC, 0x1CA),
    (0x1CE, 0x1CD),
    (0x1D0, 0x1CF),
    (0x1D2, 0x1D1),
    (0x1D4, 0x1D3),
    (0x1D6, 0x1D5),
    (0x1D8, 0x1D7),
    (0x1DA, 0x1D9),
    (0x1DC, 0x1DB),
    (0x1DD, 0x18E),
    (0x1DF, 0x1DE),
    (0x1E1, 0x1E0),
    (0x1E3, 0x1E2),
    (0x1E5, 0x1E4),
    (0x1E7, 0x1E6),
    (0x1E9, 0x1E8),
    (0x1EB, 0x1EA),
    (0x1ED, 0x1EC),
    (0x1EF, 0x1EE),
    (0x1F2, 0x1F1),
    (0x1F3, 0x1F1),
    (0x1F5, 0x1F4),
    (0x1F9, 0x1F8),
    (0x1FB, 0x1FA),
    (0x1FD, 0x1FC),
    (0x1FF, 0x1FE),
    (0x201, 0x200),
    (0x203, 0x202),
    (0x205, 0x204),
    (0x207, 0x206),
    (0x209, 0x208),
    (0x20B, 0x20A),
    (0x20D, 0x20C),
    (0x20F, 0x20E),
    (0x211, 0x210),
    (0x213, 0x212),
    (0x215, 0x214),
    (0x217, 0x216),
    (0x219, 0x218),
    (0x21B, 0x21A),
    (0x21D, 0x21C),
    (0x21F, 0x21E),
    (0x223, 0x222),
    (0x225, 0x224),
    (0x227, 0x226),
    (0x229, 0x228),
    (0x22B, 0x22A),
    (0x22D, 0x22C),
    (0x22F, 0x22E),
    (0x231, 0x230),
    (0x233, 0x232),
    (0x23C, 0x23B),
    (0x23F, 0x2C7E),
    (0x240, 0x2C7F),
    (0x242, 0x241),
    (0x247, 0x246),
    (0x249, 0x248),
    (0x24B, 0x24A),
    (0x24D, 0x24C),
    (0x24F, 0x24E),
    (0x250, 0x2C6F),
    (0x251, 0x2C6D),
    (0x252, 0x2C70),
    (0x253, 0x181),
    (0x254, 0x186),
    (0x256, 0x189),
    (0x257, 0x18A),
    (0x259, 0x18F),
    (0x25B, 0x190),
    (0x25C, 0xA7AB),
    (0x260, 0x193),
    (0x261, 0xA7AC),
    (0x263, 0x194),
    (0x265, 0xA78D),
    (0x266, 0xA7AA),
    (0x268, 0x197),
    (0x269, 0x196),
    (0x26A, 0xA7AE),
    (0x26B, 0x2C62),
    (0x26C, 0xA7AD),
    (0x26F, 0x19C),
    (0x271, 0x2C6E),
    (0x272, 0x19D),
    (0x275, 0x19F),
    (0x27D, 0x2C64),
    (0x280, 0x1A6),
    (0x282, 0xA7C5),
    (0x283, 0x1A9),
    (0x287, 0xA7B1),
    (0x288, 0x1AE),
    (0x289, 0x244),
    (0x28A, 0x1B1),
    (0x28B, 0x1B2),
    (0x28C, 0x245),
    (0x292, 0x1B7),
    (0x29D, 0xA7B2),
    (0x29E, 0xA7B0),
    (0x345, 0x399),
    (0x371, 0x370),
    (0x373, 0x372),
    (0x377, 0x376),
    (0x37B, 0x3FD),
    (0x37C, 0x3FE),
    (0x37D, 0x3FF),
    (0x3AC, 0x386),
    (0x3AD, 0x388),
    (0x3AE, 0x389),
    (0x3AF, 0x38A),
    (0x3B1, 0x391),
    (0x3B2, 0x392),
    (0x3B3, 0x393),
    (0x3B4, 0x394),
    (0x3B5, 0x395),
    (0x3B6, 0x396),
    (0x3B7, 0x397),
    (0x3B8, 0x398),
    (0x3B9, 0x399),
    (0x3BA, 0x39A),
    (0x3BB, 0x39B),
    (0x3BC, 0x39C),
    (0x3BD, 0x39D),
    (0x3BE, 0x39E),
    (0x3BF, 0x39F),
    (0x3C0, 0x3A0),
    (0x3C1, 0x3A1),
    (0x3C2, 0x3A3),
    (0x3C3, 0x3A3),
    (0x3C4, 0x3A4),
    (0x3C5, 0x3A5),
    (0x3C6, 0x3A6),
    (0x3C7, 0x3A7),
    (0x3C8, 0x3A8),
    (0x3C9, 0x3A9),
    (0x3CA, 0x3AA),
    (0x3CB, 0x3AB),
    (0x3CC, 0x38C),
    (0x3CD, 0x38E),
    (0x3CE, 0x38F),
    (0x3D0, 0x392),
    (0x3D1, 0x398),
    (0x3D5, 0x3A6),
    (0x3D6, 0x3A0),
    (0x3D7, 0x3CF),
    (0x3D9, 0x3D8),
    (0x3DB, 0x3DA),
    (0x3DD, 0x3DC),
    (0x3DF, 0x3DE),
    (0x3E1, 0x3E0),
    (0x3E3, 0x3E2),
    (0x3E5, 0x3E4),
    (0x3E7, 0x3E6),
    (0x3E9, 0x3E8),
    (0x3EB, 0x3EA),
    (0x3ED, 0x3EC),
    (0x3EF, 0x3EE),
    (0x3F0, 0x39A),
    (0x3F1, 0x3A1),
    (0x3F2, 0x3F9),
    (0x3F3, 0x37F),
    (0x3F5, 0x395),
    (0x3F8, 0x3F7),
    (0x3FB, 0x3FA),
    (0x430, 0x410),
    (0x431, 0x411),
    (0x432, 0x412),
    (0x433, 0x413),
    (0x434, 0x414),
    (0x435, 0x415),
    (0x436, 0x416),
    (0x437, 0x417),
    (0x438, 0x418),
    (0x439, 0x419),
    (0x43A, 0x41A),
    (0x43B, 0x41B),
    (0x43C, 0x41C),
    (0x43D, 0x41D),
    (0x43E, 0x41E),
    (0x43F, 0x41F),
    (0x440, 0x420),
    (0x441, 0x421),
    (0x442, 0x422),
    (0x443, 0x423),
    (0x444, 0x424),
    (0x445, 0x425),
    (0x446, 0x426),
    (0x447, 0x427),
    (0x448, 0x428),
    (0x449, 0x429),
    (0x44A, 0x42A),
    (0x44B, 0x42B),
    (0x44C, 0x42C),
    (0x44D, 0x42D),
    (0x44E, 0x42E),
    (0x44F, 0x42F),
    (0x450, 0x400),
    (0x451, 0x401),
    (0x452, 0x402),
    (0x453, 0x403),
    (0x454, 0x404),
    (0x455, 0x405),
    (0x456, 0x406),
    (0x457, 0x407),
    (0x458, 0x408),
    (0x459, 0x409),
    (0x45A, 0x40A),
    (0x45B, 0x40B),
    (0x45C, 0x40C),
    (0x45D, 0x40D),
    (0x45E, 0x40E),
    (0x45F, 0x40F),
    (0x461, 0x460),
    (0x463, 0x462),
    (0x465, 0x464),
    (0x467, 0x466),
    (0x469, 0x468),
    (0x46B, 0x46A),
    (0x46D, 0x46C),
    (0x46F, 0x46E),
    (0x471, 0x470),
    (0x473, 0x472),
    (0x475, 0x474),
    (0x477, 0x476),
    (0x479, 0x478),
    (0x47B, 0x47A),
    (0x47D, 0x47C),
    (0x47F, 0x47E),
    (0x481, 0x480),
    (0x48B, 0x48A),
    (0x48D, 0x48C),
    (0x48F, 0x48E),
    (0x491, 0x490),
    (0x493, 0x492),
    (0x495, 0x494),
    (0x497, 0x496),
    (0x499, 0x498),
    (0x49B, 0x49A),
    (0x49D, 0x49C),
    (0x49F, 0x49E),
    (0x4A1, 0x4A0),
    (0x4A3, 0x4A2),
    (0x4A5, 0x4A4),
    (0x4A7, 0x4A6),
    (0x4A9, 0x4A8),
    (0x4AB, 0x4AA),
    (0x4AD, 0x4AC),
    (0x4AF, 0x4AE),
    (0x4B1, 0x4B0),
    (0x4B3, 0x4B2),
    (0x4B5, 0x4B4),
    (0x4B7, 0x4B6),
    (0x4B9, 0x4B8),
    (0x4BB, 0x4BA),
    (0x4BD, 0x4BC),
    (0x4BF, 0x4BE),
    (0x4C2, 0x4C1),
    (0x4C4, 0x4C3),
    (0x4C6, 0x4C5),
    (0x4C8, 0x4C7),
    (0x4CA, 0x4C9),
    (0x4CC, 0x4CB),
    (0x4CE, 0x4CD),
    (0x4CF, 0x4C0),
    (0x4D1, 0x4D0),
    (0x4D3, 0x4D2),
    (0x4D5, 0x4D4),
    (0x4D7, 0x4D6),
    (0x4D9, 0x4D8),
    (0x4DB, 0x4DA),
    (0x4DD, 0x4DC),
    (0x4DF, 0x4DE),
    (0x4E1, 0x4E0),
    (0x4E3, 0x4E2),
    (0x4E5, 0x4E4),
    (0x4E7, 0x4E6),
    (0x4E9, 0x4E8),
    (0x4EB, 0x4EA),
    (0x4ED, 0x4EC),
    (0x4EF, 0x4EE),
    (0x4F1, 0x4F0),
    (0x4F3, 0x4F2),
    (0x4F5, 0x4F4),
    (0x4F7, 0x4F6),
    (0x4F9, 0x4F8),
    (0x4FB, 0x4FA),
    (0x4FD, 0x4FC),
    (0x4FF, 0x4FE),
    (0x501, 0x500),
    (0x503, 0x502),
    (0x505, 0x504),
    (0x507, 0x506),
    (0x509, 0x508),
    (0x50B, 0x50A),
    (0x50D, 0x50C),
    (0x50F, 0x50E),
    (0x511, 0x510),
    (0x513, 0x512),
    (0x515, 0x514),
    (0x517, 0x516),
    (0x519, 0x518),
    (0x51B, 0x51A),
    (0x51D, 0x51C),
    (0x51F, 0x51E),
    (0x521, 0x520),
    (0x523, 0x522),
    (0x525, 0x524),
    (0x527, 0x526),
    (0x529, 0x528),
    (0x52B, 0x52A),
    (0x52D, 0x52C),
    (0x52F, 0x52E),
    (0x561, 0x531),
    (0x562, 0x532),
    (0x563, 0x533),
    (0x564, 0x534),
    (0x565, 0x535),
    (0x566, 0x536),
    (0x567, 0x537),
    (0x568, 0x538),
    (0x569, 0x539),
    (0x56A, 0x53A),
    (0x56B, 0x53B),
    (0x56C, 0x53C),
    (0x56D, 0x53D),
    (0x56E, 0x53E),
    (0x56F, 0x53F),
    (0x570, 0x540),
    (0x571, 0x541),
    (0x572, 0x542),
    (0x573, 0x543),
    (0x574, 0x544),
    (0x575, 0x545),
    (0x576, 0x546),
    (0x577, 0x547),
    (0x578, 0x548),
    (0x579, 0x549),
    (0x57A, 0x54A),
    (0x57B, 0x54B),
    (0x57C, 0x54C),
    (0x57D, 0x54D),
    (0x57E, 0x54E),
    (0x57F, 0x54F),
    (0x580, 0x550),
    (0x581, 0x551),
    (0x582, 0x552),
    (0x583, 0x553),
    (0x584, 0x554),
    (0x585, 0x555),
    (0x586, 0x556),
    (0x10D0, 0x1C90),
    (0x10D1, 0x1C91),
    (0x10D2, 0x1C92),
    (0x10D3, 0x1C93),
    (0x10D4, 0x1C94),
    (0x10D5, 0x1C95),
    (0x10D6, 0x1C96),
    (0x10D7, 0x1C97),
    (0x10D8, 0x1C98),
    (0x10D9, 0x1C99),
    (0x10DA, 0x1C9A),
    (0x10DB, 0x1C9B),
    (0x10DC, 0x1C9C),
    (0x10DD, 0x1C9D),
    (0x10DE, 0x1C9E),
    (0x10DF, 0x1C9F),
    (0x10E0, 0x1CA0),
    (0x10E1, 0x1CA1),
    (0x10E2, 0x1CA2),
    (0x10E3, 0x1CA3),
    (0x10E4, 0x1CA4),
    (0x10E5, 0x1CA5),
    (0x10E6, 0x1CA6),
    (0x10E7, 0x1CA7),
    (0x10E8, 0x1CA8),
    (0x10E9, 0x1CA9),
    (0x10EA, 0x1CAA),
    (0x10EB, 0x1CAB),
    (0x10EC, 0x1CAC),
    (0x10ED, 0x1CAD),
    (0x10EE, 0x1CAE),
    (0x10EF, 0x1CAF),
    (0x10F0, 0x1CB0),
    (0x10F1, 0x1CB1),
    (0x10F2, 0x1CB2),
    (0x10F3, 0x1CB3),
    (0x10F4, 0x1CB4),
    (0x10F5, 0x1CB5),
    (0x10F6, 0x1CB6),
    (0x10F7, 0x1CB7),
    (0x10F8, 0x1CB8),
    (0x10F9, 0x1CB9),
    (0x10FA, 0x1CBA),
    (0x10FD, 0x1CBD),
    (0x10FE, 0x1CBE),
    (0x10FF, 0x1CBF),
    (0x13F8, 0x13F0),
    (0x13F9, 0x13F1),
    (0x13FA, 0x13F2),
    (0x13FB, 0x13F3),
    (0x13FC, 0x13F4),
    (0x13FD, 0x13F5),
    (0x1C80, 0x412),
    (0x1C81, 0x414),
    (0x1C82, 0x41E),
    (0x1C83, 0x421),
    (0x1C84, 0x422),
    (0x1C85, 0x422),
    (0x1C86, 0x42A),
    (0x1C87, 0x462),
    (0x1C88, 0xA64A),
    (0x1D79, 0xA77D),
    (0x1D7D, 0x2C63),
    (0x1D8E, 0xA7C6),
    (0x1E01, 0x1E00),
    (0x1E03, 0x1E02),
    (0x1E05, 0x1E04),
    (0x1E07, 0x1E06),
    (0x1E09, 0x1E08),
    (0x1E0B, 0x1E0A),
    (0x1E0D, 0x1E0C),
    (0x1E0F, 0x1E0E),
    (0x1E11, 0x1E10),
    (0x1E13, 0x1E12),
    (0x1E15, 0x1E14),
    (0x1E17, 0x1E16),
    (0x1E19, 0x1E18),
    (0x1E1B, 0x1E1A),
    (0x1E1D, 0x1E1C),
    (0x1E1F, 0x1E1E),
    (0x1E21, 0x1E20),
    (0x1E23, 0x1E22),
    (0x1E25, 0x1E24),
    (0x1E27, 0x1E26),
    (0x1E29, 0x1E28),
    (0x1E2B, 0x1E2A),
    (0x1E2D, 0x1E2C),
    (0x1E2F, 0x1E2E),
    (0x1E31, 0x1E30),
    (0x1E33, 0x1E32),
    (0x1E35, 0x1E34),
    (0x1E37, 0x1E36),
    (0x1E39, 0x1E38),
    (0x1E3B, 0x1E3A),
    (0x1E3D, 0x1E3C),
    (0x1E3F, 0x1E3E),
    (0x1E41, 0x1E40),
    (0x1E43, 0x1E42),
    (0x1E45, 0x1E44),
    (0x1E47, 0x1E46),
    (0x1E49, 0x1E48),
    (0x1E4B, 0x1E4A),
    (0x1E4D, 0x1E4C),
    (0x1E4F, 0x1E4E),
    (0x1E51, 0x1E50),
    (0x1E53, 0x1E52),
    (0x1E55, 0x1E54),
    (0x1E57, 0x1E56),
    (0x1E59, 0x1E58),
    (0x1E5B, 0x1E5A),
    (0x1E5D, 0x1E5C),
    (0x1E5F, 0x1E5E),
    (0x1E61, 0x1E60),
    (0x1E63, 0x1E62),
    (0x1E65, 0x1E64),
    (0x1E67, 0x1E66),
    (0x1E69, 0x1E68),
    (0x1E6B, 0x1E6A),
    (0x1E6D, 0x1E6C),
    (0x1E6F, 0x1E6E),
    (0x1E71, 0x1E70),
    (0x1E73, 0x1E72),
    (0x1E75, 0x1E74),
    (0x1E77, 0x1E76),
    (0x1E79, 0x1E78),
    (0x1E7B, 0x1E7A),
    (0x1E7D, 0x1E7C),
    (0x1E7F, 0x1E7E),
    (0x1E81, 0x1E80),
    (0x1E83, 0x1E82),
    (0x1E85, 0x1E84),
    (0x1E87, 0x1E86),
    (0x1E89, 0x1E88),
    (0x1E8B, 0x1E8A),
    (0x1E8D, 0x1E8C),
    (0x1E8F, 0x1E8E),
    (0x1E91, 0x1E90),
    (0x1E93, 0x1E92),
    (0x1E95, 0x1E94),
    (0x1E9B, 0x1E60),
    (0x1EA1, 0x1EA0),
    (0x1EA3, 0x1EA2),
    (0x1EA5, 0x1EA4),
    (0x1EA7, 0x1EA6),
    (0x1EA9, 0x1EA8),
    (0x1EAB, 0x1EAA),
    (0x1EAD, 0x1EAC),
    (0x1EAF, 0x1EAE),
    (0x1EB1, 0x1EB0),
    (0x1EB3, 0x1EB2),
    (0x1EB5, 0x1EB4),
    (0x1EB7, 0x1EB6),
    (0x1EB9, 0x1EB8),
    (0x1EBB, 0x1EBA),
    (0x1EBD, 0x1EBC),
    (0x1EBF, 0x1EBE),
    (0x1EC1, 0x1EC0),
    (0x1EC3, 0x1EC2),
    (0x1EC5, 0x1EC4),
    (0x1EC7, 0x1EC6),
    (0x1EC9, 0x1EC8),
    (0x1ECB, 0x1ECA),
    (0x1ECD, 0x1ECC),
    (0x1ECF, 0x1ECE),
    (0x1ED1, 0x1ED0),
    (0x1ED3, 0x1ED2),
    (0x1ED5, 0x1ED4),
    (0x1ED7, 0x1ED6),
    (0x1ED9, 0x1ED8),
    (0x1EDB, 0x1EDA),
    (0x1EDD, 0x1EDC),
    (0x1EDF, 0x1EDE),
    (0x1EE1, 0x1EE0),
    (0x1EE3, 0x1EE2),
    (0x1EE5, 0x1EE4),
    (0x1EE7, 0x1EE6),
    (0x1EE9, 0x1EE8),
    (0x1EEB, 0x1EEA),
    (0x1EED, 0x1EEC),
    (0x1EEF, 0x1EEE),
    (0x1EF1, 0x1EF0),
    (0x1EF3, 0x1EF2),
    (0x1EF5, 0x1EF4),
    (0x1EF7, 0x1EF6),
    (0x1EF9, 0x1EF8),
    (0x1EFB, 0x1EFA),
    (0x1EFD, 0x1EFC),
    (0x1EFF, 0x1EFE),
    (0x1F00, 0x1F08),
    (0x1F01, 0x1F09),
    (0x1F02, 0x1F0A),
    (0x1F03, 0x1F0B),
    (0x1F04, 0x1F0C),
    (0x1F05, 0x1F0D),
    (0x1F06, 0x1F0E),
    (0x1F07, 0x1F0F),
    (0x1F10, 0x1F18),
    (0x1F11, 0x1F19),
    (0x1F12, 0x1F1A),
    (0x1F13, 0x1F1B),
    (0x1F14, 0x1F1C),
    (0x1F15, 0x1F1D),
    (0x1F20, 0x1F28),
    (0x1F21, 0x1F29),
    (0x1F22, 0x1F2A),
    (0x1F23, 0x1F2B),
    (0x1F24, 0x1F2C),
    (0x1F25, 0x1F2D),
    (0x1F26, 0x1F2E),
    (0x1F27, 0x1F2F),
    (0x1F30, 0x1F38),
    (0x1F31, 0x1F39),
    (0x1F32, 0x1F3A),
    (0x1F33, 0x1F3B),
    (0x1F34, 0x1F3C),
    (0x1F35, 0x1F3D),
    (0x1F36, 0x1F3E),
    (0x1F37, 0x1F3F),
    (0x1F40, 0x1F48),
    (0x1F41, 0x1F49),
    (0x1F42, 0x1F4A),
    (0x1F43, 0x1F4B),
    (0x1F44, 0x1F4C),
    (0x1F45, 0x1F4D),
    (0x1F51, 0x1F59),
    (0x1F53, 0x1F5B),
    (0x1F55, 0x1F5D),
    (0x1F57, 0x1F5F),
    (0x1F60, 0x1F68),
    (0x1F61, 0x1F69),
    (0x1F62, 0x1F6A),
    (0x1F63, 0x1F6B),
    (0x1F64, 0x1F6C),
    (0x1F65, 0x1F6D),
    (0x1F66, 0x1F6E),
    (0x1F67, 0x1F6F),
    (0x1F70, 0x1FBA),
    (0x1F71, 0x1FBB),
    (0x1F72, 0x1FC8),
    (0x1F73, 0x1FC9),
    (0x1F74, 0x1FCA),
    (0x1F75, 0x1FCB),
    (0x1F76, 0x1FDA),
    (0x1F77, 0x1FDB),
    (0x1F78, 0x1FF8),
    (0x1F79, 0x1FF9),
    (0x1F7A, 0x1FEA),
    (0x1F7B, 0x1FEB),
    (0x1F7C, 0x1FFA),
    (0x1F7D, 0x1FFB),
    (0x1F80, 0x1F88),
    (0x1F81, 0x1F89),
    (0x1F82, 0x1F8A),
    (0x1F83, 0x1F8B),
    (0x1F84, 0x1F8C),
    (0x1F85, 0x1F8D),
    (0x1F86, 0x1F8E),
    (0x1F87, 0x1F8F),
    (0x1F90, 0x1F98),
    (0x1F91, 0x1F99),
    (0x1F92, 0x1F9A),
    (0x1F93, 0x1F9B),
    (0x1F94, 0x1F9C),
    (0x1F95, 0x1F9D),
    (0x1F96, 0x1F9E),
    (0x1F97, 0x1F9F),
    (0x1FA0, 0x1FA8),
    (0x1FA1, 0x1FA9),
    (0x1FA2, 0x1FAA),
    (0x1FA3, 0x1FAB),
    (0x1FA4, 0x1FAC),
    (0x1FA5, 0x1FAD),
    (0x1FA6, 0x1FAE),
    (0x1FA7, 0x1FAF),
    (0x1FB0, 0x1FB8),
    (0x1FB1, 0x1FB9),
    (0x1FB3, 0x1FBC),
    (0x1FBE, 0x399),
    (0x1FC3, 0x1FCC),
    (0x1FD0, 0x1FD8),
    (0x1FD1, 0x1FD9),
    (0x1FE0, 0x1FE8),
    (0x1FE1, 0x1FE9),
    (0x1FE5, 0x1FEC),
    (0x1FF3, 0x1FFC),
    (0x214E, 0x2132),
    (0x2170, 0x2160),
    (0x2171, 0x2161),
    (0x2172, 0x2162),
    (0x2173, 0x2163),
    (0x2174, 0x2164),
    (0x2175, 0x2165),
    (0x2176, 0x2166),
    (0x2177, 0x2167),
    (0x2178, 0x2168),
    (0x2179, 0x2169),
    (0x217A, 0x216A),
    (0x217B, 0x216B),
    (0x217C, 0x216C),
    (0x217D, 0x216D),
    (0x217E, 0x216E),
    (0x217F, 0x216F),
    (0x2184, 0x2183),
    (0x24D0, 0x24B6),
    (0x24D1, 0x24B7),
    (0x24D2, 0x24B8),
    (0x24D3, 0x24B9),
    (0x24D4, 0x24BA),
    (0x24D5, 0x24BB),
    (0x24D6, 0x24BC),
    (0x24D7, 0x24BD),
    (0x24D8, 0x24BE),
    (0x24D9, 0x24BF),
    (0x24DA, 0x24C0),
    (0x24DB, 0x24C1),
    (0x24DC, 0x24C2),
    (0x24DD, 0x24C3),
    (0x24DE, 0x24C4),
    (0x24DF, 0x24C5),
    (0x24E0, 0x24C6),
    (0x24E1, 0x24C7),
    (0x24E2, 0x24C8),
    (0x24E3, 0x24C9),
    (0x24E4, 0x24CA),
    (0x24E5, 0x24CB),
    (0x24E6, 0x24CC),
    (0x24E7, 0x24CD),
    (0x24E8, 0x24CE),
    (0x24E9, 0x24CF),
    (0x2C30, 0x2C00),
    (0x2C31, 0x2C01),
    (0x2C32, 0x2C02),
    (0x2C33, 0x2C03),
    (0x2C34, 0x2C04),
    (0x2C35, 0x2C05),
    (0x2C36, 0x2C06),
    (0x2C37, 0x2C07),
    (0x2C38, 0x2C08),
    (0x2C39, 0x2C09),
    (0x2C3A, 0x2C0A),
    (0x2C3B, 0x2C0B),
    (0x2C3C, 0x2C0C),
    (0x2C3D, 0x2C0D),
    (0x2C3E, 0x2C0E),
    (0x2C3F, 0x2C0F),
    (0x2C40, 0x2C10),
    (0x2C41, 0x2C11),
    (0x2C42, 0x2C12),
    (0x2C43, 0x2C13),
    (0x2C44, 0x2C14),
    (0x2C45, 0x2C15),
    (0x2C46, 0x2C16),
    (0x2C47, 0x2C17),
    (0x2C48, 0x2C18),
    (0x2C49, 0x2C19),
    (0x2C4A, 0x2C1A),
    (0x2C4B, 0x2C1B),
    (0x2C4C, 0x2C1C),
    (0x2C4D, 0x2C1D),
    (0x2C4E, 0x2C1E),
    (0x2C4F, 0x2C1F),
    (0x2C50, 0x2C20),
    (0x2C51, 0x2C21),
    (0x2C52, 0x2C22),
    (0x2C53, 0x2C23),
    (0x2C54, 0x2C24),
    (0x2C55, 0x2C25),
    (0x2C56, 0x2C26),
    (0x2C57, 0x2C27),
    (0x2C58, 0x2C28),
    (0x2C59, 0x2C29),
    (0x2C5A, 0x2C2A),
    (0x2C5B, 0x2C2B),
    (0x2C5C, 0x2C2C),
    (0x2C5D, 0x2C2D),
    (0x2C5E, 0x2C2E),
    (0x2C5F, 0x2C2F),
    (0x2C61, 0x2C60),
    (0x2C65, 0x23A),
    (0x2C66, 0x23E),
    (0x2C68, 0x2C67),
    (0x2C6A, 0x2C69),
    (0x2C6C, 0x2C6B),
    (0x2C73, 0x2C72),
    (0x2C76, 0x2C75),
    (0x2C81, 0x2C80),
    (0x2C83, 0x2C82),
    (0x2C85, 0x2C84),
    (0x2C87, 0x2C86),
    (0x2C89, 0x2C88),
    (0x2C8B, 0x2C8A),
    (0x2C8D, 0x2C8C),
    (0x2C8F, 0x2C8E),
    (0x2C91, 0x2C90),
    (0x2C93, 0x2C92),
    (0x2C95, 0x2C94),
    (0x2C97, 0x2C96),
    (0x2C99, 0x2C98),
    (0x2C9B, 0x2C9A),
    (0x2C9D, 0x2C9C),
    (0x2C9F, 0x2C9E),
    (0x2CA1, 0x2CA0),
    (0x2CA3, 0x2CA2),
    (0x2CA5, 0x2CA4),
    (0x2CA7, 0x2CA6),
    (0x2CA9, 0x2CA8),
    (0x2CAB, 0x2CAA),
    (0x2CAD, 0x2CAC),
    (0x2CAF, 0x2CAE),
    (0x2CB1, 0x2CB0),
    (0x2CB3, 0x2CB2),
    (0x2CB5, 0x2CB4),
    (0x2CB7, 0x2CB6),
    (0x2CB9, 0x2CB8),
    (0x2CBB, 0x2CBA),
    (0x2CBD, 0x2CBC),
    (0x2CBF, 0x2CBE),
    (0x2CC1, 0x2CC0),
    (0x2CC3, 0x2CC2),
    (0x2CC5, 0x2CC4),
    (0x2CC7, 0x2CC6),
    (0x2CC9, 0x2CC8),
    (0x2CCB, 0x2CCA),
    (0x2CCD, 0x2CCC),
    (0x2CCF, 0x2CCE),
    (0x2CD1, 0x2CD0),
    (0x2CD3, 0x2CD2),
    (0x2CD5, 0x2CD4),
    (0x2CD7, 0x2CD6),
    (0x2CD9, 0x2CD8),
    (0x2CDB, 0x2CDA),
    (0x2CDD, 0x2CDC),
    (0x2CDF, 0x2CDE),
    (0x2CE1, 0x2CE0),
    (0x2CE3, 0x2CE2),
    (0x2CEC, 0x2CEB),
    (0x2CEE, 0x2CED),
    (0x2CF3, 0x2CF2),
    (0x2D00, 0x10A0),
    (0x2D01, 0x10A1),
    (0x2D02, 0x10A2),
    (0x2D03, 0x10A3),
    (0x2D04, 0x10A4),
    (0x2D05, 0x10A5),
    (0x2D06, 0x10A6),
    (0x2D07, 0x10A7),
    (0x2D08, 0x10A8),
    (0x2D09, 0x10A9),
    (0x2D0A, 0x10AA),
    (0x2D0B, 0x10AB),
    (0x2D0C, 0x10AC),
    (0x2D0D, 0x10AD),
    (0x2D0E, 0x10AE),
    (0x2D0F, 0x10AF),
    (0x2D10, 0x10B0),
    (0x2D11, 0x10B1),
    (0x2D12, 0x10B2),
    (0x2D13, 0x10B3),
    (0x2D14, 0x10B4),
    (0x2D15, 0x10B5),
    (0x2D16, 0x10B6),
    (0x2D17, 0x10B7),
    (0x2D18, 0x10B8),
    (0x2D19, 0x10B9),
    (0x2D1A, 0x10BA),
    (0x2D1B, 0x10BB),
    (0x2D1C, 0x10BC),
    (0x2D1D, 0x10BD),
    (0x2D1E, 0x10BE),
    (0x2D1F, 0x10BF),
    (0x2D20, 0x10C0),
    (0x2D21, 0x10C1),
    (0x2D22, 0x10C2),
    (0x2D23, 0x10C3),
    (0x2D24, 0x10C4),
    (0x2D25, 0x10C5),
    (0x2D27, 0x10C7),
    (0x2D2D, 0x10CD),
    (0xA641, 0xA640),
    (0xA643, 0xA642),
    (0xA645, 0xA644),
    (0xA647, 0xA646),
    (0xA649, 0xA648),
    (0xA64B, 0xA64A),
    (0xA64D, 0xA64C),
    (0xA64F, 0xA64E),
    (0xA651, 0xA650),
    (0xA653, 0xA652),
    (0xA655, 0xA654),
    (0xA657, 0xA656),
    (0xA659, 0xA658),
    (0xA65B, 0xA65A),
    (0xA65D, 0xA65C),
    (0xA65F, 0xA65E),
    (0xA661, 0xA660),
    (0xA663, 0xA662),
    (0xA665, 0xA664),
    (0xA667, 0xA666),
    (0xA669, 0xA668),
    (0xA66B, 0xA66A),
    (0xA66D, 0xA66C),
    (0xA681, 0xA680),
    (0xA683, 0xA682),
    (0xA685, 0xA684),
    (0xA687, 0xA686),
    (0xA689, 0xA688),
    (0xA68B, 0xA68A),
    (0xA68D, 0xA68C),
    (0xA68F, 0xA68E),
    (0xA691, 0xA690),
    (0xA693, 0xA692),
    (0xA695, 0xA694),
    (0xA697, 0xA696),
    (0xA699, 0xA698),
    (0xA69B, 0xA69A),
    (0xA723, 0xA722),
    (0xA725, 0xA724),
    (0xA727, 0xA726),
    (0xA729, 0xA728),
    (0xA72B, 0xA72A),
    (0xA72D, 0xA72C),
    (0xA72F, 0xA72E),
    (0xA733, 0xA732),
    (0xA735, 0xA734),
    (0xA737, 0xA736),
    (0xA739, 0xA738),
    (0xA73B, 0xA73A),
    (0xA73D, 0xA73C),
    (0xA73F, 0xA73E),
    (0xA741, 0xA740),
    (0xA743, 0xA742),
    (0xA745, 0xA744),
    (0xA747, 0xA746),
    (0xA749, 0xA748),
    (0xA74B, 0xA74A),
    (0xA74D, 0xA74C),
    (0xA74F, 0xA74E),
    (0xA751, 0xA750),
    (0xA753, 0xA752),
    (0xA755, 0xA754),
    (0xA757, 0xA756),
    (0xA759, 0xA758),
    (0xA75B, 0xA75A),
    (0xA75D, 0xA75C),
    (0xA75F, 0xA75E),
    (0xA761, 0xA760),
    (0xA763, 0xA762),
    (0xA765, 0xA764),
    (0xA767, 0xA766),
    (0xA769, 0xA768),
    (0xA76B, 0xA76A),
    (0xA76D, 0xA76C),
    (0xA76F, 0xA76E),
    (0xA77A, 0xA779),
    (0xA77C, 0xA77B),
    (0xA77F, 0xA77E),
    (0xA781, 0xA780),
    (0xA783, 0xA782),
    (0xA785, 0xA784),
    (0xA787, 0xA786),
    (0xA78C, 0xA78B),
    (0xA791, 0xA790),
    (0xA793, 0xA792),
    (0xA794, 0xA7C4),
    (0xA797, 0xA796),
    (0xA799, 0xA798),
    (0xA79B, 0xA79A),
    (0xA79D, 0xA79C),
    (0xA79F, 0xA79E),
    (0xA7A1, 0xA7A0),
    (0xA7A3, 0xA7A2),
    (0xA7A5, 0xA7A4),
    (0xA7A7, 0xA7A6),
    (0xA7A9, 0xA7A8),
    (0xA7B5, 0xA7B4),
    (0xA7B7, 0xA7B6),
    (0xA7B9, 0xA7B8),
    (0xA7BB, 0xA7BA),
    (0xA7BD, 0xA7BC),
    (0xA7BF, 0xA7BE),
    (0xA7C1, 0xA7C0),
    (0xA7C3, 0xA7C2),
    (0xA7C8, 0xA7C7),
    (0xA7CA, 0xA7C9),
    (0xA7D1, 0xA7D0),
    (0xA7D7, 0xA7D6),
    (0xA7D9, 0xA7D8),
    (0xA7F6, 0xA7F5),
    (0xAB53, 0xA7B3),
    (0xAB70, 0x13A0),
    (0xAB71, 0x13A1),
    (0xAB72, 0x13A2),
    (0xAB73, 0x13A3),
    (0xAB74, 0x13A4),
    (0xAB75, 0x13A5),
    (0xAB76, 0x13A6),
    (0xAB77, 0x13A7),
    (0xAB78, 0x13A8),
    (0xAB79, 0x13A9),
    (0xAB7A, 0x13AA),
    (0xAB7B, 0x13AB),
    (0xAB7C, 0x13AC),
    (0xAB7D, 0x13AD),
    (0xAB7E, 0x13AE),
    (0xAB7F, 0x13AF),
    (0xAB80, 0x13B0),
    (0xAB81, 0x13B1),
    (0xAB82, 0x13B2),
    (0xAB83, 0x13B3),
    (0xAB84, 0x13B4),
    (0xAB85, 0x13B5),
    (0xAB86, 0x13B6),
    (0xAB87, 0x13B7),
    (0xAB88, 0x13B8),
    (0xAB89, 0x13B9),
    (0xAB8A, 0x13BA),
    (0xAB8B, 0x13BB),
    (0xAB8C, 0x13BC),
    (0xAB8D, 0x13BD),
    (0xAB8E, 0x13BE),
    (0xAB8F, 0x13BF),
    (0xAB90, 0x13C0),
    (0xAB91, 0x13C1),
    (0xAB92, 0x13C2),
    (0xAB93, 0x13C3),
    (0xAB94, 0x13C4),
    (0xAB95, 0x13C5),
    (0xAB96, 0x13C6),
    (0xAB97, 0x13C7),
    (0xAB98, 0x13C8),
    (0xAB99, 0x13C9),
    (0xAB9A, 0x13CA),
    (0xAB9B, 0x13CB),
    (0xAB9C, 0x13CC),
    (0xAB9D, 0x13CD),
    (0xAB9E, 0x13CE),
    (0xAB9F, 0x13CF),
    (0xABA0, 0x13D0),
    (0xABA1, 0x13D1),
    (0xABA2, 0x13D2),
    (0xABA3, 0x13D3),
    (0xABA4, 0x13D4),
    (0xABA5, 0x13D5),
    (0xABA6, 0x13D6),
    (0xABA7, 0x13D7),
    (0xABA8, 0x13D8),
    (0xABA9, 0x13D9),
    (0xABAA, 0x13DA),
    (0xABAB, 0x13DB),
    (0xABAC, 0x13DC),
    (0xABAD, 0x13DD),
    (0xABAE, 0x13DE),
    (0xABAF, 0x13DF),
    (0xABB0, 0x13E0),
    (0xABB1, 0x13E1),
    (0xABB2, 0x13E2),
    (0xABB3, 0x13E3),
    (0xABB4, 0x13E4),
    (0xABB5, 0x13E5),
    (0xABB6, 0x13E6),
    (0xABB7, 0x13E7),
    (0xABB8, 0x13E8),
    (0xABB9, 0x13E9),
    (0xABBA, 0x13EA),
    (0xABBB, 0x13EB),
    (0xABBC, 0x13EC),
    (0xABBD, 0x13ED),
    (0xABBE, 0x13EE),
    (0xABBF, 0x13EF),
    (0xFF41, 0xFF21),
    (0xFF42, 0xFF22),
    (0xFF43, 0xFF23),
    (0xFF44, 0xFF24),
    (0xFF45, 0xFF25),
    (0xFF46, 0xFF26),
    (0xFF47, 0xFF27),
    (0xFF48, 0xFF28),
    (0xFF49, 0xFF29),
    (0xFF4A, 0xFF2A),
    (0xFF4B, 0xFF2B),
    (0xFF4C, 0xFF2C),
    (0xFF4D, 0xFF2D),
    (0xFF4E, 0xFF2E),
    (0xFF4F, 0xFF2F),
    (0xFF50, 0xFF30),
    (0xFF51, 0xFF31),
    (0xFF52, 0xFF32),
    (0xFF53, 0xFF33),
    (0xFF54, 0xFF34),
    (0xFF55, 0xFF35),
    (0xFF56, 0xFF36),
    (0xFF57, 0xFF37),
    (0xFF58, 0xFF38),
    (0xFF59, 0xFF39),
    (0xFF5A, 0xFF3A),
    (0x10428, 0x10400),
    (0x10429, 0x10401),
    (0x1042A, 0x10402),
    (0x1042B, 0x10403),
    (0x1042C, 0x10404),
    (0x1042D, 0x10405),
    (0x1042E, 0x10406),
    (0x1042F, 0x10407),
    (0x10430, 0x10408),
    (0x10431, 0x10409),
    (0x10432, 0x1040A),
    (0x10433, 0x1040B),
    (0x10434, 0x1040C),
    (0x10435, 0x1040D),
    (0x10436, 0x1040E),
    (0x10437, 0x1040F),
    (0x10438, 0x10410),
    (0x10439, 0x10411),
    (0x1043A, 0x10412),
    (0x1043B, 0x10413),
    (0x1043C, 0x10414),
    (0x1043D, 0x10415),
    (0x1043E, 0x10416),
    (0x1043F, 0x10417),
    (0x10440, 0x10418),
    (0x10441, 0x10419),
    (0x10442, 0x1041A),
    (0x10443, 0x1041B),
    (0x10444, 0x1041C),
    (0x10445, 0x1041D),
    (0x10446, 0x1041E),
    (0x10447, 0x1041F),
    (0x10448, 0x10420),
    (0x10449, 0x10421),
    (0x1044A, 0x10422),
    (0x1044B, 0x10423),
    (0x1044C, 0x10424),
    (0x1044D, 0x10425),
    (0x1044E, 0x10426),
    (0x1044F, 0x10427),
    (0x104D8, 0x104B0),
    (0x104D9, 0x104B1),
    (0x104DA, 0x104B2),
    (0x104DB, 0x104B3),
    (0x104DC, 0x104B4),
    (0x104DD, 0x104B5),
    (0x104DE, 0x104B6),
    (0x104DF, 0x104B7),
    (0x104E0, 0x104B8),
    (0x104E1, 0x104B9),
    (0x104E2, 0x104BA),
    (0x104E3, 0x104BB),
    (0x104E4, 0x104BC),
    (0x104E5, 0x104BD),
    (0x104E6, 0x104BE),
    (0x104E7, 0x104BF),
    (0x104E8, 0x104C0),
    (0x104E9, 0x104C1),
    (0x104EA, 0x104C2),
    (0x104EB, 0x104C3),
    (0x104EC, 0x104C4),
    (0x104ED, 0x104C5),
    (0x104EE, 0x104C6),
    (0x104EF, 0x104C7),
    (0x104F0, 0x104C8),
    (0x104F1, 0x104C9),
    (0x104F2, 0x104CA),
    (0x104F3, 0x104CB),
    (0x104F4, 0x104CC),
    (0x104F5, 0x104CD),
    (0x104F6, 0x104CE),
    (0x104F7, 0x104CF),
    (0x104F8, 0x104D0),
    (0x104F9, 0x104D1),
    (0x104FA, 0x104D2),
    (0x104FB, 0x104D3),
    (0x10597, 0x10570),
    (0x10598, 0x10571),
    (0x10599, 0x10572),
    (0x1059A, 0x10573),
    (0x1059B, 0x10574),
    (0x1059C, 0x10575),
    (0x1059D, 0x10576),
    (0x1059E, 0x10577),
    (0x1059F, 0x10578),
    (0x105A0, 0x10579),
    (0x105A1, 0x1057A),
    (0x105A3, 0x1057C),
    (0x105A4, 0x1057D),
    (0x105A5, 0x1057E),
    (0x105A6, 0x1057F),
    (0x105A7, 0x10580),
    (0x105A8, 0x10581),
    (0x105A9, 0x10582),
    (0x105AA, 0x10583),
    (0x105AB, 0x10584),
    (0x105AC, 0x10585),
    (0x105AD, 0x10586),
    (0x105AE, 0x10587),
    (0x105AF, 0x10588),
    (0x105B0, 0x10589),
    (0x105B1, 0x1058A),
    (0x105B3, 0x1058C),
    (0x105B4, 0x1058D),
    (0x105B5, 0x1058E),
    (0x105B6, 0x1058F),
    (0x105B7, 0x10590),
    (0x105B8, 0x10591),
    (0x105B9, 0x10592),
    (0x105BB, 0x10594),
    (0x105BC, 0x10595),
    (0x10CC0, 0x10C80),
    (0x10CC1, 0x10C81),
    (0x10CC2, 0x10C82),
    (0x10CC3, 0x10C83),
    (0x10CC4, 0x10C84),
    (0x10CC5, 0x10C85),
    (0x10CC6, 0x10C86),
    (0x10CC7, 0x10C87),
    (0x10CC8, 0x10C88),
    (0x10CC9, 0x10C89),
    (0x10CCA, 0x10C8A),
    (0x10CCB, 0x10C8B),
    (0x10CCC, 0x10C8C),
    (0x10CCD, 0x10C8D),
    (0x10CCE, 0x10C8E),
    (0x10CCF, 0x10C8F),
    (0x10CD0, 0x10C90),
    (0x10CD1, 0x10C91),
    (0x10CD2, 0x10C92),
    (0x10CD3, 0x10C93),
    (0x10CD4, 0x10C94),
    (0x10CD5, 0x10C95),
    (0x10CD6, 0x10C96),
    (0x10CD7, 0x10C97),
    (0x10CD8, 0x10C98),
    (0x10CD9, 0x10C99),
    (0x10CDA, 0x10C9A),
    (0x10CDB, 0x10C9B),
    (0x10CDC, 0x10C9C),
    (0x10CDD, 0x10C9D),
    (0x10CDE, 0x10C9E),
    (0x10CDF, 0x10C9F),
    (0x10CE0, 0x10CA0),
    (0x10CE1, 0x10CA1),
    (0x10CE2, 0x10CA2),
    (0x10CE3, 0x10CA3),
    (0x10CE4, 0x10CA4),
    (0x10CE5, 0x10CA5),
    (0x10CE6, 0x10CA6),
    (0x10CE7, 0x10CA7),
    (0x10CE8, 0x10CA8),
    (0x10CE9, 0x10CA9),
    (0x10CEA, 0x10CAA),
    (0x10CEB, 0x10CAB),
    (0x10CEC, 0x10CAC),
    (0x10CED, 0x10CAD),
    (0x10CEE, 0x10CAE),
    (0x10CEF, 0x10CAF),
    (0x10CF0, 0x10CB0),
    (0x10CF1, 0x10CB1),
    (0x10CF2, 0x10CB2),
    (0x118C0, 0x118A0),
    (0x118C1, 0x118A1),
    (0x118C2, 0x118A2),
    (0x118C3, 0x118A3),
    (0x118C4, 0x118A4),
    (0x118C5, 0x118A5),
    (0x118C6, 0x118A6),
    (0x118C7, 0x118A7),
    (0x118C8, 0x118A8),
    (0x118C9, 0x118A9),
    (0x118CA, 0x118AA),
    (0x118CB, 0x118AB),
    (0x118CC, 0x118AC),
    (0x118CD, 0x118AD),
    (0x118CE, 0x118AE),
    (0x118CF, 0x118AF),
    (0x118D0, 0x118B0),
    (0x118D1, 0x118B1),
    (0x118D2, 0x118B2),
    (0x118D3, 0x118B3),
    (0x118D4, 0x118B4),
    (0x118D5, 0x118B5),
    (0x118D6, 0x118B6),
    (0x118D7, 0x118B7),
    (0x118D8, 0x118B8),
    (0x118D9, 0x118B9),
    (0x118DA, 0x118BA),
    (0x118DB, 0x118BB),
    (0x118DC, 0x118BC),
    (0x118DD, 0x118BD),
    (0x118DE, 0x118BE),
    (0x118DF, 0x118BF),
    (0x16E60, 0x16E40),
    (0x16E61, 0x16E41),
    (0x16E62, 0x16E42),
    (0x16E63, 0x16E43),
    (0x16E64, 0x16E44),
    (0x16E65, 0x16E45),
    (0x16E66, 0x16E46),
    (0x16E67, 0x16E47),
    (0x16E68, 0x16E48),
    (0x16E69, 0x16E49),
    (0x16E6A, 0x16E4A),
    (0x16E6B, 0x16E4B),
    (0x16E6C, 0x16E4C),
    (0x16E6D, 0x16E4D),
    (0x16E6E, 0x16E4E),
    (0x16E6F, 0x16E4F),
    (0x16E70, 0x16E50),
    (0x16E71, 0x16E51),
    (0x16E72, 0x16E52),
    (0x16E73, 0x16E53),
    (0x16E74, 0x16E54),
    (0x16E75, 0x16E55),
    (0x16E76, 0x16E56),
    (0x16E77, 0x16E57),
    (0x16E78, 0x16E58),
    (0x16E79, 0x16E59),
    (0x16E7A, 0x16E5A),
    (0x16E7B, 0x16E5B),
    (0x16E7C, 0x16E5C),
    (0x16E7D, 0x16E5D),
    (0x16E7E, 0x16E5E),
    (0x16E7F, 0x16E5F),
    (0x1E922, 0x1E900),
    (0x1E923, 0x1E901),
    (0x1E924, 0x1E902),
    (0x1E925, 0x1E903),
    (0x1E926, 0x1E904),
    (0x1E927, 0x1E905),
    (0x1E928, 0x1E906),
    (0x1E929, 0x1E907),
    (0x1E92A, 0x1E908),
    (0x1E92B, 0x1E909),
    (0x1E92C, 0x1E90A),
    (0x1E92D, 0x1E90B),
    (0x1E92E, 0x1E90C),
    (0x1E92F, 0x1E90D),
    (0x1E930, 0x1E90E),
    (0x1E931, 0x1E90F),
    (0x1E932, 0x1E910),
    (0x1E933, 0x1E911),
    (0x1E934, 0x1E912),
    (0x1E935, 0x1E913),
    (0x1E936, 0x1E914),
    (0x1E937, 0x1E915),
    (0x1E938, 0x1E916),
    (0x1E939, 0x1E917),
    (0x1E93A, 0x1E918),
    (0x1E93B, 0x1E919),
    (0x1E93C, 0x1E91A),
    (0x1E93D, 0x1E91B),
    (0x1E93E, 0x1E91C),
    (0x1E93F, 0x1E91D),
    (0x1E940, 0x1E91E),
    (0x1E941, 0x1E91F),
    (0x1E942, 0x1E920),
    (0x1E943, 0x1E921),
];
#[cfg(not(windows))]
pub fn start_agent(_: &Path, _: &str, _: &Path) -> Result<()> {
    bail!("Windows update agent is required")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::traffic::{Category, Traffic};
    use std::sync::Arc;

    fn stage_release() -> Release {
        Release {
            version: "1.6.1".into(),
            url: Url::parse("https://untrusted.invalid/installer.exe").unwrap(),
            sha256: "a".repeat(64),
        }
    }

    #[tokio::test]
    async fn staging_rejects_an_untrusted_destination_and_removes_its_prepared_receipt() {
        let root = std::env::temp_dir().join(format!("NetLightsStageRejected-{}", unique_name()));
        let traffic = Arc::new(Traffic::default());
        let transport = Transport::new(traffic.clone(), Category::Updates).unwrap();
        let error = tokio::time::timeout(
            Duration::from_secs(1),
            stage_update(&transport, &stage_release(), &root),
        )
        .await
        .expect("An untrusted download destination must be rejected without network waits")
        .unwrap_err();
        assert!(
            error
                .to_string()
                .contains("Download redirect isn't allowed")
        );
        assert!(
            traffic.snapshot().is_empty(),
            "Destination rejection opened a transport connection"
        );
        assert!(
            root.is_dir(),
            "The staged API didn't exercise receipt preparation"
        );
        assert_eq!(
            fs::read_dir(&root).unwrap().count(),
            0,
            "A rejected download retained its transaction receipt or payload"
        );
        fs::remove_dir(root).unwrap();
    }

    #[tokio::test]
    async fn invalid_staging_identity_never_creates_the_update_directory() {
        let root = std::env::temp_dir().join(format!("NetLightsStageIdentity-{}", unique_name()));
        let traffic = Arc::new(Traffic::default());
        let transport = Transport::new(traffic.clone(), Category::Updates).unwrap();
        for release in [
            Release {
                version: "../../outside".into(),
                ..stage_release()
            },
            Release {
                sha256: "not-a-sha256".into(),
                ..stage_release()
            },
        ] {
            let error = stage_update(&transport, &release, &root).await.unwrap_err();
            assert!(error.to_string().contains("Invalid update identity"));
            assert!(
                !root.exists(),
                "Invalid metadata created a staging directory"
            );
        }
        assert!(traffic.snapshot().is_empty());
    }

    #[tokio::test]
    async fn invalid_current_version_fails_release_lookup_before_any_request() {
        let traffic = Arc::new(Traffic::default());
        let transport = Transport::new(traffic.clone(), Category::Updates).unwrap();
        let error = tokio::time::timeout(
            Duration::from_secs(1),
            find_update(&transport, "not-a-version"),
        )
        .await
        .expect("Invalid local version reached a network wait")
        .unwrap_err();
        assert!(error.to_string().contains("Invalid current version"));
        assert!(traffic.snapshot().is_empty());
    }

    #[test]
    fn public_staging_cleanup_preserves_foreign_directories_and_removes_only_owned_payloads() {
        let directory =
            std::env::temp_dir().join(format!("NetLightsStageContainment-{}", unique_name()));
        let root = directory.join("updates");
        let stage = create_owned_stage(&root);
        let outside = create_owned_stage(&directory.join("foreign"));
        let installer = "NetLights-Setup-win-x64-1.6.1.exe";
        let sentinel = root.join("keep.txt");
        fs::write(&sentinel, b"unrelated user data").unwrap();
        assert!(cleanup_staged_update(&outside.join(installer), &root).is_err());
        assert_eq!(
            fs::read(outside.join(installer)).unwrap(),
            b"partial installer"
        );
        assert!(outside.join(".stage.json").is_file());
        cleanup_staged_update(&stage.join(installer), &root).unwrap();
        assert!(!stage.exists());
        assert_eq!(fs::read(&sentinel).unwrap(), b"unrelated user data");
        assert!(outside.exists());
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn versions_are_numeric_and_stable() {
        assert!(version("v1.10.0") > version("1.9.9"));
        assert_eq!(version("1.6.0+commit"), version("1.6.0"));
        assert!(version("1.6.0-beta").is_none());
        assert!(version("1.6/0").is_none());
    }
    #[test]
    fn legacy_identity_casing_preserves_distinct_paths_and_dotnet_scalar_semantics() {
        assert_eq!(
            legacy_invariant_upper("C:\\Users\\Straße\\NetLights.exe"),
            "C:\\USERS\\STRAßE\\NETLIGHTS.EXE"
        );
        assert_ne!(
            legacy_invariant_upper("Straße"),
            legacy_invariant_upper("Strasse")
        );
        assert_eq!(legacy_invariant_upper("ſıΐᾀéя"), "SıΐᾈÉЯ");
    }
    #[test]
    fn asset_paths_reject_traversal_and_wrong_owners() {
        assert!(safe_filename("NetLights-Setup-win-x64-1.6.1.exe"));
        for name in [
            "../NetLights-Setup-win-x64-1.6.1.exe",
            "NetLights-Setup-win-x64-1.6.1.exe:stream",
            "NetLights-Setup-win-x64-x.exe",
        ] {
            assert!(!safe_filename(name));
        }
        for target in [
            "http://github.com/AryaPaw/net-lights/releases/download/v1/a",
            "https://github.com/other/project/releases/download/v1/a",
            "https://github.com.evil/AryaPaw/net-lights/releases/download/v1/a",
            "https://user@github.com/AryaPaw/net-lights/releases/download/v1/a",
        ] {
            assert!(!allowed_download(&Url::parse(target).unwrap(), false));
        }
    }
    #[test]
    fn manifest_selects_exact_verified_candidate_and_rejects_duplicates() {
        let asset = serde_json::json!({"name":"NetLights-Setup-win-x64-1.6.1.exe","size":10,"browser_download_url":"https://github.com/AryaPaw/net-lights/releases/download/v1.6.1/NetLights-Setup-win-x64-1.6.1.exe","digest":format!("sha256:{}", "a".repeat(64))});
        let manifest = |assets| {
            serde_json::to_vec(
                &serde_json::json!({"tag_name":"v1.6.1","prerelease":false,"assets":assets}),
            )
            .unwrap()
        };
        assert_eq!(
            parse_release(&manifest(vec![asset.clone()]), [1, 6, 0, 0])
                .unwrap()
                .unwrap()
                .version,
            "1.6.1"
        );
        assert!(parse_release(&manifest(vec![asset.clone(), asset]), [1, 6, 0, 0]).is_err());
    }
    #[test]
    fn interrupted_transaction_or_missing_agent_blocks_preflight_without_mutating_or_staging() {
        let directory =
            std::env::temp_dir().join(format!("NetLightsUpdatePreflight-{}", unique_name()));
        let installation = directory.join("installed");
        fs::create_dir_all(&installation).unwrap();
        let executable = installation.join("NetLights.exe");
        for name in ["NetLights.exe", "unins000.exe", "NetLights.UpdateAgent.exe"] {
            fs::write(installation.join(name), b"owned fixture").unwrap();
        }
        let root = directory.join("updates");
        assert!(preflight(&root, &executable).is_ok());
        assert!(!root.exists());
        fs::remove_file(installation.join("NetLights.UpdateAgent.exe")).unwrap();
        assert!(preflight(&root, &executable).is_err());
        assert!(!root.exists());
        fs::write(
            installation.join("NetLights.UpdateAgent.exe"),
            b"owned fixture",
        )
        .unwrap();
        fs::create_dir(&root).unwrap();
        let (_, identity) = installation_identity(&executable).unwrap();
        let marker = root.join(format!("active-{identity}.json"));
        let bytes = b"unreadable or foreign state must be preserved";
        fs::write(&marker, bytes).unwrap();
        let blocked = preflight(&root, &executable).unwrap_err();
        assert!(blocked.is::<UnresolvedTransaction>());
        assert_eq!(blocked.to_string(), UNRESOLVED_TRANSACTION_NOTICE);
        let another = directory.join("another-installation");
        fs::create_dir(&another).unwrap();
        for name in ["NetLights.exe", "unins000.exe", "NetLights.UpdateAgent.exe"] {
            fs::write(another.join(name), b"another owned installation").unwrap();
        }
        assert!(
            preflight(&root, &another.join("NetLights.exe")).is_ok(),
            "Another installation's interrupted marker mustn't block this installation"
        );
        assert_eq!(fs::read(&marker).unwrap(), bytes);
        assert_eq!(
            fs::read_dir(&root).unwrap().count(),
            1,
            "Preflight created a staging payload before refusing the interrupted update"
        );
        fs::remove_dir_all(directory).unwrap();
    }
    #[test]
    fn persisted_failures_guard_same_candidate_and_corruption_fails_closed() {
        let root = std::env::temp_dir().join(format!("NetLightsRetry-{}", unique_name()));
        fs::create_dir(&root).unwrap();
        let release = Release {
            version: "1.6.1".into(),
            url: Url::parse("https://github.com/").unwrap(),
            sha256: "a".repeat(64),
        };
        assert!(retry_allowed(&root, &release));
        record_failure(&root, &release.version, &release.sha256).unwrap();
        assert!(!retry_allowed(&root, &release));
        let changed = Release {
            sha256: "b".repeat(64),
            ..release
        };
        assert!(retry_allowed(&root, &changed));
        fs::write(root.join("failed-candidate.json"), b"null").unwrap();
        assert!(!retry_allowed(&root, &changed));
        fs::remove_dir_all(root).unwrap();
    }
    fn create_owned_stage(root: &Path) -> PathBuf {
        fs::create_dir_all(root).unwrap();
        let stage = root.join(unique_name());
        fs::create_dir(&stage).unwrap();
        let name = "NetLights-Setup-win-x64-1.6.1.exe";
        atomic_write(
            &stage.join(".stage.json"),
            &serde_json::to_vec(&StageReceipt {
                schema: 1,
                owner: "NetLights".into(),
                installer: name.into(),
            })
            .unwrap(),
        )
        .unwrap();
        fs::write(stage.join(name), b"partial installer").unwrap();
        stage
    }
    #[tokio::test]
    async fn cancelled_download_lifetime_cleans_its_owned_partial_payload() {
        let root = std::env::temp_dir().join(format!("NetLightsStageCancel-{}", unique_name()));
        let stage = create_owned_stage(&root);
        let path = stage.clone();
        let (started, ready) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(async move {
            let _owned = PendingStage {
                directory: path,
                armed: true,
            };
            let _ = started.send(());
            std::future::pending::<()>().await;
        });
        ready.await.unwrap();
        task.abort();
        assert!(task.await.unwrap_err().is_cancelled());
        assert!(!stage.exists());
        fs::remove_dir(root).unwrap();
    }
    #[test]
    fn stale_cleanup_preserves_active_transactions_and_unrelated_or_unproven_directories() {
        let root = std::env::temp_dir().join(format!("NetLightsStageSweep-{}", unique_name()));
        let stage = create_owned_stage(&root);
        let unrelated = root.join(unique_name());
        fs::create_dir(&unrelated).unwrap();
        fs::write(unrelated.join("user-data"), b"preserve").unwrap();
        let rollback = root.join("rollback");
        fs::create_dir(&rollback).unwrap();
        fs::write(rollback.join("backup"), b"preserve").unwrap();
        fs::write(root.join("active-transaction.json"), b"active").unwrap();
        assert_eq!(cleanup_old_staging(&root, Duration::ZERO).unwrap(), 0);
        assert!(stage.exists());
        fs::remove_file(root.join("active-transaction.json")).unwrap();
        assert_eq!(cleanup_old_staging(&root, Duration::ZERO).unwrap(), 1);
        assert!(!stage.exists());
        assert_eq!(fs::read(unrelated.join("user-data")).unwrap(), b"preserve");
        assert_eq!(fs::read(rollback.join("backup")).unwrap(), b"preserve");
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(windows)]
    #[test]
    fn locked_payload_retains_its_receipt_for_a_later_cleanup_retry() {
        use std::os::windows::fs::OpenOptionsExt;
        let root = std::env::temp_dir().join(format!("NetLightsStageLocked-{}", unique_name()));
        let stage = create_owned_stage(&root);
        let agent = stage.join("NetLights.UpdateAgent.exe");
        fs::write(&agent, b"locked agent").unwrap();
        let lock = OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(&agent)
            .unwrap();
        assert!(cleanup_owned_stage(&stage).is_err());
        assert!(stage.join(".stage.json").is_file());
        drop(lock);
        cleanup_owned_stage(&stage).unwrap();
        assert!(!stage.exists());
        fs::remove_dir(root).unwrap();
    }
}
