using System;
using System.Diagnostics;
using System.Text;

namespace SE2VR.Rendering;

/// <summary>
/// Diagnostics (environment variable SE2VR_CPU_PROFILE=1): render-thread time of each stage of a VR frame, averaged per
/// frame every 10 seconds. Stages are marked in order with <see cref="Mark"/>; the time since the previous mark is
/// charged to the named stage.
/// </summary>
internal static class CpuProfile
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SE2VR_CPU_PROFILE") == "1";

    private const int MaxStages = 32;
    private static readonly string[] _names = new string[MaxStages];
    private static readonly double[] _totals = new double[MaxStages];
    private static int _stageCount;
    // Sections inside a stage (timed by CpuProfilePassPatch); reported separately, they overlap the stages.
    private static readonly System.Collections.Generic.Dictionary<string, double> _inner = new();
    private static long _last;
    private static int _frames;
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static void BeginFrame()
    {
        if (Enabled)
        {
            _last = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Charges the time since the previous mark (or the frame start) to <paramref name="stage"/>.</summary>
    public static void Mark(string stage)
    {
        if (!Enabled || _last == 0)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        int index = Array.IndexOf(_names, stage, 0, _stageCount);
        if (index < 0 && _stageCount < MaxStages)
        {
            index = _stageCount++;
            _names[index] = stage;
        }
        if (index >= 0)
        {
            _totals[index] += (now - _last) * 1000.0 / Stopwatch.Frequency;
        }
        _last = now;
    }

    /// <summary>Adds the time of a section inside a stage (render thread only).</summary>
    public static void AddInner(string name, long startTimestamp)
    {
        double ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _inner[name] = (_inner.TryGetValue(name, out double total) ? total : 0) + ms;
    }

    public static void EndFrame()
    {
        if (!Enabled || _last == 0)
        {
            return;
        }
        _frames++;
        if (Stopwatch.GetElapsedTime(_windowStart).TotalSeconds < 10)
        {
            return;
        }
        var text = new StringBuilder($"CPU ms per frame ({_frames} frames):");
        double sum = 0;
        for (int i = 0; i < _stageCount; i++)
        {
            text.Append($" {_names[i]} {_totals[i] / _frames:0.00},");
            sum += _totals[i];
            _totals[i] = 0;
        }
        text.Append($" total {sum / _frames:0.00}");
        if (_inner.Count > 0)
        {
            text.Append(" | inside:");
            foreach ((string name, double total) in System.Linq.Enumerable.OrderByDescending(_inner, p => p.Value))
            {
                text.Append($" {name} {total / _frames:0.00},");
            }
            _inner.Clear();
        }
        Log.Info(text.ToString());
        _frames = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }
}
