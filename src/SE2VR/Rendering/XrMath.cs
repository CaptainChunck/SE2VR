using System;
using Keen.VRage.Library.Mathematics;
using SE2VR.OpenXR;

namespace SE2VR.Rendering;

/// <summary>
/// Conversions between OpenXR and VRage math. Both are right-handed with +Y up and the camera looking down -Z.
/// VRage matrices use the row-vector convention (v' = v * M, translation in M41..M43), like XNA.
/// </summary>
internal static class XrMath
{
    /// <summary>Local-to-parent transform of a pose; the translation is multiplied by <paramref name="translationScale"/>.</summary>
    public static MatrixD ToMatrixD(in XrPosef pose, double translationScale)
    {
        double x = pose.Orientation.X, y = pose.Orientation.Y, z = pose.Orientation.Z, w = pose.Orientation.W;
        double length = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (length > 1e-9)
        {
            x /= length;
            y /= length;
            z /= length;
            w /= length;
        }
        else
        {
            x = y = z = 0;
            w = 1;
        }
        MatrixD m = default;
        m.M11 = 1 - 2 * (y * y + z * z);
        m.M12 = 2 * (x * y + z * w);
        m.M13 = 2 * (x * z - y * w);
        m.M21 = 2 * (x * y - z * w);
        m.M22 = 1 - 2 * (x * x + z * z);
        m.M23 = 2 * (y * z + x * w);
        m.M31 = 2 * (x * z + y * w);
        m.M32 = 2 * (y * z - x * w);
        m.M33 = 1 - 2 * (x * x + y * y);
        m.M41 = pose.Position.X * translationScale;
        m.M42 = pose.Position.Y * translationScale;
        m.M43 = pose.Position.Z * translationScale;
        m.M44 = 1;
        return m;
    }

    /// <summary>
    /// Asymmetric version of the engine's reversed-Z infinite projection (Matrix.CreatePerspectiveFovRhInfiniteComplementary):
    /// depth is 1 at the near plane and 0 at infinity.
    /// </summary>
    public static Matrix CreateProjection(in XrFovf fov, float nearPlane)
    {
        GetTangents(fov, out float left, out float right, out float up, out float down);
        Matrix m = default;
        m.M11 = 2f / (right - left);
        m.M22 = 2f / (up - down);
        m.M31 = (right + left) / (right - left);
        m.M32 = (up + down) / (up - down);
        m.M33 = 0f;
        m.M34 = -1f;
        m.M43 = nearPlane;
        m.M44 = 0f;
        return m;
    }

    /// <summary>
    /// Parameters for RenderView.SetCameraParameters that describe the same frustum: the engine feeds its FOV into the
    /// M22 term and the projection offset into M31/M32.
    /// </summary>
    public static void GetEngineCameraParameters(in XrFovf fov, out float engineFov, out Vector2 projectionOffset)
    {
        GetTangents(fov, out float left, out float right, out float up, out float down);
        engineFov = 2f * MathF.Atan((up - down) * 0.5f);
        projectionOffset = new Vector2((right + left) / (right - left), (up + down) / (up - down));
    }

    /// <summary>Width / height of the frustum at unit distance.</summary>
    public static float AspectRatio(in XrFovf fov)
    {
        GetTangents(fov, out float left, out float right, out float up, out float down);
        return (right - left) / (up - down);
    }

    private static void GetTangents(in XrFovf fov, out float left, out float right, out float up, out float down)
    {
        left = MathF.Tan(fov.AngleLeft);
        right = MathF.Tan(fov.AngleRight);
        up = MathF.Tan(fov.AngleUp);
        down = MathF.Tan(fov.AngleDown);
    }
}
