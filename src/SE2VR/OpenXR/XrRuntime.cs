using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SE2VR.OpenXR;

/// <summary>
/// One OpenXR instance + session driven from the game's render thread.
/// Instance and system are created early (before the D3D12 device exists) so the per-eye resolution can be forced
/// onto the game window; the session is created on the first rendered frame using the game's own device and queue.
/// </summary>
internal sealed unsafe class XrRuntime : IDisposable
{
    public readonly XrApi Api;

    public ulong Instance { get; private set; }
    public ulong SystemId { get; private set; }
    public ulong Session { get; private set; }
    public ulong LocalSpace { get; private set; }
    public ulong ViewSpace { get; private set; }

    /// <summary>
    /// LOCAL space re-based on the head pose captured at the last recenter (position + yaw), so "straight ahead" is where
    /// the player was looking. Views are located and layers submitted in this space.
    /// </summary>
    public ulong RecenteredSpace { get; private set; }

    public ulong RenderSpace => RecenteredSpace != 0 ? RecenteredSpace : LocalSpace;
    public string RuntimeName { get; private set; } = "";
    public string SystemName { get; private set; } = "";
    public XrViewConfigurationView[] ViewConfigs { get; } = new XrViewConfigurationView[2];
    public uint MaxLayerCount { get; private set; }

    public XrSessionState State { get; private set; } = XrSessionState.Unknown;

    /// <summary>xrBeginSession succeeded and xrEndSession has not been called yet: frames must be submitted.</summary>
    public bool IsSessionRunning { get; private set; }

    /// <summary>The instance or session hit an unrecoverable error; VR stays off for the rest of the game session.</summary>
    public bool IsLost { get; private set; }

    // Per-frame state, valid between BeginFrame and EndFrame.
    public XrFrameState FrameState;
    public readonly XrView[] Views = new XrView[2];
    public bool ViewsValid { get; private set; }
    public bool IsFrameBegun { get; private set; }

    private readonly List<long> _swapchainFormats = new();

    private XrRuntime(XrApi api)
    {
        Api = api;
    }

    /// <summary>Loads the runtime, creates the instance and waits (up to <paramref name="systemWaitSeconds"/>) for a headset.</summary>
    public static XrRuntime Create(string pluginDirectory, float systemWaitSeconds)
    {
        XrApi api = XrApi.Load(pluginDirectory);
        Log.Info($"OpenXR entry points from {api.Source}");
        var runtime = new XrRuntime(api);
        try
        {
            runtime.CreateInstance();
            runtime.WaitForSystem(systemWaitSeconds);
            runtime.QueryViewConfiguration();
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private void CreateInstance()
    {
        uint count = 0;
        Check(Api.EnumerateInstanceExtensionProperties(null, 0, &count, null), "xrEnumerateInstanceExtensionProperties");
        var extensions = new XrExtensionProperties[count];
        for (int i = 0; i < extensions.Length; i++)
        {
            extensions[i].Type = XrStructureType.ExtensionProperties;
        }
        bool hasD3D12 = false;
        fixed (XrExtensionProperties* p = extensions)
        {
            Check(Api.EnumerateInstanceExtensionProperties(null, count, &count, p), "xrEnumerateInstanceExtensionProperties");
            for (int i = 0; i < count; i++)
            {
                string name = Utf8(p[i].ExtensionName, 128);
                Log.Debug($"  OpenXR extension: {name} v{p[i].ExtensionVersion}");
                hasD3D12 |= name == XrConst.KhrD3D12EnableExtension;
            }
        }
        if (!hasD3D12)
        {
            throw new InvalidOperationException($"The active OpenXR runtime does not support {XrConst.KhrD3D12EnableExtension}, which Space Engineers 2's DirectX 12 renderer needs.");
        }

        XrInstanceCreateInfo info = new() { Type = XrStructureType.InstanceCreateInfo };
        WriteUtf8(info.ApplicationInfo.ApplicationName, 128, "Space Engineers 2 VR");
        info.ApplicationInfo.ApplicationVersion = 1;
        WriteUtf8(info.ApplicationInfo.EngineName, 128, "VRage3 (SE2VR plugin)");
        info.ApplicationInfo.EngineVersion = 1;
        info.ApplicationInfo.ApiVersion = XrConst.MakeVersion(1, 0, 0);

        byte[] extName = Encoding.ASCII.GetBytes(XrConst.KhrD3D12EnableExtension + "\0");
        fixed (byte* pName = extName)
        {
            byte** names = stackalloc byte*[1];
            names[0] = pName;
            info.EnabledExtensionCount = 1;
            info.EnabledExtensionNames = names;
            ulong instance;
            Check(Api.CreateInstance(&info, &instance), "xrCreateInstance");
            Instance = instance;
        }
        Api.LoadInstanceFunctions(Instance);

        XrInstanceProperties props = new() { Type = XrStructureType.InstanceProperties };
        if (Api.GetInstanceProperties(Instance, &props) >= 0)
        {
            RuntimeName = Utf8(props.RuntimeName, 128);
            ulong v = props.RuntimeVersion;
            Log.Info($"OpenXR runtime: {RuntimeName} {v >> 48}.{(v >> 32) & 0xffff}.{v & 0xffffffff}");
        }
    }

    private void WaitForSystem(float waitSeconds)
    {
        XrSystemGetInfo info = new() { Type = XrStructureType.SystemGetInfo, FormFactor = XrConst.FormFactorHeadMountedDisplay };
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0f, waitSeconds));
        while (true)
        {
            ulong systemId;
            XrResult result = Api.GetSystem(Instance, &info, &systemId);
            if (result >= 0)
            {
                SystemId = systemId;
                break;
            }
            if (result != XrResult.ErrorFormFactorUnavailable || DateTime.UtcNow > deadline)
            {
                throw new XrException("xrGetSystem (is the headset connected and SteamVR running?)", result);
            }
            Thread.Sleep(250);
        }

        XrSystemProperties props = new() { Type = XrStructureType.SystemProperties };
        Check(Api.GetSystemProperties(Instance, SystemId, &props), "xrGetSystemProperties");
        SystemName = Utf8(props.SystemName, 256);
        MaxLayerCount = props.MaxLayerCount;
        Log.Info($"Headset: {SystemName} (vendor 0x{props.VendorId:X}), max swapchain {props.MaxSwapchainImageWidth}x{props.MaxSwapchainImageHeight}, " +
                 $"{props.MaxLayerCount} layers, position tracking {(props.PositionTracking != 0 ? "yes" : "no")}");
    }

    private void QueryViewConfiguration()
    {
        uint count = 0;
        Check(Api.EnumerateViewConfigurationViews(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, 0, &count, null), "xrEnumerateViewConfigurationViews");
        if (count != 2)
        {
            throw new InvalidOperationException($"Expected 2 stereo views, runtime reports {count}.");
        }
        ViewConfigs[0].Type = ViewConfigs[1].Type = XrStructureType.ViewConfigurationView;
        fixed (XrViewConfigurationView* p = ViewConfigs)
        {
            Check(Api.EnumerateViewConfigurationViews(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, 2, &count, p), "xrEnumerateViewConfigurationViews");
        }
        for (int i = 0; i < 2; i++)
        {
            Log.Info($"Eye {i}: recommended {ViewConfigs[i].RecommendedImageRectWidth}x{ViewConfigs[i].RecommendedImageRectHeight}, " +
                     $"max {ViewConfigs[i].MaxImageRectWidth}x{ViewConfigs[i].MaxImageRectHeight}");
        }
    }

    /// <summary>Must be called before the session is created (OpenXR requirement); returns the adapter LUID the runtime wants.</summary>
    public long GetRequiredAdapterLuid(out int minFeatureLevel)
    {
        XrGraphicsRequirementsD3D12KHR req = new() { Type = XrStructureType.GraphicsRequirementsD3D12KHR };
        Check(Api.GetD3D12GraphicsRequirementsKHR(Instance, SystemId, &req), "xrGetD3D12GraphicsRequirementsKHR");
        minFeatureLevel = req.MinFeatureLevel;
        return ((long)req.AdapterLuidHighPart << 32) | req.AdapterLuidLowPart;
    }

    public void CreateSession(nint d3d12Device, nint d3d12DirectQueue)
    {
        XrGraphicsBindingD3D12KHR binding = new()
        {
            Type = XrStructureType.GraphicsBindingD3D12KHR,
            Device = d3d12Device,
            Queue = d3d12DirectQueue
        };
        XrSessionCreateInfo info = new() { Type = XrStructureType.SessionCreateInfo, Next = &binding, SystemId = SystemId };
        ulong session;
        Check(Api.CreateSession(Instance, &info, &session), "xrCreateSession");
        Session = session;

        XrReferenceSpaceCreateInfo spaceInfo = new()
        {
            Type = XrStructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = XrConst.ReferenceSpaceLocal,
            PoseInReferenceSpace = XrPosef.Identity
        };
        ulong space;
        Check(Api.CreateReferenceSpace(Session, &spaceInfo, &space), "xrCreateReferenceSpace(LOCAL)");
        LocalSpace = space;
        spaceInfo.ReferenceSpaceType = XrConst.ReferenceSpaceView;
        Check(Api.CreateReferenceSpace(Session, &spaceInfo, &space), "xrCreateReferenceSpace(VIEW)");
        ViewSpace = space;
        RequestRecenter(0);

        uint count = 0;
        Check(Api.EnumerateSwapchainFormats(Session, 0, &count, null), "xrEnumerateSwapchainFormats");
        var formats = new long[count];
        fixed (long* p = formats)
        {
            Check(Api.EnumerateSwapchainFormats(Session, count, &count, p), "xrEnumerateSwapchainFormats");
        }
        _swapchainFormats.AddRange(formats);
        Log.Info($"Session created. Swapchain formats (DXGI): {string.Join(", ", formats)}");
    }

    public bool SupportsSwapchainFormat(long dxgiFormat) => _swapchainFormats.Contains(dxgiFormat);

    /// <summary>Drains the event queue and drives the session state machine. Call once per frame.</summary>
    public void PollEvents()
    {
        if (Instance == 0 || IsLost)
        {
            return;
        }
        XrEventDataBuffer buffer;
        while (true)
        {
            buffer.Type = XrStructureType.EventDataBuffer;
            buffer.Next = null;
            XrResult result = Api.PollEvent(Instance, &buffer);
            if (result == XrResult.EventUnavailable)
            {
                return;
            }
            if (result < 0)
            {
                Log.Error($"xrPollEvent failed: {Describe(result)}");
                return;
            }
            switch (buffer.Type)
            {
                case XrStructureType.EventDataSessionStateChanged:
                    OnSessionStateChanged(((XrEventDataSessionStateChanged*)&buffer)->State);
                    break;
                case XrStructureType.EventDataInstanceLossPending:
                    Log.Warn("OpenXR instance loss pending (runtime is shutting down). VR disabled.");
                    IsLost = true;
                    IsSessionRunning = false;
                    return;
                case XrStructureType.EventDataReferenceSpaceChangePending:
                    // The runtime moved LOCAL (e.g. SteamVR "Reset seated position"); rebuild our space once it took effect.
                    Log.Info("Runtime recentered its seated origin.");
                    RequestRecenter(15);
                    break;
                case XrStructureType.EventDataEventsLost:
                    Log.Warn("OpenXR events lost.");
                    break;
            }
        }
    }

    private void OnSessionStateChanged(XrSessionState state)
    {
        Log.Info($"Session state: {State} -> {state}");
        State = state;
        switch (state)
        {
            case XrSessionState.Ready:
            {
                XrSessionBeginInfo info = new() { Type = XrStructureType.SessionBeginInfo, PrimaryViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo };
                XrResult result = Api.BeginSession(Session, &info);
                if (result >= 0)
                {
                    IsSessionRunning = true;
                    Log.Info("Session running - rendering to the headset.");
                }
                else
                {
                    Log.Error($"xrBeginSession failed: {Describe(result)}");
                }
                break;
            }
            case XrSessionState.Stopping:
                IsSessionRunning = false;
                Api.EndSession(Session);
                Log.Info("Session stopped.");
                break;
            case XrSessionState.LossPending:
            case XrSessionState.Exiting:
                IsSessionRunning = false;
                IsLost = true;
                Log.Warn($"Session {state}. VR disabled until the game is restarted.");
                break;
        }
    }

    /// <summary>xrWaitFrame + xrBeginFrame + xrLocateViews. Returns false when no frame was begun (session not running).</summary>
    public bool BeginFrame()
    {
        IsFrameBegun = false;
        ViewsValid = false;
        if (!IsSessionRunning || IsLost)
        {
            return false;
        }

        XrFrameWaitInfo waitInfo = new() { Type = XrStructureType.FrameWaitInfo };
        XrFrameState frameState = new() { Type = XrStructureType.FrameState };
        XrResult result = Api.WaitFrame(Session, &waitInfo, &frameState);
        if (result < 0)
        {
            Fail("xrWaitFrame", result);
            return false;
        }
        FrameState = frameState;

        XrFrameBeginInfo beginInfo = new() { Type = XrStructureType.FrameBeginInfo };
        result = Api.BeginFrame(Session, &beginInfo);
        if (result < 0)
        {
            Fail("xrBeginFrame", result);
            return false;
        }
        IsFrameBegun = true;

        if (FrameState.ShouldRender != 0)
        {
            if (_recenterInFrames >= 0 && _recenterInFrames-- == 0 && !TryRecenter(FrameState.PredictedDisplayTime))
            {
                _recenterInFrames = 5; // head not tracked yet; try again shortly
            }
            XrViewLocateInfo locateInfo = new()
            {
                Type = XrStructureType.ViewLocateInfo,
                ViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo,
                DisplayTime = FrameState.PredictedDisplayTime,
                Space = RenderSpace
            };
            XrViewState viewState = new() { Type = XrStructureType.ViewState };
            Views[0].Type = Views[1].Type = XrStructureType.View;
            Views[0].Next = Views[1].Next = null;
            uint count = 0;
            fixed (XrView* views = Views)
            {
                result = Api.LocateViews(Session, &locateInfo, &viewState, 2, &count, views);
            }
            const ulong needed = XrConst.ViewStateOrientationValid | XrConst.ViewStatePositionValid;
            ViewsValid = result >= 0 && count == 2 && (viewState.ViewStateFlags & needed) == needed;
            if (result < 0)
            {
                Log.Warn($"xrLocateViews failed: {Describe(result)}");
            }
        }
        return true;
    }

    private int _recenterInFrames = -1;

    /// <summary>Recenter on the head pose <paramref name="frames"/> rendered frames from now (0 = this frame).</summary>
    public void RequestRecenter(int frames)
    {
        _recenterInFrames = Math.Max(0, frames);
    }

    /// <summary>
    /// Makes the current head position the origin and its heading "forward". Pitch and roll are ignored so the
    /// horizon stays level no matter how the head was tilted when recentering.
    /// </summary>
    private bool TryRecenter(long time)
    {
        // Before the session is visible SteamVR reports a placeholder identity pose; wait for real tracking.
        if (State != XrSessionState.Visible && State != XrSessionState.Focused)
        {
            return false;
        }
        XrSpaceLocation head = new() { Type = XrStructureType.SpaceLocation };
        XrResult result = Api.LocateSpace(ViewSpace, LocalSpace, time, &head);
        const ulong needed = XrConst.SpaceLocationOrientationValid | XrConst.SpaceLocationPositionValid |
                             XrConst.SpaceLocationOrientationTracked | XrConst.SpaceLocationPositionTracked;
        if (result < 0 || (head.LocationFlags & needed) != needed)
        {
            return false;
        }

        // Heading of the head's forward (-Z) axis projected onto the horizontal plane.
        XrQuaternionf q = head.Pose.Orientation;
        float forwardX = -2f * (q.X * q.Z + q.Y * q.W);
        float forwardZ = -(1f - 2f * (q.X * q.X + q.Y * q.Y));
        float yaw = MathF.Atan2(-forwardX, -forwardZ);
        XrReferenceSpaceCreateInfo info = new()
        {
            Type = XrStructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = XrConst.ReferenceSpaceLocal,
            PoseInReferenceSpace = new XrPosef
            {
                Orientation = new XrQuaternionf { Y = MathF.Sin(yaw * 0.5f), W = MathF.Cos(yaw * 0.5f) },
                Position = head.Pose.Position
            }
        };
        ulong space;
        result = Api.CreateReferenceSpace(Session, &info, &space);
        if (result < 0)
        {
            Log.Warn($"Recenter failed: {Describe(result)}");
            return true; // keep the old space; retrying would not help
        }
        if (RecenteredSpace != 0)
        {
            Api.DestroySpace(RecenteredSpace);
        }
        RecenteredSpace = space;
        Log.Info($"Recentered: head at ({head.Pose.Position.X:0.00}, {head.Pose.Position.Y:0.00}, {head.Pose.Position.Z:0.00}) m, heading {yaw * 180f / MathF.PI:0} deg.");
        return true;
    }

    /// <summary>Submits the frame. <paramref name="layers"/> are pointers to composition layer structs.</summary>
    public void EndFrame(void** layers, uint layerCount)
    {
        if (!IsFrameBegun)
        {
            return;
        }
        IsFrameBegun = false;
        XrFrameEndInfo info = new()
        {
            Type = XrStructureType.FrameEndInfo,
            DisplayTime = FrameState.PredictedDisplayTime,
            EnvironmentBlendMode = XrConst.EnvironmentBlendModeOpaque,
            LayerCount = layerCount,
            Layers = layerCount > 0 ? layers : null
        };
        XrResult result = Api.EndFrame(Session, &info);
        if (result < 0)
        {
            Fail("xrEndFrame", result);
        }
    }

    public ulong CreateSwapchain(long format, int width, int height, out nint[] images)
    {
        XrSwapchainCreateInfo info = new()
        {
            Type = XrStructureType.SwapchainCreateInfo,
            UsageFlags = XrConst.SwapchainUsageColorAttachment | XrConst.SwapchainUsageTransferDst | XrConst.SwapchainUsageSampled,
            Format = format,
            SampleCount = 1,
            Width = (uint)width,
            Height = (uint)height,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1
        };
        ulong swapchain;
        Check(Api.CreateSwapchain(Session, &info, &swapchain), $"xrCreateSwapchain({width}x{height}, format {format})");
        uint count = 0;
        Check(Api.EnumerateSwapchainImages(swapchain, 0, &count, null), "xrEnumerateSwapchainImages");
        var buffer = new XrSwapchainImageD3D12KHR[count];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i].Type = XrStructureType.SwapchainImageD3D12KHR;
        }
        fixed (XrSwapchainImageD3D12KHR* p = buffer)
        {
            Check(Api.EnumerateSwapchainImages(swapchain, count, &count, p), "xrEnumerateSwapchainImages");
        }
        images = new nint[count];
        for (int i = 0; i < count; i++)
        {
            images[i] = buffer[i].Texture;
        }
        return swapchain;
    }

    /// <summary>Acquires and waits for the next image. Returns the image index, or -1 on failure/timeout.</summary>
    public int AcquireImage(ulong swapchain)
    {
        XrSwapchainImageAcquireInfo acquireInfo = new() { Type = XrStructureType.SwapchainImageAcquireInfo };
        uint index;
        XrResult result = Api.AcquireSwapchainImage(swapchain, &acquireInfo, &index);
        if (result < 0)
        {
            Log.Warn($"xrAcquireSwapchainImage failed: {Describe(result)}");
            return -1;
        }
        XrSwapchainImageWaitInfo waitInfo = new() { Type = XrStructureType.SwapchainImageWaitInfo, Timeout = 1_000_000_000L };
        result = Api.WaitSwapchainImage(swapchain, &waitInfo);
        if (result != XrResult.Success)
        {
            // An acquired image must still be released, even if we never wrote to it.
            Log.Warn($"xrWaitSwapchainImage returned {Describe(result)}");
            ReleaseImage(swapchain);
            return -1;
        }
        return (int)index;
    }

    public void ReleaseImage(ulong swapchain)
    {
        XrSwapchainImageReleaseInfo info = new() { Type = XrStructureType.SwapchainImageReleaseInfo };
        XrResult result = Api.ReleaseSwapchainImage(swapchain, &info);
        if (result < 0)
        {
            Log.Warn($"xrReleaseSwapchainImage failed: {Describe(result)}");
        }
    }

    public void DestroySwapchain(ulong swapchain)
    {
        if (swapchain != 0)
        {
            Api.DestroySwapchain(swapchain);
        }
    }

    public string Describe(XrResult result)
    {
        if (Instance != 0 && Api.ResultToString != null)
        {
            byte* buffer = stackalloc byte[64];
            if (Api.ResultToString(Instance, result, buffer) >= 0)
            {
                return $"{Utf8(buffer, 64)} ({(int)result})";
            }
        }
        return $"{result} ({(int)result})";
    }

    private void Fail(string call, XrResult result)
    {
        Log.Error($"{call} failed: {Describe(result)}");
        if (result is XrResult.ErrorSessionLost or XrResult.ErrorInstanceLost or XrResult.ErrorRuntimeFailure)
        {
            IsLost = true;
            IsSessionRunning = false;
        }
    }

    private void Check(XrResult result, string call)
    {
        if (result < 0)
        {
            throw new XrException(call, result);
        }
    }

    public void Dispose()
    {
        foreach (ulong space in new[] { RecenteredSpace, ViewSpace, LocalSpace })
        {
            if (space != 0)
            {
                Api.DestroySpace(space);
            }
        }
        RecenteredSpace = ViewSpace = LocalSpace = 0;
        if (Session != 0)
        {
            Api.DestroySession(Session);
            Session = 0;
        }
        if (Instance != 0)
        {
            Api.DestroyInstance(Instance);
            Instance = 0;
        }
        IsSessionRunning = false;
    }

    private static string Utf8(byte* text, int capacity)
    {
        int length = 0;
        while (length < capacity && text[length] != 0)
        {
            length++;
        }
        return Encoding.UTF8.GetString(text, length);
    }

    private static void WriteUtf8(byte* destination, int capacity, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int length = Math.Min(bytes.Length, capacity - 1);
        Marshal.Copy(bytes, 0, (nint)destination, length);
        destination[length] = 0;
    }
}
