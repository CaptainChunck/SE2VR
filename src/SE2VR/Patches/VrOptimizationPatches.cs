using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.Resources.Views;
using SE2VR.Rendering;
using Vortice.Direct3D12;

namespace SE2VR.Patches;

/// <summary>
/// Coarse shading at the edges of the view (<see cref="Foveation"/>) for the full-screen lighting passes - sun light
/// and its shadow filtering, local lights, ambient light, atmosphere - and for transparents and particles. Each of
/// these runs only for the main view; the G-buffer itself is left at full rate (it mostly writes triangle IDs here,
/// which must stay exact).
/// </summary>
[HarmonyPatch]
internal static class FoveationPatch
{
    private static readonly string[] Passes = { "ComputeDirectionalLighting", "ComputeLocalLights", "ComputeGI", "ApplyAtmosphere", "RenderTransparent" };

    private static IEnumerable<MethodBase> TargetMethods() =>
        Passes.Select(name => AccessTools.Method(typeof(SceneDrawSystem), name)).Where(method => method != null)!;

    private static void Prefix(object[] __args, out DirectCommandList? __state)
    {
        DirectCommandList? commandList = __args.OfType<DirectCommandList>().FirstOrDefault();
        __state = commandList != null && Foveation.Begin(commandList) ? commandList : null;
    }

    private static void Postfix(DirectCommandList? __state)
    {
        if (__state != null)
        {
            Foveation.End(__state);
        }
    }
}

/// <summary>
/// The right eye uses the exposure the left eye's eye adaptation just computed. Run per eye, the adaptation advanced
/// twice per frame and left the two eyes a step apart in brightness; it also saves a histogram pass and a readback.
/// </summary>
[HarmonyPatch(typeof(SceneDrawSystem), "ComputeExposure")]
internal static class SharedExposurePatch
{
    private static bool Prefix(SceneDrawSystem __instance, ref ITexture2DView exposure, ref Keen.VRage.Render12.Resources.BindableTextures.Borrowed<Keen.VRage.Render12.Resources.BindableTextures.RenderTargetTexture>? debugHistogram)
    {
        if (StereoRenderer.CurrentEye != 1 || !VRConfig.Current.ShareWorkBetweenEyes)
        {
            return true;
        }
        exposure = __instance._eyeAdaptationJob.Exposure;
        debugHistogram = null;
        return false;
    }
}
