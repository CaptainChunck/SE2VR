using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Render12.EngineComponents;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.PipelineStates;
using Keen.VRage.Render12.TransparentStage;
using SE2VR.Rendering;
using Vortice.Direct3D12;
using BlendState = Keen.VRage.Render12.Resources.PipelineStates.BlendState;

namespace SE2VR.Patches;

/// <summary>While an OpenXR session runs, the whole draw step is replaced by the stereo renderer.</summary>
[HarmonyPatch(typeof(Render12EngineComponent), "Draw")]
internal static class DrawPatch
{
    private static bool Prefix(Render12EngineComponent __instance, bool draw, ResizableRWRenderTargetTexture finalLDRBuffer)
    {
        bool vr;
        try
        {
            vr = VRSystem.PrepareFrame();
        }
        catch (Exception e)
        {
            Log.Error("VR frame preparation failed.", e);
            VRSystem.Disable();
            vr = false;
        }
        if (!vr)
        {
            return true;
        }
        StereoRenderer.Draw(__instance, draw, finalLDRBuffer);
        return false;
    }
}

/// <summary>
/// The headset images are copies of the game window's back buffer, so the window must keep the per-eye resolution.
/// Display-mode changes made in the options menu are ignored while VR is active (they are still saved for non-VR play).
/// </summary>
[HarmonyPatch(typeof(Render12EngineComponent), "OnDisplayOptionsPartUpdate")]
internal static class DisplayOptionsPatch
{
    private static bool Prefix()
    {
        if (!VRSystem.IsResolutionLocked || VRSystem.IsDisabled)
        {
            return true;
        }
        Log.Info("Ignored a display-mode change from the game options while VR is active (see LockResolution in SE2VR.json).");
        return false;
    }
}

/// <summary>
/// Text and vector graphics ("Slug" blend state) blend color normally but keep the destination's alpha. On the monitor
/// alpha is ignored, but the VR UI panel starts transparent, so text would never become opaque and the compositor
/// would add it on top of the world like a glow. Accumulating coverage in alpha (the standard "over" rule) fixes that
/// without changing the colors that end up on screen.
/// </summary>
[HarmonyPatch(typeof(BlendStateManager), MethodType.Constructor)]
internal static class SlugAlphaPatch
{
    private static void Postfix(BlendStateManager __instance)
    {
        if (VRSystem.Xr == null || VRSystem.IsDisabled)
        {
            return;
        }
        BlendDescription slug = __instance._blendDescriptions[BlendState.Slug];
        slug.RenderTarget[0].SourceBlendAlpha = Blend.One;
        slug.RenderTarget[0].DestinationBlendAlpha = Blend.InverseSourceAlpha;
        slug.RenderTarget[0].BlendOperationAlpha = BlendOperation.Add;
        __instance._blendDescriptions[BlendState.Slug] = slug;
        Log.Info("UI text and vector shapes now write coverage to alpha, for the transparent VR HUD panel.");
    }
}

/// <summary>Release the OpenXR images and session while the D3D12 device still exists.</summary>
[HarmonyPatch(typeof(Render12EngineComponent), "DisposeInternal")]
internal static class DisposePatch
{
    private static void Prefix() => VRSystem.ShutdownRenderer();
}

/// <summary>
/// GPU particles are emitted and simulated inside each scene draw. The second eye reuses the first eye's simulation,
/// otherwise particles would run at double speed.
/// </summary>
[HarmonyPatch]
internal static class ParticlePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ParticleEmitterProcessingJob), nameof(ParticleEmitterProcessingJob.DoWork));
        yield return AccessTools.Method(typeof(ParticleEmissionJob), nameof(ParticleEmissionJob.DoWork));
        yield return AccessTools.Method(typeof(ParticleSimulationJob), nameof(ParticleSimulationJob.DoWork));
        yield return AccessTools.Method(typeof(ParticleLightingJob), nameof(ParticleLightingJob.DoWork));
    }

    private static bool Prefix() => StereoRenderer.CurrentEye != 1;
}
