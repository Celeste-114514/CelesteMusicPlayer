#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把 W5/W6 的薄宿主 PoC 自包含发布到 dist/thin-host-poc（覆盖更新，不动主播放器入口）。"""
import os, subprocess, sys

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
PROJ = os.path.join(REPO, "tools", "ThinHostPoC", "ThinHostPoC.csproj")
OUT = os.path.join(REPO, "dist", "thin-host-poc")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"

env = dict(os.environ)
env.update({
    "APPDATA": r"C:\Users\MSI\AppData\Roaming",
    "LOCALAPPDATA": r"C:\Users\MSI\AppData\Local",
    "ProgramData": r"C:\ProgramData",
    "ALLUSERSPROFILE": r"C:\ProgramData",
    "ProgramFiles": r"C:\Program Files",
    "ProgramFiles(x86)": r"C:\Program Files (x86)",
    "CommonProgramFiles": r"C:\Program Files\Common Files",
    "CommonProgramW6432": r"C:\Program Files\Common Files",
    "USERPROFILE": r"C:\Users\MSI",
    "SystemRoot": r"C:\Windows",
    "TEMP": r"C:\Users\MSI\AppData\Local\Temp",
    "TMP": r"C:\Users\MSI\AppData\Local\Temp",
    "DOTNET_ROOT": r"C:\Program Files\dotnet",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
})

for attempt in range(1, 6):
    subprocess.run([DOTNET, "build-server", "shutdown"], env=env, cwd=REPO,
                   capture_output=True, text=True, timeout=120)
    rc = subprocess.call([DOTNET, "publish", PROJ, "-c", "Release", "-r", "win-x64",
                          "--self-contained", "true", "-p:PublishSingleFile=false",
                          "-o", OUT], env=env, cwd=REPO)
    print("attempt %d rc=%d" % (attempt, rc))
    if rc == 0:
        print("POC_PUBLISH_OK")
        sys.exit(0)
print("POC_PUBLISH_FAILED")
sys.exit(1)
