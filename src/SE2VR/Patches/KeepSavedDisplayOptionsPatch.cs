using HarmonyLib;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.EngineComponents;
using Keen.VRage.Render.Options;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.EngineComponents;

namespace SE2VR.Patches;

/// <summary>
/// When the renderer starts, the game copies forced display options - here the VR window size, windowed mode and
/// v-sync off - into the player's saved display settings, which would leave a small square window behind once the
/// player plays without VR. The saved values are put back at the end of the renderer's initialization, before the
/// options are written to disk (that happens on a later engine tick). The renderer keeps using the forced values,
/// which take precedence over the saved ones; display changes made in the options menu are still saved for non-VR
/// play (see <see cref="DisplayOptionsPatch"/>).
/// </summary>
[HarmonyPatch(typeof(Render12EngineComponent), "Init", new[] { typeof(RenderObjectBuilder) })]
internal static class KeepSavedDisplayOptionsPatch
{
    internal readonly record struct SavedDisplayOptions(bool FullScreen, Vector2I Windowed, Vector2I Fullscreen, VSyncMode VSync);

    private static void Prefix(Render12EngineComponent __instance, out SavedDisplayOptions? __state)
    {
        RenderDisplayOptionsPart? part = __instance._engineOptionsStorage?.TryGetPart<RenderDisplayOptionsPart>();
        __state = part == null ? null : new SavedDisplayOptions(part._fullScreen, part._windowedResolution, part._fullscreenResolution, part._vSyncMode);
    }

    private static void Postfix(Render12EngineComponent __instance, SavedDisplayOptions? __state)
    {
        if (!VRSystem.IsDisplayForced)
        {
            return;
        }
        RenderDisplayOptionsPart? part = __instance.MT?.DisplayOptionsPart;
        if (part == null)
        {
            return;
        }
        // A first start has no saved settings yet: put back the game's defaults for a new profile.
        SavedDisplayOptions saved = __state ?? Defaults(__instance, part);
        // Fields, not properties: no change notification, so nothing is re-applied to the renderer; the save that the
        // forced values queued writes these.
        part._fullScreen = saved.FullScreen;
        part._windowedResolution = saved.Windowed;
        part._fullscreenResolution = saved.Fullscreen;
        part._vSyncMode = saved.VSync;
        Vector2I size = saved.FullScreen ? saved.Fullscreen : saved.Windowed;
        Log.Info($"Kept the player's own display settings ({(saved.FullScreen ? "full screen" : "window")} {size.X}x{size.Y}, v-sync {saved.VSync}); " +
                 "the VR window size is not saved.");
    }

    private static SavedDisplayOptions Defaults(Render12EngineComponent engine, RenderDisplayOptionsPart part)
    {
        Vector2I desktop = CoreSystems.Adapters.GetAdapter(part.AdapterName).DesktopResolution;
        return new SavedDisplayOptions(engine._renderConfig.DefaultFullScreen, desktop, desktop, engine._renderConfig.DefaultVSyncMode);
    }
}
