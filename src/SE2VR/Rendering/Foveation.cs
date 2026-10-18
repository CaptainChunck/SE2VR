using System;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using SE2VR.OpenXR;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace SE2VR.Rendering;

/// <summary>
/// Fixed foveated rendering with variable rate shading (D3D12 VRS tier 2, e.g. NVIDIA RTX / GTX 16, AMD RX 6000+).
/// <para>
/// A flat (rectilinear) eye image spends most of its pixels on the edges of the view: on a headset with about 100
/// degrees per eye, well over half of the image lies more than 35 degrees from the lens center, where the lens blurs
/// it anyway. While the lighting, transparents and particles are drawn, each 16x16 tile there is shaded at one color
/// per 2x2 (or 4x4 in the far corners) pixels instead of per pixel. Geometry, depth and textures stay per pixel (the
/// G-buffer is untouched), so edges stay sharp; it is the lighting that gets coarser where the lens blurs anyway, and
/// FSR's jittered accumulation smooths it further. The rates follow each eye's own lens center, which sits off the
/// image center (eye fields of view are asymmetric).
/// </para>
/// Render thread only.
/// </summary>
internal static class Foveation
{
    private const byte Rate1x1 = 0x0, Rate2x2 = 0x5, Rate4x4 = 0xA;

    private static bool _checked;
    private static bool _supported;
    private static bool _additionalRates;
    private static int _tileSize = 16;
    private static readonly ID3D12Resource?[] _images = new ID3D12Resource?[2];
    private static readonly int[] _imageTilesX = new int[2], _imageTilesY = new int[2];
    private static readonly string?[] _built = new string?[2];
    private static readonly bool[] _ready = new bool[2];
    // The engine records passes on worker threads too, one command list per thread at a time.
    [ThreadStatic] private static ID3D12GraphicsCommandList5? _list5;
    [ThreadStatic] private static nint _list5Source;
    [ThreadStatic] private static bool _applied;
    private static bool _loggedLevel;
    private static readonly XrVector2f[]?[] _outlines = new XrVector2f[]?[2];
    private static int _outlineVersion = -1;

    /// <summary>Degrees from the lens center beyond which tiles are shaded 2x2, and 4x4; 0 = off.</summary>
    // 4x4 lighting is visibly blocky on bright, glossy surfaces even at the edge of the view, so only the
    // Performance level uses it inside the lens outline; outside it (never seen) every level does.
    private static (float Coarse, float Coarsest) Thresholds(string level) => level switch
    {
        "Quality" => (48f, 999f),
        "Balanced" => (40f, 999f),
        "Performance" => (32f, 50f),
        _ => (0f, 0f)
    };

    public static bool IsSupported
    {
        get
        {
            CheckSupport();
            return _supported;
        }
    }

    private static void CheckSupport()
    {
        if (_checked)
        {
            return;
        }
        _checked = true;
        try
        {
            FeatureDataD3D12Options6 options = CoreSystems.DeviceWrap.D3DDevice.Options6;
            _supported = options.VariableShadingRateTier >= VariableShadingRateTier.Tier2;
            _additionalRates = options.AdditionalShadingRatesSupported;
            _tileSize = (int)Math.Max(options.ShadingRateImageTileSize, 8u);
            Log.Info(_supported
                ? $"Foveated rendering available (variable rate shading tier 2, {_tileSize}px tiles{(_additionalRates ? ", 4x4 rate" : "")})."
                : "Foveated rendering is not available: the GPU does not support variable rate shading tier 2.");
        }
        catch (Exception e)
        {
            _supported = false;
            Log.Warn($"Variable rate shading check failed: {e.Message}");
        }
    }

    /// <summary>Before an eye's scene draw: make sure that eye's rate map matches the render size and field of view.</summary>
    public static void PrepareEye(XrRuntime xr, int eye, Vector2I renderSize, in XrFovf fov)
    {
        _ready[eye] = false;
        string level = VRConfig.Current.Foveation;
        (float coarse, float coarsest) = Thresholds(level);
        if (coarse <= 0 || !IsSupported || renderSize.X <= 0 || renderSize.Y <= 0)
        {
            return;
        }
        if (_outlineVersion != xr.VisibilityMaskVersion)
        {
            // The part of each image the lenses show; tiles entirely outside it get the coarsest rate.
            _outlineVersion = xr.VisibilityMaskVersion;
            for (int i = 0; i < 2; i++)
            {
                _outlines[i] = xr.GetVisibleOutline(i);
                _built[i] = null;
            }
            Log.Info(_outlines[0] != null
                ? $"Lens outline from the VR runtime: {_outlines[0]!.Length} points per eye."
                : "The VR runtime gives no lens outline; foveation uses the angle from the lens center only.");
        }
        if (!_loggedLevel)
        {
            _loggedLevel = true;
            Log.Info($"Foveated rendering: {level} (2x2 shading beyond {coarse:0} degrees from the lens center, 4x4 beyond {coarsest:0}).");
        }
        string key = $"{renderSize.X}x{renderSize.Y} {fov.AngleLeft:0.000} {fov.AngleRight:0.000} {fov.AngleUp:0.000} {fov.AngleDown:0.000} {level}";
        try
        {
            if (_built[eye] != key)
            {
                Upload(eye, renderSize, fov, coarse, _additionalRates ? coarsest : 999f);
                _built[eye] = key;
            }
            _ready[eye] = _images[eye] != null;
        }
        catch (Exception e)
        {
            Log.Error("Foveated rendering failed; turning it off.", e);
            VRConfig.Current.Foveation = "Off";
        }
    }

    private static unsafe void Upload(int eye, Vector2I renderSize, in XrFovf fov, float coarseDegrees, float coarsestDegrees)
    {
        ID3D12Device device = CoreSystems.DeviceWrap.D3DDevice;
        int tilesX = (renderSize.X + _tileSize - 1) / _tileSize;
        int tilesY = (renderSize.Y + _tileSize - 1) / _tileSize;
        bool fresh = false;
        if (_images[eye] == null || _imageTilesX[eye] < tilesX || _imageTilesY[eye] < tilesY)
        {
            if (_images[eye] != null)
            {
                CoreSystems.GPUResourcePool.DelayedDisposeD3D(_images[eye]!);
            }
            // Sized for the whole eye image, so render-size changes (dynamic resolution) never need a new one.
            Vector2I full = CoreSystems.SwapChain.Resolution;
            int maxX = Math.Max(tilesX, (full.X + _tileSize - 1) / _tileSize);
            int maxY = Math.Max(tilesY, (full.Y + _tileSize - 1) / _tileSize);
            _images[eye] = device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
                ResourceDescription.Texture2D(Format.R8_UInt, (uint)maxX, (uint)maxY, 1, 1), ResourceStates.CopyDest);
            _images[eye]!.Name = $"SE2VR foveation {(eye == 0 ? "left" : "right")}";
            _imageTilesX[eye] = maxX;
            _imageTilesY[eye] = maxY;
            fresh = true;
        }

        // Rates in tangent space: the image spans tan(left)..tan(right) by tan(up)..tan(down).
        float tanLeft = MathF.Tan(fov.AngleLeft), tanRight = MathF.Tan(fov.AngleRight);
        float tanUp = MathF.Tan(fov.AngleUp), tanDown = MathF.Tan(fov.AngleDown);
        float coarseTan = MathF.Tan(coarseDegrees * MathF.PI / 180f);
        float coarsestTan = coarsestDegrees >= 89f ? float.MaxValue : MathF.Tan(coarsestDegrees * MathF.PI / 180f);
        int width = _imageTilesX[eye], height = _imageTilesY[eye];
        const int pitchAlignment = 256;
        int rowPitch = (width + pitchAlignment - 1) / pitchAlignment * pitchAlignment;
        ID3D12Resource upload = device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)(rowPitch * height)), ResourceStates.GenericRead);
        byte* data;
        upload.Map(0, null, (void**)&data).CheckError();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte rate = Rate1x1;
                if (x < tilesX && y < tilesY)
                {
                    // Nearest point of the tile to the lens center decides, so a tile is never coarser than its best pixel.
                    float u0 = x * _tileSize / (float)renderSize.X, u1 = Math.Min((x + 1) * _tileSize / (float)renderSize.X, 1f);
                    float v0 = y * _tileSize / (float)renderSize.Y, v1 = Math.Min((y + 1) * _tileSize / (float)renderSize.Y, 1f);
                    float tx0 = tanLeft + u0 * (tanRight - tanLeft), tx1 = tanLeft + u1 * (tanRight - tanLeft);
                    float ty0 = tanUp + v0 * (tanDown - tanUp), ty1 = tanUp + v1 * (tanDown - tanUp);
                    float nx = tx0 > 0 ? tx0 : tx1 < 0 ? tx1 : 0f;
                    float ny = Math.Min(ty0, ty1) > 0 ? Math.Min(ty0, ty1) : Math.Max(ty0, ty1) < 0 ? Math.Max(ty0, ty1) : 0f;
                    // Angle from the view axis of the direction (nx, ny, 1).
                    float radius = MathF.Sqrt(nx * nx + ny * ny);
                    rate = radius >= coarsestTan ? Rate4x4 : radius >= coarseTan ? Rate2x2 : Rate1x1;
                    if (rate != Rate4x4 && _outlines[eye] is { } outline && OutsideOutline(outline, tx0, tx1, ty0, ty1))
                    {
                        rate = _additionalRates ? Rate4x4 : Rate2x2; // the lenses never show this tile
                    }
                }
                data[y * rowPitch + x] = rate;
            }
        }
        upload.Unmap(0);

        using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.Foveation"))
        {
            ID3D12GraphicsCommandList list = commandList._d3dCommandList;
            if (!fresh)
            {
                list.ResourceBarrierTransition(_images[eye]!, ResourceStates.ShadingRateSource, ResourceStates.CopyDest);
            }
            var footprint = new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint(Format.R8_UInt, (uint)width, (uint)height, 1, (uint)rowPitch)
            };
            list.CopyTextureRegion(new TextureCopyLocation(_images[eye]!, 0), 0, 0, 0, new TextureCopyLocation(upload, footprint));
            list.ResourceBarrierTransition(_images[eye]!, ResourceStates.CopyDest, ResourceStates.ShadingRateSource);
        }
        CoreSystems.GPUResourcePool.DelayedDisposeD3D(upload);
        if (Log.Verbose)
        {
            Log.Info($"Foveation map for the {(eye == 0 ? "left" : "right")} eye: {tilesX}x{tilesY} tiles of {renderSize.X}x{renderSize.Y}.");
        }
    }

    /// <summary>
    /// True when the whole tile (tangent-space corners) and a margin around it lie outside the lens outline: every
    /// corner of the grown tile is outside, and no outline point falls inside it.
    /// </summary>
    private static bool OutsideOutline(XrVector2f[] outline, float x0, float x1, float y0, float y1)
    {
        float marginX = Math.Abs(x1 - x0), marginY = Math.Abs(y1 - y0);
        float minX = Math.Min(x0, x1) - marginX, maxX = Math.Max(x0, x1) + marginX;
        float minY = Math.Min(y0, y1) - marginY, maxY = Math.Max(y0, y1) + marginY;
        if (Inside(outline, minX, minY) || Inside(outline, minX, maxY) || Inside(outline, maxX, minY) || Inside(outline, maxX, maxY))
        {
            return false;
        }
        foreach (XrVector2f p in outline)
        {
            if (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Point in polygon (even-odd rule).</summary>
    private static bool Inside(XrVector2f[] polygon, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            XrVector2f a = polygon[i], b = polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Turns coarse shading on for the current eye's following draws (see FoveationPatch); returns whether it did.</summary>
    public static bool Begin(DirectCommandList commandList)
    {
        int eye = StereoRenderer.CurrentEye;
        if (eye < 0 || _applied || !_ready[eye] || _images[eye] == null)
        {
            return false;
        }
        try
        {
            ID3D12GraphicsCommandList5? list = GetList5(commandList);
            if (list == null)
            {
                return false;
            }
            list.RSSetShadingRate(ShadingRate.Rate1x1, new[] { ShadingRateCombiner.Passthrough, ShadingRateCombiner.Override });
            list.RSSetShadingRateImage(_images[eye]);
            _applied = true;
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Foveated rendering failed; turning it off.", e);
            VRConfig.Current.Foveation = "Off";
            return false;
        }
    }

    public static void End(DirectCommandList commandList)
    {
        if (!_applied)
        {
            return;
        }
        _applied = false;
        try
        {
            ID3D12GraphicsCommandList5? list = GetList5(commandList);
            if (list == null)
            {
                return;
            }
            list.RSSetShadingRateImage(null);
            list.RSSetShadingRate(ShadingRate.Rate1x1, new[] { ShadingRateCombiner.Passthrough, ShadingRateCombiner.Passthrough });
        }
        catch (Exception e)
        {
            Log.Error("Foveated rendering failed; turning it off.", e);
            VRConfig.Current.Foveation = "Off";
        }
    }

    private static ID3D12GraphicsCommandList5? GetList5(DirectCommandList commandList)
    {
        ID3D12GraphicsCommandList list = commandList._d3dCommandList;
        if (_list5 != null && _list5Source == list.NativePointer)
        {
            return _list5;
        }
        _list5?.Dispose();
        _list5 = list.QueryInterfaceOrNull<ID3D12GraphicsCommandList5>();
        _list5Source = list.NativePointer;
        return _list5;
    }

    public static void Reset()
    {
        for (int eye = 0; eye < 2; eye++)
        {
            _images[eye]?.Dispose();
            _images[eye] = null;
            _built[eye] = null;
            _ready[eye] = false;
        }
        _outlines[0] = _outlines[1] = null;
        _outlineVersion = -1;
    }
}
