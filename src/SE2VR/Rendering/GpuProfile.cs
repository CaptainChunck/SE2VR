using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Keen.VRage.Render12.Core.Device;
using Keen.VRage.Render12.Core.Profiling;

namespace SE2VR.Rendering;

/// <summary>
/// Diagnostics (environment variable SE2VR_GPU_PROFILE=1): logs the GPU time of every top-level block - one per
/// command list, named after it - averaged per frame every 10 seconds. The engine already measures these with
/// timestamp queries; this only reads its results. Both eyes create command lists with the same names, so the n-th
/// occurrence of a name within a frame is listed as "name #n".
/// </summary>
internal static class GpuProfile
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SE2VR_GPU_PROFILE") == "1";

    /// <summary>SE2VR_GPU_PROFILE_AB=1: flip <see cref="VRConfig.ShareWorkBetweenEyes"/> every window, for a same-session A/B comparison.</summary>
    private static readonly bool _alternateSharing = Environment.GetEnvironmentVariable("SE2VR_GPU_PROFILE_AB") == "1";

    private static readonly Dictionary<string, double> _totals = new();
    private static readonly Dictionary<string, int> _occurrences = new();
    private static double _commandListTotal;
    private static int _frames;
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static void Collect(List<GPUProfiler.Report> reports)
    {
        _occurrences.Clear();
        foreach (GPUProfiler.Report report in reports)
        {
            // Command lists (depth 0) and the passes timed by GpuProfilePassPatch.
            bool pass = report.Tag?.StartsWith(GpuProfilePassPatch.TagPrefix, StringComparison.Ordinal) == true;
            if ((report.Depth != 0 && !pass) || report.QueueType != CommandQueueType.Direct || string.IsNullOrEmpty(report.Tag))
            {
                continue;
            }
            int n = _occurrences.TryGetValue(report.Tag, out int seen) ? seen + 1 : 1;
            _occurrences[report.Tag] = n;
            string key = n == 1 ? report.Tag : $"{report.Tag} #{n}";
            _totals[key] = (_totals.TryGetValue(key, out double total) ? total : 0) + report.Duration.TotalMilliseconds;
            if (!pass)
            {
                _commandListTotal += report.Duration.TotalMilliseconds; // passes are inside command lists already
            }
        }
        _frames++;
        if (Stopwatch.GetElapsedTime(_windowStart).TotalSeconds < 10 || _frames == 0)
        {
            return;
        }
        string top = string.Join(", ", _totals.OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key} {p.Value / _frames:0.00}"));
        Log.Info($"GPU ms per frame ({_frames} frames, share={(VRConfig.Current.ShareWorkBetweenEyes ? "on" : "off")}, " +
                 $"all command lists {_commandListTotal / _frames:0.00}): {top}");
        _totals.Clear();
        _commandListTotal = 0;
        _frames = 0;
        _windowStart = Stopwatch.GetTimestamp();
        if (_alternateSharing)
        {
            VRConfig.Current.ShareWorkBetweenEyes = !VRConfig.Current.ShareWorkBetweenEyes;
        }
    }
}

/// <summary>
/// Adds GPU timestamps around the individual passes of a scene draw, which the engine only times as one command list.
/// Diagnostics only (<see cref="GpuProfile.Enabled"/>); passes the compiler inlined simply do not show up.
/// </summary>
[HarmonyPatch]
internal static class GpuProfilePassPatch
{
    public const string TagPrefix = "pass:";

    private static readonly string[] Passes =
    {
        "RenderShadowCascades", "RenderCharacterShadows", "RenderLocalLightShadows", "RenderEnvironmentProbe", "RenderMainView",
        "ProcessParticles", "RenderDecals", "ExecuteHBAO", "ComputeCloudShadows", "ExecuteWaterPrepass"
    };

    private static bool Prepare() => GpuProfile.Enabled;

    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        Passes.Select(name => AccessTools.Method(typeof(Keen.VRage.Render12.Core.Systems.SceneDrawSystem), name))
              .Where(method => method != null)!;

    // Another prefix may skip the pass (and this prefix with it), while postfixes always run: only close what was opened,
    // as an unmatched end query breaks the engine's profiler.
    private static void Prefix(Keen.VRage.Render12.Core.CommandLists.DirectCommandList commandList, System.Reflection.MethodBase __originalMethod, out bool __state)
    {
        Keen.VRage.Render12.Core.CoreSystems.GPUProfiler.AddQuery(commandList._d3dCommandList, CommandQueueType.Direct, TagPrefix + __originalMethod.Name, GPUProfiler.QueryType.Begin);
        __state = true;
    }

    private static void Postfix(Keen.VRage.Render12.Core.CommandLists.DirectCommandList commandList, bool __state)
    {
        if (__state)
        {
            Keen.VRage.Render12.Core.CoreSystems.GPUProfiler.AddQuery(commandList._d3dCommandList, CommandQueueType.Direct, string.Empty, GPUProfiler.QueryType.End);
        }
    }
}

/// <summary>Hands the engine's per-frame GPU timing results to <see cref="GpuProfile"/>.</summary>
[HarmonyPatch(typeof(GPUProfiler.Resolver), nameof(GPUProfiler.Resolver.ReportResults))]
internal static class GpuProfilePatch
{
    private static bool Prepare() => GpuProfile.Enabled;

    private static void Postfix(GPUProfiler.Resolver __instance, List<GPUProfiler.Report> reports)
    {
        if (__instance._queueType == CommandQueueType.Direct)
        {
            GpuProfile.Collect(reports);
        }
    }
}
