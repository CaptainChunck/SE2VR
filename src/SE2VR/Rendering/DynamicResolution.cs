using System;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.Profiling;

namespace SE2VR.Rendering;

/// <summary>
/// Keeps the frame rate locked to the headset by adjusting the resolution FSR upscales from, a little at a time.
/// <para>
/// A headset shows a new picture every refresh (e.g. every 11.1 ms at 90 Hz). A frame that misses that deadline is
/// shown late, and once the game misses often SteamVR halves the rate (45 fps, with reprojection filling in) or worse.
/// Half rate is fine when it is steady; dropping from 45 to 30 is what judders. The frame's GPU time is measured
/// by the engine itself; when it gets near the budget (90% of one refresh, or of two at half rate) the render
/// resolution goes down, and when there is room again it goes back up to the graphics preset's own resolution. FSR
/// is made for a changing input size, so this shows as a slightly softer picture rather than a stutter.
/// </para>
/// The budget is the headset's full refresh when the GPU can make it at nearly the preset's resolution, otherwise two
/// refreshes. Render thread only.
/// </summary>
internal static class DynamicResolution
{
    private const int AdjustEveryFrames = 15;
    private const int WarmUpFrames = 450;
    private const float Step = 1f / 64f;

    private static int _stereoFrames;
    private static int _fullRateEvidence;

    private static GPUProfiler.Watch? _gpuWatch;
    private static double _gpuMsSum;
    private static int _samples;
    private static float _scale = -1f;
    private static double _nativePeriodMs = double.MaxValue;
    private static bool _fullRate;
    private static double _lastGpuMs;
    private static string _lastLogged = "";

    /// <summary>Fraction of the eye image size the scene is rendered at this frame (before FSR), or 0 when not active.</summary>
    public static float CurrentScale => _scale > 0 ? _scale : 0;

    public static double LastGpuMs => _lastGpuMs;

    public static double TargetMs => _nativePeriodMs == double.MaxValue ? 0 : 0.9 * _nativePeriodMs * (_fullRate ? 1 : 2);

    /// <summary>
    /// Call at the start of every stereo frame, before anything asks for the render resolution. Returns the scale to
    /// use, or a negative value to leave the engine's own choice alone.
    /// </summary>
    public static float Update(long displayPeriodNs, bool stereo)
    {
        VRConfig config = VRConfig.Current;
        float presetScale = CoreSystems.Settings.DRS.ScaleFactor; // the graphics preset's FSR mode (1 = native)
        if (!config.DynamicResolution || presetScale <= 0)
        {
            _scale = -1f;
            return -1f;
        }
        float min = Math.Min(presetScale, Math.Clamp(config.DynamicResolutionMin, 0.3f, 1f));
        if (_scale < 0)
        {
            _scale = presetScale;
        }
        _scale = Math.Clamp(_scale, min, presetScale);

        // SteamVR reports the period it currently runs the game at: a multiple of the headset's refresh while it
        // throttles (45 Hz on a 90 Hz headset). Headsets refresh at 72 Hz or more, so take the smallest multiple
        // that gets there, and the fastest rate seen.
        if (displayPeriodNs > 0)
        {
            double reportedHz = 1e9 / displayPeriodNs;
            int multiple = 1;
            while (reportedHz * multiple < 71 && multiple < 4)
            {
                multiple++;
            }
            _nativePeriodMs = Math.Min(_nativePeriodMs, 1000.0 / (reportedHz * multiple));
        }
        // Menus and loading screens cost next to nothing and say nothing about the world; measure stereo frames only,
        // starting a little after the world appears.
        if (!stereo)
        {
            _stereoFrames = 0;
            _gpuMsSum = 0;
            _samples = 0;
            return _scale;
        }
        if (++_stereoFrames < WarmUpFrames)
        {
            return _scale;
        }
        _gpuWatch ??= CoreSystems.GPUProfiler.GetBlockWatch(GPUProfilerWatchType.WorkTime);
        double gpuMs = _gpuWatch.Value.TotalMilliseconds;
        if (gpuMs > 0)
        {
            _gpuMsSum += gpuMs;
            _samples++;
        }
        if (_samples < AdjustEveryFrames || _nativePeriodMs == double.MaxValue)
        {
            return _scale;
        }
        double average = _gpuMsSum / _samples;
        _gpuMsSum = 0;
        _samples = 0;
        _lastGpuMs = average;

        // Full rate only when it can be had at (nearly) the preset's own resolution; a much blurrier picture is a
        // worse trade than steady half rate with reprojection.
        double atPresetScale = average * (presetScale / _scale) * (presetScale / _scale);
        switch (config.DynamicResolutionTarget)
        {
            case "Full":
                _fullRate = true;
                break;
            case "Half":
                _fullRate = false;
                break;
            default:
                // Full rate after a few seconds of clear headroom at the preset's resolution; back to half rate as
                // soon as holding full rate would need a noticeably blurrier picture.
                _fullRateEvidence = atPresetScale < 0.75 * _nativePeriodMs ? _fullRateEvidence + 1 : 0;
                if (!_fullRate && _fullRateEvidence >= 30)
                {
                    _fullRate = true;
                }
                else if (_fullRate && average > TargetMs * 1.05 && _scale < presetScale * 0.9f + 0.001f)
                {
                    _fullRate = false;
                    _fullRateEvidence = 0;
                }
                break;
        }
        double target = TargetMs;

        // The pixel count goes with the square of the scale; only part of the frame does, so move in small steps
        // (faster down than up) and leave it alone near the target.
        double ratio = target / average;
        float next = _scale;
        if (ratio < 0.97)
        {
            next = _scale * (float)Math.Max(Math.Sqrt(ratio), 0.9);
        }
        else if (ratio > 1.08)
        {
            next = _scale * (float)Math.Min(Math.Sqrt(ratio), 1.03);
        }
        next = MathF.Round(next / Step) * Step;
        _scale = Math.Clamp(next, min, presetScale);

        string state = $"{_scale * 100:0}% at {(_fullRate ? "full" : "half")} rate";
        if (state != _lastLogged && Log.Verbose)
        {
            Log.Info($"Dynamic resolution: {state} (GPU {average:0.0} ms, budget {target:0.0} ms).");
        }
        _lastLogged = state;
        return _scale;
    }

    public static void Reset()
    {
        _scale = -1f;
        _gpuMsSum = 0;
        _samples = 0;
        _stereoFrames = 0;
        _fullRateEvidence = 0;
        _fullRate = false;
    }
}
