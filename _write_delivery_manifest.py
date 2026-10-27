# -*- coding: utf-8 -*-
"""重写交付哈希清单：人工维护的抬头 + 关键文件段，其余按 dist 当前内容重算。"""
import hashlib, os, time

ROOT = r"C:\Users\MSI\source\repos\CelesteMusicPlayer\dist\web-shell-dsp-tags-complete"

KEY = [
    "CelesteMusicPlayer.exe",
    "CelesteMusicPlayer.dll",
    "hostfxr.dll",
    "coreclr.dll",
    "WebShell/index.html",
    "WebShell/shell.js",
    "WebShell/shell.css",
    "WebShell/theme.js",
    "WebShell/theme.css",
    "WebShell/background.js",
    "WebShell/nav-layout.js",
    "WebShell/player-layout.js",
    "WebShell/transport.css",
    "WebShell/appearance.js",
    "WebShell/waveform.js",
    "WebShell/settings.js",
    "WebShell/settings.css",
    "WebShell/motion.js",
    "WebShell/motion.css",
    "WebShell/popup-manager.js",
    "WebShell/ui-controls.js",
    "WebShell/ui-controls.css",
    "WebShell/dsp.js",
    "WebShell/dsp-motion.css",
    "WebShell/tags-tools.js",
    "WebShell/sources.js",
]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


total = sum(len(fs) for _, _, fs in os.walk(ROOT))
lines = [
    "CelesteMusicPlayer 交付哈希清单",
    "生成时间：%s" % time.strftime("%Y-%m-%d %H:%M:%S"),
    "目录：%s" % os.path.basename(ROOT),
    "形态：自包含（目标机无需安装 .NET）· net10.0-windows10.0.19041.0 win-x64",
    "本次内容：网页外观全面可调 —— 24 套预设 / 明暗 / 元素细调 / 底栏（文字色·三围·按钮构成）"
    " / 字体与排版 / 交互状态与浮层 / 半圆导航（几何·项目） / 背景层（纯色·渐变·封面·图片） / 撤销重做",
    "文件总数：%d" % total,
    "",
    "== 关键文件 ==",
]
for rel in KEY:
    p = os.path.join(ROOT, rel.replace("/", os.sep))
    if not os.path.isfile(p):
        lines.append("%64s %10s  %s" % ("缺失", "-", rel))
        continue
    lines.append("%s %10d  %s" % (sha256(p), os.path.getsize(p), rel))

out = os.path.join(ROOT, "交付哈希清单.txt")
with open(out, "w", encoding="utf-8", newline="\r\n") as f:
    f.write("\n".join(lines) + "\n")
print("wrote %s total=%d entries=%d" % (out, total, len(KEY)))
