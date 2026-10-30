#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_strip_appsdk_residue.py —— 清掉产物里的 Windows App SDK 陈旧残留。

背景（2026-10-30 实测）：
  W13 把 AppSDK 依赖断掉之后，`CelesteMusicPlayer.deps.json` 里已经**一处 Microsoft.UI 都没有**，
  但 publish 目录是从不清空的，于是几个月前留下的 18 个 `Microsoft.UI.*`（含
  `Microsoft.ui.xaml.dll` 整个 WinUI 运行时）一直躺在里面，并被 `_sync_webshell_dist.py`
  一起同步进交付目录。

  实测结论：移走这 18 个文件后，接口回归全过、真实起播照旧出声
  （`HiFi播放 mode=WasapiShared ok=True` + `[journal] 起播记录`）。也就是说它们确实是死重量。

安全约束：
  只有 `deps.json` 里**不含** `Microsoft.UI` 时才动手；一旦将来又把 AppSDK 加回来，
  这个脚本会拒绝执行，不会误删需要的运行时。

用法（仓库根目录）：
    python _strip_appsdk_residue.py                 # 只报告，不动文件
    python _strip_appsdk_residue.py --apply         # 移到备份目录
    python _strip_appsdk_residue.py --apply <目录>  # 指定目标目录（可多个）
"""
import glob
import os
import shutil
import sys
import time

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
PUBLISH = os.path.join(REPO, "CelesteMusicPlayer", "bin", "Release",
                       "net10.0-windows10.0.19041.0", "win-x64", "publish")
DEFAULT_TARGETS = [PUBLISH, os.path.join(REPO, "dist", "web-ui-20261028")]

argv = [a for a in sys.argv[1:]]
apply = "--apply" in argv
dirs = [a for a in argv if not a.startswith("--")] or DEFAULT_TARGETS
dirs = [d if os.path.isabs(d) else os.path.join(REPO, d) for d in dirs]

backup_root = r"C:\Users\MSI\_appsdk_residue_backup_" + time.strftime("%Y%m%d_%H%M%S")


def deps_mentions_appsdk(folder):
    deps = glob.glob(os.path.join(folder, "*.deps.json"))
    for path in deps:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                if "Microsoft.UI" in f.read():
                    return path
        except OSError:
            continue
    return None


total = 0
for folder in dirs:
    if not os.path.isdir(folder):
        print("SKIP 目录不存在: " + folder)
        continue
    offender = deps_mentions_appsdk(folder)
    if offender:
        print("REFUSE %s 的 deps.json 仍依赖 Microsoft.UI（%s），不动。" % (folder, os.path.basename(offender)))
        continue
    files = sorted(n for n in os.listdir(folder) if n.lower().startswith("microsoft.ui"))
    if not files:
        print("CLEAN %s（没有 Microsoft.UI* 文件）" % folder)
        continue
    print("FOUND %d 个（%s）：%s" % (len(files), folder, ", ".join(files)))
    total += len(files)
    if not apply:
        continue
    target = os.path.join(backup_root, os.path.basename(folder.rstrip("\\/")))
    os.makedirs(target, exist_ok=True)
    for name in files:
        shutil.move(os.path.join(folder, name), os.path.join(target, name))
    print("  -> 已移到 " + target)

if total and not apply:
    print("\n合计 %d 个待清理。加 --apply 执行（会先移到备份目录，不直接删）。" % total)
elif total:
    print("\n合计清理 %d 个，备份在 %s" % (total, backup_root))
else:
    print("\n没有需要清理的文件。")
