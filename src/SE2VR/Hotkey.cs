using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SE2VR;

/// <summary>
/// A keyboard shortcut such as "Home" or "Ctrl+Shift+F9", polled once per frame from the render thread.
/// Fires once per press, and only while the game window is in the foreground.
/// </summary>
internal sealed class Hotkey
{
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;

    private static readonly Dictionary<string, int> _names = BuildNames();

    private readonly int _key;
    private readonly bool _ctrl, _shift, _alt;
    private bool _wasDown;

    public string Text { get; }

    private Hotkey(string text, int key, bool ctrl, bool shift, bool alt)
    {
        Text = text;
        _key = key;
        _ctrl = ctrl;
        _shift = shift;
        _alt = alt;
    }

    /// <summary>Parses "Ctrl+Shift+F9"-style text. Returns null (with a log warning) for empty or unknown keys.</summary>
    public static Hotkey? Parse(string? text, string purpose)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        bool ctrl = false, shift = false, alt = false;
        int key = 0;
        foreach (string rawPart in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    ctrl = true;
                    break;
                case "SHIFT":
                    shift = true;
                    break;
                case "ALT":
                    alt = true;
                    break;
                default:
                    if (!_names.TryGetValue(part, out key))
                    {
                        Log.Warn($"Unknown key '{rawPart}' in {purpose} hotkey '{text}'; hotkey disabled.");
                        return null;
                    }
                    break;
            }
        }
        if (key == 0)
        {
            Log.Warn($"No main key in {purpose} hotkey '{text}'; hotkey disabled.");
            return null;
        }
        return new Hotkey(text, key, ctrl, shift, alt);
    }

    public bool WasPressed()
    {
        bool down = IsDown(_key) && IsDown(VkControl) == _ctrl && IsDown(VkShift) == _shift && IsDown(VkMenu) == _alt;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        return pressed && GameHasFocus();
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool GameHasFocus()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static Dictionary<string, int> BuildNames()
    {
        var names = new Dictionary<string, int>
        {
            ["HOME"] = 0x24, ["END"] = 0x23, ["INSERT"] = 0x2D, ["DELETE"] = 0x2E,
            ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["PAUSE"] = 0x13, ["SCROLLLOCK"] = 0x91,
            ["SPACE"] = 0x20, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["BACKSPACE"] = 0x08,
            ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
        };
        for (int i = 1; i <= 24; i++)
        {
            names[$"F{i}"] = 0x70 + i - 1;
        }
        for (int i = 0; i <= 9; i++)
        {
            names[$"{i}"] = 0x30 + i;
            names[$"NUMPAD{i}"] = 0x60 + i;
        }
        for (char c = 'A'; c <= 'Z'; c++)
        {
            names[c.ToString()] = c;
        }
        return names;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
