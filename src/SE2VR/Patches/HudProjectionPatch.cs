using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.CameraSystems;
using Keen.VRage.Library.Mathematics;
using SE2VR.OpenXR;
using SE2VR.Rendering;

namespace SE2VR.Patches;

/// <summary>
/// GPS, antenna and other world markers are placed on screen by the game camera's projection: its field of view over
/// the window resolution. In VR the UI is a 16:9 canvas (see <see cref="UiCanvasPatch"/>) floating as a panel that
/// covers a known angle of view, and a flat panel at a fixed spot in front of the seated head is exactly the image plane
/// of a perspective projection. With the camera's marker projection set to the panel's frustum, every marker lands on
/// the panel where its object appears in the headset. The 3D view itself is unaffected: the game passes its field of
/// view to the renderer separately, and the eyes use the headset's own projections anyway.
/// <para>
/// The projection is applied after the camera update, which is where the game builds the view-projection used for
/// markers. Both methods are patched in case one is inlined into the other (the game is ReadyToRun-compiled); the work
/// is idempotent.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class HudProjectionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CameraComponent), "UpdateRenderSettingsInternal")
                     ?? throw new MissingMethodException(nameof(CameraComponent), "UpdateRenderSettingsInternal");
        yield return AccessTools.Method(typeof(CameraComponent), "Update")
                     ?? throw new MissingMethodException(nameof(CameraComponent), "Update");
    }

    private static void Postfix(CameraComponent __instance)
    {
        XrRuntime? xr = VRSystem.Xr;
        if (xr == null || !UiCanvas.Enabled)
        {
            return;
        }
        // The camera takes its resolution from the saved display settings, which keep the player's non-VR values
        // (see KeepSavedDisplayOptionsPatch); markers must use the canvas the UI is actually drawn on.
        Vector2I canvas = UiCanvas.Current;
        if (canvas.X <= 0 || canvas.Y <= 0)
        {
            return;
        }
        if (__instance._resolution != canvas)
        {
            __instance._resolution = canvas;
            __instance.AspectRatio = canvas.X / (float)canvas.Y;
        }
        Matrix projection = PanelProjection(xr, canvas, __instance.NearPlane);
        Matrix current = __instance.ProjectionMatrix;
        if (current == projection)
        {
            return;
        }
        // The view-projection was just built with the game's projection; swap the projection part and keep the view
        // (camera shake included).
        __instance.ProjectionMatrix = projection;
        __instance.ViewProjectionMatrix = __instance.ViewProjectionMatrix * Matrix.Invert(current) * projection;
    }

    /// <summary>Perspective projection whose frustum is the UI panel as seen from the seated head position.</summary>
    private static Matrix PanelProjection(XrRuntime xr, Vector2I canvas, float nearPlane)
    {
        UiPanel panel = UiPanel.FromConfig(xr, canvas.X, canvas.Y);
        // Flat: the panel's half height over its distance. Curved: exact at the center line, angles are linear across.
        float halfTanY = panel.Curved ? panel.Angle / (2f * panel.Aspect) : MathF.Tan(panel.Angle * 0.5f) / panel.Aspect;
        return Matrix.CreatePerspectiveFovRhInfiniteComplementary(2f * MathF.Atan(halfTanY), panel.Aspect, nearPlane);
    }
}
