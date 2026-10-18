<#
.SYNOPSIS
  Builds a release zip for players: dist\SE2VR-v<version>.zip with Install.cmd, the mod files and a short README.
  Players need no .NET SDK; the game brings the .NET runtime.
#>
param(
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$project = Join-Path $repo 'src\SE2VR\SE2VR.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$buildArgs = @('build', $project, '-c', 'Release', '-nologo', '-v:minimal')
if ($GameDir) { $buildArgs += "/p:SE2GameDir=$GameDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$name = "SE2VR-v$version"
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'files') | Out-Null

$build = Join-Path $repo 'build\Release'
foreach ($file in 'SE2VR.dll', 'SE2VR.pdb', 'SE2VR.deps.json', 'SE2VR.runtimeconfig.json', '0Harmony.dll', 'SE2VR.json') {
    Copy-Item (Join-Path $build $file) (Join-Path $stage 'files')
}
# Text files get Windows line endings (cmd.exe needs them in .cmd files).
foreach ($file in Get-ChildItem (Join-Path $repo 'package') -Recurse -File) {
    $relative = $file.FullName.Substring((Join-Path $repo 'package').Length + 1)
    $target = Join-Path $stage $relative
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    $text = [System.IO.File]::ReadAllText($file.FullName) -replace "`r?`n", "`r`n"
    [System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding $false))
}

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 2)) MB)"
Get-ChildItem $stage -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName.Substring($stage.Length + 1))" }
