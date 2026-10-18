using System;
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

    /// <summary>Width of the floating menu/HUD panel, in meters. Height follows the game's aspect ratio.</summary>
    public float UiWidth { get; set; } = 1.6f;

    /// <summary>Force the game into a window with the per-eye resolution, so every rendered pixel is used by the headset.</summary>
    public bool ForceWindowedResolution { get; set; } = true;

    /// <summary>Ignore display-mode changes from the game's options menu while VR is active (they would resize the eye images).</summary>
    public bool LockResolution { get; set; } = true;

    /// <summary>Key that makes the current head position and heading the new center, e.g. "Home" or "Ctrl+R". Empty disables it.</summary>
    public string RecenterKey { get; set; } = "Home";

    /// <summary>Key that saves the images sent to the headset as PNGs in the "captures" folder. Empty disables it.</summary>
    public string CaptureKey { get; set; } = "Ctrl+Shift+F10";

    /// <summary>FSR keeps one temporal history, which both eyes would share and smear. Falls back to FXAA in VR.</summary>
    public bool DisableFsr { get; set; } = true;

    /// <summary>How long to wait at start-up for SteamVR to report the headset before giving up.</summary>
    public float SystemWaitSeconds { get; set; } = 15f;

    public bool VerboseLogging { get; set; }

    public static VRConfig Current { get; private set; } = new();

    // Relaxed escaping keeps hotkeys readable ("Ctrl+R" instead of "Ctrl\u002BR").
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Load(string path)
    {
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
        Log.Verbose = Current.VerboseLogging;
        AddMissingKeys(path);
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
