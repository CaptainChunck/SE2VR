using System;
using System.Runtime.InteropServices;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.BindableBuffers;
using Keen.VRage.Render12.Resources.BindableTextures;
using SE2VR.OpenXR;
using Vortice.Direct3D12;

namespace SE2VR.Rendering;

/// <summary>
/// The game's mouse pointer is the Windows cursor, which Windows draws on the monitor only, so it is invisible in the
/// headset. While Windows shows the pointer over the focused game window, a pointer image is placed on the UI panel as
/// a small layer of its own, at the position the game's UI uses for the mouse.
/// </summary>
internal static unsafe class VrCursor
{
    public const int ImageSize = 64;

    /// <summary>Edge length of the pointer image on the panel, in UI design pixels (1080 per canvas height at 16:9).</summary>
    private const float SizeInDesignPixels = 44f;

    /// <summary>Where the arrow tip sits in the image, in pixels from the top-left corner (room for the outline).</summary>
    private const float HotSpot = 3f;

    private static XrImageSwapchain? _swapchain;
    private static UploadBuffer? _upload;
    private static bool _failed;

    /// <summary>The pointer image: premultiplied RGBA, sRGB-encoded, <see cref="ImageSize"/> pixels square.</summary>
    public static readonly byte[] Pixels = DrawArrow();

    /// <summary>
    /// Mouse position on the UI canvas (0..1 from its top-left corner) while Windows shows the pointer over the focused
    /// game window. The canvas is the top-left part of the window (see <see cref="UiCanvas"/>).
    /// </summary>
    /// <summary>True while Windows shows the mouse pointer: the game shows it with menus and hides it during play (mouse look).</summary>
    public static bool IsPointerShowing()
    {
        CursorInfo info = new() { Size = sizeof(CursorInfo) };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0 && info.Cursor != 0;
    }

    public static bool TryGetCanvasPosition(Vector2I window, Vector2I canvas, out float u, out float v)
    {
        u = v = 0f;
        if (!VRConfig.Current.ShowCursor || _failed)
        {
            return false;
        }
        CursorInfo info = new() { Size = sizeof(CursorInfo) };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == 0)
        {
            return false; // hidden by the game (mouse look) or by Windows
        }
        nint window32 = GetForegroundWindow();
        if (window32 == 0 || GetWindowThreadProcessId(window32, out uint processId) == 0 || processId != (uint)Environment.ProcessId)
        {
            return false;
        }
        Point point = info.ScreenPosition;
        if (!ScreenToClient(window32, ref point) || !GetClientRect(window32, out Rect client) || client.Right <= 0 || client.Bottom <= 0)
        {
            return false;
        }
        // The game's own mapping (GameForm): client pixels scaled to the render resolution, as the window may have been
        // shrunk to fit the screen.
        float x = point.X * (float)window.X / client.Right;
        float y = point.Y * (float)window.Y / client.Bottom;
        u = x / canvas.X;
        v = y / canvas.Y;
        return u >= 0f && u < 1f && v >= 0f && v < 1f;
    }

    /// <summary>
    /// Creates the pointer image on first use: records its upload into a static OpenXR image. The image must be released
    /// (<see cref="ReleaseImage"/>) after this frame's work was submitted to the GPU queue.
    /// </summary>
    public static bool EnsureImage(XrRuntime xr)
    {
        if (_swapchain != null || _failed)
        {
            return _swapchain != null;
        }
        try
        {
            _swapchain = new XrImageSwapchain(xr, "Cursor", ImageSize, ImageSize, staticImage: true);
            if (!_swapchain.Acquire())
            {
                throw new InvalidOperationException("Could not acquire the pointer image.");
            }
            BackBuffer image = _swapchain.Current!;
            SubresourceFootPrint footprint = CoreSystems.DeviceContext.CreateSubresourceFootprint(image.D3DResourceDescription, 0);
            int rowBytes = ImageSize * 4;
            _upload = CoreSystems.BindableBuffers.CreateUploadBuffer("SE2VR cursor upload", 1, (int)(footprint.RowPitch * footprint.Height));
            Span<byte> memory = _upload.MappedMemory;
            for (int y = 0; y < ImageSize; y++)
            {
                Pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(memory.Slice(y * (int)footprint.RowPitch));
            }

            using DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.CursorUpload");
            commandList.OnAction();
            commandList.ExplicitStateTransition(image.AutoResourceState, ResourceStates.CopyDest, discardResource: false);
            commandList.ResolveResourceStates();
            commandList._d3dCommandList.CopyTextureRegion(
                new TextureCopyLocation(image.D3DResource, 0), 0, 0, 0,
                new TextureCopyLocation(_upload.D3DResource, new PlacedSubresourceFootPrint { Footprint = footprint, Offset = 0 }));
            commandList.ExplicitStateTransition(image.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
            commandList.ResolveResourceStates();
            Log.Info("Created the VR mouse pointer.");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Could not create the VR mouse pointer; the UI works without it.", e);
            _failed = true;
            Dispose();
            return false;
        }
    }

    private static int _placementChecks;

    /// <summary>
    /// The mouse can only reach the part of the UI canvas that is on a monitor. The per-eye window is often taller than
    /// the screen; if part of the canvas (the top of the window) is off screen, the window is moved once so all of it is
    /// reachable. Call every frame; it does its work once, as soon as the game window exists.
    /// </summary>
    public static void CheckWindowPlacement(Vector2I window, Vector2I canvas)
    {
        if (_placementChecks < 0 || ++_placementChecks % 30 != 1)
        {
            return;
        }
        if (_placementChecks > 30 * 60)
        {
            _placementChecks = -1; // no window after a minute of frames: give up quietly
            return;
        }
        nint handle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (handle == 0 || !GetClientRect(handle, out Rect client) || client.Right <= 0 || client.Bottom <= 0)
        {
            return;
        }
        _placementChecks = -1;
        Point origin = default;
        ClientToScreen(handle, ref origin);
        // The canvas is the top of the client area, scaled like the render resolution.
        int width = client.Right, height = (int)MathF.Ceiling(client.Bottom * (float)canvas.Y / window.Y);
        string where = $"Game window client area {client.Right}x{client.Bottom} at ({origin.X}, {origin.Y}); UI canvas {width}x{height} on screen.";
        if (OnSomeMonitor(origin.X, origin.Y) && OnSomeMonitor(origin.X + width - 1, origin.Y) &&
            OnSomeMonitor(origin.X, origin.Y + height - 1) && OnSomeMonitor(origin.X + width - 1, origin.Y + height - 1))
        {
            Log.Info(where);
            return;
        }
        // Put the client area's top-left corner at the top-left of the primary monitor's work area.
        MonitorInfo monitor = new() { Size = sizeof(MonitorInfo) };
        if (!GetMonitorInfo(MonitorFromPoint(default, 1 /* primary */), ref monitor) || !GetWindowRect(handle, out Rect frame))
        {
            Log.Warn(where + " Part of the UI is off screen, where the mouse cannot reach it; move the game window onto a monitor.");
            return;
        }
        int x = monitor.Work.Left - (origin.X - frame.Left), y = monitor.Work.Top - (origin.Y - frame.Top);
        // Asynchronous: the window belongs to another thread, which may be waiting for this one.
        SetWindowPos(handle, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
        Log.Warn(where + $" Part of it was off screen, where the mouse cannot reach it; moved the window to ({x}, {y}).");
    }

    private static bool OnSomeMonitor(int x, int y) => MonitorFromPoint(new Point { X = x, Y = y }, 0 /* null if none */) != 0;

    /// <summary>Releases the image after its one-time upload; nothing to do afterwards.</summary>
    public static void ReleaseImage() => _swapchain?.Release();

    /// <summary>The pointer layer with its tip at canvas position (<paramref name="u"/>, <paramref name="v"/>).</summary>
    public static XrCompositionLayerQuad CreateLayer(ulong space, in UiPanel panel, float u, float v, out float size)
    {
        // Meters per UI design pixel: the design canvas is 1080 high, or 1920 wide when the canvas is narrower than 16:9.
        float meterPerDesignPixel = panel.Width / MathF.Max(1920f, 1080f * panel.Aspect);
        size = SizeInDesignPixels * meterPerDesignPixel;
        // The tip is near the image's top-left corner; the quad is positioned by its center.
        float offset = (0.5f - HotSpot / ImageSize) * size;
        XrPosef pose = panel.SurfacePose(u + offset / panel.Width, v + offset / panel.Height);
        return new XrCompositionLayerQuad
        {
            Type = XrStructureType.CompositionLayerQuad,
            LayerFlags = XrConst.CompositionLayerBlendTextureSourceAlpha,
            Space = space,
            EyeVisibility = XrConst.EyeVisibilityBoth,
            SubImage = _swapchain!.SubImage,
            Pose = pose,
            Size = new XrExtent2Df { Width = size, Height = size }
        };
    }

    /// <summary>Render thread, GPU idle.</summary>
    public static void Dispose()
    {
        _swapchain?.Dispose();
        _swapchain = null;
        _upload?.Dispose();
        _upload = null;
    }

    /// <summary>A classic arrow pointer: white with a dark outline, anti-aliased, tip at (<see cref="HotSpot"/>, <see cref="HotSpot"/>).</summary>
    private static byte[] DrawArrow()
    {
        // Outline of the arrow in "pointer units" (tip at the origin, y down), scaled to the image below.
        (float X, float Y)[] shape =
        {
            (0f, 0f), (0f, 16.5f), (4.1f, 12.8f), (7f, 19.2f), (9.6f, 18f), (6.8f, 11.8f), (11.8f, 11.8f)
        };
        const float scale = 2.75f;
        const float outline = 2.2f; // dark border inside the shape, in pixels
        var points = new (float X, float Y)[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            points[i] = (HotSpot + shape[i].X * scale, HotSpot + shape[i].Y * scale);
        }

        const int samples = 4;
        byte[] pixels = new byte[ImageSize * ImageSize * 4];
        for (int py = 0; py < ImageSize; py++)
        {
            for (int px = 0; px < ImageSize; px++)
            {
                float color = 0f, alpha = 0f; // linear, premultiplied
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float x = px + (sx + 0.5f) / samples, y = py + (sy + 0.5f) / samples;
                        if (!Inside(points, x, y))
                        {
                            continue;
                        }
                        alpha += 1f;
                        color += DistanceToEdges(points, x, y) > outline ? 1f : 0.02f;
                    }
                }
                alpha /= samples * samples;
                color /= samples * samples;
                int i = (py * ImageSize + px) * 4;
                byte encoded = (byte)MathF.Round(LinearToSrgb(color) * 255f);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = encoded;
                pixels[i + 3] = (byte)MathF.Round(alpha * 255f);
            }
        }
        return pixels;
    }

    private static bool Inside((float X, float Y)[] polygon, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            (float xi, float yi) = polygon[i];
            (float xj, float yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static float DistanceToEdges((float X, float Y)[] polygon, float x, float y)
    {
        float best = float.MaxValue;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            (float ax, float ay) = polygon[j];
            (float bx, float by) = polygon[i];
            float dx = bx - ax, dy = by - ay;
            float t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
            float ex = ax + t * dx - x, ey = ay + t * dy - y;
            best = MathF.Min(best, MathF.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    private static float LinearToSrgb(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private const int CursorShowing = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public Point ScreenPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint window, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpAsyncWindowPos = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
