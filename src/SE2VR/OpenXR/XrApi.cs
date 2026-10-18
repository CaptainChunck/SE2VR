using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SE2VR.OpenXR;

/// <summary>
/// OpenXR entry points, resolved through xrGetInstanceProcAddr.
/// <para>
/// The game ships no OpenXR loader, so by default the active runtime (SteamVR, Oculus, WMR, ...) is located the same way
/// the Khronos loader does it - XR_RUNTIME_JSON or the ActiveRuntime registry value - and initialized through the
/// standard loader/runtime negotiation. If openxr_loader.dll is placed next to the plugin it is used instead, which also
/// enables OpenXR API layers.
/// </para>
/// </summary>
internal sealed unsafe class XrApi
{
    public delegate* unmanaged<ulong, byte*, nint*, XrResult> GetInstanceProcAddr;
    public delegate* unmanaged<byte*, uint, uint*, XrExtensionProperties*, XrResult> EnumerateInstanceExtensionProperties;
    public delegate* unmanaged<XrInstanceCreateInfo*, ulong*, XrResult> CreateInstance;
    public delegate* unmanaged<ulong, XrResult> DestroyInstance;
    public delegate* unmanaged<ulong, XrInstanceProperties*, XrResult> GetInstanceProperties;
    public delegate* unmanaged<ulong, XrResult, byte*, XrResult> ResultToString;
    public delegate* unmanaged<ulong, XrSystemGetInfo*, ulong*, XrResult> GetSystem;
    public delegate* unmanaged<ulong, ulong, XrSystemProperties*, XrResult> GetSystemProperties;
    public delegate* unmanaged<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, XrResult> EnumerateViewConfigurationViews;
    public delegate* unmanaged<ulong, ulong, XrGraphicsRequirementsD3D12KHR*, XrResult> GetD3D12GraphicsRequirementsKHR;
    public delegate* unmanaged<ulong, XrSessionCreateInfo*, ulong*, XrResult> CreateSession;
    public delegate* unmanaged<ulong, XrResult> DestroySession;
    public delegate* unmanaged<ulong, XrReferenceSpaceCreateInfo*, ulong*, XrResult> CreateReferenceSpace;
    public delegate* unmanaged<ulong, XrResult> DestroySpace;
    public delegate* unmanaged<ulong, ulong, long, XrSpaceLocation*, XrResult> LocateSpace;
    public delegate* unmanaged<ulong, uint, uint*, long*, XrResult> EnumerateSwapchainFormats;
    public delegate* unmanaged<ulong, XrSwapchainCreateInfo*, ulong*, XrResult> CreateSwapchain;
    public delegate* unmanaged<ulong, XrResult> DestroySwapchain;
    public delegate* unmanaged<ulong, uint, uint*, XrSwapchainImageD3D12KHR*, XrResult> EnumerateSwapchainImages;
    public delegate* unmanaged<ulong, XrSwapchainImageAcquireInfo*, uint*, XrResult> AcquireSwapchainImage;
    public delegate* unmanaged<ulong, XrSwapchainImageWaitInfo*, XrResult> WaitSwapchainImage;
    public delegate* unmanaged<ulong, XrSwapchainImageReleaseInfo*, XrResult> ReleaseSwapchainImage;
    public delegate* unmanaged<ulong, XrSessionBeginInfo*, XrResult> BeginSession;
    public delegate* unmanaged<ulong, XrResult> EndSession;
    public delegate* unmanaged<ulong, XrResult> RequestExitSession;
    public delegate* unmanaged<ulong, XrFrameWaitInfo*, XrFrameState*, XrResult> WaitFrame;
    public delegate* unmanaged<ulong, XrFrameBeginInfo*, XrResult> BeginFrame;
    public delegate* unmanaged<ulong, XrFrameEndInfo*, XrResult> EndFrame;
    public delegate* unmanaged<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, XrResult> LocateViews;
    public delegate* unmanaged<ulong, XrEventDataBuffer*, XrResult> PollEvent;

    /// <summary>Where the entry points came from, for the log.</summary>
    public string Source { get; private set; } = "";

    private nint _library;

    private XrApi()
    {
    }

    public static XrApi Load(string pluginDirectory)
    {
        var api = new XrApi();
        string loaderPath = Path.Combine(pluginDirectory, "openxr_loader.dll");
        if (File.Exists(loaderPath))
        {
            api._library = NativeLibrary.Load(loaderPath);
            api.GetInstanceProcAddr = (delegate* unmanaged<ulong, byte*, nint*, XrResult>)NativeLibrary.GetExport(api._library, "xrGetInstanceProcAddr");
            api.Source = $"Khronos loader {loaderPath}";
        }
        else
        {
            api.NegotiateWithActiveRuntime();
        }
        api.EnumerateInstanceExtensionProperties = (delegate* unmanaged<byte*, uint, uint*, XrExtensionProperties*, XrResult>)api.Get(0, "xrEnumerateInstanceExtensionProperties");
        api.CreateInstance = (delegate* unmanaged<XrInstanceCreateInfo*, ulong*, XrResult>)api.Get(0, "xrCreateInstance");
        return api;
    }

    /// <summary>Resolves every instance-level function. Call once after xrCreateInstance succeeded.</summary>
    public void LoadInstanceFunctions(ulong instance)
    {
        DestroyInstance = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrDestroyInstance");
        GetInstanceProperties = (delegate* unmanaged<ulong, XrInstanceProperties*, XrResult>)Get(instance, "xrGetInstanceProperties");
        ResultToString = (delegate* unmanaged<ulong, XrResult, byte*, XrResult>)Get(instance, "xrResultToString");
        GetSystem = (delegate* unmanaged<ulong, XrSystemGetInfo*, ulong*, XrResult>)Get(instance, "xrGetSystem");
        GetSystemProperties = (delegate* unmanaged<ulong, ulong, XrSystemProperties*, XrResult>)Get(instance, "xrGetSystemProperties");
        EnumerateViewConfigurationViews = (delegate* unmanaged<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, XrResult>)Get(instance, "xrEnumerateViewConfigurationViews");
        GetD3D12GraphicsRequirementsKHR = (delegate* unmanaged<ulong, ulong, XrGraphicsRequirementsD3D12KHR*, XrResult>)Get(instance, "xrGetD3D12GraphicsRequirementsKHR");
        CreateSession = (delegate* unmanaged<ulong, XrSessionCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateSession");
        DestroySession = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrDestroySession");
        CreateReferenceSpace = (delegate* unmanaged<ulong, XrReferenceSpaceCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateReferenceSpace");
        DestroySpace = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrDestroySpace");
        LocateSpace = (delegate* unmanaged<ulong, ulong, long, XrSpaceLocation*, XrResult>)Get(instance, "xrLocateSpace");
        EnumerateSwapchainFormats = (delegate* unmanaged<ulong, uint, uint*, long*, XrResult>)Get(instance, "xrEnumerateSwapchainFormats");
        CreateSwapchain = (delegate* unmanaged<ulong, XrSwapchainCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateSwapchain");
        DestroySwapchain = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrDestroySwapchain");
        EnumerateSwapchainImages = (delegate* unmanaged<ulong, uint, uint*, XrSwapchainImageD3D12KHR*, XrResult>)Get(instance, "xrEnumerateSwapchainImages");
        AcquireSwapchainImage = (delegate* unmanaged<ulong, XrSwapchainImageAcquireInfo*, uint*, XrResult>)Get(instance, "xrAcquireSwapchainImage");
        WaitSwapchainImage = (delegate* unmanaged<ulong, XrSwapchainImageWaitInfo*, XrResult>)Get(instance, "xrWaitSwapchainImage");
        ReleaseSwapchainImage = (delegate* unmanaged<ulong, XrSwapchainImageReleaseInfo*, XrResult>)Get(instance, "xrReleaseSwapchainImage");
        BeginSession = (delegate* unmanaged<ulong, XrSessionBeginInfo*, XrResult>)Get(instance, "xrBeginSession");
        EndSession = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrEndSession");
        RequestExitSession = (delegate* unmanaged<ulong, XrResult>)Get(instance, "xrRequestExitSession");
        WaitFrame = (delegate* unmanaged<ulong, XrFrameWaitInfo*, XrFrameState*, XrResult>)Get(instance, "xrWaitFrame");
        BeginFrame = (delegate* unmanaged<ulong, XrFrameBeginInfo*, XrResult>)Get(instance, "xrBeginFrame");
        EndFrame = (delegate* unmanaged<ulong, XrFrameEndInfo*, XrResult>)Get(instance, "xrEndFrame");
        LocateViews = (delegate* unmanaged<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, XrResult>)Get(instance, "xrLocateViews");
        PollEvent = (delegate* unmanaged<ulong, XrEventDataBuffer*, XrResult>)Get(instance, "xrPollEvent");
    }

    private nint Get(ulong instance, string name)
    {
        int length = Encoding.ASCII.GetByteCount(name);
        byte* utf8 = stackalloc byte[length + 1];
        Encoding.ASCII.GetBytes(name, new Span<byte>(utf8, length));
        utf8[length] = 0;
        nint function = 0;
        XrResult result = GetInstanceProcAddr(instance, utf8, &function);
        if (result < 0 || function == 0)
        {
            throw new XrException($"xrGetInstanceProcAddr({name})", result);
        }
        return function;
    }

    private void NegotiateWithActiveRuntime()
    {
        string manifestPath = FindActiveRuntimeManifest();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement runtime = manifest.RootElement.GetProperty("runtime");
        string libraryPath = runtime.GetProperty("library_path").GetString() ?? throw new InvalidDataException("library_path missing");
        if (!Path.IsPathRooted(libraryPath))
        {
            libraryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, libraryPath));
        }
        string negotiateName = "xrNegotiateLoaderRuntimeInterface";
        if (runtime.TryGetProperty("functions", out JsonElement functions) &&
            functions.TryGetProperty("xrNegotiateLoaderRuntimeInterface", out JsonElement renamed) &&
            renamed.GetString() is { Length: > 0 } customName)
        {
            negotiateName = customName;
        }

        _library = NativeLibrary.Load(libraryPath);
        var negotiate = (delegate* unmanaged<XrNegotiateLoaderInfo*, XrNegotiateRuntimeRequest*, XrResult>)NativeLibrary.GetExport(_library, negotiateName);

        XrNegotiateLoaderInfo loaderInfo = new()
        {
            StructType = 1, // XR_LOADER_INTERFACE_STRUCT_LOADER_INFO
            StructVersion = 1, // XR_LOADER_INFO_STRUCT_VERSION
            StructSize = (nuint)sizeof(XrNegotiateLoaderInfo),
            MinInterfaceVersion = 1,
            MaxInterfaceVersion = 1, // XR_CURRENT_LOADER_RUNTIME_VERSION
            MinApiVersion = XrConst.MakeVersion(1, 0, 0),
            MaxApiVersion = XrConst.MakeVersion(1, 0x3ff, 0xfff)
        };
        XrNegotiateRuntimeRequest request = new()
        {
            StructType = 3, // XR_LOADER_INTERFACE_STRUCT_RUNTIME_REQUEST
            StructVersion = 1, // XR_RUNTIME_INFO_STRUCT_VERSION
            StructSize = (nuint)sizeof(XrNegotiateRuntimeRequest)
        };
        XrResult result = negotiate(&loaderInfo, &request);
        if (result < 0 || request.GetInstanceProcAddr == 0)
        {
            throw new XrException($"{negotiateName} in {libraryPath}", result);
        }
        GetInstanceProcAddr = (delegate* unmanaged<ulong, byte*, nint*, XrResult>)request.GetInstanceProcAddr;
        Source = $"runtime {runtime.GetProperty("name").GetString() ?? "?"} ({libraryPath}), interface v{request.RuntimeInterfaceVersion}";
    }

    private static string FindActiveRuntimeManifest()
    {
        string? overridePath = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
        if (key?.GetValue("ActiveRuntime") is string path && File.Exists(path))
        {
            return path;
        }
        throw new FileNotFoundException("No active OpenXR runtime is registered. Start SteamVR once and set it as the OpenXR runtime (SteamVR Settings > OpenXR).");
    }
}

internal sealed class XrException : Exception
{
    public XrResult Result { get; }

    public XrException(string call, XrResult result)
        : base($"{call} failed: {result} ({(int)result})")
    {
        Result = result;
    }
}
