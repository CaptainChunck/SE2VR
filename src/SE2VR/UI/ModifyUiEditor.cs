namespace SE2VR.UI;

/// <summary>
/// State of the Modify UI section of the VR settings tab, read by the renderer: while it is open every HUD part that
/// follows your head is shown in a frame (the selected part highlighted), with the HUD's last picture from gameplay,
/// even though a menu is open.
/// </summary>
internal static class ModifyUiEditor
{
    public static volatile bool IsOpen;

    /// <summary>Index into <see cref="HudLayout.Pieces"/> of the part being placed.</summary>
    public static volatile int Selected;
}
