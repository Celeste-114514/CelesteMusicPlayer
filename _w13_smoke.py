#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_smoke.py —— 启动 publish 产物、跑一段时间、结束后做「硬判据」检查。

判据（任何一条不过就 exit 1，不再只看进程还活着）：
  1. 本次运行没有 ERR 行
  2. 曲库真的加载了（日志里最后一次 `library sent tracks=N`，N ≥ MIN_TRACKS）
  3. 启动初始化只跑了一次（`MainWindow_Loaded begin` 恰好 1 次）
  4. 启动收尾走到过（`启动加载遮罩已撤下`，或确认走了恢复页）

用法：python _w13_smoke.py [exe路径] [秒数]
"""
import datetime
import os
import re
import subprocess
import sys
import time

EXE = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\MSI\source\repos\CelesteMusicPlayer\CelesteMusicPlayer\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\CelesteMusicPlayer.exe"
SECONDS = int(sys.argv[2]) if len(sys.argv) > 2 else 30
MIN_TRACKS = 1000

LOCAL = os.environ.get("LOCALAPPDATA", r"C:\Users\MSI\AppData\Local")
OLD_LOG = os.path.join(LOCAL, "CelesteMusicPlayer", "CelesteMusicPlayer.log")
APP_LOG = os.path.join(LOCAL, "CelesteMusicPlayer", "logs", "app",
                       datetime.date.today().strftime("%Y-%m-%d.log"))

if not os.path.exists(EXE):
    print("EXE 不存在")
    sys.exit(2)


def size_of(p):
    return os.path.getsize(p) if os.path.exists(p) else 0


def new_lines(p, base):
    if not os.path.exists(p):
        return []
    with open(p, "rb") as f:
        f.seek(base)
        raw = f.read()
    return [l for l in raw.decode("utf-8", "replace").splitlines() if l.strip()]


base_old, base_app = size_of(OLD_LOG), size_of(APP_LOG)
print("日志基线 old=%d app=%d" % (base_old, base_app))

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                     stdout=subprocess.PIPE, stderr=subprocess.PIPE)
t0 = time.time()
died = False
while time.time() - t0 < SECONDS:
    if p.poll() is not None:
        died = True
        break
    time.sleep(0.5)

if died:
    out, err = p.communicate()
    print("!! 进程提前退出 code=%s (0x%08X)" % (p.returncode, p.returncode & 0xFFFFFFFF))
    if err:
        print("stderr:", err.decode("utf-8", "replace")[:800])
    sys.exit(1)

p.kill()
try:
    p.communicate(timeout=5)
except Exception:
    pass
print("运行 %ds 后已被结束" % SECONDS)

time.sleep(0.6)
lines = new_lines(APP_LOG, base_app)
if not lines:
    lines = new_lines(OLD_LOG, base_old)
print("新增日志 %d 行" % len(lines))
for l in lines[-18:]:
    print("  " + l[:160])

# ---- 硬判据 ----
fails = []

errs = [l for l in lines if " ERR " in l]
if errs:
    fails.append("ERR 行 %d 条，第一条：%s" % (len(errs), errs[0][:160]))

tracks = [int(m.group(1)) for m in
          (re.search(r"library sent tracks=(\d+)", l) for l in lines) if m]
if not tracks:
    fails.append("整段日志里没有 `library sent tracks=N`：曲库根本没下发")
elif max(tracks) < MIN_TRACKS:
    fails.append("曲库只下发了 %d 首（阈值 %d）：启动初始化可能没跑" % (max(tracks), MIN_TRACKS))

begins = [l for l in lines if "MainWindow_Loaded begin" in l]
if len(begins) != 1:
    fails.append("启动初始化跑了 %d 次（应为 1 次）" % len(begins))

settled = [l for l in lines if "启动加载遮罩已撤下" in l or "启动收尾超时" in l]
if not settled:
    fails.append("启动收尾没有走到（既没正常撤下也没超时）")

print("--- 曲库下发 %s 首 ---" % (max(tracks) if tracks else "无"))
if fails:
    print("!! 冒烟失败 %d 项：" % len(fails))
    for f in fails:
        print("   - " + f)
    sys.exit(1)

print("SMOKE=PASS")
