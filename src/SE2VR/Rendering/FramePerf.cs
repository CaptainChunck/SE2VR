using System;
using System.Diagnostics;

namespace SE2VR.Rendering;

/// <summary>
/// Frame timing summary written to the log every few seconds: frame rate versus the headset's refresh, render-thread
/// time spent drawing (both eyes), and time spent waiting for the headset in xrWaitFrame.
/// </summary>
internal static class FramePerf
{
    private const double ReportSeconds = 10.0;

    private static long _windowStart;
    private static long _lastFrame;
    private static int _frames;
    private static int _stereoFrames;
    private static double _drawMs, _maxDrawMs, _waitMs, _maxIntervalMs;

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

        double seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds < ReportSeconds)
        {
            return;
        }
        double headsetHz = displayPeriodNs > 0 ? 1e9 / displayPeriodNs : 0;
        Log.Info($"Perf: {_frames / seconds:0.0} fps (headset {headsetHz:0} Hz, {100.0 * _stereoFrames / _frames:0}% stereo), " +
                 $"draw {_drawMs / _frames:0.0} ms avg / {_maxDrawMs:0.0} ms max, wait {_waitMs / _frames:0.0} ms avg, " +
                 $"worst frame interval {_maxIntervalMs:0.0} ms");
        _windowStart = now;
        _frames = _stereoFrames = 0;
        _drawMs = _maxDrawMs = _waitMs = _maxIntervalMs = 0;
    }
}
