using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HarmonyLib;
using Keen.Game2.Client.UI.Library.Controls.ContentControls;
using Keen.Game2.Client.UI.Menu.Options;
using AccessText = Avalonia.Controls.Primitives.AccessText;
using Carousel = Keen.Game2.Client.UI.Library.Controls.ContentControls.Carousel;

namespace SE2VR.UI;

/// <summary>
/// Adds a "VR" tab to the game's Settings screen with every VR setting, applied as soon as it changes (and saved to
/// SE2VR.json), plus buttons to open the Modify UI editor, recenter the view and restore the defaults.
/// <para>
/// The rows are built in code from the game's own controls (DescriptionControl, SliderControl, Switch, Carousel), so
/// they pick up the game's styles. The game creates a new Settings screen every time it opens; each gets a new tab.
/// </para>
/// Runs on the game's UI (main) thread; the renderer reads the plain VRConfig fields on its own thread.
/// </summary>
[HarmonyPatch(typeof(OptionsScreen), MethodType.Constructor)]
internal static class VrSettingsTab
{
    private const double ValueWidth = 516;

    private static void Postfix(OptionsScreen __instance)
    {
        try
        {
            if (VRSystem.Xr == null)
            {
                return; // no headset this session: nothing to set
            }
            var page = new Page { Screen = __instance };
            var tab = new TabItem { Header = "VR", Content = Build(page) };
            page.Tab = tab;
            __instance.PART_TabControl.Items.Add(tab);
            AddRestoreButton(__instance, tab);
            // The headset shows the HUD parts only while the Modify UI section is on screen.
            tab.PropertyChanged += (_, e) =>
            {
                if (e.Property == TabItem.IsSelectedProperty)
                {
                    page.UpdateEditorState();
                }
            };
        }
        catch (Exception e)
        {
            Log.Error("Could not add the VR tab to the Settings screen.", e);
        }
    }

    private sealed class Page
    {
        public required OptionsScreen Screen { get; init; }
        public List<Action> Refreshers { get; } = new();
        public StackPanel Editor { get; } = new() { IsVisible = false };
        public TabItem? Tab { get; set; }

        /// <summary>The editor counts as open while its section is shown on the selected VR tab.</summary>
        public void UpdateEditorState() => ModifyUiEditor.IsOpen = Editor.IsVisible && Tab is { IsSelected: true };
    }

    private static Control Build(Page page)
    {
        OptionsScreen screen = page.Screen;
        var rows = new StackPanel { Margin = new Thickness(0, 0, 11, 0) };

        StackPanel editor = page.Editor;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8), Spacing = 16 };
        buttons.Children.Add(MenuButton("MODIFY UI", "Take parts of the HUD off the menu screen and place them around your view, where they follow your head. " +
                                                     "While it is open the headset shows every part that follows your head in a frame (the chosen part in blue), " +
                                                     "with a picture of your HUD from the last moment you played, and every change shows right away.", page, () =>
        {
            editor.IsVisible = !editor.IsVisible;
            page.UpdateEditorState();
        }));
        buttons.Children.Add(MenuButton("RECENTER VIEW", "Makes the direction you are looking the new forward (same as the Home key).", page, () => VRSystem.Xr?.RequestRecenter(0)));
        rows.Children.Add(buttons);
        BuildEditor(editor, page);
        rows.Children.Add(editor);
        screen.DetachedFromVisualTree += (_, _) => ModifyUiEditor.IsOpen = false;

        Header(rows, "Headset picture");
        Slider(rows, page, "Render resolution, % (restart)", "Resolution of each eye compared to what SteamVR recommends. Takes effect the next time the game starts.",
            40, 150, () => VRConfig.Current.RenderScale * 100, v => VRConfig.Current.RenderScale = (float)(v / 100));
        Toggle(rows, page, "Dynamic resolution", "Lowers the picture's resolution a little when a frame would be late, so the frame rate stays steady instead of dropping.",
            () => VRConfig.Current.DynamicResolution, v => VRConfig.Current.DynamicResolution = v);
        Slider(rows, page, "Lowest dynamic resolution, %", "The lowest resolution dynamic resolution may go down to, as a share of the eye image.",
            30, 100, () => VRConfig.Current.DynamicResolutionMin * 100, v => VRConfig.Current.DynamicResolutionMin = (float)(v / 100));
        Choice(rows, page, "Frame rate target", "Auto aims for the headset's full refresh rate when the graphics card nearly makes it, otherwise for half (45 fps on a 90 Hz headset, with SteamVR filling in).",
            new[] { "Auto", "Full", "Half" }, () => VRConfig.Current.DynamicResolutionTarget, v => VRConfig.Current.DynamicResolutionTarget = v);
        Choice(rows, page, "Foveated rendering", "Shades the edges of the view at a lower rate, where the lenses blur anyway. Saves graphics time on cards with variable rate shading (NVIDIA RTX / GTX 16, AMD RX 6000 or newer).",
            new[] { "Off", "Quality", "Balanced", "Performance" }, () => VRConfig.Current.Foveation, v => VRConfig.Current.Foveation = v);
        Toggle(rows, page, "FSR in VR", "Keeps the game's FSR anti-aliasing and upscaling in VR, with a separate history for each eye. Off uses FXAA, which shimmers when you move your head.",
            () => VRConfig.Current.FsrInVR, v => VRConfig.Current.FsrInVR = v);
        Toggle(rows, page, "Ray tracing in VR", "Ray tracing costs about as much again as the rest of a frame for each eye and needs a lot of video memory. Your normal setting is kept for the monitor.",
            () => VRConfig.Current.RaytracingInVR, v => VRConfig.Current.RaytracingInVR = v);
        Toggle(rows, page, "VR post-processing", "No lens dirt (it is glued to the screen and moves with your head), and lighter bloom.",
            () => VRConfig.Current.VrPostEffects, v => VRConfig.Current.VrPostEffects = v);
        Toggle(rows, page, "Lens flares in VR", "Lens flares and lamp glare are flat screen effects that look wrong in a headset.",
            () => VRConfig.Current.LensFlaresInVR, v => VRConfig.Current.LensFlaresInVR = v);
        Toggle(rows, page, "Share work between eyes", "Does shadow, reflection and exposure updates once per frame instead of once per eye. Turn off only if one eye shows a glitch.",
            () => VRConfig.Current.ShareWorkBetweenEyes, v => VRConfig.Current.ShareWorkBetweenEyes = v);
        Slider(rows, page, "World scale, %", "Above 100% the world feels smaller; below, bigger. 100% is real size.",
            50, 200, () => VRConfig.Current.WorldScale * 100, v => VRConfig.Current.WorldScale = (float)(v / 100));

        Header(rows, "Menu screen");
        Slider(rows, page, "Screen size, degrees", "How much of your view the menu screen covers. Bigger makes text easier to read. Also Ctrl+PageUp / Ctrl+PageDown in the game.",
            30, 150, () => VRConfig.Current.UiWidthDegrees, v => VRConfig.Current.UiWidthDegrees = (float)v);
        Slider(rows, page, "Screen distance, cm", "How far away the menu screen floats.",
            50, 500, () => VRConfig.Current.UiDistance * 100, v => VRConfig.Current.UiDistance = (float)(v / 100));
        Toggle(rows, page, "Curved screen", "Curves the screen around you, if your VR runtime supports it (SteamVR does not; the screen stays flat there).",
            () => VRConfig.Current.UiCurved, v => VRConfig.Current.UiCurved = v);
        Toggle(rows, page, "Mouse pointer", "Shows the mouse pointer on the menu screen.",
            () => VRConfig.Current.ShowCursor, v => VRConfig.Current.ShowCursor = v);

        _pages.Add(new WeakReference<Page>(page));
        return new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static readonly List<WeakReference<Page>> _pages = new();

    /// <summary>The Modify UI section: pick a HUD piece, let it follow your head, and place it.</summary>
    private static void BuildEditor(StackPanel editor, Page page)
    {
        Header(editor, "Modify UI");
        HudLayout.Piece Selected() => HudLayout.Pieces[Math.Clamp(ModifyUiEditor.Selected, 0, HudLayout.Pieces.Length - 1)];
        HudPieceSettings Current() => HudLayout.SettingsFor(Selected());
        // A part only moves in the headset while it follows your head: placing one switches that on, so it shows at once.
        void Place(Action<HudPieceSettings> change)
        {
            HudPieceSettings settings = Current();
            change(settings);
            if (!settings.FollowHead)
            {
                settings.FollowHead = true;
                Dispatch(RefreshAll);
            }
        }
        string[] names = Array.ConvertAll(HudLayout.Pieces, p => p.Name);
        Choice(editor, page, "Part of the HUD", "The part of the HUD to place; its frame is the blue one in the headset. Each part can stay on the menu screen or follow your head.",
            names, () => Selected().Name, name =>
            {
                ModifyUiEditor.Selected = Math.Max(0, Array.IndexOf(names, name));
                Dispatch(RefreshAll); // show the newly picked part's values
            }, save: false);
        Toggle(editor, page, "Follows your head", "Takes this part off the menu screen and keeps it in your view wherever you look. Moving the part switches this on.",
            () => Current().FollowHead, v => Current().FollowHead = v);
        Slider(editor, page, "Left / right, degrees", "Where the part sits in your view: to the right (positive) or left (negative) of the middle.",
            -70, 70, () => Current().LeftRight, v => Place(s => s.LeftRight = (float)v));
        Slider(editor, page, "Up / down, degrees", "Where the part sits in your view: above (positive) or below (negative) the middle.",
            -60, 60, () => Current().UpDown, v => Place(s => s.UpDown = (float)v));
        Slider(editor, page, "Distance, cm", "How far from your eyes the part floats. Parts at the same distance sit on the same plane.",
            30, 300, () => Current().Distance * 100, v => Place(s => s.Distance = (float)(v / 100)));
        Slider(editor, page, "Size, %", "100% is the size it has on the menu screen.",
            50, 300, () => Current().Size * 100, v => Place(s => s.Size = (float)(v / 100)));
        var reset = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
        reset.Children.Add(MenuButton("RESET THIS PART", "Puts the chosen part back where it starts, on the menu screen.", page, () =>
        {
            HudLayout.Reset(Selected());
            RefreshAll();
            Changed();
        }));
        editor.Children.Add(reset);
    }

    private static void Dispatch(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    /// <summary>Puts the controls of every open VR tab back in line with VRConfig (after RESTORE or a hotkey change).</summary>
    public static void RefreshAll()
    {
        foreach (WeakReference<Page> reference in _pages)
        {
            if (reference.TryGetTarget(out Page? page))
            {
                page.Refreshers.ForEach(refresh => refresh());
            }
        }
        _pages.RemoveAll(reference => !reference.TryGetTarget(out _));
    }

    private static void Header(StackPanel rows, string text)
    {
        var header = new TextBlock { Text = text.ToUpperInvariant(), Margin = new Thickness(16, 20, 16, 4), HorizontalAlignment = HorizontalAlignment.Left };
        header.Classes.Add("OptionSubTitle");
        header.Opacity = 0.7;
        rows.Children.Add(header);
    }

    /// <summary>One settings row like the game's: label on the left, value control on the right, help text when pointed at.</summary>
    private static void Row(StackPanel rows, Page page, string label, string help, Control value)
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(15)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ValueWidth)));
        var text = new TextBlock
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0)
        };
        text.Classes.Add("OptionSubTitle");
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        var row = new DescriptionControl { Content = grid, Focusable = true };
        // The screen's own handlers clear the help text first (no DescriptionKey); then ours writes it.
        void Show(object? sender, EventArgs e) => page.Screen.PART_SettingsDescriptionTextBlock.Text = help;
        row.PointerEntered += Show;
        row.GotFocus += Show;
        rows.Children.Add(row);
    }

    private static void Slider(StackPanel rows, Page page, string label, string help, double min, double max, Func<double> get, Action<double> set)
    {
        var slider = new SliderControl { Minimum = min, Maximum = max, Rounding = 0, FormatString = "F0" };
        slider.Classes.Add("OptionSubTitle");
        slider.Value = Math.Round(Math.Clamp(get(), min, max));
        bool refreshing = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == SliderControl.ValueProperty && !refreshing)
            {
                set(slider.Value);
                Changed();
            }
        };
        page.Refreshers.Add(() =>
        {
            refreshing = true;
            slider.Value = Math.Round(Math.Clamp(get(), min, max));
            refreshing = false;
        });
        Row(rows, page, label, help, slider);
    }

    private static void Toggle(StackPanel rows, Page page, string label, string help, Func<bool> get, Action<bool> set)
    {
        var toggle = new Switch { IsChecked = get(), HorizontalAlignment = HorizontalAlignment.Left };
        bool refreshing = false;
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == Switch.IsCheckedProperty && !refreshing)
            {
                set(toggle.IsChecked);
                Changed();
            }
        };
        page.Refreshers.Add(() =>
        {
            refreshing = true;
            toggle.IsChecked = get();
            refreshing = false;
        });
        Row(rows, page, label, help, toggle);
    }

    private static void Choice(StackPanel rows, Page page, string label, string help, string[] options, Func<string> get, Action<string> set, bool save = true)
    {
        var carousel = new Carousel { ItemsSource = options };
        carousel.Classes.Add("OptionSubTitle");
        int Index() => Math.Max(0, Array.FindIndex(options, o => string.Equals(o, get(), StringComparison.OrdinalIgnoreCase)));
        carousel.SelectedIndex = Index();
        bool refreshing = false;
        carousel.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty && !refreshing && carousel.SelectedIndex >= 0)
            {
                set(options[carousel.SelectedIndex]);
                if (save)
                {
                    Changed();
                }
            }
        };
        page.Refreshers.Add(() =>
        {
            refreshing = true;
            carousel.SelectedIndex = Index();
            refreshing = false;
        });
        Row(rows, page, label, help, carousel);
    }

    private static Button MenuButton(string text, string help, Page page, Action click)
    {
        var button = new Button { Content = new AccessText { Text = text }, HorizontalAlignment = HorizontalAlignment.Center };
        button.Classes.Add("Menu");
        button.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (Exception e)
            {
                Log.Error($"VR settings button '{text}' failed.", e);
            }
        };
        button.PointerEntered += (_, _) => page.Screen.PART_SettingsDescriptionTextBlock.Text = help;
        return button;
    }

    /// <summary>The game's own bottom buttons (Apply, Reset) sit on the screen's canvas; RESTORE goes there while the VR tab is open.</summary>
    private static void AddRestoreButton(OptionsScreen screen, TabItem tab)
    {
        if (screen.PART_TabControl.Parent is not Canvas canvas)
        {
            return;
        }
        var restore = new Button { Content = new AccessText { Text = "RESTORE" }, IsVisible = false };
        restore.Classes.Add("Menu");
        Canvas.SetBottom(restore, 56);
        Canvas.SetLeft(restore, 769);
        restore.Click += (_, _) =>
        {
            VRConfig.RestoreDefaults();
            RefreshAll();
            Changed();
            Log.Info("VR settings restored to their defaults.");
        };
        tab.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty)
            {
                restore.IsVisible = tab.IsSelected;
            }
        };
        canvas.Children.Add(restore);
    }

    private static void Changed() => VRConfig.SaveSoon();
}
