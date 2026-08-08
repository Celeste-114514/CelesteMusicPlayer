# One-click publish: self-contained zip for friends (no .NET / Windows App Runtime install).
# Save this file as UTF-8. Avoid fancy quotes in Write-Host strings.
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "CelesteMusicPlayer.csproj"
$rid = "win-$Arch"
$platform = if ($Arch -eq "arm64") { "ARM64" } else { $Arch }

Write-Host "Publishing CelesteMusicPlayer ($rid) ..." -ForegroundColor Cyan
dotnet publish $csproj -c Release -p:Platform=$platform -r $rid -p:CelesteSelfContainedDistribute=true --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed: $LASTEXITCODE"
}

$publishCandidates = @(
    (Join-Path $root "bin\$platform\Release\net8.0-windows10.0.19041.0\$rid\publish"),
    (Join-Path $root "bin\Release\net8.0-windows10.0.19041.0\$rid\publish")
)
$publishDir = $publishCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $publishDir) {
    throw "Publish folder not found."
}

$launcherSrc = Join-Path $PSScriptRoot "Launch.cmd"
if (-not (Test-Path -LiteralPath $launcherSrc)) {
    throw "Missing Launch.cmd in Distribution folder."
}
Copy-Item -LiteralPath $launcherSrc -Destination (Join-Path $publishDir "Launch.cmd") -Force
$cnLauncher = Join-Path $PSScriptRoot "请先点我启动.cmd"
if (Test-Path -LiteralPath $cnLauncher) {
    Copy-Item -LiteralPath $cnLauncher -Destination (Join-Path $publishDir "请先点我启动.cmd") -Force
} else {
    Copy-Item -LiteralPath $launcherSrc -Destination (Join-Path $publishDir "请先点我启动.cmd") -Force
}

$required = @(
    "CelesteMusicPlayer.exe",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "coreclr.dll"
)
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $publishDir $name))) {
        throw "Missing required file: $name"
    }
}

$desktop = [Environment]::GetFolderPath("Desktop")
$zipPath = Join-Path $desktop "CelesteMusicPlayer-$Arch.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Write-Host "Zipping to $zipPath ..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "1) Desktop zip: $zipPath"
Write-Host "2) Publish folder: $publishDir"
Write-Host "3) Friend: extract whole folder, then run Launch.cmd (or 请先点我启动.cmd)"
Write-Host "   If blocked: right-click zip/folder -> Properties -> Unblock -> OK"
Write-Host "   If crash: send CelesteMusicPlayer.log next to the exe"
