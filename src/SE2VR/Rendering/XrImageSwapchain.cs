using System;
using System.Runtime.InteropServices;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Resources.BindableTextures;
using SE2VR.OpenXR;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Color = Vortice.Mathematics.Color;

namespace SE2VR.Rendering;

/// <summary>
/// An OpenXR swapchain whose images are wrapped in the engine's own <see cref="BackBuffer"/> type, so the renderer's
/// resource-state tracking and copy helpers work on them exactly like on the desktop swapchain.
/// </summary>
internal sealed class XrImageSwapchain : IDisposable
{
    private readonly XrRuntime _xr;
    private readonly BackBuffer[] _images;
    private int _acquired = -1;

    public ulong Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public string Name { get; }

    /// <summary>The image acquired for the current frame, or null.</summary>
    public BackBuffer? Current => _acquired >= 0 ? _images[_acquired] : null;

    /// <param name="staticImage">One image, acquired and written once, then shown unchanged (OpenXR static swapchain).</param>
    public XrImageSwapchain(XrRuntime xr, string name, int width, int height, bool staticImage = false)
    {
        _xr = xr;
        Name = name;
        Width = width;
        Height = height;
        Handle = xr.CreateSwapchain(XrConst.DxgiR8G8B8A8UnormSrgb, width, height, staticImage, out nint[] textures);
        _images = new BackBuffer[textures.Length];
        for (int i = 0; i < textures.Length; i++)
        {
            // The runtime owns the texture; take our own reference because BackBuffer.GPUDispose releases one.
            Marshal.AddRef(textures[i]);
            var resource = new ID3D12Resource(textures[i]);
            resource.Name = $"OpenXR {name}[{i}]";
            var image = new BackBuffer();
            // XR_KHR_D3D12_enable: color images are handed out (and must be returned) in RENDER_TARGET state.
            image.Initialize(resource, ResourceStates.RenderTarget, new Vector2I(width, height), Format.R8G8B8A8_UNorm_SRgb, new Color(0f, 0f, 0f, 0f));
            _images[i] = image;
        }
        Log.Info($"Created OpenXR swapchain '{name}' {width}x{height} with {_images.Length} images");
    }

    public bool Acquire()
    {
        if (_acquired >= 0)
        {
            return true;
        }
        _acquired = _xr.AcquireImage(Handle);
        return _acquired >= 0;
    }

    /// <summary>Call only after the GPU work writing the image has been submitted to the queue given to OpenXR.</summary>
    public void Release()
    {
        if (_acquired < 0)
        {
            return;
        }
        _acquired = -1;
        _xr.ReleaseImage(Handle);
    }

    public XrSwapchainSubImage SubImage => new()
    {
        Swapchain = Handle,
        ImageRect = new XrRect2Di { Width = Width, Height = Height }
    };

    /// <summary>The GPU must be idle (or at least done with these images) before calling this.</summary>
    public void Dispose()
    {
        Release();
        foreach (BackBuffer image in _images)
        {
            image.GPUDispose();
        }
        _xr.DestroySwapchain(Handle);
    }
}
