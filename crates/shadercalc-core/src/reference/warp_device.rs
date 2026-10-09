use std::collections::HashMap;
use std::ffi::c_void;
use std::mem::ManuallyDrop;
use std::path::{Path, PathBuf};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Instant;

use windows::Win32::Foundation::{HANDLE, HMODULE};
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_11_0;
use windows::Win32::Graphics::Direct3D::Dxc::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory2, DXGI_CREATE_FACTORY_FLAGS, IDXGIAdapter, IDXGIFactory4};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{GUID, HRESULT, HSTRING, Interface, PCWSTR, s};

#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum ReferenceMode {
    /// DXC with -Gis: strict IEEE, every float operation as written (what the interpreter computes).
    Strict,
    /// DXC defaults: float operations may be reassociated or fused, like a real GPU build.
    Default,
}

#[derive(Clone, Copy, PartialEq, Debug, Default)]
pub struct ComputeTimings {
    pub compile_milliseconds: f64,
    pub pipeline_milliseconds: f64,
    pub run_milliseconds: f64,
}

/// Why the reference couldn't run a shader.
#[derive(Clone, PartialEq, Debug)]
pub enum ReferenceError {
    /// DXC rejected the code: its messages.
    Compile(String),
    /// DXC or D3D12 isn't available, or failed outside the shader.
    Unavailable(String),
}

impl std::fmt::Display for ReferenceError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ReferenceError::Compile(errors) => write!(formatter, "DXC rejected the code:\n{errors}"),
            ReferenceError::Unavailable(message) => write!(formatter, "{message}"),
        }
    }
}

pub const ENTRY_POINT: &str = "RefMain";
pub const ROOT_SIGNATURE: &str = "UAV(u0), SRV(t0)";

type DxcCreateInstance = unsafe extern "system" fn(*const GUID, *const GUID, *mut *mut c_void) -> HRESULT;

/// dxcompiler.dll and dxil.dll (the signer, loaded first: dxcompiler finds it by name, in the exe folder only).
struct DxcLibrary {
    create_instance: DxcCreateInstance,
}

/// Where the DXC DLLs are: SHADERCALC_DXC_DIR, the exe's folder, or its `dxc` subfolder.
fn dxc_folders() -> Vec<PathBuf> {
    let mut folders: Vec<PathBuf> = Vec::new();
    if let Some(folder) = std::env::var_os("SHADERCALC_DXC_DIR") {
        folders.push(PathBuf::from(folder));
    }
    if let Some(exe_folder) = std::env::current_exe().ok().and_then(|exe| exe.parent().map(Path::to_path_buf)) {
        folders.push(exe_folder.join("dxc"));
        folders.push(exe_folder);
    }
    folders
}

fn load_library(path: &Path) -> Result<HMODULE, String> {
    let wide: HSTRING = HSTRING::from(path.as_os_str());
    unsafe { LoadLibraryW(&wide) }.map_err(|error| format!("can't load {}: {}", path.display(), error.message()))
}

static DXC: LazyLock<Result<DxcLibrary, String>> = LazyLock::new(|| {
    let folders: Vec<PathBuf> = dxc_folders();
    let Some(folder) = folders.iter().find(|folder| folder.join("dxcompiler.dll").is_file()) else {
        let searched: Vec<String> = folders.iter().map(|folder| folder.display().to_string()).collect();
        return Err(format!("dxcompiler.dll not found (searched {})", searched.join(", ")));
    };
    let signer: PathBuf = folder.join("dxil.dll");
    if signer.is_file() {
        load_library(&signer)?;
    }
    let compiler: HMODULE = load_library(&folder.join("dxcompiler.dll"))?;
    let address = unsafe { GetProcAddress(compiler, s!("DxcCreateInstance")) }
        .ok_or_else(|| "dxcompiler.dll has no DxcCreateInstance".to_string())?;
    let create_instance: DxcCreateInstance = unsafe { std::mem::transmute(address) };
    Ok(DxcLibrary { create_instance })
});

fn create_compiler() -> Result<IDxcCompiler3, ReferenceError> {
    let library: &DxcLibrary = DXC.as_ref().map_err(|error| ReferenceError::Unavailable(error.clone()))?;
    let mut instance: *mut c_void = std::ptr::null_mut();
    unsafe { (library.create_instance)(&CLSID_DxcCompiler, &IDxcCompiler3::IID, &mut instance) }
        .ok()
        .map_err(|error| ReferenceError::Unavailable(format!("DXC: {}", error.message())))?;
    Ok(unsafe { IDxcCompiler3::from_raw(instance) })
}

fn blob_bytes(blob: &IDxcBlob) -> Vec<u8> {
    unsafe { std::slice::from_raw_parts(blob.GetBufferPointer() as *const u8, blob.GetBufferSize()).to_vec() }
}

/// Compiles to signed DXIL, or returns DXC's messages.
pub fn compile(hlsl: &str, mode: ReferenceMode) -> Result<Vec<u8>, ReferenceError> {
    let compiler: IDxcCompiler3 = create_compiler()?;
    let mut arguments: Vec<&str> = vec!["-E", ENTRY_POINT, "-T", "cs_6_0", "-HV", "2021", "-O3"];
    if mode == ReferenceMode::Strict {
        arguments.push("-Gis");
    }
    let wide: Vec<HSTRING> = arguments.iter().map(|argument| HSTRING::from(*argument)).collect();
    let pointers: Vec<PCWSTR> = wide.iter().map(|argument| PCWSTR(argument.as_ptr())).collect();
    let source: DxcBuffer =
        DxcBuffer { Ptr: hlsl.as_ptr() as *const c_void, Size: hlsl.len(), Encoding: DXC_CP_UTF8.0 };
    let unavailable = |error: windows::core::Error| ReferenceError::Unavailable(format!("DXC: {}", error.message()));
    let result: IDxcResult = unsafe { compiler.Compile(&source, Some(&pointers), None) }.map_err(unavailable)?;
    let status: HRESULT = unsafe { result.GetStatus() }.map_err(unavailable)?;
    if status.is_err() {
        let errors: String = match unsafe { result.GetErrorBuffer() } {
            Ok(buffer) => {
                String::from_utf8_lossy(&blob_bytes(&buffer.cast::<IDxcBlob>().map_err(unavailable)?)).into_owned()
            }
            Err(_) => format!("{status:?}"),
        };
        return Err(ReferenceError::Compile(errors.trim_end_matches('\0').trim().to_string()));
    }
    let object: IDxcBlob = unsafe { result.GetResult() }.map_err(unavailable)?;
    Ok(blob_bytes(&object))
}

/// The D3D12 objects on WARP, the software rasterizer that ships with Windows. Used under the lock only.
struct Device {
    device: ID3D12Device,
    queue: ID3D12CommandQueue,
    allocator: ID3D12CommandAllocator,
    fence: ID3D12Fence,
    fence_value: u64,
}

// D3D12 devices are free-threaded; the queue, allocator and fence are only used under WarpDevice's lock.
unsafe impl Send for Device {}

fn d3d_error(context: &str) -> impl Fn(windows::core::Error) -> ReferenceError + '_ {
    move |error: windows::core::Error| ReferenceError::Unavailable(format!("{context}: {}", error.message()))
}

impl Device {
    fn create() -> Result<Device, ReferenceError> {
        unsafe {
            let factory: IDXGIFactory4 =
                CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0)).map_err(d3d_error("DXGI factory"))?;
            let adapter: IDXGIAdapter = factory.EnumWarpAdapter().map_err(d3d_error("WARP adapter"))?;
            let mut device: Option<ID3D12Device> = None;
            D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_11_0, &mut device).map_err(d3d_error("D3D12 device"))?;
            let device: ID3D12Device =
                device.ok_or_else(|| ReferenceError::Unavailable("no D3D12 device".to_string()))?;
            let queue_description: D3D12_COMMAND_QUEUE_DESC =
                D3D12_COMMAND_QUEUE_DESC { Type: D3D12_COMMAND_LIST_TYPE_DIRECT, ..Default::default() };
            let queue: ID3D12CommandQueue =
                device.CreateCommandQueue(&queue_description).map_err(d3d_error("command queue"))?;
            let allocator: ID3D12CommandAllocator = device
                .CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)
                .map_err(d3d_error("command allocator"))?;
            let fence: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE).map_err(d3d_error("fence"))?;
            Ok(Device { device, queue, allocator, fence, fence_value: 0 })
        }
    }

    fn buffer(
        &self,
        heap: D3D12_HEAP_TYPE,
        bytes: u64,
        flags: D3D12_RESOURCE_FLAGS,
        state: D3D12_RESOURCE_STATES,
    ) -> Result<ID3D12Resource, ReferenceError> {
        let properties: D3D12_HEAP_PROPERTIES = D3D12_HEAP_PROPERTIES { Type: heap, ..Default::default() };
        let description: D3D12_RESOURCE_DESC = D3D12_RESOURCE_DESC {
            Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
            Alignment: 0,
            Width: bytes,
            Height: 1,
            DepthOrArraySize: 1,
            MipLevels: 1,
            Format: DXGI_FORMAT_UNKNOWN,
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            Flags: flags,
        };
        let mut resource: Option<ID3D12Resource> = None;
        unsafe {
            self.device
                .CreateCommittedResource(&properties, D3D12_HEAP_FLAG_NONE, &description, state, None, &mut resource)
                .map_err(d3d_error("buffer"))?;
        }
        resource.ok_or_else(|| ReferenceError::Unavailable("no buffer".to_string()))
    }

    fn run(
        &mut self,
        bytecode: &[u8],
        inputs: &[u32],
        output_words: usize,
    ) -> Result<(Vec<u32>, f64, f64), ReferenceError> {
        let mut clock: Instant = Instant::now();
        unsafe {
            let root_signature: ID3D12RootSignature =
                self.device.CreateRootSignature(0, bytecode).map_err(d3d_error("root signature"))?;
            let description: D3D12_COMPUTE_PIPELINE_STATE_DESC = D3D12_COMPUTE_PIPELINE_STATE_DESC {
                pRootSignature: ManuallyDrop::new(Some(root_signature.clone())),
                CS: D3D12_SHADER_BYTECODE {
                    pShaderBytecode: bytecode.as_ptr() as *const c_void,
                    BytecodeLength: bytecode.len(),
                },
                NodeMask: 0,
                CachedPSO: D3D12_CACHED_PIPELINE_STATE::default(),
                Flags: D3D12_PIPELINE_STATE_FLAG_NONE,
            };
            let pipeline: Result<ID3D12PipelineState, windows::core::Error> =
                self.device.CreateComputePipelineState(&description);
            drop(ManuallyDrop::into_inner(description.pRootSignature));
            let pipeline: ID3D12PipelineState = pipeline.map_err(d3d_error("pipeline"))?;
            let pipeline_milliseconds: f64 = clock.elapsed().as_secs_f64() * 1000.0;

            let output_bytes: u64 = output_words.max(1) as u64 * 4;
            let input_bytes: u64 = inputs.len().max(1) as u64 * 4;
            let output: ID3D12Resource = self.buffer(
                D3D12_HEAP_TYPE_DEFAULT,
                output_bytes,
                D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                D3D12_RESOURCE_STATE_COMMON,
            )?;
            let readback: ID3D12Resource = self.buffer(
                D3D12_HEAP_TYPE_READBACK,
                output_bytes,
                D3D12_RESOURCE_FLAG_NONE,
                D3D12_RESOURCE_STATE_COPY_DEST,
            )?;
            let input: ID3D12Resource = self.buffer(
                D3D12_HEAP_TYPE_UPLOAD,
                input_bytes,
                D3D12_RESOURCE_FLAG_NONE,
                D3D12_RESOURCE_STATE_GENERIC_READ,
            )?;
            let mut mapped: *mut c_void = std::ptr::null_mut();
            input.Map(0, None, Some(&mut mapped)).map_err(d3d_error("map input"))?;
            std::ptr::copy_nonoverlapping(inputs.as_ptr(), mapped as *mut u32, inputs.len());
            input.Unmap(0, None);

            clock = Instant::now();
            self.allocator.Reset().map_err(d3d_error("allocator"))?;
            let commands: ID3D12GraphicsCommandList = self
                .device
                .CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &self.allocator, &pipeline)
                .map_err(d3d_error("command list"))?;
            commands.SetComputeRootSignature(&root_signature);
            commands.SetComputeRootUnorderedAccessView(0, output.GetGPUVirtualAddress());
            commands.SetComputeRootShaderResourceView(1, input.GetGPUVirtualAddress());
            commands.Dispatch(1, 1, 1);
            let barrier: D3D12_RESOURCE_BARRIER = D3D12_RESOURCE_BARRIER {
                Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
                Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
                Anonymous: D3D12_RESOURCE_BARRIER_0 {
                    Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                        pResource: ManuallyDrop::new(Some(output.clone())),
                        Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                        StateBefore: D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                        StateAfter: D3D12_RESOURCE_STATE_COPY_SOURCE,
                    }),
                },
            };
            commands.ResourceBarrier(std::slice::from_ref(&barrier));
            let transition: D3D12_RESOURCE_TRANSITION_BARRIER = ManuallyDrop::into_inner(barrier.Anonymous.Transition);
            drop(ManuallyDrop::into_inner(transition.pResource));
            commands.CopyResource(&readback, &output);
            commands.Close().map_err(d3d_error("close"))?;
            let list: ID3D12CommandList = commands.cast().map_err(d3d_error("command list"))?;
            self.queue.ExecuteCommandLists(&[Some(list)]);
            self.fence_value += 1;
            self.queue.Signal(&self.fence, self.fence_value).map_err(d3d_error("signal"))?;
            // No event handle: blocks until the fence reaches the value
            self.fence.SetEventOnCompletion(self.fence_value, HANDLE::default()).map_err(d3d_error("wait"))?;
            let run_milliseconds: f64 = clock.elapsed().as_secs_f64() * 1000.0;

            let mut words: Vec<u32> = vec![0; output_words];
            let mut mapped: *mut c_void = std::ptr::null_mut();
            readback.Map(0, None, Some(&mut mapped)).map_err(d3d_error("map output"))?;
            std::ptr::copy_nonoverlapping(mapped as *const u32, words.as_mut_ptr(), output_words);
            readback.Unmap(0, None);
            Ok((words, pipeline_milliseconds, run_milliseconds))
        }
    }
}

/// Compiles HLSL compute shaders with DXC (signed DXIL) and runs them on D3D12's WARP adapter. One thread, one
/// output buffer (u0), one input buffer (t0).
pub struct WarpDevice {
    device: Mutex<Device>,
}

static SHARED: LazyLock<Result<Arc<WarpDevice>, ReferenceError>> =
    LazyLock::new(|| Device::create().map(|device| Arc::new(WarpDevice { device: Mutex::new(device) })));

type CachedRun = Result<(Vec<u32>, ComputeTimings), ReferenceError>;

const CACHE_LIMIT: usize = 4096;

static CACHE: LazyLock<Mutex<HashMap<String, CachedRun>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

impl WarpDevice {
    /// The process-wide device (created on first use, ~150 ms).
    pub fn shared() -> Result<Arc<WarpDevice>, ReferenceError> {
        SHARED.clone()
    }

    /// Compiles and runs one thread; returns the output words.
    pub fn run(&self, hlsl: &str, inputs: &[u32], output_words: usize, mode: ReferenceMode) -> CachedRun {
        let clock: Instant = Instant::now();
        let bytecode: Vec<u8> = compile(hlsl, mode)?;
        let compile_milliseconds: f64 = clock.elapsed().as_secs_f64() * 1000.0;
        let mut device =
            self.device.lock().map_err(|_| ReferenceError::Unavailable("the WARP device is poisoned".to_string()))?;
        let (words, pipeline_milliseconds, run_milliseconds) = device.run(&bytecode, inputs, output_words)?;
        Ok((words, ComputeTimings { compile_milliseconds, pipeline_milliseconds, run_milliseconds }))
    }

    /// `run`, remembered by shader, inputs and mode: a worksheet re-evaluated on every edit only pays for the lines
    /// that changed. DXC rejections are remembered too. The flag says whether the result came from the cache.
    pub fn run_cached(
        &self,
        hlsl: &str,
        inputs: &[u32],
        output_words: usize,
        mode: ReferenceMode,
    ) -> Result<(Vec<u32>, ComputeTimings, bool), ReferenceError> {
        let input_text: Vec<String> = inputs.iter().map(u32::to_string).collect();
        let key: String = format!("{:?}|{}|{}|{}", mode, output_words, input_text.join(","), hlsl);
        if let Some(cached) = CACHE.lock().expect("cache lock").get(&key) {
            return cached.clone().map(|(words, timings)| (words, timings, true));
        }
        let outcome: CachedRun = self.run(hlsl, inputs, output_words, mode);
        if !matches!(outcome, Err(ReferenceError::Unavailable(_))) {
            let mut cache = CACHE.lock().expect("cache lock");
            if cache.len() > CACHE_LIMIT {
                cache.clear();
            }
            cache.insert(key, outcome.clone());
        }
        outcome.map(|(words, timings)| (words, timings, false))
    }
}
