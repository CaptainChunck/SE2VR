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

    /// <summary>
    /// SE2VR_GPU_PROFILE_AB flips one optimization every window, for a same-session A/B comparison: "1" or "share"
    /// (<see cref="VRConfig.ShareWorkBetweenEyes"/>) or "foveation".
    /// </summary>
    private static readonly string _abSetting = (Environment.GetEnvironmentVariable("SE2VR_GPU_PROFILE_AB") ?? "").Trim().ToLowerInvariant();
    private static bool _abOn = true;
    private static string? _abFoveationLevel;

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
            // Command lists (depth 0), the engine's own blocks inside them (depth 1, shown as "name/"), and the passes
            // timed by GpuProfilePassPatch.
            bool pass = report.Tag?.StartsWith(GpuProfilePassPatch.TagPrefix, StringComparison.Ordinal) == true;
            if ((report.Depth > 1 && !pass) || report.QueueType != CommandQueueType.Direct || string.IsNullOrEmpty(report.Tag))
            {
                continue;
            }
            string tag = report.Depth == 1 && !pass ? report.Tag + "/" : report.Tag;
            int n = _occurrences.TryGetValue(tag, out int seen) ? seen + 1 : 1;
            _occurrences[tag] = n;
            string key = n == 1 ? tag : $"{tag} #{n}";
            _totals[key] = (_totals.TryGetValue(key, out double total) ? total : 0) + report.Duration.TotalMilliseconds;
            if (report.Depth == 0 && !pass)
            {
                _commandListTotal += report.Duration.TotalMilliseconds; // blocks and passes are inside command lists already
            }
        }
        _frames++;
        if (Stopwatch.GetElapsedTime(_windowStart).TotalSeconds < 10 || _frames == 0)
        {
            return;
        }
        string top = string.Join(", ", _totals.OrderByDescending(p => p.Value).Take(48).Select(p => $"{p.Key} {p.Value / _frames:0.00}"));
        VRConfig config = VRConfig.Current;
        string ab = _abSetting is "" or "0" ? "" : $", A/B {_abSetting} {(_abOn ? "ON" : "OFF")}";
        Log.Info($"GPU ms per frame ({_frames} frames, share={(config.ShareWorkBetweenEyes ? "on" : "off")}, foveation={config.Foveation}{ab}, " +
                 $"all command lists {_commandListTotal / _frames:0.00}): {top}");
        _totals.Clear();
        _commandListTotal = 0;
        _frames = 0;
        _windowStart = Stopwatch.GetTimestamp();
        if (ab.Length > 0)
        {
            FlipAB(config);
        }
    }

    private static void FlipAB(VRConfig config)
    {
        _abOn = !_abOn;
        _abFoveationLevel ??= config.Foveation == "Off" ? "Balanced" : config.Foveation;
        switch (_abSetting)
        {
            case "1":
            case "share":
                config.ShareWorkBetweenEyes = _abOn;
                break;
            case "foveation":
                config.Foveation = _abOn ? _abFoveationLevel : "Off";
                break;
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

    // None of these runs inside another one, so the queries never nest.
    private static readonly string[] Passes =
    {
        "RenderShadowCascades", "RenderCharacterShadows", "RenderLocalLightShadows", "RenderEnvironmentProbe", "RenderMainView",
        "ProcessParticles", "RenderDecals", "ExecuteHBAO", "ComputeCloudShadows", "ExecuteWaterPrepass",
        // Forward passes
        "DrawUnlit", "ComputeSSR", "ExecuteVolumetricPasses", "RenderTransparent", "DrawWater", "ResolveStochasticTransparency",
        "RenderHighlightsAndTransparentUnlit", "RenderHolograms", "ResolveOIT", "RenderFlares",
        // Post passes
        "PatchHoles", "ComputeExposure", "UpscaleTargetFSR", "ApplyBloom", "ApplyToneMapping", "ApplyNonFSRUpscalingAndAA"
    };

    private static bool Prepare() => GpuProfile.Enabled;

    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        Passes.Select(name => AccessTools.Method(typeof(Keen.VRage.Render12.Core.Systems.SceneDrawSystem), name))
              .Where(method => method != null)!;

    // Another prefix may skip the pass (and this prefix with it), while postfixes always run: only close what was opened,
    // as an unmatched end query breaks the engine's profiler. A pass without a command list (it makes its own) is skipped.
    private static void Prefix(object[] __args, System.Reflection.MethodBase __originalMethod, out Keen.VRage.Render12.Core.CommandLists.DirectCommandList? __state)
    {
        __state = __args.OfType<Keen.VRage.Render12.Core.CommandLists.DirectCommandList>().FirstOrDefault();
        if (__state != null)
        {
            Keen.VRage.Render12.Core.CoreSystems.GPUProfiler.AddQuery(__state._d3dCommandList, CommandQueueType.Direct, TagPrefix + __originalMethod.Name, GPUProfiler.QueryType.Begin);
        }
    }

    private static void Postfix(Keen.VRage.Render12.Core.CommandLists.DirectCommandList? __state)
    {
        if (__state != null)
        {
            Keen.VRage.Render12.Core.CoreSystems.GPUProfiler.AddQuery(__state._d3dCommandList, CommandQueueType.Direct, string.Empty, GPUProfiler.QueryType.End);
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
