#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod dxc;

use std::sync::Arc;

use serde_json::Value;
use shadercalc_backend::Backend;

/// Every backend command goes through here (app/src/api.ts), off the UI thread.
#[tauri::command]
async fn run_command(command: String, args: Value, state: tauri::State<'_, Arc<Backend>>) -> Result<Value, String> {
    let backend: Arc<Backend> = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || backend.handle(&command, &args))
        .await
        .map_err(|error| error.to_string())?
}

fn main() {
    // Without the DLLs the worksheet still runs; the reference column then says why it can't check
    if let Err(error) = dxc::install() {
        eprintln!("DXC not unpacked: {error}");
    }
    let backend: Backend = Backend::open().unwrap_or_else(|error| panic!("can't open the worksheet folder: {error}"));
    tauri::Builder::default()
        .plugin(tauri_plugin_window_state::Builder::default().build())
        .manage(Arc::new(backend))
        .invoke_handler(tauri::generate_handler![run_command])
        .run(tauri::generate_context!())
        .expect("ShaderCalc failed to start");
}
