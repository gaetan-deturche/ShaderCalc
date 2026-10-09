#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod dxc;
mod update;

use std::sync::Arc;

use serde_json::Value;
use shadercalc_backend::Backend;
use tauri::AppHandle;
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject};

/// Passed to the new exe by `restart_app`: the process it replaces.
const UPDATED_FROM: &str = "--updated-from";

/// Every backend command goes through here (app/src/api.ts), off the UI thread.
#[tauri::command]
async fn run_command(command: String, args: Value, state: tauri::State<'_, Arc<Backend>>) -> Result<Value, String> {
    let backend: Arc<Backend> = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || backend.handle(&command, &args))
        .await
        .map_err(|error| error.to_string())?
}

#[tauri::command]
async fn check_update() -> Result<Option<update::Available>, String> {
    tauri::async_runtime::spawn_blocking(update::check).await.map_err(|error| error.to_string())?
}

#[tauri::command]
async fn install_update() -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(update::install).await.map_err(|error| error.to_string())?
}

/// Starts the exe now in place (the update) and quits; the page has saved everything first.
#[tauri::command]
fn restart_app(app: AppHandle) -> Result<(), String> {
    let current: std::path::PathBuf = std::env::current_exe().map_err(|error| error.to_string())?;
    std::process::Command::new(current)
        .args([UPDATED_FROM, &std::process::id().to_string()])
        .spawn()
        .map_err(|error| error.to_string())?;
    app.exit(0);
    Ok(())
}

/// After an update restart, waits (10 s at most) for the old process to end, so the two never share WebView2's
/// profile, then removes the old exe.
fn finish_update() {
    let arguments: Vec<String> = std::env::args().collect();
    let Some(pid) = arguments
        .iter()
        .position(|argument| argument == UPDATED_FROM)
        .and_then(|index| arguments.get(index + 1))
        .and_then(|pid| pid.parse::<u32>().ok())
    else {
        update::remove_previous();
        return;
    };
    unsafe {
        if let Ok(process) = OpenProcess(PROCESS_SYNCHRONIZE, false, pid) {
            WaitForSingleObject(process, 10_000);
            let _ = CloseHandle(process);
        }
    }
    update::remove_previous();
}

fn main() {
    finish_update();
    // Without the DLLs the worksheet still runs; the reference column then says why it can't check
    if let Err(error) = dxc::install() {
        eprintln!("DXC not unpacked: {error}");
    }
    let backend: Backend = Backend::open().unwrap_or_else(|error| panic!("can't open the worksheet folder: {error}"));
    tauri::Builder::default()
        .plugin(tauri_plugin_window_state::Builder::default().build())
        .manage(Arc::new(backend))
        .invoke_handler(tauri::generate_handler![run_command, check_update, install_update, restart_app])
        .run(tauri::generate_context!())
        .expect("ShaderCalc failed to start");
}
