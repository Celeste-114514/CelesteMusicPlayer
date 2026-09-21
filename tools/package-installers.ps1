#requires -Version 5.1
<#
.SYNOPSIS
  一键打包 CelesteMusicPlayer 双安装包（框架依赖 + 自包含）并生成 SHA256SUMS.txt。

.DESCRIPTION
  产物（默认输出到仓库根目录 dist\）：
    CelesteMusicPlayer-Setup-<版本>.exe        框架依赖版（体积小，需 .NET 9 + Windows App SDK 运行时）
    CelesteMusicPlayer-Setup-SC-<版本>.exe     自包含版（体积大，无需任何运行时）
    SHA256SUMS.txt                             两个安装包的 SHA-256 清单，随 release 上传，
                                                应用内更新下载后据此校验（见 UpdateChecker）

  版本号默认从 CelesteMusicPlayer.csproj 的 <Version> 读取，也可用 -Version 覆盖。
  需要本机安装 NSIS 3（https://nsis.sourceforge.io/）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package-installers.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package-installers.ps1 -SkipSelfContained
.NOTES
  GitHub CI 中同样调用本脚本打包并上传 artifact，保证本地与 CI 产物一致。
#>
param(
    [string] $Version = '',
    [string] $OutDir = 'dist',
    [switch] $SkipFrameworkDep,
    [switch] $SkipSelfContained,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$mainCsproj = Join-Path $repo 'CelesteMusicPlayer\CelesteMusicPlayer.csproj'
$nsi = Join-Path $repo 'installer\CelesteMusicPlayer.nsi'
$outDir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }

function Get-Makensis {
    $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles}\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "${env:ProgramFiles}\NSIS\Bin\makensis.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw '找不到 makensis.exe，请先安装 NSIS 3：https://nsis.sourceforge.io/'
}

function Get-ProjectVersion {
    param([string] $Csproj)
    $text = Get-Content -LiteralPath $Csproj -Raw
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "无法从 $Csproj 读取 <Version>" }
    return $m.Groups[1].Value.Trim()
}

function Invoke-Step {
    param([string] $Description, [string] $Exe, [string[]] $ArgList)
    Write-Host "==> $Description"
    & $Exe @ArgList
    if ($LASTEXITCODE -ne 0) { throw "$Description 失败（exit=$LASTEXITCODE）：$Exe $($ArgList -join ' ')" }
}

# ---------- 0. 版本号 ----------
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProjectVersion $mainCsproj }
Write-Host "版本: $Version"
Write-Host "输出: $outDir"

$makensis = Get-Makensis
$null = New-Item -ItemType Directory -Force -Path $outDir

# ---------- 1. publish 两个变体 ----------
$publishFd = Join-Path $outDir 'publish-fd'
$publishSc = Join-Path $outDir 'publish-sc'
$installers = @()

if (-not $SkipFrameworkDep) {
    if (-not $SkipPublish) {
        Invoke-Step 'publish 框架依赖版 (fd)' 'dotnet' @(
            'publish', $mainCsproj, '-c', 'Release', '-r', 'win-x64',
            '-o', $publishFd)
    }
    $fdOut = Join-Path $outDir "CelesteMusicPlayer-Setup-$Version.exe"
    Invoke-Step 'makensis 框架依赖安装包' $makensis @(
        "-DAPP_VERSION=$Version",
        "-DPUBLISH_DIR=$publishFd",
        "-DOUTFILE=$fdOut",
        '-DVARIANT=fd',
        $nsi)
    $installers += $fdOut
}

if (-not $SkipSelfContained) {
    if (-not $SkipPublish) {
        Invoke-Step 'publish 自包含版 (sc)' 'dotnet' @(
            'publish', $mainCsproj, '-c', 'Release', '-r', 'win-x64',
            '-p:CelesteSelfContainedDistribute=true',
            '-o', $publishSc)
    }
    $scOut = Join-Path $outDir "CelesteMusicPlayer-Setup-SC-$Version.exe"
    Invoke-Step 'makensis 自包含安装包' $makensis @(
        "-DAPP_VERSION=$Version",
        "-DPUBLISH_DIR=$publishSc",
        "-DOUTFILE=$scOut",
        '-DVARIANT=sc',
        $nsi)
    $installers += $scOut
}

# ---------- 2. SHA256SUMS.txt ----------
$sums = Join-Path $outDir 'SHA256SUMS.txt'
$lines = foreach ($f in $installers) {
    $hash = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $f)"
}
$lines | Set-Content -LiteralPath $sums -Encoding ASCII
Write-Host "==> 已生成 $sums"

# ---------- 3. 摘要 ----------
Write-Host ''
Write-Host '========== 打包完成 =========='
foreach ($f in ($installers + $sums)) {
    $sizeMB = [math]::Round((Get-Item -LiteralPath $f).Length / 1MB, 1)
    Write-Host ("  {0,-50} {1,8} MB" -f (Split-Path -Leaf $f), $sizeMB)
}
Write-Host '提示：蓝奏云上传可对 .exe 再打 7z（LZMA2）压缩；发布到 GitHub Releases 时'
Write-Host '      请将两个 .exe 与 SHA256SUMS.txt 一起上传，应用内更新依赖它们。'
