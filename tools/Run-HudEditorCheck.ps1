<#
.SYNOPSIS
  Automated check of the Modify UI editor's preview: in a built-in world it opens Settings > VR > MODIFY UI from the
  pause menu and captures what the headset receives (every HUD part that follows your head in a frame, the chosen
  part highlighted, with the HUD picture kept from play), picks other parts, moves one, then checks play again. A
  second start checks the editor from the main menu (frames only: no HUD picture before a world was played).

.DESCRIPTION
  Same isolation as Run-UiTour.ps1, plus its own copy of SE2VR.json (SE2VR_CONFIG), so the player's settings and HUD
  layout are never touched. Suit status and the toolbar start out following the head; picking "Ship status" and
  nudging its left/right slider must switch "Follows your head" on and save it.
  Click positions are fractions of the UI canvas, measured on game version 2.4 (pause menu, Settings tabs, the VR tab
  as the plugin lays it out); a game update that moves them needs new positions here.
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>_hudeditor.
#>
param(
    [string]$World = 'EmptyWorld',
    [switch]$NoBuild,
    [switch]$SkipMainMenu
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_hudeditor')
New-Item -ItemType Directory -Force $run | Out-Null

# The test's own settings, from the shipped defaults: suit status and the toolbar follow the head.
$config = Get-Content (Join-Path $PSScriptRoot '..\config\SE2VR.json') -Raw | ConvertFrom-Json
foreach ($piece in $config.HudPieces) { $piece.FollowHead = $piece.Id -in @('status', 'toolbar') }
$configPath = Join-Path $run 'SE2VR.json'
[System.IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))

# Positions on the UI canvas (fractions of its width and height).
$pauseSettings = @(0.1439, 0.6866) # pause menu: Settings
$menuSettings  = @(0.1439, 0.7676) # main menu: Settings
$vrTab         = @(0.7692, 0.1386) # Settings: the VR tab, after Controls
$modifyUi      = @(0.2720, 0.2650) # VR tab: MODIFY UI
$nextPart      = @(0.6600, 0.3840) # VR tab: right arrow of "Part of the HUD"
$leftRightPlus = @(0.6600, 0.4905) # VR tab: right arrow of "Left / right, degrees"
$outOfTheWay   = @(0.9000, 0.9500) # pointer position for captures in menus

function Move-Canvas([double]$U, [double]$V) {
    Send-SE2MouseMove -X ([int]($U * $resolution.CanvasWidth)) -Y ([int]($V * $resolution.CanvasHeight)) `
                      -ImageWidth $resolution.Width -ImageHeight $resolution.Height
}

function Click-Canvas([double]$U, [double]$V) {
    # The game's buttons and arrows can miss a click that comes right after the pointer arrives.
    Move-Canvas $U $V
    Start-Sleep -Milliseconds 1000
    Send-SE2Click -X ([int]($U * $resolution.CanvasWidth)) -Y ([int]($V * $resolution.CanvasHeight)) `
                  -ImageWidth $resolution.Width -ImageHeight $resolution.Height
}

$summary = New-Object System.Collections.Generic.List[string]

# Keys first, then clicks, then a capture. -Menu parks the pointer first (during play the mouse turns the character).
function Invoke-Step([string]$Name, [string[]]$Press = @(), [object[]]$Clicks = @(), [switch]$Menu) {
    if ($Press) { Send-SE2Key $Press; Start-Sleep -Seconds 4 } # menus slide in; a click before that hits the wrong button
    foreach ($click in $Clicks) { Click-Canvas $click[0] $click[1]; Start-Sleep -Seconds 2 }
    if ($Menu) { Move-Canvas $outOfTheWay[0] $outOfTheWay[1]; Start-Sleep -Milliseconds 800 }
    $files = @(Invoke-SE2Capture -Label $Name)
    foreach ($f in $files) {
        # The plugin may still be writing the last image when its log line appears.
        for ($try = 1; $try -le 20; $try++) {
            try { Move-Item $f $run -Force -ErrorAction Stop; break }
            catch { if ($try -eq 20) { Write-Warning "Could not move $f" } else { Start-Sleep -Milliseconds 250 } }
        }
    }
    $log = @(Get-SE2PluginLog)
    $viewLine = $log -match "Simulated headset view .*_${Name}_left_view\.png" | Select-Object -Last 1
    $layers = if ($viewLine -match 'left_view\.png: (.*)$') { $Matches[1] } else { 'no simulated view' }
    $images = ($files | ForEach-Object { ([IO.Path]::GetFileNameWithoutExtension($_) -split "_${Name}_")[-1] }) -join ', '
    $summary.Add("${Name}: images $images")
    # The parts' image: during play this frame's HUD, in the editor the picture kept from play (not nearly empty).
    $hudLine = $log -match "Captured .*_${Name}_hud\.png" | Select-Object -Last 1
    if ($hudLine -match 'alpha: ([\d.]+)% transparent') { $summary.Add("    HUD parts' image: $($Matches[1])% transparent") }
    $summary.Add("    left eye: $layers")
}

function Save-Results([string]$Part) {
    $report = Get-SE2TestReport
    $summary.Add("")
    $summary.Add("$Part - plugin warnings/errors:")
    $report.PluginErrors | ForEach-Object { $summary.Add("    $_") }
    $summary.Add("$Part - game assertions/exceptions:")
    $report.GameAssertions | ForEach-Object { $summary.Add("    $_") }
    Get-SE2PluginLog | Set-Content (Join-Path $run "SE2VR_$Part.log")
    $summary.Add("")
}

# ---- In a world: play, then the editor from the pause menu ----
$null = Start-SE2VRTest -World $World -Environment @{ SE2VR_CONFIG = $configPath }
if (-not (Wait-SE2Log -Pattern 'First stereo frame' -TimeoutSeconds 300)) {
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR_world.log')
    Stop-SE2VRTest
    throw "The world did not reach stereo rendering; see $run\SE2VR_world.log"
}
Start-Sleep -Seconds 8 # let streaming settle and the spawn animation finish
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    $check = @(Invoke-SE2Capture -Label 'loading_check')
    $line = @(Get-SE2PluginLog) -match 'Captured .*_loading_check_ui(main)?\.png' | Select-Object -Last 1
    $check | Remove-Item -ErrorAction SilentlyContinue
    if ($line -match 'alpha: ([\d.]+)% transparent' -and [double]$Matches[1] -gt 50) { break }
    Write-Host "Still loading..."
    Start-Sleep -Seconds 5
}
Start-Sleep -Seconds 3
$resolution = Get-SE2Resolution

Invoke-Step 'play'
Invoke-Step 'editor_ingame' -Press 'Escape' -Clicks $pauseSettings, $vrTab, $modifyUi -Menu
Invoke-Step 'editor_toolbar' -Clicks (, $nextPart) -Menu
Invoke-Step 'editor_shipstatus' -Clicks $nextPart, $leftRightPlus -Menu
Start-Sleep -Seconds 2 # settings are saved a second after the last change
$cockpit = (Get-Content $configPath -Raw | ConvertFrom-Json).HudPieces | Where-Object Id -eq 'cockpit'
$summary.Add("Ship status after one step right: FollowHead $($cockpit.FollowHead), LeftRight $($cockpit.LeftRight) (expected True, 21)")
Invoke-Step 'play_again' -Press 'Escape', 'Escape'
Save-Results 'world'
Stop-SE2VRTest

# ---- From the main menu: no world played yet, so frames without a HUD picture ----
if (-not $SkipMainMenu) {
    $null = Start-SE2VRTest -Environment @{ SE2VR_CONFIG = $configPath }
    if (Wait-SE2Log -Pattern 'Showing the menu on a floating panel' -TimeoutSeconds 300) {
        Start-Sleep -Seconds 10 # intro animations, background loading
        $resolution = Get-SE2Resolution
        Invoke-Step 'editor_mainmenu' -Press 'Escape' -Clicks $menuSettings, $vrTab, $modifyUi -Menu
    }
    else {
        $summary.Add("editor_mainmenu: the main menu was not reached")
    }
    Save-Results 'mainmenu'
    Stop-SE2VRTest
}

$lines = @("Modify UI editor check $(Get-Date)", "World: $World", "") + $summary
$lines | Set-Content (Join-Path $run 'report.txt')
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
