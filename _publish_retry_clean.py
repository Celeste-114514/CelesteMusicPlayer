#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_publish_retry_clean.py —— 带清理的自包含 publish 重试（交付用）。

与 _build_retry_clean.py 同源：XamlCompiler(32位) 环境性 0xC0000005 崩溃会留下
0 字节 .g.cs，不清会让下一轮 csc 次生崩溃（反馈环）。每次尝试前把 obj/x64、bin/x64
移走（同卷 move，秒完）。判据：rc==0 且无 error CS；有真编译错立即停。

产出目录由 win-x64.pubxml 指定：bin\Release\<tfm>\win-x64\publish
（与 _copy_run_latest.py 的 SRC 一致）。
"""
import os, subprocess, sys, time

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
PROJ = os.path.join(REPO, "CelesteMusicPlayer", "CelesteMusicPlayer.csproj")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
OBJ = os.path.join(REPO, "CelesteMusicPlayer", "obj", "x64")
BIN = os.path.join(REPO, "CelesteMusicPlayer", "bin", "x64")
BAKROOT = r"C:\Users\MSI\_objbak_retry"

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

ATTEMPTS = int(sys.argv[1]) if len(sys.argv) > 1 else 6


def clean_state():
    stamp = time.strftime("%H%M%S")
    bak = os.path.join(BAKROOT, "pub" + stamp)
    os.makedirs(bak, exist_ok=True)
    moved = []
    for src in (OBJ, BIN):
        if os.path.isdir(src):
            dst = os.path.join(bak, os.path.basename(src))
            try:
                os.replace(src, dst)
                moved.append(os.path.basename(src))
            except Exception as e:
                print("  [warn] move %s failed: %s" % (src, e))
    return moved


def publish_once():
    p = subprocess.run([DOTNET, "publish", PROJ, "-c", "Release", "-r", "win-x64",
                        "-p:Platform=x64", "-p:WindowsPackageType=None",
                        "-p:CelesteSelfContainedDistribute=true",
                        "-p:EnableSourceControlManagerQueries=false"],
                       cwd=REPO, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=900)
    log = (p.stdout or "") + "\n" + (p.stderr or "")
    return p.returncode, log


for i in range(1, ATTEMPTS + 1):
    moved = clean_state()
    print("\n=== publish attempt %d/%d (cleaned: %s) ===" % (i, ATTEMPTS, ",".join(moved) or "nothing"), flush=True)
    t0 = time.time()
    try:
        rc, log = publish_once()
    except subprocess.TimeoutExpired:
        print("  TIMEOUT 900s", flush=True)
        continue
    dt = time.time() - t0
    cs_lines = [l for l in log.splitlines() if "error CS" in l]
    xaml_crash = "XamlCompiler.exe" in log and "MSB3073" in log
    print("  exit=%d CS_err=%d xamlCrash=%s (%.0fs)" % (rc, len(cs_lines), xaml_crash, dt), flush=True)
    if cs_lines:
        print("[CODE ERROR] 真实编译错误，停：", flush=True)
        for l in cs_lines[:10]:
            print("  " + l[:300], flush=True)
        sys.exit(1)
    if rc == 0:
        print("PUBLISH_OK at attempt %d" % i, flush=True)
        sys.exit(0)

print("\n[FAIL] %d 次全崩（疑似环境/硬件不稳定）" % ATTEMPTS, flush=True)
sys.exit(2)
