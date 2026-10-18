<#
.SYNOPSIS
  Loads a world in VR (isolated test profile) and captures the headset view a few times, without opening any menus.
  For checking 3D rendering: sky, clouds, temporal effects and stereo consistency between frames.
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>_view.
#>
param(
    [string]$World = 'SurvivalNOFTUE',
    [int]$Captures = 3,
    [int]$IntervalSeconds = 4,
    [switch]$NoBuild,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_view')
New-Item -ItemType Directory -Force $run | Out-Null

$null = Start-SE2VRTest -World $World
if (-not (Wait-SE2Log -Pattern 'First stereo frame' -TimeoutSeconds 300)) {
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
    Stop-SE2VRTest
    throw "The world did not reach stereo rendering; see $run\SE2VR.log"
}
# Wait until the loading screen (an opaque UI) is gone.
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    $check = @(Invoke-SE2Capture -Label 'loading_check')
    $line = @(Get-SE2PluginLog) -match 'Captured .*_loading_check_ui\.png' | Select-Object -Last 1
    $check | Remove-Item -ErrorAction SilentlyContinue
    if ($line -match 'alpha: ([\d.]+)% transparent' -and [double]$Matches[1] -gt 50) { break }
    Start-Sleep -Seconds 5
}
Start-Sleep -Seconds 8 # streaming, temporal effects settle
for ($i = 1; $i -le $Captures; $i++) {
    foreach ($f in @(Invoke-SE2Capture -Label "view$i")) { Move-Item $f $run -Force }
    Start-Sleep -Seconds $IntervalSeconds
}
$report = Get-SE2TestReport
$lines = @("View check $(Get-Date)", "World: $World", "", "Plugin warnings/errors:") + $report.PluginErrors +
         @("", "Game assertions/exceptions:") + $report.GameAssertions + @("", "Performance:") + $report.Perf
$lines | Set-Content (Join-Path $run 'report.txt')
Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
if (-not $KeepRunning) { Stop-SE2VRTest }
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
