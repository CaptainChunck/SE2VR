using System;
using System.Collections.Generic;
using System.Text;
using SE2VR.OpenXR;

namespace SE2VR.Rendering;

/// <summary>A quad or cylinder layer as submitted to the compositor, in the render (recentered) space.</summary>
internal sealed class PreviewLayer
{
    /// <summary>Name of the captured image shown on the layer ("ui", "menu" or "cursor").</summary>
    public required string Image { get; init; }

    public required bool Blended { get; init; }
    public required XrPosef Pose { get; init; }

    /// <summary>Quad size in meters.</summary>
    public float Width { get; init; }

    public float Height { get; init; }

    /// <summary>Cylinder radius in meters; 0 for a quad.</summary>
    public float Radius { get; init; }

    public float CentralAngle { get; init; }
    public float AspectRatio { get; init; }

    public bool IsCylinder => Radius > 0f;
}

/// <summary>What was submitted to the compositor in one frame.</summary>
internal sealed class CompositionSnapshot
{
    public required XrView[] Views { get; init; }
    public required bool HasProjection { get; init; }
    public required PreviewLayer[] Layers { get; init; }
}

/// <summary>
/// CPU re-creation of what the headset compositor shows each eye (without lens distortion): the eye image with the
/// UI panel and pointer layers ray-traced into it using the frame's real eye poses and fields of view. Used to check
/// UI placement and stereo alignment without wearing the headset.
/// </summary>
internal static class CompositionPreview
{
    public readonly struct Image
    {
        public required byte[] Pixels { get; init; } // RGBA, sRGB-encoded, premultiplied where it has alpha
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <param name="eye">Eye image, or null when no projection layer was submitted (menus): black background.</param>
    /// <param name="images">Layer images by name.</param>
    /// <param name="width">Output size when <paramref name="eye"/> is null.</param>
    public static Image Compose(CompositionSnapshot frame, int eyeIndex, Image? eye, IReadOnlyDictionary<string, Image> images, int width, int height, out string stats)
    {
        int w = eye?.Width ?? width, h = eye?.Height ?? height;
        byte[] output = eye != null ? (byte[])eye.Value.Pixels.Clone() : new byte[w * h * 4];
        if (eye == null)
        {
            for (int i = 3; i < output.Length; i += 4)
            {
                output[i] = 255;
            }
        }

        XrView view = frame.Views[eyeIndex];
        float tanLeft = MathF.Tan(view.Fov.AngleLeft), tanRight = MathF.Tan(view.Fov.AngleRight);
        float tanUp = MathF.Tan(view.Fov.AngleUp), tanDown = MathF.Tan(view.Fov.AngleDown);
        XrQuaternionf q = view.Pose.Orientation;
        XrVector3f origin = view.Pose.Position;

        PreviewLayer[] layers = frame.Layers;
        var covered = new int[layers.Length];
        var bounds = new (int MinX, int MaxX, int MinY, int MaxY)[layers.Length];
        for (int l = 0; l < layers.Length; l++)
        {
            bounds[l] = (int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);
        }

        for (int py = 0; py < h; py++)
        {
            float ty = tanUp - (py + 0.5f) / h * (tanUp - tanDown);
            for (int px = 0; px < w; px++)
            {
                float tx = tanLeft + (px + 0.5f) / w * (tanRight - tanLeft);
                Rotate(q, tx, ty, -1f, out float dx, out float dy, out float dz);
                int d = (py * w + px) * 4;
                for (int l = 0; l < layers.Length; l++)
                {
                    PreviewLayer layer = layers[l];
                    if (!images.TryGetValue(layer.Image, out Image image) || !Intersect(layer, origin, dx, dy, dz, out float u, out float v))
                    {
                        continue;
                    }
                    covered[l]++;
                    bounds[l] = (Math.Min(bounds[l].MinX, px), Math.Max(bounds[l].MaxX, px), Math.Min(bounds[l].MinY, py), Math.Max(bounds[l].MaxY, py));
                    int s = ((int)(v * image.Height) * image.Width + (int)(u * image.Width)) * 4;
                    if (layer.Blended)
                    {
                        // Premultiplied "over", as the compositor does with BLEND_TEXTURE_SOURCE_ALPHA.
                        int inverse = 255 - image.Pixels[s + 3];
                        output[d] = (byte)Math.Min(255, image.Pixels[s] + output[d] * inverse / 255);
                        output[d + 1] = (byte)Math.Min(255, image.Pixels[s + 1] + output[d + 1] * inverse / 255);
                        output[d + 2] = (byte)Math.Min(255, image.Pixels[s + 2] + output[d + 2] * inverse / 255);
                    }
                    else
                    {
                        output[d] = image.Pixels[s];
                        output[d + 1] = image.Pixels[s + 1];
                        output[d + 2] = image.Pixels[s + 2];
                    }
                }
            }
        }

        var text = new StringBuilder();
        for (int l = 0; l < layers.Length; l++)
        {
            PreviewLayer layer = layers[l];
            if (text.Length > 0)
            {
                text.Append("; ");
            }
            string shape = layer.IsCylinder ? $"curved {layer.CentralAngle * 180f / MathF.PI:0} deg" : "flat";
            if (covered[l] == 0)
            {
                text.Append($"{layer.Image} ({shape}) NOT visible in this eye");
                continue;
            }
            text.Append($"{layer.Image} ({shape}) covers {100.0 * covered[l] / (w * h):0.##}% of the eye image, pixels x {bounds[l].MinX}-{bounds[l].MaxX}, y {bounds[l].MinY}-{bounds[l].MaxY}");
            if (layer.Image != "cursor")
            {
                bool cornersVisible = true;
                foreach (XrVector3f corner in Corners(layer))
                {
                    cornersVisible &= ProjectsInside(view, corner, tanLeft, tanRight, tanUp, tanDown);
                }
                text.Append($", all corners {(cornersVisible ? "visible" : "NOT visible")}");
            }
        }
        stats = layers.Length == 0 ? "no layers besides the eye images" : text.ToString();
        return new Image { Pixels = output, Width = w, Height = h };
    }

    /// <summary>Ray (in render space) against a layer; (u, v) from the image's top-left corner.</summary>
    private static bool Intersect(PreviewLayer layer, XrVector3f origin, float dx, float dy, float dz, out float u, out float v)
    {
        u = v = 0f;
        XrQuaternionf inverse = Conjugate(layer.Pose.Orientation);
        Rotate(inverse, origin.X - layer.Pose.Position.X, origin.Y - layer.Pose.Position.Y, origin.Z - layer.Pose.Position.Z, out float ox, out float oy, out float oz);
        Rotate(inverse, dx, dy, dz, out float lx, out float ly, out float lz);
        if (!layer.IsCylinder)
        {
            if (MathF.Abs(lz) < 1e-7f)
            {
                return false;
            }
            float t = -oz / lz;
            if (t <= 0f)
            {
                return false;
            }
            u = (ox + t * lx) / layer.Width + 0.5f;
            v = 0.5f - (oy + t * ly) / layer.Height;
        }
        else
        {
            // Inside of the cylinder x^2 + z^2 = r^2 around the layer's Y axis; the arc is centered on -Z.
            float r = layer.Radius;
            float a = lx * lx + lz * lz;
            if (a < 1e-12f)
            {
                return false;
            }
            float b = 2f * (ox * lx + oz * lz);
            float c = ox * ox + oz * oz - r * r;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
            {
                return false;
            }
            float t = (-b + MathF.Sqrt(discriminant)) / (2f * a);
            if (t <= 0f)
            {
                return false;
            }
            float x = ox + t * lx, y = oy + t * ly, z = oz + t * lz;
            float height = r * layer.CentralAngle / layer.AspectRatio;
            u = MathF.Atan2(x, -z) / layer.CentralAngle + 0.5f;
            v = 0.5f - y / height;
        }
        return u >= 0f && u < 1f && v >= 0f && v < 1f;
    }

    private static IEnumerable<XrVector3f> Corners(PreviewLayer layer)
    {
        foreach ((float sx, float sy) in new[] { (-1f, 1f), (1f, 1f), (-1f, -1f), (1f, -1f) })
        {
            float x, y, z;
            if (layer.IsCylinder)
            {
                float height = layer.Radius * layer.CentralAngle / layer.AspectRatio;
                float theta = sx * layer.CentralAngle * 0.5f;
                x = layer.Radius * MathF.Sin(theta);
                y = sy * height * 0.5f;
                z = -layer.Radius * MathF.Cos(theta);
            }
            else
            {
                x = sx * layer.Width * 0.5f;
                y = sy * layer.Height * 0.5f;
                z = 0f;
            }
            Rotate(layer.Pose.Orientation, x, y, z, out float rx, out float ry, out float rz);
            yield return new XrVector3f { X = rx + layer.Pose.Position.X, Y = ry + layer.Pose.Position.Y, Z = rz + layer.Pose.Position.Z };
        }
    }

    private static bool ProjectsInside(in XrView view, XrVector3f point, float tanLeft, float tanRight, float tanUp, float tanDown)
    {
        // Render space -> eye space: inverse-rotate the offset from the eye.
        Rotate(Conjugate(view.Pose.Orientation), point.X - view.Pose.Position.X, point.Y - view.Pose.Position.Y, point.Z - view.Pose.Position.Z,
               out float ex, out float ey, out float ez);
        if (ez >= 0f)
        {
            return false;
        }
        float tx = ex / -ez, ty = ey / -ez;
        return tx >= tanLeft && tx <= tanRight && ty >= tanDown && ty <= tanUp;
    }

    private static XrQuaternionf Conjugate(in XrQuaternionf q) => new() { X = -q.X, Y = -q.Y, Z = -q.Z, W = q.W };

    private static void Rotate(in XrQuaternionf q, float x, float y, float z, out float rx, out float ry, out float rz)
    {
        // v' = v + w*t + q.xyz x t, with t = 2 * (q.xyz x v)
        float tx = 2f * (q.Y * z - q.Z * y);
        float ty = 2f * (q.Z * x - q.X * z);
        float tz = 2f * (q.X * y - q.Y * x);
        rx = x + q.W * tx + (q.Y * tz - q.Z * ty);
        ry = y + q.W * ty + (q.Z * tx - q.X * tz);
        rz = z + q.W * tz + (q.X * ty - q.Y * tx);
    }
}
