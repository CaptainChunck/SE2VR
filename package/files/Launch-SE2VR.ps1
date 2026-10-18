<#
  Starts Space Engineers 2 with the SE2VR mod; used by the "Space Engineers 2 VR" desktop shortcut.
  The game is started directly, with the SteamAppId environment variable telling it which Steam game it is, so
  Steam's launch options stay untouched and a normal start from Steam is still a normal (non-VR) game.
  Steam has to be running and signed in.
  -GameArguments adds arguments for the game (for example -appData:<folder> for a separate profile).
  Every start is logged to launcher.log next to this script.
#>
param([string[]]$GameArguments = @())
$ErrorActionPreference = 'Stop'
$logPath = Join-Path $PSScriptRoot 'launcher.log'

function Write-LaunchLog([string]$Message) {
    try { Add-Content -Path $logPath -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message) -Encoding UTF8 } catch {}
}

function Show-LaunchError([string]$Message) {
    Write-LaunchLog "Error: $Message"
    try {
        Add-Type -AssemblyName System.Windows.Forms
        # This script has no window of its own; a top-most owner keeps the message in front of everything else.
        $owner = New-Object System.Windows.Forms.Form
        $owner.TopMost = $true
        [void][System.Windows.Forms.MessageBox]::Show($owner, $Message, 'Space Engineers 2 VR',
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
        $owner.Dispose()
    } catch {}
}

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

function Test-SteamVRIsActiveRuntime {
    $runtime = (Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -ErrorAction SilentlyContinue).ActiveRuntime
    return [bool]($runtime -and $runtime -match 'steamvr')
}

function Test-SteamSignedIn {
    $user = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
    return [bool]$user
}

try {
    if ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt 256KB) { Remove-Item $logPath -ErrorAction SilentlyContinue }
    Write-LaunchLog '---- Space Engineers 2 VR launcher'
    $plugin = Join-Path $PSScriptRoot 'SE2VR.dll'
    if (-not (Test-Path $plugin)) { throw "SE2VR.dll is missing from $PSScriptRoot. Run Install.cmd again." }
    $running = @(Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw "Space Engineers 2 is already running (process $($running[0].Id)). Close it first; if it is not visible, end it in Task Manager."
    }
    $exe = Find-SpaceEngineers2
    if (-not $exe) { throw "Space Engineers 2 was not found in your Steam libraries." }
    Write-LaunchLog "Game: $exe"

    if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
        Write-LaunchLog 'Starting Steam'
        Start-Process (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamExe
    }
    $deadline = (Get-Date).AddMinutes(2)
    while (-not (Test-SteamSignedIn) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (-not (Test-SteamSignedIn)) { throw "Steam is not signed in. Sign in to Steam and try again." }

    # The mod waits only briefly for the headset at start-up, so bring SteamVR up first if it is the VR runtime.
    if ((Test-SteamVRIsActiveRuntime) -and -not (Get-Process vrcompositor -ErrorAction SilentlyContinue)) {
        Write-LaunchLog 'Starting SteamVR'
        Start-Process 'steam://rungameid/250820'
        $deadline = (Get-Date).AddSeconds(90)
        while (-not (Get-Process vrcompositor -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
        Start-Sleep -Seconds 5 # let it find the headset
    }

    $start = New-Object System.Diagnostics.ProcessStartInfo $exe
    $start.WorkingDirectory = Split-Path $exe
    $start.UseShellExecute = $false
    $start.Arguments = (@('-loadScripts', "-plugins:`"$plugin`"") + $GameArguments) -join ' '
    $start.EnvironmentVariables['SteamAppId'] = '1133870'
    $start.EnvironmentVariables['SteamGameId'] = '1133870'
    $process = [System.Diagnostics.Process]::Start($start)
    Write-LaunchLog "Started the game (process $($process.Id)) with: $($start.Arguments)"

    # The game restarts itself once while starting, so look for any game process a little later.
    Start-Sleep -Seconds 20
    $running = @(Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        $code = if ($process.HasExited) { $process.ExitCode } else { 'unknown' }
        throw "Space Engineers 2 closed right after starting (exit code $code).`n`nThe game's own logs are in %APPDATA%\SpaceEngineers2\Temp\Logs, the mod's log is $PSScriptRoot\SE2VR.log."
    }
    Write-LaunchLog "The game is running (process $(($running | ForEach-Object Id) -join ', '))."
}
catch {
    Show-LaunchError $_.Exception.Message
    exit 1
}
