<#
.SYNOPSIS
  Checks particle effects in VR: loads the Creative world (isolated test profile), looks at the floor, uses the first
  three toolbar tools on it (welder/grinder/drill sparks and dust) and flies with the jetpack (thruster flames), and
  captures both eyes during each. Particles are simulated once per frame and drawn per eye, so both eyes must show the
  same effects at the same places (apart from the eye offset).
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>_particles.
#>
param(
    [string]$World = 'CRF',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_particles')
New-Item -ItemType Directory -Force $run | Out-Null

$null = Start-SE2VRTest -World $World
try {
    if (-not (Wait-SE2Log -Pattern 'First stereo frame' -TimeoutSeconds 300)) {
        throw "The world did not reach stereo rendering."
    }
    Start-Sleep -Seconds 35 # loading screen, streaming
    function Capture([string]$Label) {
        foreach ($f in @(Invoke-SE2Capture -Label $Label)) { Move-Item $f $run -Force }
    }
    Capture 'before'
    Send-SE2MouseLook -DY 450 # look down at the floor in front
    Start-Sleep -Seconds 1
    foreach ($slot in 'D1', 'D2', 'D3') {
        Send-SE2Key $slot
        Start-Sleep -Seconds 1
        Set-SE2MouseButton -Down
        Start-Sleep -Seconds 2
        Capture "tool_$slot"
        Set-SE2MouseButton
        Start-Sleep -Seconds 1
    }
    Send-SE2MouseLook -DY -450
    Send-SE2Key 'X' # jetpack on
    Start-Sleep -Seconds 1
    Set-SE2KeyState -Key Space -Down
    Start-Sleep -Milliseconds 1500
    Capture 'jetpack_up'
    Set-SE2KeyState -Key Space
    Set-SE2KeyState -Key W -Down
    Start-Sleep -Milliseconds 1200
    Capture 'jetpack_forward'
    Set-SE2KeyState -Key W
}
finally {
    $report = Get-SE2TestReport
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
    Stop-SE2VRTest
}
$lines = @("Particle check $(Get-Date)", "World: $World", "", "Plugin warnings/errors:") + $report.PluginErrors +
         @("", "Game assertions/exceptions:") + $report.GameAssertions + @("", "Performance:") + $report.Perf
$lines | Set-Content (Join-Path $run 'report.txt')
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
