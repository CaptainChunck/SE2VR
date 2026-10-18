using System.Runtime.InteropServices;

// Hand-written subset of openxr.h / openxr_platform.h (OpenXR 1.0, x64 layout).
// Handles (XrInstance, XrSession, XrSpace, XrSwapchain) are pointers on 64-bit and are passed as ulong.
// Every struct mirrors the C declaration field-for-field so LayoutKind.Sequential reproduces the native offsets.
namespace SE2VR.OpenXR;

internal enum XrResult : int
{
    Success = 0,
    TimeoutExpired = 1,
    SessionLossPending = 3,
    EventUnavailable = 4,
    SpaceBoundsUnavailable = 7,
    SessionNotFocused = 8,
    FrameDiscarded = 9,
    ErrorValidationFailure = -1,
    ErrorRuntimeFailure = -2,
    ErrorOutOfMemory = -3,
    ErrorApiVersionUnsupported = -4,
    ErrorInitializationFailed = -6,
    ErrorFunctionUnsupported = -7,
    ErrorExtensionNotPresent = -9,
    ErrorSizeInsufficient = -11,
    ErrorHandleInvalid = -12,
    ErrorInstanceLost = -13,
    ErrorSessionRunning = -14,
    ErrorSessionNotRunning = -16,
    ErrorSessionLost = -17,
    ErrorSystemInvalid = -18,
    ErrorSwapchainFormatUnsupported = -26,
    ErrorSessionNotReady = -28,
    ErrorSessionNotStopping = -29,
    ErrorFormFactorUnavailable = -35,
    ErrorCallOrderInvalid = -37,
    ErrorGraphicsDeviceInvalid = -38,
    ErrorGraphicsRequirementsCallMissing = -50,
    ErrorRuntimeUnavailable = -51,
}

internal enum XrStructureType : int
{
    Unknown = 0,
    ExtensionProperties = 2,
    InstanceCreateInfo = 3,
    SystemGetInfo = 4,
    SystemProperties = 5,
    ViewLocateInfo = 6,
    View = 7,
    SessionCreateInfo = 8,
    SwapchainCreateInfo = 9,
    SessionBeginInfo = 10,
    ViewState = 11,
    FrameEndInfo = 12,
    EventDataBuffer = 16,
    EventDataInstanceLossPending = 17,
    EventDataSessionStateChanged = 18,
    InstanceProperties = 32,
    FrameWaitInfo = 33,
    CompositionLayerProjection = 35,
    CompositionLayerQuad = 36,
    ReferenceSpaceCreateInfo = 37,
    EventDataReferenceSpaceChangePending = 40,
    ViewConfigurationView = 41,
    SpaceLocation = 42,
    FrameState = 44,
    FrameBeginInfo = 46,
    CompositionLayerProjectionView = 48,
    EventDataEventsLost = 49,
    EventDataInteractionProfileChanged = 52,
    SwapchainImageAcquireInfo = 55,
    SwapchainImageWaitInfo = 56,
    SwapchainImageReleaseInfo = 57,
    CompositionLayerCylinderKHR = 1000017000,
    GraphicsBindingD3D12KHR = 1000028000,
    SwapchainImageD3D12KHR = 1000028001,
    GraphicsRequirementsD3D12KHR = 1000028002,
    VisibilityMaskKHR = 1000031000,
    EventDataVisibilityMaskChangedKHR = 1000031001,
}

/// <summary>XR_KHR_visibility_mask: the part of each eye's image the lenses show, in tangent space (directions (x, y, -1)).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrVisibilityMaskKHR
{
    public XrStructureType Type;
    public void* Next;
    public uint VertexCapacityInput;
    public uint VertexCountOutput;
    public XrVector2f* Vertices;
    public uint IndexCapacityInput;
    public uint IndexCountOutput;
    public uint* Indices;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrVector2f
{
    public float X;
    public float Y;
}

internal enum XrSessionState : int
{
    Unknown = 0,
    Idle = 1,
    Ready = 2,
    Synchronized = 3,
    Visible = 4,
    Focused = 5,
    Stopping = 6,
    LossPending = 7,
    Exiting = 8,
}

internal static class XrConst
{
    public const int FormFactorHeadMountedDisplay = 1;
    public const int ViewConfigurationPrimaryStereo = 2;
    public const int ReferenceSpaceView = 1;
    public const int ReferenceSpaceLocal = 2;
    public const int ReferenceSpaceStage = 3;
    public const int EnvironmentBlendModeOpaque = 1;
    public const int EyeVisibilityBoth = 0;

    public const ulong SwapchainCreateStaticImage = 0x2;

    public const ulong SwapchainUsageColorAttachment = 0x1;
    public const ulong SwapchainUsageTransferDst = 0x10;
    public const ulong SwapchainUsageSampled = 0x20;

    public const ulong CompositionLayerBlendTextureSourceAlpha = 0x2;
    public const ulong CompositionLayerUnpremultipliedAlpha = 0x4;

    public const ulong ViewStateOrientationValid = 0x1;
    public const ulong ViewStatePositionValid = 0x2;

    public const ulong SpaceLocationOrientationValid = 0x1;
    public const ulong SpaceLocationPositionValid = 0x2;
    public const ulong SpaceLocationOrientationTracked = 0x4;
    public const ulong SpaceLocationPositionTracked = 0x8;

    public const long InfiniteDuration = 0x7fffffffffffffffL;

    // DXGI_FORMAT values used for swapchains.
    public const long DxgiR8G8B8A8UnormSrgb = 29;

    public const string KhrD3D12EnableExtension = "XR_KHR_D3D12_enable";
    public const string KhrCompositionLayerCylinderExtension = "XR_KHR_composition_layer_cylinder";
    public const string KhrVisibilityMaskExtension = "XR_KHR_visibility_mask";

    public const int VisibilityMaskTypeLineLoop = 3;

    public static ulong MakeVersion(ulong major, ulong minor, ulong patch) =>
        ((major & 0xffff) << 48) | ((minor & 0xffff) << 32) | (patch & 0xffffffff);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrApplicationInfo
{
    public fixed byte ApplicationName[128];
    public uint ApplicationVersion;
    public fixed byte EngineName[128];
    public uint EngineVersion;
    public ulong ApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrInstanceCreateInfo
{
    public XrStructureType Type;
    public void* Next;
    public ulong CreateFlags;
    public XrApplicationInfo ApplicationInfo;
    public uint EnabledApiLayerCount;
    public byte** EnabledApiLayerNames;
    public uint EnabledExtensionCount;
    public byte** EnabledExtensionNames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrExtensionProperties
{
    public XrStructureType Type;
    public void* Next;
    public fixed byte ExtensionName[128];
    public uint ExtensionVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrInstanceProperties
{
    public XrStructureType Type;
    public void* Next;
    public ulong RuntimeVersion;
    public fixed byte RuntimeName[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSystemGetInfo
{
    public XrStructureType Type;
    public void* Next;
    public int FormFactor;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSystemProperties
{
    public XrStructureType Type;
    public void* Next;
    public ulong SystemId;
    public uint VendorId;
    public fixed byte SystemName[256];
    public uint MaxSwapchainImageHeight;
    public uint MaxSwapchainImageWidth;
    public uint MaxLayerCount;
    public uint OrientationTracking;
    public uint PositionTracking;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewConfigurationView
{
    public XrStructureType Type;
    public void* Next;
    public uint RecommendedImageRectWidth;
    public uint MaxImageRectWidth;
    public uint RecommendedImageRectHeight;
    public uint MaxImageRectHeight;
    public uint RecommendedSwapchainSampleCount;
    public uint MaxSwapchainSampleCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrGraphicsRequirementsD3D12KHR
{
    public XrStructureType Type;
    public void* Next;
    public uint AdapterLuidLowPart;
    public int AdapterLuidHighPart;
    public int MinFeatureLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrGraphicsBindingD3D12KHR
{
    public XrStructureType Type;
    public void* Next;
    public nint Device;
    public nint Queue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSessionCreateInfo
{
    public XrStructureType Type;
    public void* Next;
    public ulong CreateFlags;
    public ulong SystemId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrQuaternionf
{
    public float X, Y, Z, W;

    public static readonly XrQuaternionf Identity = new() { W = 1f };
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrVector3f
{
    public float X, Y, Z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrPosef
{
    public XrQuaternionf Orientation;
    public XrVector3f Position;

    public static readonly XrPosef Identity = new() { Orientation = XrQuaternionf.Identity };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrReferenceSpaceCreateInfo
{
    public XrStructureType Type;
    public void* Next;
    public int ReferenceSpaceType;
    public XrPosef PoseInReferenceSpace;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainCreateInfo
{
    public XrStructureType Type;
    public void* Next;
    public ulong CreateFlags;
    public ulong UsageFlags;
    public long Format;
    public uint SampleCount;
    public uint Width;
    public uint Height;
    public uint FaceCount;
    public uint ArraySize;
    public uint MipCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageD3D12KHR
{
    public XrStructureType Type;
    public void* Next;
    public nint Texture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageAcquireInfo
{
    public XrStructureType Type;
    public void* Next;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageWaitInfo
{
    public XrStructureType Type;
    public void* Next;
    public long Timeout;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSwapchainImageReleaseInfo
{
    public XrStructureType Type;
    public void* Next;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSessionBeginInfo
{
    public XrStructureType Type;
    public void* Next;
    public int PrimaryViewConfigurationType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameWaitInfo
{
    public XrStructureType Type;
    public void* Next;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameState
{
    public XrStructureType Type;
    public void* Next;
    public long PredictedDisplayTime;
    public long PredictedDisplayPeriod;
    public uint ShouldRender;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameBeginInfo
{
    public XrStructureType Type;
    public void* Next;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewLocateInfo
{
    public XrStructureType Type;
    public void* Next;
    public int ViewConfigurationType;
    public long DisplayTime;
    public ulong Space;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrViewState
{
    public XrStructureType Type;
    public void* Next;
    public ulong ViewStateFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrFovf
{
    public float AngleLeft, AngleRight, AngleUp, AngleDown;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrView
{
    public XrStructureType Type;
    public void* Next;
    public XrPosef Pose;
    public XrFovf Fov;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrSpaceLocation
{
    public XrStructureType Type;
    public void* Next;
    public ulong LocationFlags;
    public XrPosef Pose;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrRect2Di
{
    public int OffsetX, OffsetY;
    public int Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrSwapchainSubImage
{
    public ulong Swapchain;
    public XrRect2Di ImageRect;
    public uint ImageArrayIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerProjectionView
{
    public XrStructureType Type;
    public void* Next;
    public XrPosef Pose;
    public XrFovf Fov;
    public XrSwapchainSubImage SubImage;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerProjection
{
    public XrStructureType Type;
    public void* Next;
    public ulong LayerFlags;
    public ulong Space;
    public uint ViewCount;
    public XrCompositionLayerProjectionView* Views;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrExtent2Df
{
    public float Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerQuad
{
    public XrStructureType Type;
    public void* Next;
    public ulong LayerFlags;
    public ulong Space;
    public int EyeVisibility;
    public XrSwapchainSubImage SubImage;
    public XrPosef Pose;
    public XrExtent2Df Size;
}

/// <summary>XR_KHR_composition_layer_cylinder: an image on the inside of a cylinder section around the viewer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrCompositionLayerCylinderKHR
{
    public XrStructureType Type;
    public void* Next;
    public ulong LayerFlags;
    public ulong Space;
    public int EyeVisibility;
    public XrSwapchainSubImage SubImage;
    public XrPosef Pose;
    public float Radius;
    public float CentralAngle;
    public float AspectRatio;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrFrameEndInfo
{
    public XrStructureType Type;
    public void* Next;
    public long DisplayTime;
    public int EnvironmentBlendMode;
    public uint LayerCount;
    public void** Layers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrEventDataBuffer
{
    public XrStructureType Type;
    public void* Next;
    public fixed byte Varying[4000];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XrEventDataSessionStateChanged
{
    public XrStructureType Type;
    public void* Next;
    public ulong Session;
    public XrSessionState State;
    public long Time;
}

// Loader <-> runtime negotiation (openxr_loader_negotiation.h).
[StructLayout(LayoutKind.Sequential)]
internal struct XrNegotiateLoaderInfo
{
    public int StructType;
    public uint StructVersion;
    public nuint StructSize;
    public uint MinInterfaceVersion;
    public uint MaxInterfaceVersion;
    public ulong MinApiVersion;
    public ulong MaxApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XrNegotiateRuntimeRequest
{
    public int StructType;
    public uint StructVersion;
    public nuint StructSize;
    public uint RuntimeInterfaceVersion;
    public ulong RuntimeApiVersion;
    public nint GetInstanceProcAddr;
}
