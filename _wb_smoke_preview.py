#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Single-launch smoke for the web-shell preview build (no UI automation, per project rule).

Launches dist/web-shell-library-preview, keeps it alive for N seconds, then reads only the log
lines written during this run and prints anything that smells like a failure.
"""
import os, subprocess, sys, time

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
FOLDER = os.path.join(REPO, "dist", "web-shell-library-preview")
EXE = os.path.join(FOLDER, "CelesteMusicPlayer.exe")
LOCAL = os.path.join(os.environ.get("LOCALAPPDATA", r"C:\Users\MSI\AppData\Local"), "CelesteMusicPlayer")
SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 12

logs = []
for root, _, files in os.walk(LOCAL):
    for name in files:
        if not name.lower().endswith(".log") or name.startswith("CelesteUpdater"):
            continue
        # StartupLog splits by folder: logs/app and logs/audio. Both must be read — a web-shell
        # failure can land in either one depending on which partial wrote it.
        full = os.path.join(root, name)
        if os.sep + "logs" + os.sep in full:
            logs.append(full)
sizes = {p: (os.path.getsize(p) if os.path.isfile(p) else 0) for p in logs}
print("watching %d log files" % len(logs), flush=True)

p = subprocess.Popen([EXE], cwd=FOLDER)
print("launched pid=%d" % p.pid, flush=True)
alive = True
for i in range(SECONDS):
    time.sleep(1)
    if p.poll() is not None:
        alive = False
        print("DIED at %ds exit=%s" % (i + 1, p.returncode), flush=True)
        break
if alive:
    print("ALIVE after %ds" % SECONDS, flush=True)

new_text = ""
for path in logs:
    try:
        size = os.path.getsize(path)
    except OSError:
        continue
    before = sizes.get(path, 0)
    if size <= before:
        continue
    with open(path, "rb") as f:
        f.seek(before)
        chunk = f.read().decode("utf-8", "replace")
    if chunk.strip():
        new_text += "\n---- %s ----\n" % os.path.basename(path) + chunk

lines = [l for l in new_text.splitlines() if l.strip()]
errs = [l for l in lines
        if any(k in l for k in ("Exception", "ERR", "error", "失败", "崩溃", "Fatal", "CRASH"))]
print("NEWLOG_LINES=%d ERR_LINES=%d" % (len(lines), len(errs)), flush=True)
for l in errs[:25]:
    print("  ERR| " + l[:220], flush=True)
hits = [l for l in lines if "[WebDsp]" in l or "[WebShell]" in l or "[WebTags]" in l]
for l in hits[:10]:
    print("  HIT| " + l[:220], flush=True)
print("--- tail ---", flush=True)
for l in lines[-8:]:
    print("  " + l[:220], flush=True)

if alive:
    p.terminate()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
print("DONE", flush=True)
