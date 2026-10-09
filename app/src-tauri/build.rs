fn main() {
    // The DXC DLLs are embedded in the exe (src/dxc.rs); rebuild when they change
    for file in ["dxcompiler.dll", "dxil.dll"] {
        let path: String = format!("../../third_party/dxc/{file}");
        assert!(std::path::Path::new(&path).is_file(), "{path} is missing: run scripts/fetch-dxc.ps1");
        println!("cargo:rerun-if-changed={path}");
    }
    tauri_build::build()
}
