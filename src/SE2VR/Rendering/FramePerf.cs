using System;
using System.Diagnostics;
using Keen.VRage.Render12.Core;
using Vortice.DXGI;

namespace SE2VR.Rendering;

/// <summary>
/// Frame timing summary written to the log every few seconds: frame rate versus the headset's refresh, render-thread
/// time spent drawing (both eyes), time spent waiting for the headset in xrWaitFrame, and the game's video memory use
/// against what Windows lets it have. Running out of video memory makes the game stutter and can end in a GPU crash, so
/// that also gets a warning.
/// </summary>
internal static class FramePerf
{
    private const double ReportSeconds = 10.0;
    private const double WarnEverySeconds = 60.0;
    private const double GB = 1024.0 * 1024.0 * 1024.0;

    private static long _windowStart;
    private static long _lastFrame;
    private static int _frames;
    private static int _stereoFrames;
    private static double _drawMs, _maxDrawMs, _waitMs, _maxIntervalMs;
    private static long _memoryUsage, _memoryBudget;
    private static bool _memoryUnavailable;
    private static long _lastMemoryWarning;

    public static void Record(long drawTicks, long waitTicks, bool stereo, long displayPeriodNs)
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStart == 0)
        {
            _windowStart = _lastFrame = now;
            return;
        }
        double drawMs = drawTicks * 1000.0 / Stopwatch.Frequency;
        _frames++;
        _stereoFrames += stereo ? 1 : 0;
        _drawMs += drawMs;
        _maxDrawMs = Math.Max(_maxDrawMs, drawMs);
        _waitMs += waitTicks * 1000.0 / Stopwatch.Frequency;
        _maxIntervalMs = Math.Max(_maxIntervalMs, (now - _lastFrame) * 1000.0 / Stopwatch.Frequency);
        _lastFrame = now;
        if (_frames % 30 == 1)
        {
            SampleVideoMemory();
        }

        double seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds < ReportSeconds)
        {
            return;
        }
        double headsetHz = displayPeriodNs > 0 ? 1e9 / displayPeriodNs : 0;
        string memory = _memoryBudget > 0 ? $", video memory {_memoryUsage / GB:0.0} of {_memoryBudget / GB:0.0} GB" : "";
        string resolution = DynamicResolution.CurrentScale > 0
            ? $", GPU {DynamicResolution.LastGpuMs:0.0} ms of {DynamicResolution.TargetMs:0.0}, render {DynamicResolution.CurrentScale * 100:0}%"
            : "";
        Log.Info($"Perf: {_frames / seconds:0.0} fps (headset {headsetHz:0} Hz, {100.0 * _stereoFrames / _frames:0}% stereo), " +
                 $"draw {_drawMs / _frames:0.0} ms avg / {_maxDrawMs:0.0} ms max, wait {_waitMs / _frames:0.0} ms avg, " +
                 $"worst frame interval {_maxIntervalMs:0.0} ms{memory}{resolution}");
        if (_memoryBudget > 0 && _memoryUsage > _memoryBudget &&
            (_lastMemoryWarning == 0 || Stopwatch.GetElapsedTime(_lastMemoryWarning).TotalSeconds >= WarnEverySeconds))
        {
            _lastMemoryWarning = now;
            Log.Warn($"Video memory is full: the game uses {_memoryUsage / GB:0.0} GB but Windows gives it {_memoryBudget / GB:0.0} GB. " +
                     "Expect stutters, blurry textures or a GPU crash. Use a lower graphics preset (Low or Medium in VR), " +
                     "a lower RenderScale in SE2VR.json, or close other programs that use the graphics card.");
        }
        _windowStart = now;
        _frames = _stereoFrames = 0;
        _drawMs = _maxDrawMs = _waitMs = _maxIntervalMs = 0;
        _memoryUsage = 0;
    }

    /// <summary>Keeps the highest use seen in the current report window.</summary>
    private static void SampleVideoMemory()
    {
        if (_memoryUnavailable)
        {
            return;
        }
        try
        {
            QueryVideoMemoryInfo info = CoreSystems.DeviceWrap.DXGIAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            _memoryUsage = Math.Max(_memoryUsage, (long)info.CurrentUsage);
            _memoryBudget = (long)info.Budget;
        }
        catch (Exception e)
        {
            _memoryUnavailable = true;
            Log.Warn($"Video memory use is not available: {e.Message}");
        }
    }
}
