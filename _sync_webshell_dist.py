#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把最新自包含发布产物同步到 dist\web-shell-dsp-tags-complete（只拷有差异的文件）。

⚠ 源必须是 publish 目录（自包含），不能是 bin\x64\Release\...（框架依赖）：
   从后者拷会导致 dist 里 exe 177KB（框架依赖）+ 旧自包含 DLL 残留 = 混合目录，
   启动时旁边残留的 hostfxr.dll 会把运行时解析带偏 -> 弹「必须安装 .NET」。
   publish 目录实际位置是 CelesteMusicPlayer\bin\Release\...（不带平台段），见 pubxml 的 PublishDir。
沙箱坑：对已存在文件直接覆盖写会产出「大小正确、内容全零」的假文件 -> 必须先删后写，再 md5 复核。
"""
import hashlib, os, shutil

ROOT = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
SRC = os.path.join(ROOT, "CelesteMusicPlayer", "bin", "Release",
                   "net10.0-windows10.0.19041.0", "win-x64", "publish")
DST = os.path.join(ROOT, "dist", "web-shell-dsp-tags-complete")
SKIP_DIRS = {"CelesteMusicPlayer.exe.WebView2", "EBWebView"}


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---- 起飞前自检：源必须是自包含 publish 目录，否则同步出来的 dist 就是「混合目录」 ----
PREFLIGHT = [
    ("publish 目录存在", os.path.isdir(SRC)),
    ("exe 是自包含外壳(>250KB)", os.path.isfile(os.path.join(SRC, "CelesteMusicPlayer.exe"))
     and os.path.getsize(os.path.join(SRC, "CelesteMusicPlayer.exe")) > 250000),
    ("带 hostfxr.dll", os.path.isfile(os.path.join(SRC, "hostfxr.dll"))),
    ("带 coreclr.dll", os.path.isfile(os.path.join(SRC, "coreclr.dll"))),
]
_cfg = os.path.join(SRC, "CelesteMusicPlayer.runtimeconfig.json")
PREFLIGHT.append(("runtimeconfig 含 includedFrameworks",
                  os.path.isfile(_cfg) and "includedFrameworks" in open(_cfg, encoding="utf-8").read()))

failed = [name for name, ok in PREFLIGHT if not ok]
if failed:
    print("PREFLIGHT_FAIL：源不是自包含 publish 产物 ->", failed)
    print("先跑 python _publish_selfcontained.py")
    raise SystemExit(2)
print("PREFLIGHT_OK 自包含源已确认（%s）" % SRC)

copied, bad = [], []
for base, dirs, files in os.walk(SRC):
    dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
    for name in files:
        src = os.path.join(base, name)
        rel = os.path.relpath(src, SRC)
        dst = os.path.join(DST, rel)
        try:
            same = os.path.exists(dst) and os.path.getsize(dst) == os.path.getsize(src) and md5(dst) == md5(src)
        except Exception:
            same = False
        if same:
            continue
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if os.path.exists(dst):
            os.remove(dst)
        shutil.copy2(src, dst)
        ok = os.path.getsize(dst) == os.path.getsize(src) and md5(dst) == md5(src)
        (copied if ok else bad).append(rel)

print("COPIED=%d" % len(copied))
for rel in copied[:30]:
    print("  + " + rel)
if len(copied) > 30:
    print("  ... 其余 %d 个省略" % (len(copied) - 30))
print("BAD=%d %s" % (len(bad), bad))

# ---- 陈旧残留检查：dist 里有、publish 里没有的文件会造成混合目录 ----
src_rel = set()
for base, dirs, files in os.walk(SRC):
    dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
    for name in files:
        src_rel.add(os.path.relpath(os.path.join(base, name), SRC))
stale = []
if os.path.isdir(DST):
    for base, dirs, files in os.walk(DST):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            rel = os.path.relpath(os.path.join(base, name), DST)
            if rel not in src_rel:
                stale.append(rel)
print("STALE=%d %s" % (len(stale), stale[:10]))
if stale:
    print("⚠ dist 里有 publish 中不存在的残留，可能造成运行时解析混乱，需要清目录重建。")
