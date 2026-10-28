#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""跑一次 PoC 隐藏自测（--self-test 会自己跑完退出），打印退出码与最新一份 result.json。"""
import glob, os, subprocess, sys

EXE = r"C:\Users\MSI\source\repos\CelesteMusicPlayer\dist\thin-host-poc\CelesteThinHost.Poc.exe"
ROOT = os.path.join(os.environ.get("LOCALAPPDATA", r"C:\Users\MSI\AppData\Local"), "CelesteMusicPlayer-Poc")

env = dict(os.environ)
env.update({"APPDATA": r"C:\Users\MSI\AppData\Roaming",
            "LOCALAPPDATA": r"C:\Users\MSI\AppData\Local",
            "SystemRoot": r"C:\Windows",
            "USERPROFILE": r"C:\Users\MSI"})

before = set(glob.glob(os.path.join(ROOT, "*")))
try:
    rc = subprocess.run([EXE, "--self-test"], cwd=os.path.dirname(EXE),
                        env=env, timeout=90, capture_output=True, text=True)
    code = rc.returncode
    out = (rc.stdout or "") + (rc.stderr or "")
except subprocess.TimeoutExpired:
    code, out = -1, "TIMEOUT"

print("EXIT_CODE=", code)
if out.strip():
    print("OUTPUT:", out.strip()[:500])

after = set(glob.glob(os.path.join(ROOT, "*")))
new = sorted(after - before, key=os.path.getmtime)
if new:
    latest = new[-1]
    print("RUN_DIR=", os.path.basename(latest))
    rj = os.path.join(latest, "result.json")
    if os.path.isfile(rj):
        print("=== result.json ===")
        print(open(rj, encoding="utf-8").read())
    log = os.path.join(latest, "host.log")
    if os.path.isfile(log):
        print("=== host.log (settings 相关) ===")
        for line in open(log, encoding="utf-8").read().splitlines():
            if "settings" in line.lower() or "Settings" in line or "PASS" in line:
                print(line)
else:
    print("NO_NEW_REPORT")
sys.exit(0 if code == 0 else 1)
