using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.Systems;

namespace SE2VR.Rendering;

/// <summary>
/// Diagnostics (<see cref="CpuProfile.Enabled"/>): render-thread time of the scene draw and its main passes, per eye
/// ("#2" is the right eye). Passes the compiler inlined simply do not show up.
/// </summary>
[HarmonyPatch]
internal static class CpuProfilePassPatch
{
    private static readonly string[] Passes =
    {
        "Draw", "RenderShadowCascades", "RenderCharacterShadows", "RenderLocalLightShadows", "RenderEnvironmentProbe",
        "RenderMainView", "ProcessParticles", "RenderDecals", "ExecuteHBAO", "ComputeCloudShadows", "ExecuteWaterPrepass",
        "ForwardAndPostProcess", "ForwardAndPostPasses", "MainViewCulling", "RenderGBuffer", "PrepareDraw"
    };

    private static bool Prepare() => CpuProfile.Enabled;

    private static IEnumerable<MethodBase> TargetMethods() =>
        Passes.SelectMany(name => typeof(SceneDrawSystem).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                      .Where(m => m.Name == name && !m.IsGenericMethodDefinition && !m.IsAbstract))
              .Distinct();

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(MethodBase __originalMethod, long __state)
    {
        string eye = StereoRenderer.CurrentEye == 1 ? " #2" : "";
        CpuProfile.AddInner(__originalMethod.Name + eye, __state);
    }
}
