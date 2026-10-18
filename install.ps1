<#
.SYNOPSIS
  Builds SE2VR and installs it to %LOCALAPPDATA%\SE2VR.

.DESCRIPTION
  The install folder has no spaces in its path, so the Steam launch option needs no quoting:
      -plugins:C:\Users\<you>\AppData\Local\SE2VR\SE2VR.dll
  An existing SE2VR.json in the install folder is kept, so your settings survive updates.

.PARAMETER Launch
  Start Space Engineers 2 through Steam with the plugin after installing.

.PARAMETER GameDir
  The game's Game2 folder, if Space Engineers 2 is not in the default Steam library.
#>
param(
    [switch]$Launch,
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'

$buildArgs = @("build", (Join-Path $PSScriptRoot 'src\SE2VR\SE2VR.csproj'), '-c', 'Release', '-nologo', '-v:minimal')
if ($GameDir) { $buildArgs += "/p:SE2GameDir=$GameDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$source = Join-Path $PSScriptRoot 'build\Release'
$destination = Join-Path $env:LOCALAPPDATA 'SE2VR'
New-Item -ItemType Directory -Force $destination | Out-Null
Get-ChildItem $source -File | Where-Object { $_.Name -ne 'SE2VR.json' } | Copy-Item -Destination $destination -Force
if (-not (Test-Path (Join-Path $destination 'SE2VR.json'))) {
    Copy-Item (Join-Path $source 'SE2VR.json') $destination
}

$dll = Join-Path $destination 'SE2VR.dll'
Write-Host ""
Write-Host "Installed to $destination"
Write-Host "Steam > Space Engineers 2 > Properties > General > Launch Options:"
Write-Host "    -plugins:$dll" -ForegroundColor Cyan
Write-Host "Log file: $(Join-Path $destination 'SE2VR.log')"

if ($Launch) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamExe
    Write-Host "Starting Space Engineers 2 through Steam..."
    & $steam -applaunch 1133870 "-plugins:$dll"
}
