//! The DXC DLLs travel inside the exe and are unpacked once into the local app data folder, so ShaderCalc.exe works
//! on its own.

use std::path::{Path, PathBuf};

const DXCOMPILER: &[u8] = include_bytes!("../../../third_party/dxc/dxcompiler.dll");
const DXIL: &[u8] = include_bytes!("../../../third_party/dxc/dxil.dll");

fn unpack(folder: &Path, name: &str, bytes: &[u8]) -> std::io::Result<()> {
    let path: PathBuf = folder.join(name);
    if std::fs::metadata(&path).is_ok_and(|metadata| metadata.len() == bytes.len() as u64) {
        return Ok(());
    }
    // Through a temporary file: another ShaderCalc starting at the same time never loads half a DLL
    let temporary: PathBuf = folder.join(format!("{name}.{}.tmp", std::process::id()));
    std::fs::write(&temporary, bytes)?;
    std::fs::rename(&temporary, &path).or_else(|error| if path.is_file() { Ok(()) } else { Err(error) })
}

/// Points the reference at the embedded DLLs, unless SHADERCALC_DXC_DIR already names a folder.
pub fn install() -> std::io::Result<()> {
    if std::env::var_os("SHADERCALC_DXC_DIR").is_some() {
        return Ok(());
    }
    let base: PathBuf = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    // Named by size, so a DXC update unpacks next to the old one instead of over a loaded DLL
    let folder: PathBuf = base.join("ShaderCalc").join(format!("dxc-{}-{}", DXCOMPILER.len(), DXIL.len()));
    std::fs::create_dir_all(&folder)?;
    unpack(&folder, "dxil.dll", DXIL)?;
    unpack(&folder, "dxcompiler.dll", DXCOMPILER)?;
    // Before any other thread exists
    unsafe { std::env::set_var("SHADERCALC_DXC_DIR", &folder) };
    Ok(())
}
