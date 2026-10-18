<#
.SYNOPSIS
  Measures how much the headset image flickers: loads a world in VR (isolated test profile, ideal still head), captures
  both eyes several times and compares each capture with the previous one. With a still camera, pixels that change a
  lot from one capture to the next are shimmer, dither noise or flicker (plus whatever really moves in the scene).
  Optionally keeps the game running for a while to check that it stays up, and reports frame rate and video memory.
  Results go to %LOCALAPPDATA%\SE2VR\testruns\<timestamp>_stability_<Label>.

.PARAMETER Settings
  SE2VR.json settings for this run, e.g. @{ FsrInVR = $false }. The run uses its own copy of the settings file.

.PARAMETER RenderOptions
  A RenderOptionsPart2 file (the game's graphics settings) to use in the test profile for this run.
#>
param(
    [string]$World = 'CRF',
    [string]$Label = 'default',
    [hashtable]$Settings = @{},
    [string]$RenderOptions,
    [int]$Captures = 5,
    [int]$SettleSeconds = 12,
    [int]$RunSeconds = 0,
    [switch]$NoBuild,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SE2VRTest.psm1') -Force

if (-not ('SE2VRImageDiff' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SE2VRImageDiff
{
    static byte[] Load(string path, out int width, out int height)
    {
        using (var bitmap = new Bitmap(path))
        {
            width = bitmap.Width; height = bitmap.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            bitmap.UnlockBits(data);
            return pixels;
        }
    }

    // [mean absolute difference (0-255), % of pixels changed by more than threshold]
    public static double[] Compare(string a, string b, int threshold)
    {
        int w1, h1, w2, h2;
        byte[] p = Load(a, out w1, out h1), q = Load(b, out w2, out h2);
        if (w1 != w2 || h1 != h2) return new double[] { -1, -1 };
        long sum = 0, over = 0, n = (long)w1 * h1;
        for (long i = 0; i < n; i++)
        {
            int d = Math.Max(Math.Abs(p[i * 4] - q[i * 4]), Math.Max(Math.Abs(p[i * 4 + 1] - q[i * 4 + 1]), Math.Abs(p[i * 4 + 2] - q[i * 4 + 2])));
            sum += d;
            if (d > threshold) over++;
        }
        return new double[] { (double)sum / n, 100.0 * over / n };
    }
}
"@
}

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot '..\install.ps1') | Out-Null
}
$run = Join-Path $env:LOCALAPPDATA ("SE2VR\testruns\" + (Get-Date -Format 'yyyyMMdd_HHmmss') + "_stability_$Label")
New-Item -ItemType Directory -Force $run | Out-Null

# This run's own SE2VR.json: the shipped defaults plus the requested settings.
$defaults = Get-Content (Join-Path $PSScriptRoot '..\config\SE2VR.json') -Raw | ConvertFrom-Json
foreach ($key in $Settings.Keys) { $defaults | Add-Member -NotePropertyName $key -NotePropertyValue $Settings[$key] -Force }
$configPath = Join-Path $run 'SE2VR.json'
[IO.File]::WriteAllText($configPath, ($defaults | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

# Optional graphics settings for the test profile (restored afterwards).
$optionsPath = Join-Path $env:LOCALAPPDATA 'SE2VR\testprofile\AppData\EngineOptions\RenderOptionsPart2'
$optionsBackup = $null
if ($RenderOptions) {
    if (Test-Path $optionsPath) { $optionsBackup = Get-Content $optionsPath -Raw }
    Copy-Item $RenderOptions $optionsPath -Force
}

try {
    $null = Start-SE2VRTest -World $World -Environment @{ SE2VR_CONFIG = $configPath }
    if (-not (Wait-SE2Log -Pattern 'First stereo frame' -TimeoutSeconds 300)) {
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
    Start-Sleep -Seconds $SettleSeconds
    $shots = @()
    for ($i = 1; $i -le $Captures; $i++) {
        $files = @(Invoke-SE2Capture -Label "stab$i")
        foreach ($f in $files) { Move-Item $f $run -Force }
        $shots += $i
    }
    $started = Get-Date
    while ($RunSeconds -gt 0 -and ((Get-Date) - $started).TotalSeconds -lt $RunSeconds) {
        if (-not (Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 5
    }
    $alive = [bool](Get-Process SpaceEngineers2 -ErrorAction SilentlyContinue)
}
finally {
    $report = Get-SE2TestReport
    Get-SE2PluginLog | Set-Content (Join-Path $run 'SE2VR.log')
    if (-not $KeepRunning) { Stop-SE2VRTest }
    if ($RenderOptions) {
        if ($null -ne $optionsBackup) { [IO.File]::WriteAllText($optionsPath, $optionsBackup, (New-Object System.Text.UTF8Encoding($false))) }
        else { Remove-Item $optionsPath -ErrorAction SilentlyContinue }
    }
}

$lines = @("Stability check $(Get-Date)", "World: $World, label: $Label, settings: $(($Settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')", "")
foreach ($eye in 'left', 'right') {
    $images = @(Get-ChildItem $run -Filter "*_stab*_$eye.png" | Sort-Object Name)
    for ($i = 1; $i -lt $images.Count; $i++) {
        $d = [SE2VRImageDiff]::Compare($images[$i - 1].FullName, $images[$i].FullName, 24)
        $lines += ("{0} eye, capture {1} vs {2}: mean change {3:0.00}/255, {4:0.00}% of pixels changed by more than 24" -f $eye, $i, ($i + 1), $d[0], $d[1])
    }
}
if ($RunSeconds -gt 0) { $lines += "", "Still running after $RunSeconds s: $alive" }
$lines += @("", "Plugin warnings/errors:") + $report.PluginErrors + @("", "Game assertions/exceptions:") + $report.GameAssertions + @("", "Performance:") + $report.Perf
$lines | Set-Content (Join-Path $run 'report.txt')
Write-Host "Results: $run"
$lines | ForEach-Object { Write-Host $_ }
