using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Plugins;
using SE2VR.Patches;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace SE2VR;

/// <summary>
/// Plugin entry point. Space Engineers 2 creates it when started with
/// <c>-plugins:"C:\path\to\SE2VR.dll"</c> (Steam: Properties > General > Launch Options).
/// </summary>
public sealed class VRPlugin : IPlugin, IDisposable
{
    private const string HarmonyId = "SE2VR";

    private readonly Harmony? _harmony;

    public static string Version => typeof(VRPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public VRPlugin(PluginHost host)
    {
        string directory = Path.GetDirectoryName(typeof(VRPlugin).Assembly.Location)!;
        Log.Initialize(Path.Combine(directory, "SE2VR.log"));
        Log.Info($"SE2VR {Version} loading from {directory} (.NET {Environment.Version})");
        // SE2VR_CONFIG points the automated tests at their own settings file, leaving the player's untouched.
        VRConfig.Load(Environment.GetEnvironmentVariable("SE2VR_CONFIG") is { Length: > 0 } testConfig ? testConfig : Path.Combine(directory, "SE2VR.json"));
        VRSystem.PluginDirectory = directory;
        if (!VRConfig.Current.Enabled)
        {
            Log.Info("Disabled in SE2VR.json.");
            return;
        }

        _harmony = new Harmony(HarmonyId);
        // The draw takeover is essential; without it nothing else matters.
        if (!Patch(typeof(DrawPatch)) || !Patch(typeof(DisposePatch)))
        {
            Log.Error("The renderer could not be patched (has the game been updated?). VR disabled.");
            _harmony.UnpatchAll(HarmonyId);
            VRSystem.Disable();
            return;
        }
        Patch(typeof(ParticlePatches));
        Patch(typeof(SharedWorkPatches));
        Patch(typeof(DisplayOptionsPatch));
        Patch(typeof(KeepSavedDisplayOptionsPatch));
        Patch(typeof(SlugAlphaPatch));
        Patch(typeof(UiCanvasPatch.Initialize));
        Patch(typeof(UiCanvasPatch.DisplaySettingsChanged));
        Patch(typeof(HudProjectionPatch));
        if (Rendering.GpuProfile.Enabled)
        {
            Patch(typeof(Rendering.GpuProfilePatch));
            Patch(typeof(Rendering.GpuProfilePassPatch));
        }
        host.OnBeforeEngineInstantiated += OnBeforeEngineInstantiated;
    }

    private bool Patch(Type patchClass)
    {
        try
        {
            _harmony!.CreateClassProcessor(patchClass).Patch();
            Log.Info($"Patched {patchClass.Name}");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Patch {patchClass.Name} failed", e);
            return false;
        }
    }

    private static void OnBeforeEngineInstantiated(EngineBuilder engine)
    {
        try
        {
            VRSystem.EarlyInit(engine);
        }
        catch (Exception e)
        {
            Log.Error("VR start-up failed.", e);
            VRSystem.Disable();
        }
    }

    public void Dispose()
    {
        VRSystem.Shutdown();
        _harmony?.UnpatchAll(HarmonyId);
        Log.Info("Unloaded.");
        Log.Close();
    }
}
