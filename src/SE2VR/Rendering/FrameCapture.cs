using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.BindableBuffers;
using Keen.VRage.Render12.Resources.BindableTextures;
using Vortice.Direct3D12;

namespace SE2VR.Rendering;

/// <summary>
/// Saves exactly what is handed to the headset (left eye, right eye, UI panel) as PNG files, for debugging.
/// Triggered by the capture hotkey or by creating a file named "capture" next to the plugin.
/// </summary>
internal static class FrameCapture
{
    private readonly struct Item
    {
        public required string Name { get; init; }
        public required ReadbackBuffer Buffer { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int RowPitch { get; init; }
    }

    private static readonly List<Item> _items = new();

    /// <summary>Set to capture the next rendered VR frame.</summary>
    public static bool Requested { get; set; }

    /// <summary>True while the current frame is being captured.</summary>
    public static bool Active { get; private set; }

    public static void BeginFrame()
    {
        Active = Requested;
        Requested = false;
    }

    /// <summary>Records a GPU copy of an OpenXR image (in RENDER_TARGET state) into a CPU-readable buffer.</summary>
    public static void Record(DirectCommandList commandList, BackBuffer image, string name)
    {
        if (!Active)
        {
            return;
        }
        ResourceDescription description = image.D3DResourceDescription;
        SubresourceFootPrint footprint = CoreSystems.DeviceContext.CreateSubresourceFootprint(description, 0);
        ReadbackBuffer buffer = CoreSystems.BindableBuffers.CreateReadbackBuffer($"SE2VR capture {name}", 1, (int)(footprint.RowPitch * footprint.Height));

        // Same sequence the engine's own copy helpers use: request state, flush barriers, copy, restore state.
        commandList.OnAction();
        commandList.ExplicitStateTransition(image.AutoResourceState, ResourceStates.CopySource, discardResource: false);
        commandList.ResolveResourceStates();
        commandList._d3dCommandList.CopyTextureRegion(
            new TextureCopyLocation(buffer.D3DResource, new PlacedSubresourceFootPrint { Footprint = footprint, Offset = 0 }), 0, 0, 0,
            new TextureCopyLocation(image.D3DResource, 0));
        commandList.ExplicitStateTransition(image.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
        commandList.ResolveResourceStates();

        _items.Add(new Item
        {
            Name = name,
            Buffer = buffer,
            Width = (int)description.Width,
            Height = (int)description.Height,
            RowPitch = (int)footprint.RowPitch
        });
    }

    /// <summary>After the frame was submitted: wait for the GPU, read the pixels back and write the files in the background.</summary>
    public static void EndFrame(string directory)
    {
        Active = false;
        if (_items.Count == 0)
        {
            return;
        }
        CoreSystems.FrameDispatcher.FlushAllQueuesAndWaitCpu();
        string folder = Path.Combine(directory, "captures");
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        foreach (Item item in _items)
        {
            byte[] pixels = new byte[item.Width * item.Height * 4];
            using (ReadbackBuffer.MappedToken mapped = item.Buffer.Map())
            {
                Span<byte> data = mapped.Data;
                for (int y = 0; y < item.Height; y++)
                {
                    data.Slice(y * item.RowPitch, item.Width * 4).CopyTo(pixels.AsSpan(y * item.Width * 4));
                }
            }
            item.Buffer.Dispose();
            string path = Path.Combine(folder, $"{stamp}_{item.Name}.png");
            int width = item.Width, height = item.Height;
            string name = item.Name;
            ThreadPool.QueueUserWorkItem(_ => Save(path, width, height, pixels, name));
        }
        _items.Clear();
    }

    private static void Save(string path, int width, int height, byte[] pixels, string name)
    {
        try
        {
            PngWriter.Write(path, width, height, pixels, 4);
            int transparent = 0, opaque = 0, partial = 0, colorWithoutAlpha = 0;
            byte[] alpha = new byte[width * height];
            for (int i = 0; i < alpha.Length; i++)
            {
                byte a = pixels[i * 4 + 3];
                alpha[i] = a;
                if (a == 0)
                {
                    transparent++;
                    if ((pixels[i * 4] | pixels[i * 4 + 1] | pixels[i * 4 + 2]) > 8)
                    {
                        colorWithoutAlpha++;
                    }
                }
                else if (a == 255)
                {
                    opaque++;
                }
                else
                {
                    partial++;
                }
            }
            string stats = $"alpha: {100.0 * transparent / alpha.Length:0.#}% zero ({100.0 * colorWithoutAlpha / alpha.Length:0.#}% zero-alpha pixels still have color), " +
                           $"{100.0 * partial / alpha.Length:0.#}% partial, {100.0 * opaque / alpha.Length:0.#}% opaque";
            if (name == "ui")
            {
                PngWriter.Write(Path.ChangeExtension(path, null) + "_alpha.png", width, height, alpha, 1);
            }
            Log.Info($"Captured {path} ({width}x{height}, {stats})");
        }
        catch (Exception e)
        {
            Log.Error($"Saving capture {path} failed", e);
        }
    }
}
