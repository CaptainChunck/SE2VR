using System;
using Keen.VRage.Library.Mathematics;
using SE2VR.OpenXR;

namespace SE2VR.Rendering;

/// <summary>
/// The canvas the game lays its UI out on while VR is active: the top-left part of the game window with the configured
/// aspect ratio (16:9 by default) instead of the whole, nearly square window.
/// </summary>
internal static class UiCanvas
{
    public static bool Enabled => VRSystem.Xr != null && !VRSystem.IsDisabled;

    /// <summary>
    /// UI canvas for a window/render resolution: the full width at the configured aspect ratio, never taller than the
    /// window. Keeping the width and the top-left origin keeps the game's mouse-to-UI mapping valid unchanged.
    /// </summary>
    public static Vector2I Fit(Vector2I resolution)
    {
        if (!Enabled || resolution.X <= 0 || resolution.Y <= 0)
        {
            return resolution;
        }
        int height = (int)MathF.Round(resolution.X / VRConfig.Current.UiAspectRatio);
        return height > 0 && height < resolution.Y ? new Vector2I(resolution.X, height) : resolution;
    }
}

/// <summary>
/// Where the UI panel floats in the render (recentered seated) space: a section of a cylinder around the head, or a flat
/// rectangle straight ahead, sized by the horizontal angle it covers.
/// </summary>
internal readonly struct UiPanel
{
    public bool Curved { get; }

    /// <summary>Distance from the head: the cylinder radius, or the distance to the flat panel.</summary>
    public float Distance { get; }

    /// <summary>Horizontal angle the panel covers, in radians.</summary>
    public float Angle { get; }

    /// <summary>Canvas width / height.</summary>
    public float Aspect { get; }

    /// <summary>Panel width in meters: the arc length when curved.</summary>
    public float Width => Curved ? Distance * Angle : 2f * Distance * MathF.Tan(Angle * 0.5f);

    public float Height => Width / Aspect;

    public UiPanel(bool curved, float distance, float angle, float aspect)
    {
        Curved = curved;
        Distance = distance;
        Angle = angle;
        Aspect = aspect;
    }

    public static UiPanel FromConfig(XrRuntime xr, int canvasWidth, int canvasHeight)
    {
        VRConfig config = VRConfig.Current;
        bool curved = config.UiCurved && xr.SupportsCylinder;
        // A flat panel cannot cover 180 degrees; a cylinder section is limited to a half circle here as well.
        float angle = Math.Min(config.UiWidthDegrees, curved ? 160f : 150f) * MathF.PI / 180f;
        return new UiPanel(curved, config.UiDistance, angle, canvasWidth / (float)Math.Max(1, canvasHeight));
    }

    /// <summary>The cylinder layer's own pose: its axis runs through the head position, the arc is centered on -Z.</summary>
    public XrPosef CenterPose => Curved
        ? XrPosef.Identity
        : new XrPosef { Orientation = XrQuaternionf.Identity, Position = new XrVector3f { Z = -Distance } };

    /// <summary>
    /// Pose of the panel surface at canvas coordinates (<paramref name="u"/>, <paramref name="v"/>) from the top-left
    /// corner (0..1), facing the viewer. Used to place the mouse pointer on the panel.
    /// </summary>
    public XrPosef SurfacePose(float u, float v)
    {
        float y = (0.5f - v) * Height;
        if (!Curved)
        {
            return new XrPosef
            {
                Orientation = XrQuaternionf.Identity,
                Position = new XrVector3f { X = (u - 0.5f) * Width, Y = y, Z = -Distance }
            };
        }
        // Angle 0 is straight ahead (-Z); positive angles are to the right (+X), as in the cylinder layer.
        float theta = (u - 0.5f) * Angle;
        return new XrPosef
        {
            // Turned by -theta around Y so the surface faces back toward the axis.
            Orientation = new XrQuaternionf { Y = MathF.Sin(-theta * 0.5f), W = MathF.Cos(-theta * 0.5f) },
            Position = new XrVector3f { X = Distance * MathF.Sin(theta), Y = y, Z = -Distance * MathF.Cos(theta) }
        };
    }
}
