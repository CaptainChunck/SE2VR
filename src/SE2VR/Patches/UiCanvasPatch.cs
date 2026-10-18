using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.UI.EngineComponents;
using SE2VR.Rendering;

namespace SE2VR.Patches;

/// <summary>
/// The game sizes its UI layout from the window resolution (a design canvas of at least 1920x1080 "design pixels").
/// The VR window is almost square, like the eye images, which would make the canvas 1920x1920: menus and HUD spread
/// over a tall panel with the toolbar and status far above and below the line of sight. In VR the UI is told the
/// screen is 16:9 instead - the top part of the window - so everything keeps the layout it was designed for and the
/// panel can be made wide enough to read. Mouse input needs no change: the canvas starts at the window's top-left
/// corner and keeps its pixel scale.
/// <para>
/// The UI's display information keeps its own copy of the display settings. It is created during
/// <c>AvaloniaApp.Initialize</c> (from <c>UIEngineComponent.RenderDisplaySettings</c>) and updated through
/// <c>UIEngineComponent.OnDisplaySettingsChanged</c>. The game's assemblies are precompiled (ReadyToRun), so the small
/// methods in between may be inlined; these two entry points are large or called through a delegate and can be patched
/// reliably.
/// </para>
/// </summary>
internal static class UiCanvasPatch
{
    private static Vector2I _lastLogged;

    /// <summary>The display information is created from the settings field during initialization: lend it the canvas size.</summary>
    [HarmonyPatch]
    internal static class Initialize
    {
        // The static Initialize(AppLoop, RenderContracts, UIEngineComponent, InputEngineComponent), not Avalonia's
        // instance Initialize().
        private static MethodBase TargetMethod() =>
            typeof(UIEngineComponent).Assembly.GetType("Keen.VRage.UI.AvaloniaInterface.AvaloniaApp", throwOnError: true)!
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Initialize" && m.GetParameters().Any(p => p.ParameterType == typeof(UIEngineComponent)))
            ?? throw new MissingMethodException("AvaloniaApp", "Initialize(..., UIEngineComponent, ...)");

        private static void Prefix(UIEngineComponent engineUI, out Vector2I __state)
        {
            __state = engineUI.RenderDisplaySettings.Resolution;
            engineUI.RenderDisplaySettings.Resolution = Fit(__state);
        }

        // Restores the real resolution, which the rest of the UI (world-to-screen mapping) expects; runs even on exceptions.
        private static void Finalizer(UIEngineComponent engineUI, Vector2I __state)
        {
            engineUI.RenderDisplaySettings.Resolution = __state;
        }
    }

    /// <summary>Later display changes: the UI keeps the real settings, its layout gets the canvas.</summary>
    [HarmonyPatch(typeof(UIEngineComponent), nameof(UIEngineComponent.OnDisplaySettingsChanged))]
    internal static class DisplaySettingsChanged
    {
        private static bool Prefix(UIEngineComponent __instance, RenderDisplaySettings settings)
        {
            if (!UiCanvas.Enabled)
            {
                return true;
            }
            __instance.RenderDisplaySettings = settings;
            RenderDisplaySettings canvas = settings;
            canvas.Resolution = Fit(settings.Resolution);
            __instance.DisplayInformation?.UpdateDisplaySettings(canvas);
            return false;
        }
    }

    private static Vector2I Fit(Vector2I resolution)
    {
        Vector2I canvas = UiCanvas.Fit(resolution);
        if (canvas != resolution && canvas != _lastLogged)
        {
            _lastLogged = canvas;
            Log.Info($"UI canvas for VR: {canvas.X}x{canvas.Y} (window {resolution.X}x{resolution.Y}).");
        }
        return canvas;
    }
}
