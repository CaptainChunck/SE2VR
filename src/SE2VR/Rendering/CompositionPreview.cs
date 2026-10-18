using System;
using SE2VR.OpenXR;

namespace SE2VR.Rendering;

/// <summary>What was submitted to the compositor in one frame, in the render (recentered) space.</summary>
internal sealed class CompositionSnapshot
{
    public required XrView[] Views { get; init; }
    public required bool HasProjection { get; init; }
    public required bool HasQuad { get; init; }
    public required bool QuadBlended { get; init; }
    public required XrPosef QuadPose { get; init; }
    public required float QuadWidth { get; init; }
    public required float QuadHeight { get; init; }
}

/// <summary>
/// CPU re-creation of what the headset compositor shows each eye (without lens distortion): the eye image with the
/// UI quad layer ray-traced into it using the frame's real eye poses and fields of view. Used to check UI placement
/// and stereo alignment without wearing the headset.
/// </summary>
internal static class CompositionPreview
{
    public readonly struct Image
    {
        public required byte[] Pixels { get; init; } // RGBA, sRGB-encoded
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <param name="eye">Eye image, or null when no projection layer was submitted (menus): black background.</param>
    /// <param name="width">Output size when <paramref name="eye"/> is null.</param>
    public static Image Compose(CompositionSnapshot frame, int eyeIndex, Image? eye, Image? quad, int width, int height, out string stats)
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

        // The quad is axis-aligned in render space in this version (identity orientation, facing +Z).
        float planeZ = frame.QuadPose.Position.Z;
        float centerX = frame.QuadPose.Position.X, centerY = frame.QuadPose.Position.Y;
        int covered = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        bool cornersVisible = true;

        if (frame.HasQuad && quad != null)
        {
            Image ui = quad.Value;
            for (int py = 0; py < h; py++)
            {
                float ty = tanUp - (py + 0.5f) / h * (tanUp - tanDown);
                for (int px = 0; px < w; px++)
                {
                    float tx = tanLeft + (px + 0.5f) / w * (tanRight - tanLeft);
                    Rotate(q, tx, ty, -1f, out float dx, out float dy, out float dz);
                    if (dz > -1e-6f)
                    {
                        continue;
                    }
                    float t = (planeZ - origin.Z) / dz;
                    if (t <= 0f)
                    {
                        continue;
                    }
                    float u = (origin.X + t * dx - centerX) / frame.QuadWidth + 0.5f;
                    float v = 0.5f - (origin.Y + t * dy - centerY) / frame.QuadHeight;
                    if (u < 0f || u >= 1f || v < 0f || v >= 1f)
                    {
                        continue;
                    }
                    covered++;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);
                    int s = ((int)(v * ui.Height) * ui.Width + (int)(u * ui.Width)) * 4;
                    int d = (py * w + px) * 4;
                    if (frame.QuadBlended)
                    {
                        // Premultiplied "over", as the compositor does with BLEND_TEXTURE_SOURCE_ALPHA.
                        int inverse = 255 - ui.Pixels[s + 3];
                        output[d] = (byte)Math.Min(255, ui.Pixels[s] + output[d] * inverse / 255);
                        output[d + 1] = (byte)Math.Min(255, ui.Pixels[s + 1] + output[d + 1] * inverse / 255);
                        output[d + 2] = (byte)Math.Min(255, ui.Pixels[s + 2] + output[d + 2] * inverse / 255);
                    }
                    else
                    {
                        output[d] = ui.Pixels[s];
                        output[d + 1] = ui.Pixels[s + 1];
                        output[d + 2] = ui.Pixels[s + 2];
                    }
                }
            }

            // Are all four panel corners inside this eye's field of view?
            foreach ((float cx, float cy) in new[] { (-0.5f, 0.5f), (0.5f, 0.5f), (-0.5f, -0.5f), (0.5f, -0.5f) })
            {
                cornersVisible &= ProjectsInside(view, centerX + cx * frame.QuadWidth, centerY + cy * frame.QuadHeight, planeZ, tanLeft, tanRight, tanUp, tanDown);
            }
        }

        stats = !frame.HasQuad
            ? "no UI panel"
            : covered == 0
                ? "UI panel NOT visible in this eye"
                : $"UI panel covers {100.0 * covered / (w * h):0.#}% of the eye image, pixels x {minX}-{maxX}, y {minY}-{maxY}, " +
                  $"all corners {(cornersVisible ? "visible" : "NOT visible")}";
        return new Image { Pixels = output, Width = w, Height = h };
    }

    private static bool ProjectsInside(in XrView view, float x, float y, float z, float tanLeft, float tanRight, float tanUp, float tanDown)
    {
        // World point -> eye space: inverse-rotate the offset from the eye.
        XrQuaternionf q = view.Pose.Orientation;
        XrQuaternionf inverse = new() { X = -q.X, Y = -q.Y, Z = -q.Z, W = q.W };
        Rotate(inverse, x - view.Pose.Position.X, y - view.Pose.Position.Y, z - view.Pose.Position.Z, out float ex, out float ey, out float ez);
        if (ez >= 0f)
        {
            return false;
        }
        float tx = ex / -ez, ty = ey / -ez;
        return tx >= tanLeft && tx <= tanRight && ty >= tanDown && ty <= tanUp;
    }

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
