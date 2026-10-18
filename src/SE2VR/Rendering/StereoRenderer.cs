using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render.Data;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Core.Systems;
using Keen.VRage.Render12.EngineComponents;
using Keen.VRage.Render12.Primitives;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.SceneSystem.Components;
using Keen.VRage.Render12.Utils;
using SE2VR.OpenXR;
using SE2VR.UI;
using Vortice.Direct3D12;
using Box = Vortice.Mathematics.Box;
using Color4 = Vortice.Mathematics.Color4;
using Format = Vortice.DXGI.Format;
using RawRect = Vortice.RawRect;
using Color = Vortice.Mathematics.Color;

namespace SE2VR.Rendering;

/// <summary>
/// Replacement for <c>Render12EngineComponent.Draw</c> while an OpenXR session is running.
/// <para>
/// It performs the same per-frame work as the original, except that the scene is drawn once per eye - each with its
/// own camera, projection and temporal history - and each result is copied into that eye's OpenXR image. The game UI
/// is rendered once into a separate image and shown as a floating panel (quad layer) instead of being burned into
/// both eyes.
/// </para>
/// Everything here runs on the render thread.
/// </summary>
internal static unsafe class StereoRenderer
{
    /// <summary>Eye currently being drawn by SceneDrawSystem (0 = left, 1 = right), or -1 outside the stereo passes.</summary>
    public static int CurrentEye { get; private set; } = -1;

    private static readonly RenderView[] _eyeViews = new RenderView[2];
    private static readonly bool[] _eyeHasHistory = new bool[2];

    /// <summary>Frame counter of the stereo renderer and the last frame both eyes were drawn (for temporal history).</summary>
    private static long _frame;
    private static long _lastStereoFrame = -10;

    private static readonly XrImageSwapchain?[] _eyeSwapchains = new XrImageSwapchain?[2];
    private static XrImageSwapchain? _uiSwapchain;

    // HUD parts that follow your head (see HudLayout): the image the parts show (this frame's UI during gameplay, the
    // kept picture in the Modify UI editor) and the menu screen without those parts. Created on first use.
    private static XrImageSwapchain? _hudSwapchain;
    private static XrImageSwapchain? _uiMainSwapchain;
    private static bool _piecesLive;
    private static XrCompositionLayerQuad* _pieceLayers;
    private static readonly bool _testHudPieces = Environment.GetEnvironmentVariable("SE2VR_TEST_HUD_PIECES") == "1";

    // Modify UI editor: pictures of the HUD from gameplay (the editor is opened from a menu, where the game draws no
    // HUD) - the one shown and the newest - and the frames drawn behind the parts, one cell per part (see LayoutFrames).
    private static BackBuffer? _hudPicture, _hudPending;
    private static bool _hudHasPicture, _hudHasPending;
    private static long _hudPendingTime;
    private static XrImageSwapchain? _hudFramesSwapchain;
    private static readonly RawRect[] _frameCells = new RawRect[HudLayout.Pieces.Length];
    private static XrCompositionLayerQuad* _frameLayers;
    private static bool _editorFrames, _editorPicture, _editorFailed;
    private static readonly BackBuffer?[] _hudCopyTargets = new BackBuffer?[3];

    private static XrCompositionLayerProjection* _projectionLayer;
    private static XrCompositionLayerProjectionView* _projectionViews;
    private static XrCompositionLayerQuad* _quadLayer; // UI panel when flat
    private static XrCompositionLayerCylinderKHR* _cylinderLayer; // UI panel when curved
    private static XrCompositionLayerQuad* _cursorLayer;
    private static void** _layers;
    private static UiPanel _panel;

    /// <summary>The layers submitted this frame, for the simulated headset views of a capture.</summary>
    private static readonly List<PreviewLayer> _previewLayers = new();

    private static bool _loggedStereo;
    private static bool _loggedMenu;
    private static bool _loggedFsr;
    private static bool _loggedRaytracing;
    private static bool _loggedPost;
    private static bool _loggedFlares;
    private static int _framesWithoutWorld = 1_000_000;

    private static readonly bool _testMono = Environment.GetEnvironmentVariable("SE2VR_TEST_MONO") == "1";

    // Diagnostics: SE2VR_ENGINE_FOV=<degrees> overrides the field of view the engine's heuristics see.
    private static readonly float _engineFovOverride =
        float.TryParse(Environment.GetEnvironmentVariable("SE2VR_ENGINE_FOV"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees)
            ? degrees * MathF.PI / 180f
            : 0f;

    public static void Draw(Render12EngineComponent engine, bool draw, ResizableRWRenderTargetTexture finalLDRBuffer)
    {
        XrRuntime xr = VRSystem.Xr!;
        engine.RT.LastFrameStartTimeGPU = CoreSystems.GPUProfiler.LastFrameStartTime;
        try
        {
            DrawInternal(xr, draw, finalLDRBuffer);
            CoreSystems.UpdateStats();
        }
        catch (Exception)
        {
            CoreSystems.DeviceWrap.ProcessDebugOutput();
            throw;
        }
        finally
        {
            // Never leave the runtime waiting on an unfinished frame or an acquired image.
            ReleaseImages();
            xr.EndFrame(null, 0);
        }
        engine.RT.RenderState = Render12EngineComponent.State.Postdraw;
    }

    private static void DrawInternal(XrRuntime xr, bool draw, ResizableRWRenderTargetTexture finalLDRBuffer)
    {
        long frameStart = Stopwatch.GetTimestamp();
        CpuProfile.BeginFrame();
        _frame++;
        VRConfig config = VRConfig.Current;
        SettingsManager settings = CoreSystems.Settings;
        // Before anything asks for the render resolution (sampler LOD bias, screen buffers, FSR jitter).
        float renderScale = DynamicResolution.Update(xr.FrameState.PredictedDisplayPeriod, stereo: _lastStereoFrame == _frame - 1);
        if (renderScale > 0)
        {
            CoreSystems.GPUFrameManager.SwapChainResolutionMult = renderScale;
        }
        if (!config.FsrInVR && settings._drs.AAMode == AAMode.FSR)
        {
            settings._drs.AAMode = AAMode.FXAA;
            if (!_loggedFsr)
            {
                _loggedFsr = true;
                Log.Info("FSR is off in VR (FsrInVR in SE2VR.json); using FXAA while the headset is active.");
            }
        }
        ApplyVrPostEffects(config, settings);
        if (!config.RaytracingInVR && settings._raytracing.Enabled)
        {
            // Per eye it costs about as much again as the rest of the frame, and its buffers would be needed twice.
            RaytracingSettings raytracing = settings._raytracing;
            raytracing.Enabled = false;
            settings.SetRaytracingSettings(in raytracing);
            if (!_loggedRaytracing)
            {
                _loggedRaytracing = true;
                Log.Info("Ray tracing is off while the headset is active (RaytracingInVR in SE2VR.json); the game's own setting is unchanged.");
            }
        }

        // ---- Unchanged engine work (mirrors Render12EngineComponent.Draw) ----
        CoreSystems.ParallelBatchManager.ReplayBatches();
        CoreSystems.Scene.Tick();
        if (!CoreSystems.IsRecompilationSucceeded)
        {
            return;
        }
        CoreSystems.SwapChain.Update();
        CoreSystems.FinalizeResources();
        CoreSystems.ManagedTextureStreaming.FlushCopyList();
        CoreSystems.DescriptorHeap.UpdateImmediateHeap();
        CoreSystems.DrawContexts.WaterContextLifetimeHelper.UpdateWaterContext();
        CoreSystems.Samplers.OnBeginFrame(Render12EngineComponent.GetSamplerLODBias());
        using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("SceneUpdate"))
        {
            CoreSystems.ShaderAsserts.CheckErrors(commandList);
            CoreSystems.SparseUpdateData.ApplyDataBufferResize(commandList);
            CoreSystems.SparseUpdateJob.PrintTracking();
            foreach (SparseUpdateData data in CoreSystems.SparseUpdateData.UpdatedData)
            {
                CoreSystems.SparseUpdateJob.DoWork(commandList, data);
            }
            CoreSystems.ResourceUploadSynchronizationManager.Upload(commandList);
            if (settings.IsRaytracingSupportedAndEnabled)
            {
                CoreSystems.Materials.RefreshTables();
                settings.OnBeginDraw();
                CoreSystems.CommonResources.OnBeginDraw();
                using CopyCommandList.BlockScope block = commandList.BeginBlock("Impostor baking");
                CoreSystems.ImpostorBakingManager.BakePendingImpostors(commandList);
                CoreSystems.CommonResources.OnEndDraw();
                settings.OnEndDraw();
                if (!CoreSystems.IRCacheResources.ContainerIsInitialized)
                {
                    block.BeginNextBlock("IRCacheResourceCreation");
                    CoreSystems.IRCacheResources.CreateAndInitializeContainer(commandList);
                }
            }
            Vector2I maxResolution = new Vector2I(
                (int)(CoreSystems.SwapChain.Resolution.X * settings.DRS.ScaleFactor),
                (int)(CoreSystems.SwapChain.Resolution.Y * settings.DRS.ScaleFactor));
            CoreSystems.ScreenBuffers.Update(commandList, in maxResolution, Render12EngineComponent.GetPreUpscaleResolution());
        }
        if (CoreSystems.CrashGPUJob.Enabled)
        {
            using ComputeCommandList crashList = CoreSystems.FrameDispatcher.CreateComputeCommandList("Crash");
            CoreSystems.CrashGPUJob.DoWork(crashList);
        }
        if (CoreSystems.SceneDrawSystem != null && settings.IsRaytracingSupportedAndEnabled && !CoreSystems.SceneDrawSystem.IsRtxInitialized.IsSet)
        {
            CoreSystems.SceneDrawSystem.InitRTXJobs();
        }

        // ---- OpenXR frame: blocks until the headset wants the next frame, then samples the head pose ----
        bool sceneDraw = draw && CoreSystems.SceneDrawSystem != null && CoreSystems.SceneDrawSystem.ShouldDraw();
        if (!sceneDraw)
        {
            _framesWithoutWorld = Math.Min(_framesWithoutWorld + 1, 1_000_000);
        }
        else
        {
            if (_framesWithoutWorld > 30)
            {
                // Entering a world: recenter so the game's "forward" is where the player is looking right now.
                xr.RequestRecenter(0);
            }
            _framesWithoutWorld = 0;
        }
        CpuProfile.Mark("engine");
        long waitStart = Stopwatch.GetTimestamp();
        bool frameBegun = xr.BeginFrame();
        long waitTicks = Stopwatch.GetTimestamp() - waitStart;
        CpuProfile.Mark("xrwait");
        bool shouldRender = frameBegun && xr.ShouldRender;
        bool stereo = shouldRender && sceneDraw && xr.ViewsValid;
        FrameCapture.BeginFrame();
        bool menuPanel = shouldRender && !sceneDraw;
        if (stereo || menuPanel)
        {
            if (!EnsureSwapchains(xr) || !AcquireImages(stereo))
            {
                stereo = menuPanel = false;
            }
            else
            {
                VrCursor.CheckWindowPlacement(new Vector2I(_eyeSwapchains[0]!.Width, _eyeSwapchains[0]!.Height),
                                              new Vector2I(_uiSwapchain!.Width, _uiSwapchain.Height));
            }
        }

        uint layerCount = 0;
        _previewLayers.Clear();
        _piecesLive = _editorFrames = _editorPicture = false;
        CpuProfile.Mark("acquire");
        if (sceneDraw)
        {
            if (stereo)
            {
                bool uiWanted = settings.Overrides.TopMostPass && settings.Overrides.UI;
                DrawStereoScene();
                bool uiDrawn = uiWanted && DrawUiLayer();
                bool pieces = uiDrawn && PrepareHudPieces(xr);
                CpuProfile.Mark("ui");
                layerCount = BuildStereoLayers(xr, uiDrawn, pieces);
                if (!_loggedStereo)
                {
                    _loggedStereo = true;
                    Log.Info($"First stereo frame submitted ({_eyeSwapchains[0]!.Width}x{_eyeSwapchains[0]!.Height} per eye).");
                    for (int eye = 0; eye < 2; eye++)
                    {
                        XrFovf fov = xr.Views[eye].Fov;
                        XrVector3f p = xr.Views[eye].Pose.Position;
                        const float toDegrees = 180f / MathF.PI;
                        Log.Info($"  Eye {eye}: FOV left {fov.AngleLeft * toDegrees:0.0}, right {fov.AngleRight * toDegrees:0.0}, up {fov.AngleUp * toDegrees:0.0}, " +
                                 $"down {fov.AngleDown * toDegrees:0.0} deg (aspect {XrMath.AspectRatio(fov):0.00}); position ({p.X:0.000}, {p.Y:0.000}, {p.Z:0.000}) m");
                    }
                }
            }
            else
            {
                DrawMonoScene();
            }
        }
        else
        {
            DrawUiOnly(finalLDRBuffer);
            // The UI canvas is the top-left part of the final image (see UiCanvas).
            if (menuPanel && CopyToImage(_uiSwapchain!, finalLDRBuffer, "VR.MenuPanel", "menu"))
            {
                layerCount = BuildMenuLayer(xr, withPieces: ModifyUiEditor.IsOpen && PrepareEditor(xr));
                if (!_loggedMenu)
                {
                    _loggedMenu = true;
                    Log.Info("Showing the menu on a floating panel.");
                }
            }
        }

        // ---- Unchanged engine work: screenshots and the desktop window (mirrors the last eye drawn) ----
        CoreSystems.ScreenshotsManager.TakeRequestedScreenshots(CoreSystems.ScreenBuffers.FinalLDRTexture, withoutUi: false);
        CoreSystems.FrameHistoryRecorder?.CaptureIfDue(CoreSystems.ScreenBuffers.FinalLDRTexture);
        if (RenderUtilities.ShouldPresentQueueBeEnabled())
        {
            CoreSystems.FrameDispatcher.StartPresentQueue();
        }
        using (DirectCommandList copyList = RenderUtilities.ShouldPresentQueueBeEnabled()
                   ? CoreSystems.FrameDispatcher.CreatePresentCommandList("CopyJob")
                   : CoreSystems.FrameDispatcher.CreateDirectCommandList("CopyJob"))
        {
            using (copyList.BeginBlock("CopyToRt"))
            {
                copyList.CopyResource(CoreSystems.SwapChain.GetCurrentBackBuffer(), CoreSystems.ScreenBuffers.FinalLDRTexture);
                copyList.ExplicitStateTransition(CoreSystems.SwapChain.GetCurrentBackBuffer().AutoResourceState, ResourceStates.Common);
            }
        }
        if (CoreSystems.FrameDispatcher.IsPresentQueueBranched)
        {
            CoreSystems.FrameDispatcher.JoinPresentQueue();
        }
        CpuProfile.Mark("mirror");
        CoreSystems.Samplers.OnEndFrame();
        CoreSystems.FrameDispatcher.FlushQueues();
        CoreSystems.LoadingMonitor.OnEndDraw();
        CpuProfile.Mark("submit");

        // Everything that writes the OpenXR images has now been submitted to the direct queue.
        ReleaseImages();
        xr.EndFrame(_layers, layerCount);
        CpuProfile.Mark("xrend");
        CpuProfile.EndFrame();
        FramePerf.Record(Stopwatch.GetTimestamp() - frameStart - waitTicks, waitTicks, stereo, xr.FrameState.PredictedDisplayPeriod);
        if (FrameCapture.Active)
        {
            var snapshot = new CompositionSnapshot
            {
                Views = (XrView[])xr.Views.Clone(),
                HasProjection = stereo,
                Layers = _previewLayers.ToArray()
            };
            FrameCapture.EndFrame(VRSystem.PluginDirectory, snapshot);
        }
    }

    /// <summary>
    /// Keeps the VR versions of a few post-processing settings (see <see cref="VRConfig.VrPostEffects"/> and
    /// <see cref="VRConfig.LensFlaresInVR"/>) through the engine's own setter, so its listeners hear about the change.
    /// The game can send new settings at any time (options, world changes); they are corrected the next frame.
    /// </summary>
    private static void ApplyVrPostEffects(VRConfig config, SettingsManager settings)
    {
        if (config.VrPostEffects)
        {
            PostProcessSettings post = settings._postProcess;
            if (post.BloomDirtRatio != 0f || post.HighQualityBloom || post.BloomAntiFlickerFilter)
            {
                post.BloomDirtRatio = 0f;
                post.HighQualityBloom = false;
                post.BloomAntiFlickerFilter = false;
                settings.SetPostProcessSettings(in post);
                if (!_loggedPost)
                {
                    _loggedPost = true;
                    Log.Info("VR post-processing: lens dirt off, bloom at half resolution without its anti-flicker pass (VrPostEffects in SE2VR.json).");
                }
            }
        }
        if (!config.LensFlaresInVR && settings._light.Flares)
        {
            LightSettings light = settings._light;
            light.Flares = false;
            settings.SetLightSettings(in light);
            if (!_loggedFlares)
            {
                _loggedFlares = true;
                Log.Info("Lens flares are off in VR (LensFlaresInVR in SE2VR.json).");
            }
        }
    }

    /// <summary>Original single-view path, used when the headset does not want a frame (e.g. dashboard open).</summary>
    private static void DrawMonoScene()
    {
        CoreSystems.Settings.OnBeginDraw();
        CoreSystems.CommonResources.OnBeginDraw();
        CoreSystems.DrawContexts.OnBeginDraw();
        RenderPendingOffscreenUi();
        CoreSystems.SceneDrawSystem!.Draw(CoreSystems.ScreenBuffers.GetCurrentFrameRenderTarget());
        CoreSystems.DrawContexts.OnEndDraw();
        CoreSystems.CommonResources.OnEndDraw();
        CoreSystems.Settings.OnEndDraw();
    }

    private static void DrawStereoScene()
    {
        SettingsManager settings = CoreSystems.Settings;
        ref RenderView liveView = ref settings._renderView;
        RenderView center = liveView;
        RenderView centerPrevious = settings._previousRenderView;
        OverridesSettings overrides = settings._overrides;
        settings._overrides.UI = false; // The UI goes to its own layer, see DrawUiLayer.
        // Each eye reprojects its own last frame (see EyeHistory). Not across a camera cut (respawn, teleport) or a gap
        // in stereo rendering (menus, headset off).
        bool continuous = center.LastUpdateWasSmooth && _lastStereoFrame == _frame - 1;
        _lastStereoFrame = _frame;
        bool rightEyeHistory = false;
        try
        {
            for (int eye = 0; eye < 2; eye++)
            {
                RenderView previous = _eyeViews[eye];
                bool hadHistory = _eyeHasHistory[eye] && continuous;
                BuildEyeView(eye, in center, hadHistory);
                liveView = _eyeViews[eye];
                settings._previousRenderView = hadHistory ? previous : _eyeViews[eye];
                CurrentEye = eye;
                XrFovf eyeFov = VRSystem.Xr!.Views[eye].Fov;
                Vector2I renderSize = CoreSystems.ScreenBuffers.PreUpscaleResolution;
                Foveation.PrepareEye(VRSystem.Xr!, eye, renderSize, in eyeFov);
                if (eye == 0)
                {
                    EyeHistory.BeginLeftEye();
                }
                else
                {
                    rightEyeHistory = true;
                    EyeHistory.BeginRightEye();
                }

                settings.OnBeginDraw();
                CoreSystems.CommonResources.OnBeginDraw();
                CoreSystems.DrawContexts.OnBeginDraw();
                if (eye == 0)
                {
                    RenderPendingOffscreenUi();
                }
                ResizableRWRenderTargetTexture target = CoreSystems.ScreenBuffers.GetCurrentFrameRenderTarget();
                CoreSystems.SceneDrawSystem!.Draw(target);
                CoreSystems.DrawContexts.OnEndDraw();
                CoreSystems.CommonResources.OnEndDraw();
                settings.OnEndDraw();

                _eyeViews[eye] = liveView; // keep what the draw updated (jitter state)
                CopyToImage(_eyeSwapchains[eye]!, target, eye == 0 ? "VR.LeftEye" : "VR.RightEye", eye == 0 ? "left" : "right");
                CpuProfile.Mark(eye == 0 ? "left" : "right");
            }
        }
        finally
        {
            if (rightEyeHistory)
            {
                EyeHistory.EndRightEye();
            }
            CurrentEye = -1;
            settings._overrides = overrides;
            liveView = center;
            settings._previousRenderView = centerPrevious;
        }

        // Per-eye equivalent of SettingsManager.OnFrameEndDisposal: each eye keeps its own speed and smoothing state.
        for (int eye = 0; eye < 2; eye++)
        {
            _eyeViews[eye].OnFrameEnd();
        }
    }

    /// <summary>
    /// The eye camera is the game camera with the tracked eye pose (relative to the seated origin) applied on top, so
    /// mouse/gamepad still steer the body and the headset adds free head movement. Without <paramref name="continuous"/>
    /// history (first frame after menus, camera cut) the view is marked as a cut, which makes FSR start over.
    /// </summary>
    private static void BuildEyeView(int eye, in RenderView center, bool continuous)
    {
        if (_testMono)
        {
            // Diagnostics (SE2VR_TEST_MONO=1): both eyes get the game's own camera and projection, untouched.
            _eyeViews[eye] = center;
            _eyeHasHistory[eye] = true;
            return;
        }
        XrView xrView = VRSystem.Xr!.Views[eye];
        MatrixD eyeLocal = XrMath.ToMatrixD(xrView.Pose, VRConfig.Current.WorldScale);
        MatrixD eyeWorld = eyeLocal * center.InvViewD;

        ref RenderView view = ref _eyeViews[eye];
        RenderView history = view;
        bool hasHistory = _eyeHasHistory[eye];

        // Start from the game camera so resolution, clip planes, first-person mode and environment roots follow it,
        // then restore this eye's own temporal bookkeeping.
        view = center;
        view._cameraSpeedBuffer = hasHistory ? history._cameraSpeedBuffer : new Queue<double>();
        view.LastFrameCameraPosition = hasHistory ? history.LastFrameCameraPosition : eyeWorld.Translation;
        view.LastFrameCameraPositionWithCuts = hasHistory ? history.LastFrameCameraPositionWithCuts : eyeWorld.Translation;
        view.JitterCounter = hasHistory ? history.JitterCounter : 0;
        view.MaxBufferedCameraSpeed = hasHistory ? history.MaxBufferedCameraSpeed : 0.0;

        XrMath.GetEngineCameraParameters(xrView.Fov, out float engineFov, out Vector2 projectionOffset);
        if (_engineFovOverride > 0f)
        {
            engineFov = _engineFovOverride; // diagnostics only (SE2VR_ENGINE_FOV)
        }
        view.SetCameraParameters(in eyeWorld, engineFov, center.NearClipping, center.FarClipping, center.VeryFarClipping, projectionOffset, smooth: continuous);

        // SetCameraParameters derives the aspect ratio from the render resolution; replace the projection with the
        // exact per-eye frustum so it matches what the compositor expects even if the resolution aspect differs.
        Matrix projection = XrMath.CreateProjection(xrView.Fov, view.NearClipping);
        MatrixD viewAtOrigin = view.ViewD;
        viewAtOrigin.M14 = viewAtOrigin.M24 = viewAtOrigin.M34 = 0;
        viewAtOrigin.M41 = viewAtOrigin.M42 = viewAtOrigin.M43 = 0;
        viewAtOrigin.M44 = 1;
        MatrixD viewD = view.ViewD;
        view.Projection = RenderView.CreateProjectionMatrices(in projection, in viewAtOrigin, in viewD);

        _eyeHasHistory[eye] = true;
    }

    private static void RenderPendingOffscreenUi()
    {
        if (!CoreSystems.OffscreenTarget.HasPendingRenderRequests)
        {
            return;
        }
        using DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("OffscreenUIRenderer");
        CoreSystems.OffscreenUIRenderer.DoWork(commandList);
        CoreSystems.Scene.SceneSystems.Get<UISystemComponent>().DisposeRenderedOffscreenBatches();
    }

    /// <summary>Renders the game UI (HUD, menus, cursor) on transparent black into the panel image.</summary>
    private static bool DrawUiLayer()
    {
        BackBuffer? target = _uiSwapchain?.Current;
        if (target == null)
        {
            return false;
        }
        CoreSystems.Settings.OnBeginDraw();
        CoreSystems.CommonResources.OnBeginDraw();
        using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.UI"))
        {
            commandList.ClearRenderTargetView(target, new Color(0f, 0f, 0f, 0f));
            CoreSystems.MainUISystem.DoWork(commandList, target, target.Resolution);
            commandList.ExplicitStateTransition(target.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
            FrameCapture.Record(commandList, target, "ui");
        }
        CoreSystems.CommonResources.OnEndDraw();
        CoreSystems.Settings.OnEndDraw();
        return true;
    }

    /// <summary>Original no-scene path (main menu, loading screens): clears the final image and draws the UI into it.</summary>
    private static void DrawUiOnly(ResizableRWRenderTargetTexture finalLDRBuffer)
    {
        if (CoreSystems.FrameDispatcher.IsPresentQueueBranched)
        {
            CoreSystems.FrameDispatcher.JoinPresentQueue();
        }
        CoreSystems.Settings.OnBeginDraw();
        CoreSystems.CommonResources.OnBeginDraw();
        CoreSystems.SceneDrawSystem?.ExecuteAccelerationStructuresBuilding();
        using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("ClearFinalLDRTexture"))
        {
            using CopyCommandList.BlockScope block = commandList.BeginBlock("ClearRTV");
            commandList.ClearRenderTargetView(finalLDRBuffer);
            block.BeginNextBlock("UpdateVideoPlayer");
            foreach (VideoPlayerEntityComponent player in CoreSystems.Scene.GetEntitiesOfType<VideoPlayerEntityComponent>())
            {
                commandList.UpdateVideoPlayerTexture(player);
            }
            block.BeginNextBlock("UI");
            if (CoreSystems.Settings.Overrides.TopMostPass && CoreSystems.Settings.Overrides.UI)
            {
                // Viewport = the UI canvas, so the game's safe-area scaling (if any) stays centered on the canvas.
                CoreSystems.MainUISystem.DoWork(commandList, finalLDRBuffer, UiCanvas.Fit(finalLDRBuffer.Resolution));
            }
            block.BeginNextBlock("EndDraw");
            CoreSystems.ShaderAsserts.PrepareReadback(commandList);
        }
        CoreSystems.CommonResources.OnEndDraw();
        CoreSystems.Settings.OnEndDraw();
    }

    /// <summary>Copies the source image, or its top-left part when the OpenXR image is smaller, into the acquired image.</summary>
    private static bool CopyToImage(XrImageSwapchain swapchain, ResizableRWRenderTargetTexture source, string name, string captureName)
    {
        BackBuffer? target = swapchain.Current;
        if (target == null)
        {
            return false;
        }
        // While the game switches resolution the engine can hand out a texture of the previous size for one frame.
        var sourceDescription = source.D3DResourceDescription;
        int sourceWidth = (int)sourceDescription.Width, sourceHeight = (int)sourceDescription.Height;
        if (sourceWidth != swapchain.Width || sourceHeight < swapchain.Height)
        {
            return false;
        }
        using DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList(name);
        if (sourceHeight == swapchain.Height)
        {
            commandList.CopyResource(target, source);
        }
        else
        {
            // Same barriers as CopyResource, then a region copy.
            commandList.OnAction();
            commandList.RequestResourceState(target.AutoResourceState, ResourceStates.CopyDest, discardResource: false);
            commandList.RequestResourceState(source.AutoResourceState, ResourceStates.CopySource, discardResource: true);
            commandList.ResolveResourceStates();
            commandList._d3dCommandList.CopyTextureRegion(
                new TextureCopyLocation(target.D3DResource, 0), 0, 0, 0,
                new TextureCopyLocation(source.D3DResource, 0),
                new Box(0, 0, 0, swapchain.Width, swapchain.Height, 1));
        }
        commandList.ExplicitStateTransition(target.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
        FrameCapture.Record(commandList, target, captureName);
        return true;
    }

    /// <summary>(Re)creates the OpenXR images so they match the game's final image exactly (CopyResource needs that).</summary>
    private static bool EnsureSwapchains(XrRuntime xr)
    {
        var description = CoreSystems.ScreenBuffers.FinalLDRTexture.D3DResourceDescription;
        int width = (int)description.Width;
        int height = (int)description.Height;
        if (_eyeSwapchains[0] is { } current && current.Width == width && current.Height == height)
        {
            return true;
        }
        try
        {
            if (_eyeSwapchains[0] != null)
            {
                Log.Info($"Game resolution changed to {width}x{height}; recreating OpenXR swapchains.");
                // Older images may still be referenced by in-flight GPU work.
                CoreSystems.FrameDispatcher.FlushAllQueuesAndWaitCpu();
            }
            DisposeSwapchains();
            if (!xr.SupportsSwapchainFormat(XrConst.DxgiR8G8B8A8UnormSrgb))
            {
                Log.Error("The OpenXR runtime does not offer R8G8B8A8_UNORM_SRGB swapchains; VR disabled.");
                VRSystem.Disable();
                return false;
            }
            _eyeSwapchains[0] = new XrImageSwapchain(xr, "LeftEye", width, height);
            _eyeSwapchains[1] = new XrImageSwapchain(xr, "RightEye", width, height);
            Vector2I canvas = UiCanvas.Fit(new Vector2I(width, height));
            _uiSwapchain = new XrImageSwapchain(xr, "UI", canvas.X, canvas.Y);
            EnsureLayerMemory();
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Could not create OpenXR swapchains; VR disabled.", e);
            DisposeSwapchains();
            VRSystem.Disable();
            return false;
        }
    }

    private static bool AcquireImages(bool stereo)
    {
        if (!_uiSwapchain!.Acquire())
        {
            return false;
        }
        if (stereo && (!_eyeSwapchains[0]!.Acquire() || !_eyeSwapchains[1]!.Acquire()))
        {
            ReleaseImages();
            return false;
        }
        return true;
    }

    private static void ReleaseImages()
    {
        _eyeSwapchains[0]?.Release();
        _eyeSwapchains[1]?.Release();
        _uiSwapchain?.Release();
        _hudSwapchain?.Release();
        _uiMainSwapchain?.Release();
        _hudFramesSwapchain?.Release();
        VrCursor.ReleaseImage();
    }

    /// <summary>
    /// Seconds between two pictures of the HUD for the Modify UI editor. The editor shows the one before the newest,
    /// which is at least this old when play stops: the game hides the HUD a moment before a menu and the mouse pointer
    /// show, so the newest picture can be of an empty HUD.
    /// </summary>
    private const double HudPictureSeconds = 1.0;

    /// <summary>
    /// HUD parts that follow your head. During gameplay (the game hides the mouse pointer) this frame's UI is copied
    /// into the parts' image and into the menu screen's image with those parts cut out. With a menu open the parts go
    /// back on the menu screen, except while Modify UI is open (see <see cref="PrepareEditor"/>). Every second of
    /// gameplay a picture of the HUD is kept as well, because the editor is opened from a menu, where there is no HUD.
    /// Returns whether parts are shown this frame.
    /// </summary>
    private static bool PrepareHudPieces(XrRuntime xr)
    {
        bool gameplay = _testHudPieces || !VrCursor.IsPointerShowing();
        if (!gameplay)
        {
            return ModifyUiEditor.IsOpen && PrepareEditor(xr);
        }
        bool following = HudLayout.AnyFollowsHead();
        bool keepPicture = Stopwatch.GetTimestamp() - _hudPendingTime >= (long)(HudPictureSeconds * Stopwatch.Frequency);
        if (!following && !keepPicture)
        {
            return false;
        }
        XrImageSwapchain ui = _uiSwapchain!;
        int targets = 0;
        if (following)
        {
            EnsureSwapchain(xr, ref _hudSwapchain, "HUD", ui.Width, ui.Height);
            EnsureSwapchain(xr, ref _uiMainSwapchain, "UIMain", ui.Width, ui.Height);
            if (!_hudSwapchain!.Acquire() || !_uiMainSwapchain!.Acquire())
            {
                return false;
            }
            _hudCopyTargets[targets++] = _hudSwapchain.Current!;
            _hudCopyTargets[targets++] = _uiMainSwapchain.Current!;
        }
        if (keepPicture)
        {
            EnsurePictures(ui.Width, ui.Height);
            if (_hudHasPending)
            {
                // The newest picture is now old enough to be shown; this frame's goes into the other texture.
                (_hudPicture, _hudPending) = (_hudPending, _hudPicture);
                _hudHasPicture = true;
            }
            _hudCopyTargets[targets++] = _hudPending!;
        }
        BackBuffer source = ui.Current!;
        using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.HudParts"))
        {
            // Same sequence as the engine's copy helpers (which take OpenXR images only as a destination).
            commandList.OnAction();
            commandList.ExplicitStateTransition(source.AutoResourceState, ResourceStates.CopySource, discardResource: false);
            for (int i = 0; i < targets; i++)
            {
                commandList.ExplicitStateTransition(_hudCopyTargets[i]!.AutoResourceState, ResourceStates.CopyDest, discardResource: true);
            }
            commandList.ResolveResourceStates();
            for (int i = 0; i < targets; i++)
            {
                commandList._d3dCommandList.CopyResource(_hudCopyTargets[i]!.D3DResource, source.D3DResource);
            }
            commandList.ExplicitStateTransition(source.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
            for (int i = 0; i < targets; i++)
            {
                commandList.ExplicitStateTransition(_hudCopyTargets[i]!.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
            }
            commandList.ResolveResourceStates();
            if (following)
            {
                var cutouts = new List<RawRect>();
                foreach (HudLayout.Piece piece in HudLayout.Pieces)
                {
                    if (HudLayout.SettingsFor(piece).FollowHead)
                    {
                        cutouts.Add(new RawRect((int)(piece.X0 * ui.Width), (int)(piece.Y0 * ui.Height), (int)MathF.Ceiling(piece.X1 * ui.Width), (int)MathF.Ceiling(piece.Y1 * ui.Height)));
                    }
                }
                BackBuffer main = _uiMainSwapchain!.Current!;
                commandList._d3dCommandList.ClearRenderTargetView(main.D3DRTVCpuDescriptorHandle, new Color4(0f, 0f, 0f, 0f), cutouts.ToArray());
                FrameCapture.Record(commandList, _hudSwapchain!.Current!, "hud");
                FrameCapture.Record(commandList, main, "uimain");
            }
        }
        Array.Clear(_hudCopyTargets);
        if (keepPicture)
        {
            _hudHasPending = true;
            _hudPendingTime = Stopwatch.GetTimestamp();
        }
        _piecesLive = following;
        return following;
    }

    /// <summary>
    /// Modify UI is open (from a menu): every part that follows your head is shown in a frame, the part being placed
    /// highlighted, with the HUD's last picture from gameplay (frames only until a world has been played), so every
    /// change can be seen while the settings stay open. Returns whether the parts are shown.
    /// </summary>
    private static bool PrepareEditor(XrRuntime xr)
    {
        if (_editorFailed)
        {
            return false;
        }
        try
        {
            XrImageSwapchain ui = _uiSwapchain!;
            if (_hudFramesSwapchain == null)
            {
                LayoutFrames(ui.Width, ui.Height, out int width, out int height);
                _hudFramesSwapchain = new XrImageSwapchain(xr, "HudFrames", width, height);
            }
            if (!_hudFramesSwapchain.Acquire())
            {
                return false;
            }
            bool picture = false;
            if (_hudHasPicture && _hudPicture != null && _hudPicture.Resolution == new Vector2I(ui.Width, ui.Height))
            {
                EnsureSwapchain(xr, ref _hudSwapchain, "HUD", ui.Width, ui.Height);
                picture = _hudSwapchain!.Acquire();
            }
            BackBuffer frames = _hudFramesSwapchain.Current!;
            using (DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.HudEditor"))
            {
                commandList.OnAction();
                commandList.ExplicitStateTransition(frames.AutoResourceState, ResourceStates.RenderTarget, discardResource: true);
                if (picture)
                {
                    BackBuffer hud = _hudSwapchain!.Current!;
                    commandList.ExplicitStateTransition(_hudPicture!.AutoResourceState, ResourceStates.CopySource, discardResource: false);
                    commandList.ExplicitStateTransition(hud.AutoResourceState, ResourceStates.CopyDest, discardResource: true);
                    commandList.ResolveResourceStates();
                    commandList._d3dCommandList.CopyResource(hud.D3DResource, _hudPicture.D3DResource);
                    commandList.ExplicitStateTransition(_hudPicture.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
                    commandList.ExplicitStateTransition(hud.AutoResourceState, ResourceStates.RenderTarget, discardResource: false);
                }
                commandList.ResolveResourceStates();
                DrawFrames(commandList, frames);
                if (picture)
                {
                    FrameCapture.Record(commandList, _hudSwapchain!.Current!, "hud");
                }
                FrameCapture.Record(commandList, frames, "hudframes");
            }
            _editorFrames = true;
            _editorPicture = picture;
            return true;
        }
        catch (Exception e)
        {
            _editorFailed = true;
            Log.Error("Could not show the HUD parts in the Modify UI editor; they still show during play.", e);
            return false;
        }
    }

    /// <summary>Edge length of a frame cell compared to its part on the UI image (the frames are plain colors).</summary>
    private const int FramesScale = 2;

    /// <summary>
    /// One cell per HUD part in the frames image, stacked top to bottom with a transparent gap. Each cell has its part's
    /// shape, so the frame's border is equally thick all round when the cell is stretched over the part's quad (the
    /// parts' own rectangles on the UI image overlap, so they cannot be used directly).
    /// </summary>
    private static void LayoutFrames(int uiWidth, int uiHeight, out int width, out int height)
    {
        const int gap = 2;
        width = 1;
        height = 0;
        for (int i = 0; i < HudLayout.Pieces.Length; i++)
        {
            HudLayout.Piece piece = HudLayout.Pieces[i];
            int w = Math.Max(16, (int)MathF.Ceiling((piece.X1 - piece.X0) * uiWidth / FramesScale));
            int h = Math.Max(16, (int)MathF.Ceiling((piece.Y1 - piece.Y0) * uiHeight / FramesScale));
            _frameCells[i] = new RawRect(0, height, w, height + h);
            width = Math.Max(width, w);
            height += h + gap;
        }
    }

    // Premultiplied colors of the editor's frames: a light frame on a dark see-through fill, the chosen part in cyan.
    private static readonly Color4 FrameFill = new(0f, 0f, 0f, 0.3f);
    private static readonly Color4 FrameBorder = new(0.45f, 0.45f, 0.45f, 0.85f);
    private static readonly Color4 SelectedFill = new(0f, 0.09f, 0.11f, 0.35f);
    private static readonly Color4 SelectedBorder = new(0.05f, 0.6f, 1f, 1f);

    /// <summary>Draws every part's frame into its cell; the part being placed gets a thicker, highlighted frame.</summary>
    private static void DrawFrames(DirectCommandList commandList, BackBuffer frames)
    {
        var list = commandList._d3dCommandList;
        CpuDescriptorHandle target = frames.D3DRTVCpuDescriptorHandle;
        // An empty rectangle list would clear the whole image, so the whole image is the first clear on purpose.
        list.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 0f), new[] { new RawRect(0, 0, frames.Resolution.X, frames.Resolution.Y) });
        int selected = Math.Clamp(ModifyUiEditor.Selected, 0, HudLayout.Pieces.Length - 1);
        var fills = new List<RawRect>();
        var borders = new List<RawRect>();
        for (int i = 0; i < _frameCells.Length; i++)
        {
            if (i != selected)
            {
                fills.Add(_frameCells[i]);
                AddBorder(borders, _frameCells[i], 2);
            }
        }
        if (fills.Count > 0)
        {
            list.ClearRenderTargetView(target, FrameFill, fills.ToArray());
            list.ClearRenderTargetView(target, FrameBorder, borders.ToArray());
        }
        borders.Clear();
        AddBorder(borders, _frameCells[selected], 3);
        list.ClearRenderTargetView(target, SelectedFill, new[] { _frameCells[selected] });
        list.ClearRenderTargetView(target, SelectedBorder, borders.ToArray());
    }

    private static void AddBorder(List<RawRect> rects, RawRect cell, int thickness)
    {
        rects.Add(new RawRect(cell.Left, cell.Top, cell.Right, cell.Top + thickness));
        rects.Add(new RawRect(cell.Left, cell.Bottom - thickness, cell.Right, cell.Bottom));
        rects.Add(new RawRect(cell.Left, cell.Top + thickness, cell.Left + thickness, cell.Bottom - thickness));
        rects.Add(new RawRect(cell.Right - thickness, cell.Top + thickness, cell.Right, cell.Bottom - thickness));
    }

    /// <summary>Creates the OpenXR image on first use (the UI's size only changes with all images recreated).</summary>
    private static void EnsureSwapchain(XrRuntime xr, ref XrImageSwapchain? swapchain, string name, int width, int height)
    {
        if (swapchain == null || swapchain.Width != width || swapchain.Height != height)
        {
            swapchain?.Dispose();
            swapchain = new XrImageSwapchain(xr, name, width, height);
        }
    }

    /// <summary>The textures holding the HUD pictures for the editor: our own, as OpenXR images cannot be read once released.</summary>
    private static void EnsurePictures(int width, int height)
    {
        if (_hudPicture != null && _hudPicture.Resolution == new Vector2I(width, height))
        {
            return;
        }
        DisposePictures();
        _hudPicture = CreatePicture(width, height, "SE2VR HUD picture");
        _hudPending = CreatePicture(width, height, "SE2VR HUD picture (newest)");
    }

    private static BackBuffer CreatePicture(int width, int height, string name)
    {
        ID3D12Resource resource = CoreSystems.DeviceWrap.D3DDevice.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget);
        resource.Name = name;
        var picture = new BackBuffer();
        picture.Initialize(resource, ResourceStates.RenderTarget, new Vector2I(width, height), Format.R8G8B8A8_UNorm_SRgb, new Color(0f, 0f, 0f, 0f));
        return picture;
    }

    /// <summary>The GPU must be done with the pictures (resolution change or shutdown).</summary>
    private static void DisposePictures()
    {
        _hudPicture?.GPUDispose();
        _hudPending?.GPUDispose();
        _hudPicture = _hudPending = null;
        _hudHasPicture = _hudHasPending = false;
        _hudPendingTime = 0;
    }

    private static uint BuildStereoLayers(XrRuntime xr, bool withUi, bool withPieces)
    {
        for (int eye = 0; eye < 2; eye++)
        {
            _projectionViews[eye] = new XrCompositionLayerProjectionView
            {
                Type = XrStructureType.CompositionLayerProjectionView,
                Pose = xr.Views[eye].Pose,
                Fov = xr.Views[eye].Fov,
                SubImage = _eyeSwapchains[eye]!.SubImage
            };
        }
        *_projectionLayer = new XrCompositionLayerProjection
        {
            Type = XrStructureType.CompositionLayerProjection,
            Space = xr.RenderSpace,
            ViewCount = 2,
            Views = _projectionViews
        };
        _layers[0] = _projectionLayer;
        if (!withUi)
        {
            return 1;
        }
        // The UI was drawn over transparent black, which leaves premultiplied color: blend it over the world.
        uint count = 1;
        _layers[count++] = _piecesLive ? SetPanel(xr, blended: true, "uimain", _uiMainSwapchain!) : SetPanel(xr, blended: true, "ui", _uiSwapchain!);
        if (withPieces)
        {
            count = AddHudPieces(xr, count, frames: _editorFrames, pictures: _piecesLive || _editorPicture);
        }
        if (SetCursor(xr))
        {
            _layers[count++] = _cursorLayer;
        }
        return count;
    }

    /// <summary>
    /// A quad per HUD part that follows your head, in view space, showing its part of the HUD image. In the Modify UI
    /// editor each part also gets its frame; all frames come first, so no frame covers another part's picture.
    /// </summary>
    private static uint AddHudPieces(XrRuntime xr, uint count, bool frames, bool pictures)
    {
        XrImageSwapchain ui = _uiSwapchain!;
        int following = 0;
        foreach (HudLayout.Piece piece in HudLayout.Pieces)
        {
            following += HudLayout.SettingsFor(piece).FollowHead ? 1 : 0;
        }
        // One layer stays free for the mouse pointer; with too few layers left the pictures win over the frames.
        if (frames && pictures && count + 2 * following > xr.MaxLayerCount - 1)
        {
            frames = false;
        }
        float degreesPerPixel = VRConfig.Current.UiWidthDegrees / ui.Width;
        for (int pass = 0; pass < 2; pass++)
        {
            bool framePass = pass == 0;
            if (framePass ? !frames : !pictures)
            {
                continue;
            }
            XrImageSwapchain image = framePass ? _hudFramesSwapchain! : _hudSwapchain!;
            for (int i = 0; i < HudLayout.Pieces.Length; i++)
            {
                HudLayout.Piece piece = HudLayout.Pieces[i];
                HudPieceSettings settings = HudLayout.SettingsFor(piece);
                if (!settings.FollowHead || count >= xr.MaxLayerCount - 1)
                {
                    continue;
                }
                // The part's rectangle on the UI image sets the quad's size; the frame's cell has the same shape.
                int x = (int)(piece.X0 * ui.Width), y = (int)(piece.Y0 * ui.Height);
                int width = Math.Max(1, (int)MathF.Ceiling(piece.X1 * ui.Width) - x), height = Math.Max(1, (int)MathF.Ceiling(piece.Y1 * ui.Height) - y);
                float distance = Math.Clamp(settings.Distance, 0.2f, 10f);
                float angle = width * degreesPerPixel * Math.Clamp(settings.Size, 0.2f, 5f) * MathF.PI / 180f;
                float quadWidth = 2f * distance * MathF.Tan(Math.Min(angle, 3f) / 2f);
                float quadHeight = quadWidth * height / width;
                XrPosef pose = PoseInView(settings.LeftRight, settings.UpDown, distance);
                RawRect rect = framePass ? _frameCells[i] : new RawRect(x, y, x + width, y + height);
                XrCompositionLayerQuad* layer = framePass ? &_frameLayers[i] : &_pieceLayers[i];
                *layer = new XrCompositionLayerQuad
                {
                    Type = XrStructureType.CompositionLayerQuad,
                    LayerFlags = XrConst.CompositionLayerBlendTextureSourceAlpha,
                    Space = xr.ViewSpace,
                    EyeVisibility = XrConst.EyeVisibilityBoth,
                    SubImage = new XrSwapchainSubImage
                    {
                        Swapchain = image.Handle,
                        ImageRect = new XrRect2Di { OffsetX = rect.Left, OffsetY = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top }
                    },
                    Pose = pose,
                    Size = new XrExtent2Df { Width = quadWidth, Height = quadHeight }
                };
                _layers[count++] = layer;
                if (FrameCapture.Active)
                {
                    _previewLayers.Add(new PreviewLayer
                    {
                        Image = framePass ? "hudframes" : "hud",
                        Blended = true,
                        Pose = HeadPose(xr).Compose(pose),
                        Width = quadWidth,
                        Height = quadHeight,
                        U0 = (float)rect.Left / image.Width,
                        V0 = (float)rect.Top / image.Height,
                        U1 = (float)rect.Right / image.Width,
                        V1 = (float)rect.Bottom / image.Height
                    });
                }
            }
        }
        return count;
    }

    /// <summary>A pose <paramref name="distance"/> meters ahead of the head, turned by the given angles and facing back at it.</summary>
    private static XrPosef PoseInView(float rightDegrees, float upDegrees, float distance)
    {
        float yaw = rightDegrees * MathF.PI / 180f, pitch = upDegrees * MathF.PI / 180f;
        // Facing the head: turn by -yaw around Y (up), then by pitch around X.
        float cy = MathF.Cos(-yaw / 2f), sy = MathF.Sin(-yaw / 2f), cp = MathF.Cos(pitch / 2f), sp = MathF.Sin(pitch / 2f);
        return new XrPosef
        {
            Orientation = new XrQuaternionf { X = cy * sp, Y = sy * cp, Z = -sy * sp, W = cy * cp },
            Position = new XrVector3f
            {
                X = distance * MathF.Sin(yaw) * MathF.Cos(pitch),
                Y = distance * MathF.Sin(pitch),
                Z = -distance * MathF.Cos(yaw) * MathF.Cos(pitch)
            }
        };
    }

    /// <summary>Head pose in the render space, from the two eyes (for the simulated headset views of a capture).</summary>
    private static XrPosef HeadPose(XrRuntime xr)
    {
        XrVector3f a = xr.Views[0].Pose.Position, b = xr.Views[1].Pose.Position;
        return new XrPosef
        {
            Orientation = xr.Views[0].Pose.Orientation,
            Position = new XrVector3f { X = (a.X + b.X) / 2f, Y = (a.Y + b.Y) / 2f, Z = (a.Z + b.Z) / 2f }
        };
    }

    /// <param name="withPieces">The Modify UI editor is open: show the HUD parts that follow your head (see <see cref="PrepareEditor"/>).</param>
    private static uint BuildMenuLayer(XrRuntime xr, bool withPieces)
    {
        _layers[0] = SetPanel(xr, blended: false, "menu", _uiSwapchain!); // opaque virtual screen
        uint count = 1;
        if (withPieces)
        {
            count = AddHudPieces(xr, count, frames: _editorFrames, pictures: _editorPicture);
        }
        if (SetCursor(xr))
        {
            _layers[count++] = _cursorLayer;
        }
        return count;
    }

    /// <summary>Fills in the UI panel layer (curved or flat, see <see cref="UiPanel"/>) and returns it.</summary>
    private static void* SetPanel(XrRuntime xr, bool blended, string imageName, XrImageSwapchain ui)
    {
        _panel = UiPanel.FromConfig(xr, ui.Width, ui.Height);
        ulong flags = blended ? XrConst.CompositionLayerBlendTextureSourceAlpha : 0;
        if (_panel.Curved)
        {
            *_cylinderLayer = new XrCompositionLayerCylinderKHR
            {
                Type = XrStructureType.CompositionLayerCylinderKHR,
                LayerFlags = flags,
                Space = xr.RenderSpace,
                EyeVisibility = XrConst.EyeVisibilityBoth,
                SubImage = ui.SubImage,
                Pose = _panel.CenterPose,
                Radius = _panel.Distance,
                CentralAngle = _panel.Angle,
                AspectRatio = _panel.Aspect
            };
        }
        else
        {
            *_quadLayer = new XrCompositionLayerQuad
            {
                Type = XrStructureType.CompositionLayerQuad,
                LayerFlags = flags,
                Space = xr.RenderSpace,
                EyeVisibility = XrConst.EyeVisibilityBoth,
                SubImage = ui.SubImage,
                Pose = _panel.CenterPose,
                Size = new XrExtent2Df { Width = _panel.Width, Height = _panel.Height }
            };
        }
        if (FrameCapture.Active)
        {
            _previewLayers.Add(new PreviewLayer
            {
                Image = imageName,
                Blended = blended,
                Pose = _panel.CenterPose,
                Width = _panel.Width,
                Height = _panel.Height,
                Radius = _panel.Curved ? _panel.Distance : 0f,
                CentralAngle = _panel.Angle,
                AspectRatio = _panel.Aspect
            });
        }
        return _panel.Curved ? _cylinderLayer : _quadLayer;
    }

    /// <summary>Adds the mouse pointer on top of the panel while the game shows it (call after <see cref="SetPanel"/>).</summary>
    private static bool SetCursor(XrRuntime xr)
    {
        XrImageSwapchain ui = _uiSwapchain!;
        XrImageSwapchain eye = _eyeSwapchains[0]!;
        if (!VrCursor.TryGetCanvasPosition(new Vector2I(eye.Width, eye.Height), new Vector2I(ui.Width, ui.Height), out float u, out float v) ||
            !VrCursor.EnsureImage(xr))
        {
            return false;
        }
        *_cursorLayer = VrCursor.CreateLayer(xr.RenderSpace, in _panel, u, v, out float size);
        if (FrameCapture.Active)
        {
            _previewLayers.Add(new PreviewLayer { Image = "cursor", Blended = true, Pose = _cursorLayer->Pose, Width = size, Height = size });
        }
        return true;
    }

    private static void EnsureLayerMemory()
    {
        if (_layers != null)
        {
            return;
        }
        _projectionLayer = (XrCompositionLayerProjection*)NativeMemory.AllocZeroed((nuint)sizeof(XrCompositionLayerProjection));
        _projectionViews = (XrCompositionLayerProjectionView*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(XrCompositionLayerProjectionView)));
        _quadLayer = (XrCompositionLayerQuad*)NativeMemory.AllocZeroed((nuint)sizeof(XrCompositionLayerQuad));
        _cylinderLayer = (XrCompositionLayerCylinderKHR*)NativeMemory.AllocZeroed((nuint)sizeof(XrCompositionLayerCylinderKHR));
        _cursorLayer = (XrCompositionLayerQuad*)NativeMemory.AllocZeroed((nuint)sizeof(XrCompositionLayerQuad));
        _pieceLayers = (XrCompositionLayerQuad*)NativeMemory.AllocZeroed((nuint)(HudLayout.Pieces.Length * sizeof(XrCompositionLayerQuad)));
        _frameLayers = (XrCompositionLayerQuad*)NativeMemory.AllocZeroed((nuint)(HudLayout.Pieces.Length * sizeof(XrCompositionLayerQuad)));
        // Projection, panel, a frame and a picture per HUD part, pointer.
        _layers = (void**)NativeMemory.AllocZeroed((nuint)((4 + 2 * HudLayout.Pieces.Length) * sizeof(void*)));
    }

    private static void DisposeSwapchains()
    {
        for (int i = 0; i < 2; i++)
        {
            _eyeSwapchains[i]?.Dispose();
            _eyeSwapchains[i] = null;
        }
        _uiSwapchain?.Dispose();
        _uiSwapchain = null;
        _hudSwapchain?.Dispose();
        _hudSwapchain = null;
        _uiMainSwapchain?.Dispose();
        _uiMainSwapchain = null;
        _hudFramesSwapchain?.Dispose();
        _hudFramesSwapchain = null;
        DisposePictures();
    }

    /// <summary>Releases GPU resources. Render thread only, with the GPU idle.</summary>
    public static void Shutdown()
    {
        DisposeSwapchains();
        VrCursor.Dispose();
        for (int eye = 0; eye < 2; eye++)
        {
            _eyeHasHistory[eye] = false;
        }
        EyeHistory.Reset();
        Foveation.Reset();
        DynamicResolution.Reset();
    }
}
