#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
_publish_selfcontained.py —— 自包含发布（目标机不依赖机器上的 .NET）。

为什么必须自包含：框架依赖版（exe 177KB）放进躺有旧运行时 DLL 的目录时，
旁边的 hostfxr.dll 会把运行时解析带偏 -> 弹「必须安装 .NET」。本机装了 .NET 也照样弹。
"""
import os, subprocess, sys, time

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
PROJ = os.path.join(REPO, "CelesteMusicPlayer", "CelesteMusicPlayer.csproj")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
# ⚠ 发布目录在 CelesteMusicPlayer\bin\Release\...（不带 x64 平台段）！
# Properties\PublishProfiles\win-x64.pubxml 里 PublishDir = bin\$(Configuration)\$(TargetFramework)\$(RuntimeIdentifier)\publish\
# 是相对项目目录的相对路径且不含平台段；构建输出才在 bin\x64\Release\... 两者不是同一个目录。
PUBDIR = os.path.join(REPO, "CelesteMusicPlayer", "bin", "Release",
                      "net10.0-windows10.0.19041.0", "win-x64", "publish")

os.environ.update({
    "APPDATA": r"C:\Users\MSI\AppData\Roaming",
    "LOCALAPPDATA": r"C:\Users\MSI\AppData\Local",
    "ProgramData": r"C:\ProgramData",
    "ALLUSERSPROFILE": r"C:\ProgramData",
    "ProgramFiles": r"C:\Program Files",
    "ProgramFiles(x86)": r"C:\Program Files (x86)",
    "CommonProgramFiles": r"C:\Program Files\Common Files",
    "CommonProgramFiles(x86)": r"C:\Program Files (x86)\Common Files",
    "CommonProgramW643": r"C:\Program Files\Common Files",
    "USERPROFILE": r"C:\Users\MSI",
    "HOMEDRIVE": "C:", "HOMEPATH": r"\Users\MSI", "HOME": r"C:\Users\MSI",
    "SystemRoot": r"C:\Windows", "SystemDrive": "C:", "windir": r"C:\Windows",
    "TEMP": r"C:\Users\MSI\AppData\Local\Temp",
    "TMP": r"C:\Users\MSI\AppData\Local\Temp",
    "USERNAME": "MSI",
    "PATHEXT": ".COM;.EXE;.BAT;.CMD;.VBS;.JS;.WSF;.WSH;.MSC",
    "PROCESSOR_ARCHITECTURE": "AMD64",
    "COMSPEC": r"C:\Windows\system32\cmd.exe",
    "DOTNET_ROOT": r"C:\Program Files\dotnet",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
})

t0 = time.time()
p = subprocess.run([DOTNET, "publish", PROJ, "-c", "Release", "-p:Platform=x64",
                    "-r", "win-x64", "-p:CelesteSelfContainedDistribute=true"],
                   cwd=REPO, capture_output=True, text=True,
                   encoding="utf-8", errors="replace")
log = (p.stdout or "") + (p.stderr or "")
print("exit=%d (%.0fs)" % (p.returncode, time.time() - t0), flush=True)
errs = [l for l in log.splitlines() if "error" in l.lower()]
print("error 行数=%d" % len(errs), flush=True)
for line in errs[:15]:
    print("  " + line[:300], flush=True)

n = sum(len(fs) for _, _, fs in os.walk(PUBDIR)) if os.path.isdir(PUBDIR) else 0
exe = os.path.join(PUBDIR, "CelesteMusicPlayer.exe")
cfg = os.path.join(PUBDIR, "CelesteMusicPlayer.runtimeconfig.json")
print("publish 文件数=%d" % n, flush=True)
print("exe=%s bytes" % (os.path.getsize(exe) if os.path.isfile(exe) else "缺失"), flush=True)
if os.path.isfile(cfg):
    txt = open(cfg, encoding="utf-8").read()
    print("runtimeconfig 自包含=%s" % ("includedFrameworks" in txt), flush=True)
print("PUBLISH_OK" if p.returncode == 0 and n > 500 else "PUBLISH_FAIL", flush=True)
sys.exit(0 if p.returncode == 0 else 1)
