using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace ShaderCalc.Reference;

public enum ReferenceMode
{
    /// <summary>DXC with -Gis: strict IEEE, every float operation as written (what the interpreter computes).</summary>
    Strict,

    /// <summary>DXC defaults: float operations may be reassociated or fused, like a real GPU build.</summary>
    Default,
}

public sealed record ComputeTimings(double CompileMilliseconds, double PipelineMilliseconds, double RunMilliseconds);

/// <summary>
/// Compiles HLSL compute shaders with DXC (signed DXIL) and runs them on D3D12's WARP adapter, the software
/// rasterizer that ships with Windows. One thread, one output buffer (u0), one input buffer (t0).
/// </summary>
public sealed class WarpDevice : IDisposable
{
    public const string EntryPoint = "RefMain";
    public const string RootSignature = "UAV(u0), SRV(t0)";

    private static readonly Lazy<WarpDevice> SharedDevice = new Lazy<WarpDevice>(() => new WarpDevice(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static bool _isSignerLoaded;

    private readonly object _lock = new object();
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12Fence _fence;
    private ulong _fenceValue;

    private WarpDevice()
    {
        LoadDxilSigner();
        using IDXGIFactory4 factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        using IDXGIAdapter adapter = factory.EnumWarpAdapter<IDXGIAdapter>();
        _device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
        _queue = _device.CreateCommandQueue(CommandListType.Direct);
        _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _fence = _device.CreateFence(0);
    }

    /// <summary>The process-wide device (created on first use, ~150 ms).</summary>
    public static WarpDevice Shared => SharedDevice.Value;

    /// <summary>
    /// dxcompiler.dll signs DXIL by loading dxil.dll by name, which only searches the exe folder: NuGet puts it
    /// under runtimes/win-x64/native in framework-dependent builds, so load it explicitly first.
    /// </summary>
    private static void LoadDxilSigner()
    {
        if (_isSignerLoaded)
        {
            return;
        }
        foreach (string folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native") })
        {
            string candidate = Path.Combine(folder, "dxil.dll");
            if (File.Exists(candidate))
            {
                NativeLibrary.Load(candidate);
                _isSignerLoaded = true;
                return;
            }
        }
    }

    /// <summary>Compiles to signed DXIL; throws with DXC's messages on failure.</summary>
    public static byte[] Compile(string hlsl, ReferenceMode mode)
    {
        LoadDxilSigner();
        DxcCompilerOptions options = new DxcCompilerOptions
        {
            ShaderModel = DxcShaderModel.Model6_0,
            HLSLVersion = 2021,
            IEEEStrictness = mode == ReferenceMode.Strict,
        };
        using IDxcResult result = DxcCompiler.Compile(DxcShaderStage.Compute, hlsl, EntryPoint, options);
        if (result.GetStatus().Failure)
        {
            throw new ReferenceCompileException(result.GetErrors(), hlsl);
        }
        return result.GetObjectBytecodeArray();
    }

    /// <summary>Compiles and runs one thread; returns the output words.</summary>
    private const int CacheLimit = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (uint[]? Output, ComputeTimings? Timings, string? Error)> Cache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, (uint[]? Output, ComputeTimings? Timings, string? Error)>();

    /// <summary>
    /// <see cref="Run"/>, remembered by shader, inputs and mode: a worksheet re-evaluated on every edit only pays for
    /// the lines that changed. DXC rejections are remembered too.
    /// </summary>
    public (uint[] Output, ComputeTimings Timings, bool WasCached) RunCached(string hlsl, IReadOnlyList<uint> inputs, int outputWords, ReferenceMode mode)
    {
        string key = $"{mode}|{outputWords}|{string.Join(",", inputs)}|{hlsl}";
        if (Cache.TryGetValue(key, out (uint[]? Output, ComputeTimings? Timings, string? Error) cached))
        {
            return cached.Error != null ? throw new ReferenceCompileException(cached.Error, hlsl) : (cached.Output!, cached.Timings!, true);
        }
        if (Cache.Count > CacheLimit)
        {
            Cache.Clear();
        }
        try
        {
            (uint[] output, ComputeTimings timings) = Run(hlsl, inputs, outputWords, mode);
            Cache[key] = (output, timings, null);
            return (output, timings, false);
        }
        catch (ReferenceCompileException exception)
        {
            Cache[key] = (null, null, exception.Errors);
            throw;
        }
    }

    public (uint[] Output, ComputeTimings Timings) Run(string hlsl, IReadOnlyList<uint> inputs, int outputWords, ReferenceMode mode)
    {
        Stopwatch clock = Stopwatch.StartNew();
        byte[] bytecode = Compile(hlsl, mode);
        double compileMilliseconds = clock.Elapsed.TotalMilliseconds;

        lock (_lock)
        {
            clock.Restart();
            using ID3D12RootSignature rootSignature = _device.CreateRootSignature(0, bytecode);
            using ID3D12PipelineState pipeline = _device.CreateComputePipelineState<ID3D12PipelineState>(
                new ComputePipelineStateDescription { RootSignature = rootSignature, ComputeShader = bytecode });
            double pipelineMilliseconds = clock.Elapsed.TotalMilliseconds;

            ulong outputBytes = (ulong)Math.Max(outputWords, 1) * sizeof(uint);
            ulong inputBytes = (ulong)Math.Max(inputs.Count, 1) * sizeof(uint);
            using ID3D12Resource output = _device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                ResourceDescription.Buffer(outputBytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.Common);
            using ID3D12Resource readback = _device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
                ResourceDescription.Buffer(outputBytes), ResourceStates.CopyDest);
            using ID3D12Resource input = _device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                ResourceDescription.Buffer(inputBytes), ResourceStates.GenericRead);
            unsafe
            {
                uint* inputData = input.Map<uint>(0);
                for (int index = 0; index < inputs.Count; index++)
                {
                    inputData[index] = inputs[index];
                }
                input.Unmap(0);
            }

            clock.Restart();
            _allocator.Reset();
            using ID3D12GraphicsCommandList commands = _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocator, pipeline);
            commands.SetComputeRootSignature(rootSignature);
            commands.SetComputeRootUnorderedAccessView(0, output.GPUVirtualAddress);
            commands.SetComputeRootShaderResourceView(1, input.GPUVirtualAddress);
            commands.Dispatch(1, 1, 1);
            commands.ResourceBarrierTransition(output, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            commands.CopyResource(readback, output);
            commands.Close();
            _queue.ExecuteCommandList(commands);
            _queue.Signal(_fence, ++_fenceValue);
            // No event handle: blocks until the fence reaches the value
            _fence.SetEventOnCompletion(_fenceValue).CheckError();
            double runMilliseconds = clock.Elapsed.TotalMilliseconds;

            uint[] words = new uint[outputWords];
            unsafe
            {
                uint* outputData = readback.Map<uint>(0);
                for (int index = 0; index < words.Length; index++)
                {
                    words[index] = outputData[index];
                }
                readback.Unmap(0);
            }
            return (words, new ComputeTimings(compileMilliseconds, pipelineMilliseconds, runMilliseconds));
        }
    }

    public void Dispose()
    {
        _fence.Dispose();
        _allocator.Dispose();
        _queue.Dispose();
        _device.Dispose();
    }
}

public sealed class ReferenceCompileException : Exception
{
    public ReferenceCompileException(string errors, string hlsl) : base($"DXC rejected the code:\n{errors}")
    {
        Errors = errors;
        Hlsl = hlsl;
    }

    public string Errors { get; }

    public string Hlsl { get; }
}
