<#
.SYNOPSIS
  Automated VR check of the main menu: starts the game to the main menu in the isolated test profile, clicks through
  its screens (settings tabs, new game, load, workshop, credits) and captures what the headset receives for each.

.DESCRIPTION
  Same isolation as Run-UiTour.ps1: nothing in the player's profile or the game installation is touched.
  Click positions are fractions of the UI canvas (the 16:9 area the game lays its UI out on in VR), measured on the
  2.4 main menu; a game update that moves buttons needs new positions here.
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>_menu.
#>
param(
    [switch]$NoBuild,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_menu')
New-Item -ItemType Directory -Force $run | Out-Null

$null = Start-SE2VRTest
if (-not (Wait-SE2Log -Pattern 'Showing the menu on a floating panel' -TimeoutSeconds 300)) {
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
    Stop-SE2VRTest
    throw "The main menu was not reached; see $run\SE2VR.log"
}
Start-Sleep -Seconds 10 # intro animations, background loading
$resolution = Get-SE2Resolution

function Click-Canvas([double]$U, [double]$V) {
    $x = [int]($U * $resolution.CanvasWidth); $y = [int]($V * $resolution.CanvasHeight)
    Send-SE2MouseMove -X $x -Y $y -ImageWidth $resolution.Width -ImageHeight $resolution.Height
    Start-Sleep -Milliseconds 400
    Send-SE2Click -X $x -Y $y -ImageWidth $resolution.Width -ImageHeight $resolution.Height
}

# Click = canvas position to click before the capture; Press = keys before the capture; After = keys afterwards.
$steps = @(
    @{ Name = 'startup_screen' }                                       # "What's new" disclaimer on a fresh profile
    @{ Name = 'main_menu';         Press = @('Escape') }
    @{ Name = 'settings_game';     Click = @(0.1439, 0.7676) }
    @{ Name = 'settings_gui';      Click = @(0.2002, 0.1386) }
    @{ Name = 'settings_display';  Click = @(0.3141, 0.1386) }
    @{ Name = 'settings_graphics'; Click = @(0.4281, 0.1386) }
    @{ Name = 'settings_audio';    Click = @(0.5414, 0.1386) }
    @{ Name = 'settings_controls'; Click = @(0.6553, 0.1386); After = @('Escape') }
    @{ Name = 'new_game';          Click = @(0.1439, 0.4147); After = @('Escape') }
    @{ Name = 'load';              Click = @(0.1439, 0.4851); After = @('Escape') }
    @{ Name = 'workshop';          Click = @(0.1439, 0.6258); After = @('Escape') }
    @{ Name = 'credits';           Click = @(0.1439, 0.8369); After = @('Escape') }
    @{ Name = 'main_menu_again' }
)
$summary = New-Object System.Collections.Generic.List[string]
foreach ($step in $steps) {
    if ($step.Press) { Send-SE2Key $step.Press; Start-Sleep -Seconds 2 }
    if ($step.Click) { Click-Canvas $step.Click[0] $step.Click[1]; Start-Sleep -Seconds 3 }
    $files = @(Invoke-SE2Capture -Label $step.Name)
    foreach ($f in $files) { Move-Item $f $run -Force }
    $log = @(Get-SE2PluginLog)
    $viewLine = $log -match "Simulated headset view .*_$($step.Name)_left_view\.png" | Select-Object -Last 1
    $layers = if ($viewLine -match 'left_view\.png: (.*)$') { $Matches[1] } else { 'no simulated view' }
    $summary.Add("$($step.Name): $($files.Count) files; left eye: $layers")
    if ($step.After) { Send-SE2Key $step.After; Start-Sleep -Seconds 2 }
}

$report = Get-SE2TestReport
$lines = @("Menu tour $(Get-Date)", "") + $summary + @("", "Plugin warnings/errors:") + $report.PluginErrors +
         @("", "Game assertions/exceptions:") + $report.GameAssertions + @("", "Performance:") + $report.Perf
$lines | Set-Content (Join-Path $run 'report.txt')
Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
if ($report.GameLog) { Copy-Item $report.GameLog $run }
if (-not $KeepRunning) { Stop-SE2VRTest }
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
