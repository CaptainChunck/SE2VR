using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SE2VR;

/// <summary>
/// User settings, read from SE2VR.json next to the plugin. Missing keys keep their defaults.
/// </summary>
internal sealed class VRConfig
{
    /// <summary>Master switch. When false the plugin loads but does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Per-eye render resolution relative to what the VR runtime recommends (SteamVR's per-app resolution slider also applies).</summary>
    public float RenderScale { get; set; } = 0.75f;

    /// <summary>Scales tracked head movement and eye separation. 1.0 is real size; above 1 makes the world feel smaller.</summary>
    public float WorldScale { get; set; } = 1f;

    /// <summary>Distance of the floating menu/HUD panel from the seated head position, in meters.</summary>
    public float UiDistance { get; set; } = 1.6f;

    /// <summary>How much of your view the menu/HUD panel spans horizontally, in degrees. Larger makes text bigger.</summary>
    public float UiWidthDegrees { get; set; } = 70f;

    /// <summary>Curve the panel around you so all of it is the same distance away (if the VR runtime supports it).</summary>
    public bool UiCurved { get; set; } = true;

    /// <summary>
    /// Shape (width / height) of the canvas the game lays its UI out on. The game's UI is designed for 16:9 screens;
    /// the headset's eye images are almost square, which would stretch the HUD to the top and bottom of your view.
    /// </summary>
    public float UiAspectRatio { get; set; } = 16f / 9f;

    /// <summary>Show the mouse pointer on the panel. The game uses the Windows pointer, which only appears on the monitor.</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>Force the game into a window with the per-eye resolution, so every rendered pixel is used by the headset.</summary>
    public bool ForceWindowedResolution { get; set; } = true;

    /// <summary>Ignore display-mode changes from the game's options menu while VR is active (they would resize the eye images).</summary>
    public bool LockResolution { get; set; } = true;

    /// <summary>Key that makes the current head position and heading the new center, e.g. "Home" or "Ctrl+R". Empty disables it.</summary>
    public string RecenterKey { get; set; } = "Home";

    /// <summary>Keys that make the floating panel bigger or smaller while playing, 5 degrees per press. Saved to this file.</summary>
    public string UiBiggerKey { get; set; } = "Ctrl+PageUp";

    public string UiSmallerKey { get; set; } = "Ctrl+PageDown";

    /// <summary>Key that saves the images sent to the headset as PNGs in the "captures" folder. Empty disables it.</summary>
    public string CaptureKey { get; set; } = "Ctrl+Shift+F10";

    /// <summary>
    /// Do eye-independent work (sun and character shadow refreshes, reflection probes, cloud shadows) once per frame
    /// instead of once per eye. Turn off if you ever see shadow or reflection glitches in one eye.
    /// </summary>
    public bool ShareWorkBetweenEyes { get; set; } = true;

    /// <summary>
    /// Use FSR (the game's temporal anti-aliasing and upscaling, when selected in its graphics options) in VR; each eye
    /// keeps its own FSR history. Off = FXAA, which is cheaper but leaves edges and fine detail shimmering in the headset.
    /// </summary>
    public bool FsrInVR { get; set; } = true;

    /// <summary>
    /// Allow ray tracing while the headset is active. It roughly doubles the GPU time per eye and needs a lot of video
    /// memory, so it is off in VR by default; the game's own setting (used on the monitor) is not changed.
    /// </summary>
    public bool RaytracingInVR { get; set; }

    /// <summary>
    /// Foveated rendering: lighting, transparents and particles are shaded at a lower rate towards the edges of the view,
    /// where the lenses blur anyway. "Off", "Quality", "Balanced" or "Performance". Needs a GPU with variable rate shading
    /// tier 2 (NVIDIA RTX / GTX 16, AMD RX 6000 or newer); ignored otherwise.
    /// </summary>
    public string Foveation { get; set; } = "Quality";

    /// <summary>
    /// VR-friendly post-processing: no lens dirt (it is glued to the screen, so it moves with your head), bloom at half
    /// resolution and without its anti-flicker pass (FSR already keeps it steady).
    /// </summary>
    public bool VrPostEffects { get; set; } = true;

    /// <summary>Lens flares and lamp glare. Their ghosts are flat screen effects that look wrong in a headset; off by default.</summary>
    public bool LensFlaresInVR { get; set; }

    /// <summary>
    /// Lowers the render resolution (before FSR upscaling) a little at a time when a frame would miss the headset's
    /// refresh, and raises it again when there is room, so the frame rate stays steady instead of dropping.
    /// </summary>
    public bool DynamicResolution { get; set; } = true;

    /// <summary>Lowest render resolution dynamic resolution may use, as a fraction of the eye image size.</summary>
    public float DynamicResolutionMin { get; set; } = 0.5f;

    /// <summary>"Auto": the headset's full refresh rate when the GPU nearly makes it, otherwise half (e.g. 45 fps on 90 Hz). "Full" or "Half" forces one.</summary>
    public string DynamicResolutionTarget { get; set; } = "Auto";

    /// <summary>Placement of the HUD pieces that can follow your head (set in the game's VR settings, MODIFY UI).</summary>
    public List<SE2VR.UI.HudPieceSettings> HudPieces { get; set; } = SE2VR.UI.HudLayout.DefaultSettings();

    /// <summary>How long to wait at start-up for SteamVR to report the headset before giving up.</summary>
    public float SystemWaitSeconds { get; set; } = 15f;

    public bool VerboseLogging { get; set; }

    public static VRConfig Current { get; private set; } = new();

    private static string? _path;
    private static readonly object _saveLock = new();

    // Relaxed escaping keeps hotkeys readable ("Ctrl+R" instead of "Ctrl\u002BR").
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Load(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<VRConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new VRConfig();
                Log.Info($"Loaded config {path}");
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(Current, WriteOptions));
                Log.Info($"Wrote default config {path}");
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not read {path}; using defaults", e);
            Current = new VRConfig();
        }
        Current.RenderScale = Math.Clamp(Current.RenderScale, 0.25f, 2f);
        Current.WorldScale = Math.Clamp(Current.WorldScale, 0.1f, 10f);
        Current.UiDistance = Math.Clamp(Current.UiDistance, 0.3f, 20f);
        Current.UiWidthDegrees = Math.Clamp(Current.UiWidthDegrees, 20f, 160f);
        Current.UiAspectRatio = Math.Clamp(Current.UiAspectRatio, 1f, 4f);
        Log.Verbose = Current.VerboseLogging;
        AddMissingKeys(path);
    }

    /// <summary>Writes the current settings back to the file, off the calling thread (used for in-game adjustments).</summary>
    public static void SaveInBackground()
    {
        string? path = _path;
        if (path == null)
        {
            return;
        }
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_saveLock)
            {
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(Current, WriteOptions));
                }
                catch (Exception e)
                {
                    Log.Warn($"Could not save {path}: {e.Message}");
                }
            }
        });
    }

    private static System.Threading.Timer? _saveTimer;

    /// <summary>Saves a second after the last change (a slider being dragged changes the settings many times).</summary>
    public static void SaveSoon()
    {
        lock (_saveLock)
        {
            _saveTimer ??= new System.Threading.Timer(_ => SaveInBackground());
            _saveTimer.Change(1000, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>Puts every setting shown in the game's VR tab (and the HUD layout) back to its default; keys stay as they are.</summary>
    public static void RestoreDefaults()
    {
        VRConfig defaults = new();
        VRConfig current = Current;
        current.RenderScale = defaults.RenderScale;
        current.WorldScale = defaults.WorldScale;
        current.UiDistance = defaults.UiDistance;
        current.UiWidthDegrees = defaults.UiWidthDegrees;
        current.UiCurved = defaults.UiCurved;
        current.ShowCursor = defaults.ShowCursor;
        current.ShareWorkBetweenEyes = defaults.ShareWorkBetweenEyes;
        current.FsrInVR = defaults.FsrInVR;
        current.RaytracingInVR = defaults.RaytracingInVR;
        current.Foveation = defaults.Foveation;
        current.VrPostEffects = defaults.VrPostEffects;
        current.LensFlaresInVR = defaults.LensFlaresInVR;
        current.DynamicResolution = defaults.DynamicResolution;
        current.DynamicResolutionMin = defaults.DynamicResolutionMin;
        current.DynamicResolutionTarget = defaults.DynamicResolutionTarget;
        foreach (SE2VR.UI.HudLayout.Piece piece in SE2VR.UI.HudLayout.Pieces)
        {
            SE2VR.UI.HudLayout.Reset(piece);
        }
    }

    /// <summary>Rewrites the file when settings added by a newer version are missing, so they become discoverable.</summary>
    private static void AddMissingKeys(string path)
    {
        try
        {
            string current = File.Exists(path) ? File.ReadAllText(path) : "";
            string complete = JsonSerializer.Serialize(Current, WriteOptions);
            using JsonDocument existing = JsonDocument.Parse(current.Length > 0 ? current : "{}", new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            using JsonDocument full = JsonDocument.Parse(complete);
            foreach (JsonProperty property in full.RootElement.EnumerateObject())
            {
                bool present = false;
                foreach (JsonProperty mine in existing.RootElement.EnumerateObject())
                {
                    present |= string.Equals(mine.Name, property.Name, StringComparison.OrdinalIgnoreCase);
                }
                if (!present)
                {
                    File.WriteAllText(path, complete);
                    Log.Info($"Added new settings to {path}");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Could not update {path}: {e.Message}");
        }
    }
}
