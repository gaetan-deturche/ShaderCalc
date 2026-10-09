//! Self-update from GitHub releases. The latest release carries the exe, `update.json` (version, notes, exe size and
//! SHA-256) and `update.json.sig`, an ed25519 signature of it made by the release workflow
//! (scripts/sign-release.mjs). The signature is checked against the public key built into the app, then the exe
//! against the manifest; the running exe is renamed to `.old` (Windows can't overwrite it) and the new one put in its
//! place, ready for a restart.

use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::sync::Mutex;

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const LATEST_RELEASE: &str = "https://api.github.com/repos/gaetan-deturche/ShaderCalc/releases/latest";

/// Base64 of the raw 32-byte ed25519 public key (scripts/new-signing-key.ps1 writes it). Empty: no updates.
const PUBLIC_KEY: &str = include_str!("../update-public-key.txt");

const USER_AGENT: &str = concat!("ShaderCalc/", env!("CARGO_PKG_VERSION"));

#[derive(Deserialize)]
struct Release {
    assets: Vec<Asset>,
}

#[derive(Deserialize)]
struct Asset {
    name: String,
    browser_download_url: String,
}

#[derive(Clone, Deserialize)]
struct Manifest {
    version: String,
    notes: String,
    exe: ExeInfo,
}

#[derive(Clone, Deserialize)]
struct ExeInfo {
    name: String,
    size: u64,
    sha256: String,
}

/// A newer version than this one, as the UI shows it.
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Available {
    pub version: String,
    pub current: String,
    pub notes: String,
}

/// The update found by the last check, waiting for `install`.
#[derive(Clone)]
struct Pending {
    manifest: Manifest,
    exe_url: String,
}

static PENDING: Mutex<Option<Pending>> = Mutex::new(None);

fn public_key() -> Option<VerifyingKey> {
    let bytes: Vec<u8> = BASE64.decode(PUBLIC_KEY.trim()).ok()?;
    VerifyingKey::from_bytes(&bytes.try_into().ok()?).ok()
}

fn download(url: &str, limit: u64) -> Result<Vec<u8>, String> {
    let response: ureq::Response = ureq::get(url)
        .set("User-Agent", USER_AGENT)
        .set("Accept", "application/octet-stream")
        .call()
        .map_err(|error| format!("{url}: {error}"))?;
    let mut bytes: Vec<u8> = Vec::new();
    response.into_reader().take(limit + 1).read_to_end(&mut bytes).map_err(|error| error.to_string())?;
    if bytes.len() as u64 > limit {
        return Err(format!("{url} is larger than expected"));
    }
    Ok(bytes)
}

/// Asks GitHub for the latest release; `Some` when it is newer than this build and correctly signed.
pub fn check() -> Result<Option<Available>, String> {
    // A build without a key (a local one) never updates
    let Some(key) = public_key() else {
        return Ok(None);
    };
    // SHADERCALC_UPDATE_URL points at another release (a local test); the signature check still applies
    let url: String = std::env::var("SHADERCALC_UPDATE_URL").unwrap_or_else(|_| LATEST_RELEASE.to_string());
    let release: Release = ureq::get(&url)
        .set("User-Agent", USER_AGENT)
        .set("Accept", "application/vnd.github+json")
        .call()
        .map_err(|error| format!("GitHub: {error}"))?
        .into_json()
        .map_err(|error| format!("GitHub's answer: {error}"))?;
    let asset_url = |name: &str| -> Result<String, String> {
        release
            .assets
            .iter()
            .find(|asset| asset.name == name)
            .map(|asset| asset.browser_download_url.clone())
            .ok_or_else(|| format!("the latest release has no {name}"))
    };

    let manifest_bytes: Vec<u8> = download(&asset_url("update.json")?, 64 * 1024)?;
    let signature_text: Vec<u8> = download(&asset_url("update.json.sig")?, 1024)?;
    let signature_bytes: [u8; 64] = BASE64
        .decode(String::from_utf8_lossy(&signature_text).trim())
        .ok()
        .and_then(|bytes| bytes.try_into().ok())
        .ok_or("update.json.sig isn't a signature")?;
    key.verify(&manifest_bytes, &Signature::from_bytes(&signature_bytes))
        .map_err(|_| "update.json isn't signed with ShaderCalc's key: ignored".to_string())?;
    let manifest: Manifest =
        serde_json::from_slice(&manifest_bytes).map_err(|error| format!("update.json: {error}"))?;

    let current: semver::Version = semver::Version::parse(env!("CARGO_PKG_VERSION")).expect("the crate version");
    let offered: semver::Version =
        semver::Version::parse(&manifest.version).map_err(|error| format!("update.json's version: {error}"))?;
    let mut pending = PENDING.lock().expect("update lock");
    if offered <= current {
        *pending = None;
        return Ok(None);
    }
    let exe_url: String = asset_url(&manifest.exe.name)?;
    let available: Available =
        Available { version: manifest.version.clone(), current: current.to_string(), notes: manifest.notes.clone() };
    *pending = Some(Pending { manifest, exe_url });
    Ok(Some(available))
}

/// Downloads the update found by `check`, checks it against the signed manifest and puts it in place of the
/// running exe (which becomes `<exe>.old`). The new version starts on the next launch or `restart`.
pub fn install() -> Result<(), String> {
    let pending: Pending = PENDING.lock().expect("update lock").clone().ok_or("no update to install")?;
    let expected: &ExeInfo = &pending.manifest.exe;
    let bytes: Vec<u8> = download(&pending.exe_url, expected.size)?;
    if bytes.len() as u64 != expected.size {
        return Err(format!("the download is {} bytes, update.json says {}", bytes.len(), expected.size));
    }
    let digest: String = Sha256::digest(&bytes).iter().map(|byte| format!("{byte:02x}")).collect();
    if !digest.eq_ignore_ascii_case(&expected.sha256) {
        return Err("the download doesn't match update.json's SHA-256".to_string());
    }

    let current: PathBuf = std::env::current_exe().map_err(|error| error.to_string())?;
    let next: PathBuf = current.with_extension("exe.new");
    let previous: PathBuf = current.with_extension("exe.old");
    fs::write(&next, &bytes).map_err(|error| format!("{}: {error}", next.display()))?;
    let _ = fs::remove_file(&previous);
    fs::rename(&current, &previous).map_err(|error| format!("{}: {error}", current.display()))?;
    if let Err(error) = fs::rename(&next, &current) {
        let _ = fs::rename(&previous, &current);
        return Err(format!("{}: {error}", current.display()));
    }
    Ok(())
}

/// Removes the exe an update replaced, once its process is gone.
pub fn remove_previous() {
    if let Ok(current) = std::env::current_exe() {
        let _ = fs::remove_file(current.with_extension("exe.old"));
    }
}
