<#
  Removes SE2VR (run it through Uninstall.cmd): the %LOCALAPPDATA%\SE2VR folder and the desktop shortcut.
  Your Space Engineers 2 saves and settings are not touched.
#>
$ErrorActionPreference = 'Stop'
$folder = Join-Path $env:LOCALAPPDATA 'SE2VR'
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Space Engineers 2 VR.lnk'

if (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) {
    Write-Host "Space Engineers 2 is running. Close it, then run Uninstall.cmd again." -ForegroundColor Yellow
    exit 1
}
Write-Host "This removes $folder (the mod, its settings, logs and captures) and the desktop shortcut."
$answer = Read-Host "Continue? (y/n)"
if ($answer -notmatch '^(y|yes)$') {
    Write-Host "Nothing was removed."
    exit 0
}
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
Write-Host "SE2VR was removed."
Write-Host "If you added '-plugins:...' to the Steam launch options of Space Engineers 2, remove it there too:"
Write-Host "  Steam > Space Engineers 2 > Properties > General > Launch Options"
