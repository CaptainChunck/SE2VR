<#
  Installs SE2VR for the current Windows user (run it through Install.cmd):
  - copies the mod to %LOCALAPPDATA%\SE2VR, keeping an existing SE2VR.json with your settings,
  - creates a "Space Engineers 2 VR" shortcut on the desktop,
  - copies the Steam launch option to the clipboard, for those who prefer starting from Steam.
  Nothing in the Space Engineers 2 installation is changed.
  -NoClipboard leaves the clipboard alone (used by automated tests).
#>
param([switch]$NoClipboard)
$ErrorActionPreference = 'Stop'

function Find-SpaceEngineers2 {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam -replace '/', '\')
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($match in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libraries += $match.Groups[1].Value -replace '\\\\', '\'
        }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        $exe = Join-Path $library 'steamapps\common\SpaceEngineers2\Game2\SpaceEngineers2.exe'
        if (Test-Path $exe) { return $exe }
    }
    return $null
}

$source = $PSScriptRoot
$destination = Join-Path $env:LOCALAPPDATA 'SE2VR'
Write-Host "SE2VR - VR for Space Engineers 2" -ForegroundColor Cyan
Write-Host ""

if (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) {
    Write-Host "Space Engineers 2 is running. Close it, then run Install.cmd again." -ForegroundColor Yellow
    exit 1
}
$gameExe = Find-SpaceEngineers2
if (-not $gameExe) {
    Write-Host "Warning: Space Engineers 2 was not found in your Steam libraries. Installing anyway." -ForegroundColor Yellow
}

New-Item -ItemType Directory -Force $destination | Out-Null
# Files extracted from a downloaded zip carry Windows' "from the internet" mark; clear it.
Get-ChildItem $source -File | Unblock-File
foreach ($file in Get-ChildItem $source -File) {
    if ($file.Name -eq 'Install-SE2VR.ps1') { continue }
    $target = Join-Path $destination $file.Name
    if ($file.Name -eq 'SE2VR.json' -and (Test-Path $target)) {
        Write-Host "Kept your settings: $target"
        continue
    }
    Copy-Item $file.FullName $target -Force
}
Write-Host "Installed to $destination"

# Older versions started the game through a PowerShell script; the launcher program replaces it.
Remove-Item (Join-Path $destination 'Launch-SE2VR.ps1') -ErrorAction SilentlyContinue

# Desktop shortcut: starts the game in VR without changing Steam's launch options.
$launcher = Join-Path $destination 'SE2VR Launcher.exe'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Space Engineers 2 VR.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $launcher
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $destination
$shortcut.Description = 'Space Engineers 2 in VR (SE2VR mod)'
if ($gameExe) { $shortcut.IconLocation = "$gameExe,0" }
$shortcut.Save()
Write-Host "Created the desktop shortcut 'Space Engineers 2 VR'"

$dll = Join-Path $destination 'SE2VR.dll'
$launchOption = if ($dll.Contains(' ')) { "-plugins:`"$dll`"" } else { "-plugins:$dll" }
$copied = $false
if (-not $NoClipboard) {
    try { Set-Clipboard -Value $launchOption; $copied = $true } catch { }
}

Write-Host ""
Write-Host "How to play" -ForegroundColor Cyan
Write-Host "  1. Start SteamVR with your headset connected (Steam must be running too)."
Write-Host "  2. Double-click 'Space Engineers 2 VR' on your desktop."
Write-Host ""
Write-Host "  Or start from Steam instead: Steam > Space Engineers 2 > Properties > General > Launch Options"
Write-Host "  and paste$(if ($copied) { ' (already in your clipboard)' }):"
Write-Host "      $launchOption" -ForegroundColor Green
Write-Host "  With the launch option, every start from Steam is in VR; remove it to play on the monitor."
Write-Host ""
Write-Host "In game: Home recenters the view. Settings: $destination\SE2VR.json   Log: $destination\SE2VR.log"
