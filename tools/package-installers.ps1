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
        # ⚠ 三个 -p 开关一个都不能少（2026-10-15 事故，务必保留）：
        #   Platform=x64       —— 不带会按默认平台发布，与 x64 原生 dll 对不上；
        #   WindowsPackageType=None —— 不带会走 MSIX 打包路径，生成的 .pri 资源索引只有
        #       约 253KB（正常应约 1.6MB），XAML 页面资源整批缺失 → 用户装完启动即失败、
        #       「安装包打不开」。GitHub Actions 上打出来的坏包就是这么来的；
        #   EnableSourceControlManagerQueries=false —— 免得在无 git 环境卡住。
        Invoke-Step 'publish 框架依赖版 (fd)' 'dotnet' @(
            'publish', $mainCsproj, '-c', 'Release', '-r', 'win-x64',
            '-p:Platform=x64',
            '-p:WindowsPackageType=None',
            '-p:EnableSourceControlManagerQueries=false',
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
            '-p:Platform=x64',
            '-p:WindowsPackageType=None',
            '-p:CelesteSelfContainedDistribute=true',
            '-p:EnableSourceControlManagerQueries=false',
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

# ---------- 1.5 产物体检（2026-10-15 加）：坏包不许出门 ----------
# 事故背景：CI 上 publish 漏了 -p:WindowsPackageType=None，生成的 .pri 只有约 253KB
# （正常约 1.6MB），XAML 页面资源整批缺失 → 用户装完一启动就失败，反馈「安装包打不开」。
# 安装包能装、哈希能算，光看文件大小发现不了，只能靠这两条硬门槛拦住。
function Assert-PublishHealthy {
    param([string] $Dir, [string] $Variant, [bool] $ExpectSelfContained)

    if (-not (Test-Path -LiteralPath $Dir)) { return }

    $exe = Join-Path $Dir 'CelesteMusicPlayer.exe'
    $pri = Join-Path $Dir 'CelesteMusicPlayer.pri'
    if (-not (Test-Path -LiteralPath $exe)) { throw "$Variant 产物缺少 $exe" }
    if (-not (Test-Path -LiteralPath $pri)) { throw "$Variant 产物缺少 $pri（资源索引）" }

    $priLen = (Get-Item -LiteralPath $pri).Length
    if ($priLen -lt 900KB) {
        throw "$Variant 的 .pri 只有 $([math]::Round($priLen / 1KB))KB（正常约 1.6MB），XAML 资源缺失。" +
              "多半是 publish 漏了 -p:WindowsPackageType=None。拒绝打包。"
    }

    $exeLen = (Get-Item -LiteralPath $exe).Length
    if ($ExpectSelfContained -and $exeLen -lt 250KB) {
        throw "$Variant 的 CelesteMusicPlayer.exe 只有 $([math]::Round($exeLen / 1KB))KB（自包含应约 296KB），" +
              "疑似编成了依赖运行时版（漏 -p:CelesteSelfContainedDistribute=true）。拒绝打包。"
    }
    if (-not $ExpectSelfContained -and $exeLen -gt 250KB) {
        throw "$Variant 的 CelesteMusicPlayer.exe 有 $([math]::Round($exeLen / 1KB))KB（框架依赖版应约 173KB），" +
              "疑似编成了自包含版。拒绝打包。"
    }

    Write-Host "  [体检] $Variant 正常：exe $([math]::Round($exeLen / 1KB))KB，pri $([math]::Round($priLen / 1KB))KB"
}

if (-not $SkipFrameworkDep) { Assert-PublishHealthy $publishFd '框架依赖版(fd)' $false }
if (-not $SkipSelfContained) { Assert-PublishHealthy $publishSc '自包含版(sc)' $true }

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
