using System;
using System.Collections.Generic;
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
using Vortice.Direct3D12;
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
    private static readonly RenderView[] _eyePreviousViews = new RenderView[2];
    private static readonly bool[] _eyeHasHistory = new bool[2];

    private static readonly XrImageSwapchain?[] _eyeSwapchains = new XrImageSwapchain?[2];
    private static XrImageSwapchain? _uiSwapchain;

    private static XrCompositionLayerProjection* _projectionLayer;
    private static XrCompositionLayerProjectionView* _projectionViews;
    private static XrCompositionLayerQuad* _quadLayer;
    private static void** _layers;

    private static bool _loggedStereo;
    private static bool _loggedMenu;
    private static bool _loggedFsr;
    private static int _framesWithoutWorld = 1_000_000;

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
        VRConfig config = VRConfig.Current;
        SettingsManager settings = CoreSystems.Settings;
        if (config.DisableFsr && settings._drs.AAMode == AAMode.FSR)
        {
            // One temporal history cannot serve two different viewpoints; FXAA keeps the image stable.
            settings._drs.AAMode = AAMode.FXAA;
            if (!_loggedFsr)
            {
                _loggedFsr = true;
                Log.Info("FSR is not supported in VR yet; using FXAA while the headset is active.");
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
        bool frameBegun = xr.BeginFrame();
        bool shouldRender = frameBegun && xr.FrameState.ShouldRender != 0;
        bool stereo = shouldRender && sceneDraw && xr.ViewsValid;
        FrameCapture.BeginFrame();
        bool menuPanel = shouldRender && !sceneDraw;
        if (stereo || menuPanel)
        {
            if (!EnsureSwapchains(xr) || !AcquireImages(stereo))
            {
                stereo = menuPanel = false;
            }
        }

        uint layerCount = 0;
        if (sceneDraw)
        {
            if (stereo)
            {
                bool uiWanted = settings.Overrides.TopMostPass && settings.Overrides.UI;
                DrawStereoScene();
                bool uiDrawn = uiWanted && DrawUiLayer();
                layerCount = BuildStereoLayers(xr, uiDrawn);
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
            if (menuPanel && CopyToImage(_uiSwapchain!, finalLDRBuffer, "VR.MenuPanel", "menu"))
            {
                layerCount = BuildMenuLayer(xr);
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
        CoreSystems.Samplers.OnEndFrame();
        CoreSystems.FrameDispatcher.FlushQueues();
        CoreSystems.LoadingMonitor.OnEndDraw();

        // Everything that writes the OpenXR images has now been submitted to the direct queue.
        ReleaseImages();
        xr.EndFrame(_layers, layerCount);
        FrameCapture.EndFrame(VRSystem.PluginDirectory);
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
        try
        {
            for (int eye = 0; eye < 2; eye++)
            {
                BuildEyeView(eye, in center);
                liveView = _eyeViews[eye];
                settings._previousRenderView = _eyePreviousViews[eye];
                CurrentEye = eye;

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
            }
        }
        finally
        {
            CurrentEye = -1;
            settings._overrides = overrides;
            liveView = center;
            settings._previousRenderView = centerPrevious;
        }

        // Per-eye equivalent of SettingsManager.OnFrameEndDisposal: each eye keeps its own previous view and speed.
        for (int eye = 0; eye < 2; eye++)
        {
            _eyePreviousViews[eye] = _eyeViews[eye];
            _eyeViews[eye].OnFrameEnd();
        }
    }

    /// <summary>
    /// The eye camera is the game camera with the tracked eye pose (relative to the seated origin) applied on top, so
    /// mouse/gamepad still steer the body and the headset adds free head movement.
    /// </summary>
    private static void BuildEyeView(int eye, in RenderView center)
    {
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
        view.SetCameraParameters(in eyeWorld, engineFov, center.NearClipping, center.FarClipping, center.VeryFarClipping, projectionOffset, smooth: true);

        // SetCameraParameters derives the aspect ratio from the render resolution; replace the projection with the
        // exact per-eye frustum so it matches what the compositor expects even if the resolution aspect differs.
        Matrix projection = XrMath.CreateProjection(xrView.Fov, view.NearClipping);
        MatrixD viewAtOrigin = view.ViewD;
        viewAtOrigin.M14 = viewAtOrigin.M24 = viewAtOrigin.M34 = 0;
        viewAtOrigin.M41 = viewAtOrigin.M42 = viewAtOrigin.M43 = 0;
        viewAtOrigin.M44 = 1;
        MatrixD viewD = view.ViewD;
        view.Projection = RenderView.CreateProjectionMatrices(in projection, in viewAtOrigin, in viewD);

        if (!hasHistory)
        {
            _eyePreviousViews[eye] = view;
            _eyeHasHistory[eye] = true;
        }
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
                CoreSystems.MainUISystem.DoWork(commandList, finalLDRBuffer, finalLDRBuffer.Resolution);
            }
            block.BeginNextBlock("EndDraw");
            CoreSystems.ShaderAsserts.PrepareReadback(commandList);
        }
        CoreSystems.CommonResources.OnEndDraw();
        CoreSystems.Settings.OnEndDraw();
    }

    private static bool CopyToImage(XrImageSwapchain swapchain, ResizableRWRenderTargetTexture source, string name, string captureName)
    {
        BackBuffer? target = swapchain.Current;
        if (target == null)
        {
            return false;
        }
        // While the game switches resolution the engine can hand out a texture of the previous size for one frame.
        var sourceDescription = source.D3DResourceDescription;
        if ((int)sourceDescription.Width != swapchain.Width || (int)sourceDescription.Height != swapchain.Height)
        {
            return false;
        }
        using DirectCommandList commandList = CoreSystems.FrameDispatcher.CreateDirectCommandList(name);
        commandList.CopyResource(target, source);
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
            _uiSwapchain = new XrImageSwapchain(xr, "UI", width, height);
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
    }

    private static uint BuildStereoLayers(XrRuntime xr, bool withUi)
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
        SetQuad(xr, XrConst.CompositionLayerBlendTextureSourceAlpha);
        _layers[1] = _quadLayer;
        return 2;
    }

    private static uint BuildMenuLayer(XrRuntime xr)
    {
        SetQuad(xr, 0); // opaque virtual screen
        _layers[0] = _quadLayer;
        return 1;
    }

    private static void SetQuad(XrRuntime xr, ulong flags)
    {
        VRConfig config = VRConfig.Current;
        XrImageSwapchain ui = _uiSwapchain!;
        *_quadLayer = new XrCompositionLayerQuad
        {
            Type = XrStructureType.CompositionLayerQuad,
            LayerFlags = flags,
            Space = xr.RenderSpace,
            EyeVisibility = XrConst.EyeVisibilityBoth,
            SubImage = ui.SubImage,
            Pose = new XrPosef
            {
                Orientation = XrQuaternionf.Identity,
                Position = new XrVector3f { Z = -config.UiDistance }
            },
            Size = new XrExtent2Df { Width = config.UiWidth, Height = config.UiWidth * ui.Height / ui.Width }
        };
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
        _layers = (void**)NativeMemory.AllocZeroed((nuint)(4 * sizeof(void*)));
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
    }

    /// <summary>Releases GPU resources. Render thread only, with the GPU idle.</summary>
    public static void Shutdown()
    {
        DisposeSwapchains();
        for (int eye = 0; eye < 2; eye++)
        {
            _eyeHasHistory[eye] = false;
        }
    }
}
