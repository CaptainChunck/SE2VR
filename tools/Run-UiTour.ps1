<#
.SYNOPSIS
  Automated VR UI check: loads a built-in world in the isolated test profile, opens every main UI screen with its
  default key, captures what the headset receives (both eyes, the UI panel, and a simulated headset view per eye),
  then writes a report with plugin errors, game assertions and frame timing.

.DESCRIPTION
  Never touches the player's own saves or settings, and changes nothing in the game installation (see
  SE2VRTest.psm1). The headset can stay on the desk: rendering is forced and an ideal head pose is simulated.
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>.
#>
param(
    [string]$World = 'EmptyWorld',
    [switch]$NoBuild,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force $run | Out-Null

$null = Start-SE2VRTest -World $World
if (-not (Wait-SE2Log -Pattern 'First stereo frame' -TimeoutSeconds 300)) {
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
    Stop-SE2VRTest
    throw "The world did not reach stereo rendering; see $run\SE2VR.log"
}
Start-Sleep -Seconds 8 # let streaming settle and the spawn animation finish

# Default keys from Content\MainMenuData\Input\ActionControlMapping.def. Screens that toggle are closed with their
# own key, so a screen that fails to open cannot leave the pause menu open for the following steps.
#   Press = pressed before the capture, Hold = held down during the capture, After = pressed after the capture.
$steps = @(
    @{ Name = 'hud' }
    @{ Name = 'pause_menu';          Press = @('Escape'); After = @('Escape') }
    @{ Name = 'inventory_character'; Press = @('Tab') }
    @{ Name = 'inventory_grid';      Press = @('E') }
    @{ Name = 'inventory_build';     Press = @('E') }
    @{ Name = 'inventory_trade';     Press = @('E') }
    @{ Name = 'inventory_map';       Press = @('E');      After = @('Escape') }
    @{ Name = 'character_screen';    Press = @('K');      After = @('K') }
    @{ Name = 'toolbar_config';      Press = @('G');      After = @('G') }
    @{ Name = 'map_screen';          Press = @('M');      After = @('Escape') }
    @{ Name = 'help';                Press = @('F1');     After = @('Escape') }
    @{ Name = 'quick_menu';          Hold = 'R' }
    @{ Name = 'build_block';         Press = @('D1');     After = @('D0') }
    @{ Name = 'third_person';        Press = @('V');      After = @('V') }
    @{ Name = 'hud_hidden';          Press = @('U');      After = @('U') }
)
$summary = New-Object System.Collections.Generic.List[string]
foreach ($step in $steps) {
    if ($step.Press) { Send-SE2Key $step.Press; Start-Sleep -Seconds 2 }
    if ($step.Hold) { Set-SE2KeyState $step.Hold -Down; Start-Sleep -Milliseconds 1500 }
    $files = @(Invoke-SE2Capture -Label $step.Name)
    if ($step.Hold) { Set-SE2KeyState $step.Hold; Start-Sleep -Milliseconds 800 }
    foreach ($f in $files) { Copy-Item $f $run }
    $uiLine = @(Get-SE2PluginLog) -match "Captured .*_$($step.Name)_ui\.png" | Select-Object -Last 1
    $alpha = if ($uiLine -match 'alpha: (.*)\)$') { $Matches[1] } else { 'no UI image' }
    $summary.Add("$($step.Name): $($files.Count) files; UI $alpha")
    if ($step.After) { Send-SE2Key $step.After; Start-Sleep -Milliseconds 1500 }
}

$report = Get-SE2TestReport
$lines = @("UI tour $(Get-Date)", "World: $World", "") + $summary + @("", "Plugin warnings/errors:") + $report.PluginErrors +
         @("", "Game assertions/exceptions:") + $report.GameAssertions + @("", "Performance:") + $report.Perf
$lines | Set-Content (Join-Path $run 'report.txt')
Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
if ($report.GameLog) { Copy-Item $report.GameLog $run }
if (-not $KeepRunning) { Stop-SE2VRTest }
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
