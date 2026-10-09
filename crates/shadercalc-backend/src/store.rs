use std::collections::HashMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use serde::{Deserialize, Serialize};

pub const EXTENSION: &str = ".hlsl";

const WELCOME: &str = "// ShaderCalc: every line is HLSL, its value shows on the right.
// Functions, structs and #defines are shared by every tab. Click a result for its bits,
// units and the check against DXC + WARP. F1 on a name opens its documentation.

float KineticEnergy(float mass, float speed)
{
    return 0.5 * mass * speed * speed;
}

KineticEnergy(2 kg, 3 m/s)
float3 n = normalize(float3(1, 2, 3))
asuint(n.x)
f16tof32(f32tof16(0.1))
uint(5) - uint(7)
pow(-2.0, 3.0)";

/// Tab and panel state kept between runs (state.json; the window placement belongs to the window).
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppState {
    pub tab_order: Vec<String>,
    pub active_tab: Option<String>,
    pub side_panel_width: f64,
    pub result_column_width: f64,
    /// The semantics profile the worksheets run with ("hlsl", "slang-cpu").
    pub profile: String,
}

impl Default for AppState {
    fn default() -> AppState {
        AppState {
            tab_order: Vec::new(),
            active_tab: None,
            side_panel_width: 420.0,
            result_column_width: 340.0,
            profile: "hlsl".to_string(),
        }
    }
}

/// What a file looked like when the app last read or wrote it.
struct Known {
    name: String,
    text: String,
    modified: Option<SystemTime>,
    length: u64,
}

/// Files edited, added or removed outside the app since the last look.
#[derive(Clone, Debug, Default, Serialize)]
pub struct OutsideChanges {
    pub changed: Vec<(String, String)>,
    pub added: Vec<(String, String)>,
    pub removed: Vec<String>,
}

impl OutsideChanges {
    pub fn is_empty(&self) -> bool {
        self.changed.is_empty() && self.added.is_empty() && self.removed.is_empty()
    }
}

/// The worksheet files: every *.hlsl in the data folder (~/.shadercalc, or SHADERCALC_DATA_DIR) is a tab. Writes go
/// through a temporary file; edits made outside the app are found by comparing with what the app last saw.
pub struct WorksheetStore {
    pub folder: PathBuf,
    pub state: AppState,
    /// By lower-case name: Windows file names ignore case.
    known: HashMap<String, Known>,
}

fn key(name: &str) -> String {
    name.to_lowercase()
}

fn stamp(path: &Path) -> (Option<SystemTime>, u64) {
    match fs::metadata(path) {
        Ok(metadata) => (metadata.modified().ok(), metadata.len()),
        Err(_) => (None, 0),
    }
}

impl WorksheetStore {
    pub fn default_folder() -> PathBuf {
        if let Some(folder) = std::env::var_os("SHADERCALC_DATA_DIR") {
            return PathBuf::from(folder);
        }
        let home: PathBuf =
            std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME")).map(PathBuf::from).unwrap_or_default();
        home.join(".shadercalc")
    }

    pub fn open(folder: PathBuf) -> io::Result<WorksheetStore> {
        fs::create_dir_all(&folder)?;
        let state: AppState = fs::read_to_string(folder.join("state.json"))
            .ok()
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        let mut store: WorksheetStore = WorksheetStore { folder, state, known: HashMap::new() };
        if store.list_files()?.is_empty() {
            store.write(&format!("scratch{EXTENSION}"), WELCOME)?;
        }
        Ok(store)
    }

    fn path(&self, name: &str) -> PathBuf {
        self.folder.join(name)
    }

    fn list_files(&self) -> io::Result<Vec<String>> {
        let mut names: Vec<String> = Vec::new();
        for entry in fs::read_dir(&self.folder)? {
            let entry = entry?;
            let name: String = entry.file_name().to_string_lossy().into_owned();
            if entry.file_type()?.is_file() && name.to_lowercase().ends_with(EXTENSION) {
                names.push(name);
            }
        }
        Ok(names)
    }

    /// Files in tab order: the saved order first, then new files by name.
    pub fn ordered_names(&self) -> io::Result<Vec<String>> {
        let files: Vec<String> = self.list_files()?;
        let mut ordered: Vec<String> = self
            .state
            .tab_order
            .iter()
            .filter_map(|name| files.iter().find(|file| key(file) == key(name)).cloned())
            .collect();
        let mut rest: Vec<String> =
            files.into_iter().filter(|file| !ordered.iter().any(|name| key(name) == key(file))).collect();
        rest.sort_by_key(|name| key(name));
        ordered.extend(rest);
        Ok(ordered)
    }

    fn remember(&mut self, name: &str, text: &str) {
        let (modified, length) = stamp(&self.path(name));
        self.known.insert(key(name), Known { name: name.to_string(), text: text.to_string(), modified, length });
    }

    pub fn read(&mut self, name: &str) -> io::Result<String> {
        let text: String = fs::read_to_string(self.path(name))?;
        self.remember(name, &text);
        Ok(text)
    }

    /// Saves through a temporary file, so a crash never leaves a half-written worksheet.
    pub fn write(&mut self, name: &str, text: &str) -> io::Result<()> {
        if self.known.get(&key(name)).is_some_and(|known| known.text == text) && self.path(name).exists() {
            return Ok(());
        }
        let path: PathBuf = self.path(name);
        let temporary: PathBuf = self.path(&format!("{name}.tmp"));
        fs::write(&temporary, text)?;
        fs::rename(&temporary, &path)?;
        self.remember(name, text);
        Ok(())
    }

    /// A new empty worksheet named "worksheet N.hlsl".
    pub fn create(&mut self) -> io::Result<String> {
        let existing: Vec<String> = self.list_files()?.iter().map(|name| key(name)).collect();
        let mut number: u32 = 1;
        let name: String = loop {
            let candidate: String = format!("worksheet {number}{EXTENSION}");
            number += 1;
            if !existing.contains(&key(&candidate)) {
                break candidate;
            }
        };
        self.write(&name, "")?;
        Ok(name)
    }

    /// Renames a worksheet; returns the new file name, or why it can't.
    pub fn rename(&mut self, name: &str, new_title: &str) -> Result<String, String> {
        let mut new_name: String = new_title.trim().to_string();
        if !new_name.to_lowercase().ends_with(EXTENSION) {
            new_name.push_str(EXTENSION);
        }
        const INVALID: [char; 9] = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
        if new_name.len() <= EXTENSION.len()
            || new_name.chars().any(|character| INVALID.contains(&character) || character.is_control())
        {
            return Err("That isn't a valid file name.".to_string());
        }
        if key(&new_name) != key(name) && self.path(&new_name).exists() {
            return Err(format!("{new_name} already exists."));
        }
        fs::rename(self.path(name), self.path(&new_name)).map_err(|error| error.to_string())?;
        if let Some(known) = self.known.remove(&key(name)) {
            let text: String = known.text;
            self.remember(&new_name, &text);
        }
        Ok(new_name)
    }

    /// Sends a worksheet to the Recycle Bin.
    pub fn delete(&mut self, name: &str) -> Result<(), String> {
        trash::delete(self.path(name)).map_err(|error| error.to_string())?;
        self.known.remove(&key(name));
        Ok(())
    }

    /// Compares the folder with what the app last saw.
    pub fn outside_changes(&mut self) -> io::Result<OutsideChanges> {
        let files: Vec<String> = self.list_files()?;
        let mut changes: OutsideChanges = OutsideChanges::default();
        for name in &files {
            let path: PathBuf = self.path(name);
            let (modified, length) = stamp(&path);
            match self.known.get(&key(name)) {
                Some(known) if known.modified == modified && known.length == length => {}
                Some(known) => {
                    // Still being written: the next look will catch it
                    let Ok(text) = fs::read_to_string(&path) else {
                        continue;
                    };
                    let is_changed: bool = text != known.text;
                    self.remember(name, &text);
                    if is_changed {
                        changes.changed.push((name.clone(), text));
                    }
                }
                None => {
                    let Ok(text) = fs::read_to_string(&path) else {
                        continue;
                    };
                    self.remember(name, &text);
                    changes.added.push((name.clone(), text));
                }
            }
        }
        let removed: Vec<String> =
            self.known.keys().filter(|known| !files.iter().any(|file| key(file) == **known)).cloned().collect();
        for name in removed {
            if let Some(known) = self.known.remove(&name) {
                changes.removed.push(known.name);
            }
        }
        Ok(changes)
    }

    pub fn save_state(&self) -> io::Result<()> {
        let text: String = serde_json::to_string_pretty(&self.state).map_err(io::Error::other)?;
        fs::write(self.folder.join("state.json"), text)
    }
}
