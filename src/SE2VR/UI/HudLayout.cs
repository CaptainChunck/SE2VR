using System;
using System.Collections.Generic;
using System.Linq;

namespace SE2VR.UI;

/// <summary>Where one HUD piece is shown when it follows your head (saved in SE2VR.json).</summary>
internal sealed class HudPieceSettings
{
    public string Id { get; set; } = "";

    /// <summary>Shown as its own panel that follows your head; otherwise it stays on the menu screen.</summary>
    public bool FollowHead { get; set; }

    /// <summary>Degrees to the right of the middle of your view (negative: left).</summary>
    public float LeftRight { get; set; }

    /// <summary>Degrees above the middle of your view (negative: below).</summary>
    public float UpDown { get; set; }

    /// <summary>Meters from your eyes.</summary>
    public float Distance { get; set; } = 0.8f;

    /// <summary>1 = the same angular size as on the menu screen.</summary>
    public float Size { get; set; } = 1f;
}

/// <summary>
/// The parts of the game's HUD that can be taken off the menu screen and placed around your view.
/// <para>
/// The game lays its HUD out on a 1920x1080 design canvas; the rectangles below (fractions of that canvas, measured
/// from the game's own layout) cut each part out of the UI image the plugin already renders. A piece costs no extra
/// UI rendering: it is a part of the same image, shown as its own head-locked panel by the VR compositor.
/// </para>
/// </summary>
internal static class HudLayout
{
    public sealed record Piece(string Id, string Name, float X0, float Y0, float X1, float Y1, float LeftRight, float UpDown, float Distance, float Size);

    public static readonly Piece[] Pieces =
    {
        new("status", "Suit status", 0.010f, 0.768f, 0.245f, 0.987f, -20f, -22f, 0.8f, 1f),
        new("toolbar", "Toolbar", 0.276f, 0.740f, 0.724f, 0.982f, 0f, -28f, 0.8f, 1f),
        new("cockpit", "Ship status (in a cockpit)", 0.728f, 0.763f, 0.990f, 0.981f, 20f, -22f, 0.8f, 1f),
        new("objectives", "Objectives", 0.010f, 0.060f, 0.232f, 0.450f, -24f, 12f, 0.8f, 1f),
        new("notifications", "Notifications", 0.755f, 0.019f, 0.990f, 0.400f, 24f, 12f, 0.8f, 1f),
        new("warnings", "Warnings", 0.208f, 0.593f, 0.406f, 0.800f, -10f, -8f, 0.8f, 1f),
    };

    public static List<HudPieceSettings> DefaultSettings() => Pieces.Select(Defaults).ToList();

    public static HudPieceSettings Defaults(Piece piece) => new()
    {
        Id = piece.Id,
        FollowHead = false,
        LeftRight = piece.LeftRight,
        UpDown = piece.UpDown,
        Distance = piece.Distance,
        Size = piece.Size
    };

    /// <summary>The saved settings of a piece (added with defaults if the file has none for it).</summary>
    public static HudPieceSettings SettingsFor(Piece piece)
    {
        List<HudPieceSettings> list = VRConfig.Current.HudPieces;
        lock (list)
        {
            HudPieceSettings? settings = list.FirstOrDefault(s => string.Equals(s.Id, piece.Id, StringComparison.OrdinalIgnoreCase));
            if (settings == null)
            {
                settings = Defaults(piece);
                list.Add(settings);
            }
            return settings;
        }
    }

    public static bool AnyFollowsHead()
    {
        foreach (Piece piece in Pieces)
        {
            if (SettingsFor(piece).FollowHead)
            {
                return true;
            }
        }
        return false;
    }

    public static void Reset(Piece piece)
    {
        HudPieceSettings settings = SettingsFor(piece);
        HudPieceSettings defaults = Defaults(piece);
        settings.FollowHead = defaults.FollowHead;
        settings.LeftRight = defaults.LeftRight;
        settings.UpDown = defaults.UpDown;
        settings.Distance = defaults.Distance;
        settings.Size = defaults.Size;
    }
}
