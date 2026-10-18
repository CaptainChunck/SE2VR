using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.BindableBuffers;
using Keen.VRage.Render12.Resources.BindableTextures;
using Vortice.Direct3D12;

namespace SE2VR.Rendering;

/// <summary>
/// Saves exactly what is handed to the headset (left eye, right eye, UI panel) as PNG files, plus a simulated
/// headset view per eye with the UI panel composited the way the runtime does it. For debugging.
/// Triggered by the capture hotkey or by creating a file named "capture" next to the plugin.
/// </summary>
internal static class FrameCapture
{
    private readonly struct Pending
    {
        public required string Name { get; init; }
        public required ReadbackBuffer Buffer { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int RowPitch { get; init; }
    }

    private static readonly List<Pending> _pending = new();

    /// <summary>Set to capture the next rendered VR frame.</summary>
    public static bool Requested { get; set; }

    /// <summary>True while the current frame is being captured.</summary>
    public static bool Active { get; private set; }

    /// <summary>Optional label added to the file names (used by the test script to name UI screens).</summary>
    public static string Label { get; set; } = "";

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

        _pending.Add(new Pending
        {
            Name = name,
            Buffer = buffer,
            Width = (int)description.Width,
            Height = (int)description.Height,
            RowPitch = (int)footprint.RowPitch
        });
    }

    /// <summary>After the frame was submitted: wait for the GPU, read the pixels back and write the files in the background.</summary>
    public static void EndFrame(string directory, CompositionSnapshot? snapshot)
    {
        Active = false;
        if (_pending.Count == 0)
        {
            return;
        }
        CoreSystems.FrameDispatcher.FlushAllQueuesAndWaitCpu();
        var images = new Dictionary<string, CompositionPreview.Image>();
        foreach (Pending item in _pending)
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
            images[item.Name] = new CompositionPreview.Image { Pixels = pixels, Width = item.Width, Height = item.Height };
        }
        _pending.Clear();

        string folder = Path.Combine(directory, "captures");
        Directory.CreateDirectory(folder);
        string label = string.Concat(Label.Split(Path.GetInvalidFileNameChars()));
        string prefix = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss") + (label.Length > 0 ? "_" + label : ""));
        Label = "";
        ThreadPool.QueueUserWorkItem(_ => Save(prefix, images, snapshot));
    }

    private static void Save(string prefix, Dictionary<string, CompositionPreview.Image> images, CompositionSnapshot? snapshot)
    {
        try
        {
            foreach ((string name, CompositionPreview.Image image) in images)
            {
                PngWriter.Write($"{prefix}_{name}.png", image.Width, image.Height, image.Pixels, 4);
                Log.Info($"Captured {prefix}_{name}.png ({image.Width}x{image.Height}, {AlphaStats(image)})");
            }
            if (snapshot == null)
            {
                return;
            }
            images["cursor"] = new CompositionPreview.Image { Pixels = VrCursor.Pixels, Width = VrCursor.ImageSize, Height = VrCursor.ImageSize };
            for (int eye = 0; eye < 2; eye++)
            {
                CompositionPreview.Image? eyeImage = images.TryGetValue(eye == 0 ? "left" : "right", out var e) ? e : null;
                int size = eyeImage?.Width ?? (images.TryGetValue("menu", out var menu) ? menu.Width : 1024);
                CompositionPreview.Image view = CompositionPreview.Compose(snapshot, eye, eyeImage, images, size, size, out string stats);
                string path = $"{prefix}_{(eye == 0 ? "left" : "right")}_view.png";
                PngWriter.Write(path, view.Width, view.Height, view.Pixels, 4);
                Log.Info($"Simulated headset view {path}: {stats}");
            }
        }
        catch (Exception e)
        {
            Log.Error($"Saving capture {prefix} failed", e);
        }
    }

    private static string AlphaStats(CompositionPreview.Image image)
    {
        byte[] p = image.Pixels;
        int count = p.Length / 4, zero = 0, colorWithoutAlpha = 0, opaque = 0;
        for (int i = 0; i < count; i++)
        {
            byte a = p[i * 4 + 3];
            if (a == 0)
            {
                zero++;
                if ((p[i * 4] | p[i * 4 + 1] | p[i * 4 + 2]) > 8)
                {
                    colorWithoutAlpha++;
                }
            }
            else if (a == 255)
            {
                opaque++;
            }
        }
        return $"alpha: {100.0 * zero / count:0.#}% transparent ({100.0 * colorWithoutAlpha / count:0.##}% of pixels have color but no alpha), " +
               $"{100.0 * (count - zero - opaque) / count:0.#}% partial, {100.0 * opaque / count:0.#}% opaque";
    }
}
