using System;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Options;
using Keen.VRage.Render12.Core;
using SE2VR.OpenXR;
using SE2VR.Rendering;

namespace SE2VR;

/// <summary>Owns the OpenXR runtime and decides, per frame, whether the VR renderer takes over.</summary>
internal static class VRSystem
{
    private static bool _sessionCreationAttempted;
    private static Hotkey? _recenterKey;
    private static Hotkey? _captureKey;
    private static int _frame;

    /// <summary>True once the game window was forced to the per-eye resolution; display-mode changes are then ignored.</summary>
    public static bool IsResolutionLocked { get; private set; }

    /// <summary>The game was told to use the per-eye window size (forced display options).</summary>
    public static bool IsDisplayForced { get; private set; }

    public static XrRuntime? Xr { get; private set; }

    /// <summary>Set when anything goes wrong; the game then renders normally to the monitor.</summary>
    public static bool IsDisabled { get; private set; }

    public static string PluginDirectory { get; set; } = "";

    public static void Disable()
    {
        if (!IsDisabled)
        {
            IsDisabled = true;
            Log.Warn("VR rendering disabled; the game continues on the monitor.");
        }
    }

    /// <summary>
    /// Runs before the engine is built: connects to the VR runtime and forces the game window to the per-eye
    /// resolution, so the game's final image can be copied straight into the headset's swapchains.
    /// </summary>
    public static void EarlyInit(EngineBuilder engine)
    {
        VRConfig config = VRConfig.Current;
        try
        {
            Xr = XrRuntime.Create(PluginDirectory, config.SystemWaitSeconds);
        }
        catch (Exception e)
        {
            Log.Error("Could not start OpenXR - is SteamVR running with the headset connected? Continuing without VR.", e);
            Xr?.Dispose();
            Xr = null;
            Disable();
            return;
        }

        if (!config.ForceWindowedResolution)
        {
            return;
        }
        XrViewConfigurationView left = Xr.ViewConfigs[0];
        XrViewConfigurationView right = Xr.ViewConfigs[1];
        int width = (int)MathF.Round(Math.Max(left.RecommendedImageRectWidth, right.RecommendedImageRectWidth) * config.RenderScale);
        int height = (int)MathF.Round(Math.Max(left.RecommendedImageRectHeight, right.RecommendedImageRectHeight) * config.RenderScale);
        width = Math.Clamp(width & ~3, 256, (int)Math.Min(left.MaxImageRectWidth, right.MaxImageRectWidth));
        height = Math.Clamp(height & ~3, 256, (int)Math.Min(left.MaxImageRectHeight, right.MaxImageRectHeight));

        ForcedDisplayOptionsConfigurationObjectBuilder display = engine.Configure<ForcedDisplayOptionsConfigurationObjectBuilder>();
        display.FullScreen = false;
        display.Resolution = new Vector2I(width, height);
        // The headset paces the frame rate (xrWaitFrame); waiting for the monitor's v-sync as well would halve it.
        display.VSync = VSyncMode.None;
        display.EnableStretching = true;
        IsDisplayForced = true;
        IsResolutionLocked = config.LockResolution;
        Log.Info($"Forcing a {width}x{height} window per eye (render scale {config.RenderScale:0.##}), v-sync off.");
    }

    /// <summary>
    /// Called at the start of every Render12EngineComponent.Draw on the render thread.
    /// Returns true when this frame should be rendered for the headset.
    /// </summary>
    public static bool PrepareFrame()
    {
        XrRuntime? xr = Xr;
        if (xr == null || IsDisabled)
        {
            return false;
        }
        if (!_sessionCreationAttempted)
        {
            _sessionCreationAttempted = true;
            if (!TryCreateSession(xr))
            {
                Disable();
                return false;
            }
            _recenterKey = Hotkey.Parse(VRConfig.Current.RecenterKey, "recenter");
            _captureKey = Hotkey.Parse(VRConfig.Current.CaptureKey, "capture");
            Log.Info($"Recenter key: {_recenterKey?.Text ?? "none"}, capture key: {_captureKey?.Text ?? "none"}.");
            if (xr.ForceRender)
            {
                Log.Warn("Test mode (SE2VR_FORCE_RENDER=1): rendering even when the headset is not displaying.");
            }
            if (xr.FakeHead)
            {
                Log.Warn("Test mode (SE2VR_FAKE_HEAD=1): using an ideal straight-ahead head pose instead of tracking.");
            }
        }
        xr.PollEvents();
        if (xr.IsLost)
        {
            Disable();
            return false;
        }
        PollShortcuts(xr);
        return xr.IsSessionRunning;
    }

    private static void PollShortcuts(XrRuntime xr)
    {
        if (_recenterKey?.WasPressed() == true)
        {
            Log.Info("Recenter requested.");
            xr.RequestRecenter(0);
        }
        bool capture = _captureKey?.WasPressed() == true;
        // A file named "capture" next to the plugin also triggers a capture; its text (optional) labels the files.
        // Used by the automated test scripts, and handy when the keyboard is out of reach.
        if (++_frame % 10 == 0)
        {
            string trigger = System.IO.Path.Combine(PluginDirectory, "capture");
            if (System.IO.File.Exists(trigger))
            {
                try
                {
                    FrameCapture.Label = System.IO.File.ReadAllText(trigger).Trim();
                    System.IO.File.Delete(trigger);
                    capture = true;
                }
                catch (System.IO.IOException)
                {
                    // Still being written; pick it up next time.
                }
            }
        }
        if (capture)
        {
            Log.Info("Capturing the next VR frame.");
            FrameCapture.Requested = true;
        }
    }

    private static bool TryCreateSession(XrRuntime xr)
    {
        try
        {
            long requiredLuid = xr.GetRequiredAdapterLuid(out int minFeatureLevel);
            var adapter = CoreSystems.DeviceWrap.DXGIAdapter.Description1;
            long gameLuid = ((long)adapter.Luid.HighPart << 32) | adapter.Luid.LowPart;
            Log.Info($"Game GPU: {adapter.Description} (LUID {gameLuid:X}); headset wants LUID {requiredLuid:X}, feature level 0x{minFeatureLevel:X}");
            if (requiredLuid != gameLuid)
            {
                Log.Error("The headset is connected to a different GPU than the one the game uses. Set the game to run on the headset's GPU.");
                return false;
            }
            xr.CreateSession(CoreSystems.DeviceWrap.D3DDevice.NativePointer, CoreSystems.DeviceContext.DirectQueue.D3DQueue.NativePointer);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Could not create the OpenXR session.", e);
            return false;
        }
    }

    /// <summary>Render thread, before the renderer tears down the device.</summary>
    public static void ShutdownRenderer()
    {
        if (Xr == null)
        {
            return;
        }
        try
        {
            CoreSystems.FrameDispatcher?.FlushAllQueuesAndWaitCpu();
            StereoRenderer.Shutdown();
        }
        catch (Exception e)
        {
            Log.Error("Error releasing VR render resources.", e);
        }
        Shutdown();
    }

    public static void Shutdown()
    {
        XrRuntime? xr = Xr;
        Xr = null;
        if (xr == null)
        {
            return;
        }
        try
        {
            xr.Dispose();
            Log.Info("OpenXR shut down.");
        }
        catch (Exception e)
        {
            Log.Error("Error shutting down OpenXR.", e);
        }
    }
}
