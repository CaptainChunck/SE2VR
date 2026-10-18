<#
  Test helpers for SE2VR. They drive a real game session without touching the player's own data:
  - The game runs with -appData pointing at an isolated test profile (%LOCALAPPDATA%\SE2VR\testprofile), so saves,
    settings and logs of the real profile are never read or written.
  - It is started directly (SteamAppId environment variable instead of a steam_appid.txt), so no file in the game
    installation is created or changed.
  - Input is sent with SendInput while the game window is in the foreground.
#>

$ErrorActionPreference = 'Stop'
$script:PluginDir = Join-Path $env:LOCALAPPDATA 'SE2VR'
$script:Profile = Join-Path $script:PluginDir 'testprofile'
$script:GameDir = $null

if (-not ('SE2VRInput' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class SE2VRInput
{
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4;

    public static bool Focus(IntPtr hWnd)
    {
        if (GetForegroundWindow() == hWnd) return true;
        // Windows only lets the foreground process change the foreground window; a synthetic Alt tap lifts that lock.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        ShowWindow(hWnd, 9);
        SetForegroundWindow(hWnd);
        Thread.Sleep(200);
        return GetForegroundWindow() == hWnd;
    }

    static INPUT Key(ushort scan, bool up)
    {
        var input = new INPUT { type = 1 };
        input.u.ki.wScan = (ushort)(scan & 0xFF);
        input.u.ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | ((scan & 0xE000) == 0xE000 ? KEYEVENTF_EXTENDEDKEY : 0);
        return input;
    }

    public static void Tap(ushort scan, int holdMs)
    {
        SendInput(1, new[] { Key(scan, false) }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);
        SendInput(1, new[] { Key(scan, true) }, Marshal.SizeOf<INPUT>());
    }

    public static void Press(ushort scan, bool down)
    {
        SendInput(1, new[] { Key(scan, !down) }, Marshal.SizeOf<INPUT>());
    }

    public static void Move(int x, int y)
    {
        SetCursorPos(x, y);
        // A real (relative) move as well, so the game sees mouse input even if it ignores warps.
        var right = new INPUT { type = 0 }; right.u.mi.dx = 1; right.u.mi.dwFlags = MOUSEEVENTF_MOVE;
        var left = new INPUT { type = 0 }; left.u.mi.dx = -1; left.u.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, new[] { right }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
        SendInput(1, new[] { left }, Marshal.SizeOf<INPUT>());
    }

    public static void MouseLook(int dx, int dy)
    {
        // Relative motion in small steps, as a real mouse would send it during mouse look.
        int steps = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)) / 20);
        for (int i = 0; i < steps; i++)
        {
            var input = new INPUT { type = 0 }; input.u.mi.dx = dx / steps; input.u.mi.dy = dy / steps; input.u.mi.dwFlags = MOUSEEVENTF_MOVE;
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
        }
    }

    public static void MouseButton(bool down)
    {
        var input = new INPUT { type = 0 }; input.u.mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        var down = new INPUT { type = 0 }; down.u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        var up = new INPUT { type = 0 }; up.u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }
}
"@
}

# Scan codes (set 1); 0xE0xx = extended key.
$script:ScanCodes = @{
    Escape = 0x01; Tab = 0x0F; Enter = 0x1C; Space = 0x39; Backspace = 0x0E
    F1 = 0x3B; F2 = 0x3C; F3 = 0x3D; F4 = 0x3E; F5 = 0x3F; F6 = 0x40; F7 = 0x41; F8 = 0x42; F9 = 0x43; F10 = 0x44; F11 = 0x57; F12 = 0x58
    Q = 0x10; W = 0x11; E = 0x12; R = 0x13; T = 0x14; Y = 0x15; U = 0x16; I = 0x17; O = 0x18; P = 0x19
    A = 0x1E; S = 0x1F; D = 0x20; F = 0x21; G = 0x22; H = 0x23; J = 0x24; K = 0x25; L = 0x26
    Z = 0x2C; X = 0x2D; C = 0x2E; V = 0x2F; B = 0x30; N = 0x31; M = 0x32
    D1 = 0x02; D2 = 0x03; D3 = 0x04; D4 = 0x05; D5 = 0x06; D6 = 0x07; D7 = 0x08; D8 = 0x09; D9 = 0x0A; D0 = 0x0B
    Home = 0xE047; End = 0xE04F; Up = 0xE048; Down = 0xE050; Left = 0xE04B; Right = 0xE04D
    PageUp = 0xE049; PageDown = 0xE051; Control = 0x1D; Shift = 0x2A
}

function Get-SE2GameDir {
    if ($script:GameDir) { return $script:GameDir }
    $candidates = @()
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        $candidates += Join-Path $steam 'steamapps\common\SpaceEngineers2\Game2'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $candidates += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\SpaceEngineers2\Game2'
            }
        }
    } catch {}
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'SpaceEngineers2.exe')) { $script:GameDir = $c; return $c }
    }
    throw "Space Engineers 2 not found in any Steam library."
}

function Get-SE2Window {
    $p = Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "The game window is not open." }
    return $p.MainWindowHandle
}

function Get-SE2PluginLog { Get-Content (Join-Path $script:PluginDir 'SE2VR.log') -ErrorAction SilentlyContinue }

function Wait-SE2Log {
    <# Waits until the plugin log matches $Pattern; returns $true, or $false on timeout / game exit. #>
    param([Parameter(Mandatory)][string]$Pattern, [int]$TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-SE2PluginLog) -match $Pattern) { return $true }
        if (-not (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)) { return $false }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Start-SE2VRTest {
    <# Starts the game with the plugin in the isolated test profile. -World loads a built-in world directly. #>
    param([string]$World, [switch]$NoPlugin, [switch]$RealHead, [string[]]$ExtraArgs = @(), [hashtable]$Environment = @{})
    if (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) { throw "Space Engineers 2 is already running." }
    if (-not (Get-Process vrserver -ErrorAction SilentlyContinue)) {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamExe
        & $steam -applaunch 250820
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline -and -not (Get-Process vrcompositor -ErrorAction SilentlyContinue)) { Start-Sleep 1 }
        Start-Sleep 5
    }
    New-Item -ItemType Directory -Force $script:Profile | Out-Null
    $gameDir = Get-SE2GameDir
    $arguments = @('-loadScripts', '-nosplash', "-appData:`"$script:Profile`"")
    if (-not $NoPlugin) { $arguments += "-plugins:$(Join-Path $script:PluginDir 'SE2VR.dll')" }
    if ($World) { $arguments += "-startContent:$World" }
    $arguments += $ExtraArgs
    Remove-Item (Join-Path $script:PluginDir 'SE2VR.log') -ErrorAction SilentlyContinue
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $gameDir 'SpaceEngineers2.exe'
    $psi.WorkingDirectory = $gameDir
    $psi.UseShellExecute = $false
    $psi.Arguments = $arguments -join ' '
    # Tells Steamworks the app id without a steam_appid.txt next to the game, so nothing in the install changes.
    $psi.EnvironmentVariables['SteamAppId'] = '1133870'
    $psi.EnvironmentVariables['SteamGameId'] = '1133870'
    # Render for the headset even when nobody wears it (SteamVR then reports frames as not displayed).
    $psi.EnvironmentVariables['SE2VR_FORCE_RENDER'] = '1'
    # An unworn headset lies at a random angle; an ideal straight-ahead head makes captures comparable.
    if (-not $RealHead) { $psi.EnvironmentVariables['SE2VR_FAKE_HEAD'] = '1' }
    foreach ($name in $Environment.Keys) { $psi.EnvironmentVariables[$name] = [string]$Environment[$name] }
    $script:StartTime = Get-Date
    $process = [System.Diagnostics.Process]::Start($psi)
    Write-Host "Started Space Engineers 2 (pid $($process.Id)) with: $($psi.Arguments)"
    return $process
}

function Send-SE2Key {
    <# Taps keys in the game window, e.g. Send-SE2Key Escape, Send-SE2Key Tab -HoldMs 120 #>
    param([Parameter(Mandatory)][string[]]$Keys, [int]$HoldMs = 90, [int]$GapMs = 400)
    $hwnd = Get-SE2Window
    if (-not [SE2VRInput]::Focus($hwnd)) { Write-Warning "Could not bring the game window to the foreground." }
    foreach ($k in $Keys) {
        if (-not $script:ScanCodes.ContainsKey($k)) { throw "Unknown key '$k'." }
        [SE2VRInput]::Tap([uint16]$script:ScanCodes[$k], $HoldMs)
        Start-Sleep -Milliseconds $GapMs
    }
}

function Set-SE2KeyState {
    <# Holds (-Down) or releases a key, for menus that only show while a key is held. #>
    param([Parameter(Mandatory)][string]$Key, [switch]$Down)
    $hwnd = Get-SE2Window
    [void][SE2VRInput]::Focus($hwnd)
    [SE2VRInput]::Press([uint16]$script:ScanCodes[$Key], [bool]$Down)
}

function ConvertTo-SE2ScreenPoint {
    # Game-image pixels (render resolution ImageWidth x ImageHeight) -> screen coordinates of the game window.
    param([int]$X, [int]$Y, [int]$ImageWidth, [int]$ImageHeight)
    $hwnd = Get-SE2Window
    [void][SE2VRInput]::Focus($hwnd)
    $rect = New-Object SE2VRInput+RECT
    [void][SE2VRInput]::GetClientRect($hwnd, [ref]$rect)
    $point = New-Object SE2VRInput+POINT
    $point.X = [int]($X * ($rect.Right - $rect.Left) / $ImageWidth)
    $point.Y = [int]($Y * ($rect.Bottom - $rect.Top) / $ImageHeight)
    [void][SE2VRInput]::ClientToScreen($hwnd, [ref]$point)
    return $point
}

function Send-SE2Click {
    <# Clicks at a position given in game-image pixels (the coordinates seen in a capture of size ImageWidth x ImageHeight). #>
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [int]$ImageWidth, [int]$ImageHeight)
    $point = ConvertTo-SE2ScreenPoint $X $Y $ImageWidth $ImageHeight
    [SE2VRInput]::Click($point.X, $point.Y)
}

function Send-SE2MouseMove {
    <# Moves the mouse to a position given in game-image pixels, like Send-SE2Click without clicking. #>
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y, [int]$ImageWidth, [int]$ImageHeight)
    $point = ConvertTo-SE2ScreenPoint $X $Y $ImageWidth $ImageHeight
    [SE2VRInput]::Move($point.X, $point.Y)
}

function Set-SE2MouseButton {
    <# Holds (-Down) or releases the left mouse button in the game window, e.g. to keep a tool firing. #>
    param([switch]$Down)
    $hwnd = Get-SE2Window
    [void][SE2VRInput]::Focus($hwnd)
    [SE2VRInput]::MouseButton([bool]$Down)
}

function Send-SE2MouseLook {
    <# Turns the character with relative mouse motion (positive DY looks down). #>
    param([int]$DX = 0, [int]$DY = 0)
    $hwnd = Get-SE2Window
    [void][SE2VRInput]::Focus($hwnd)
    [SE2VRInput]::MouseLook($DX, $DY)
}

function Get-SE2Resolution {
    <# Render resolution and VR UI canvas size from the plugin log: @{ Width; Height; CanvasWidth; CanvasHeight }. #>
    $log = @(Get-SE2PluginLog)
    $window = $log | Where-Object { $_ -match 'Forcing a (\d+)x(\d+) window' } | Select-Object -Last 1
    if (-not ($window -match 'Forcing a (\d+)x(\d+) window')) { throw "Render resolution not found in the plugin log." }
    $result = @{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
    $canvas = $log | Where-Object { $_ -match 'UI canvas for VR: (\d+)x(\d+)' } | Select-Object -Last 1
    if ($canvas -match 'UI canvas for VR: (\d+)x(\d+)') { $result.CanvasWidth = [int]$Matches[1]; $result.CanvasHeight = [int]$Matches[2] }
    else { $result.CanvasWidth = $result.Width; $result.CanvasHeight = $result.Height }
    return $result
}

function Invoke-SE2Capture {
    <# Asks the plugin to capture the next VR frame and waits for the files. Returns the captured file paths. #>
    param([Parameter(Mandatory)][string]$Label, [int]$TimeoutSeconds = 30)
    $before = @(Get-SE2PluginLog).Count
    Set-Content -Path (Join-Path $script:PluginDir 'capture') -Value $Label -NoNewline -Encoding ascii
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $new = @(Get-SE2PluginLog | Select-Object -Skip $before)
        if (($new -match 'Simulated headset view .*right_view').Count -ge 1 -or (($new -match 'Captured ').Count -ge 1 -and ($new -match 'no UI panel|Simulated').Count -ge 1)) { break }
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 500
    $new = @(Get-SE2PluginLog | Select-Object -Skip $before)
    $new | Where-Object { $_ -match 'Captured |Simulated headset view' } | ForEach-Object { Write-Host "  $_" }
    return Get-ChildItem (Join-Path $script:PluginDir 'captures') -Filter "*_$Label*.png" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddSeconds(-$TimeoutSeconds - 5) } | Select-Object -ExpandProperty FullName
}

function Stop-SE2VRTest {
    <# Closes the game; kills it if it does not exit (the isolated test profile makes that harmless). #>
    param([int]$TimeoutSeconds = 30)
    $p = Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    if ($p) { [void]$p.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline -and (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)) { Start-Sleep 1 }
    if (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) {
        Write-Warning "Game did not exit in $TimeoutSeconds s; terminating it."
        Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

function Get-SE2TestReport {
    <# Summarizes the plugin log and the test profile's newest game log: errors, assertions, perf lines. #>
    $plugin = @(Get-SE2PluginLog)
    $gameLog = Get-ChildItem (Join-Path $script:Profile 'Temp\Logs') -Filter 'SpaceEngineers2_*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '_(Render12|Stats|Mission)\.log$' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $game = if ($gameLog) { @(Get-Content $gameLog.FullName) } else { @() }
    [pscustomobject]@{
        PluginErrors = @($plugin -match '\[ERROR\]|\[WARN \]')
        Perf = @($plugin -match 'Perf:')
        GameAssertions = @($game -match 'Assertion Failure|Exception' | Select-Object -First 20)
        GameLog = $gameLog.FullName
    }
}

Export-ModuleMember -Function Get-SE2GameDir, Get-SE2Window, Get-SE2PluginLog, Wait-SE2Log, Start-SE2VRTest, Send-SE2Key, Set-SE2KeyState, Send-SE2Click, Send-SE2MouseMove, Set-SE2MouseButton, Send-SE2MouseLook, Get-SE2Resolution, Invoke-SE2Capture, Stop-SE2VRTest, Get-SE2TestReport
