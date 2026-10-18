<#
  Starts Space Engineers 2 with the SE2VR mod; used by the "Space Engineers 2 VR" desktop shortcut.
  The game is started directly, with the SteamAppId environment variable telling it which Steam game it is, so
  Steam's launch options stay untouched and a normal start from Steam is still a normal (non-VR) game.
  Steam has to be running and signed in.
  -GameArguments adds arguments for the game (for example -appData:<folder> for a separate profile).
#>
param([string[]]$GameArguments = @())
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

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

function Test-SteamSignedIn {
    $user = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
    return [bool]$user
}

try {
    $plugin = Join-Path $PSScriptRoot 'SE2VR.dll'
    if (-not (Test-Path $plugin)) { throw "SE2VR.dll is missing from $PSScriptRoot. Run Install.cmd again." }
    if (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue) { throw "Space Engineers 2 is already running." }
    $exe = Find-SpaceEngineers2
    if (-not $exe) { throw "Space Engineers 2 was not found in your Steam libraries." }

    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
        Start-Process (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamExe
    }
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-SteamSignedIn) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (-not (Test-SteamSignedIn)) { throw "Steam is not signed in. Sign in to Steam and try again." }

    $start = New-Object System.Diagnostics.ProcessStartInfo $exe
    $start.WorkingDirectory = Split-Path $exe
    $start.UseShellExecute = $false
    $start.Arguments = (@('-loadScripts', "-plugins:`"$plugin`"") + $GameArguments) -join ' '
    $start.EnvironmentVariables['SteamAppId'] = '1133870'
    $start.EnvironmentVariables['SteamGameId'] = '1133870'
    [System.Diagnostics.Process]::Start($start) | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Space Engineers 2 VR', 'OK', 'Error') | Out-Null
    exit 1
}
