#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_shell_exclude_probe.py —— 用编译器量出「旧界面壳到底还被谁拴着」。

做法：临时往 csproj 里加一批 <Compile Remove>（只针对旧界面壳文件），编译一次，
把 csc 报的 CS0103/CS0117/CS1061 等按「缺失的成员名」聚合。编译完无论成败都把
csproj 还原成原样（字节级，保留换行风格）。

产出：_w13_shell_exclude_errors.json（原始错误列表）+ 控制台聚合报告。

用法：
  python _w13_shell_exclude_probe.py            # 扫描模式（加排除后编译）
  python _w13_shell_exclude_probe.py --baseline # 只做基线编译，不加排除
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

REPO = r"C:\Users\MSI\source\repos\CelesteMusicPlayer"
PROJ_DIR = os.path.join(REPO, "CelesteMusicPlayer")
CSPROJ = os.path.join(PROJ_DIR, "CelesteMusicPlayer.csproj")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
LOG = os.path.join(REPO, "_w13_shell_exclude_build.log")
OUT = os.path.join(REPO, "_w13_shell_exclude_errors.json")

MARKER = b'    <Compile Remove="SettingsPanelControl.About.cs" />'

# 旧界面壳（旧 WinUI 页面/皮肤/特效/页面级代码）。不含：播放域、网页桥、宿主接线、
# LegacyControls（控件容器，第③步才收）、LegacyBusinessState。
SHELL = [
    "MainWindow.Features.cs",
    "MainWindow.Misc.cs",
    "MainWindow.Misc2.cs",
    "MainWindow.UiTheme.cs",
    "MainWindow.Library.cs",
    "MainWindow.Library2.cs",
    "MainWindow.EqDsp.cs",
    "MainWindow.EqApo.cs",
    "MainWindow.GeekShell.cs",
    "MainWindow.GeekNowPlaying.cs",
    "MainWindow.GeekDsp.cs",
    "MainWindow.GeekTransport.cs",
    "MainWindow.GeekTables.cs",
    "MainWindow.GeekBrowse.cs",
    "MainWindow.DspNav.cs",
    "MainWindow.DspRack.cs",
    "MainWindow.DspComp.cs",
    "MainWindow.DspField.cs",
    "MainWindow.DspMatrix.cs",
    "MainWindow.DspPower.cs",
    "MainWindow.DspCrossfeed.cs",
    "MainWindow.DspProfiles.cs",
    "MainWindow.ShellPages.cs",
    "MainWindow.SrcView.cs",
    "MainWindow.Src.cs",
    "MainWindow.OpraWall.cs",
    "MainWindow.Opra.cs",
    "MainWindow.Lyrics.cs",
    "MainWindow.ArtistDetail.cs",
    "MainWindow.Search.cs",
    "MainWindow.Settings.cs",
    "MainWindow.ThemeFiles.cs",
    "MainWindow.CelesteTheme.cs",
    "MainWindow.RoomIr.cs",
    "MainWindow.StartupLoading.cs",
]

ENV = dict(os.environ)
ENV.update({
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


def inject(excluded):
    raw = open(CSPROJ, "rb").read()
    if b"_w13_shell_exclude_probe" in raw:
        raise SystemExit("csproj 里已经有上次残留的排除块，先手工清掉")
    nl = b"\r\n" if b"\r\n" in raw else b"\n"
    lines = [nl + b"    <!-- _w13_shell_exclude_probe begin -->"]
    for name in excluded:
        lines.append(nl + b'    <Compile Remove="' + name.encode("utf-8") + b'" />')
    lines.append(nl + b"    <!-- _w13_shell_exclude_probe end -->")
    block = b"".join(lines)
    idx = raw.find(MARKER)
    if idx < 0:
        raise SystemExit("找不到插入点 MARKER")
    end = raw.find(b"</ItemGroup>", idx)
    if end < 0:
        raise SystemExit("找不到 ItemGroup 结尾")
    out = raw[:end] + block + nl + raw[end:]
    shutil.copy2(CSPROJ, CSPROJ + ".w13bak")
    with open(CSPROJ, "wb") as f:
        f.write(out)


def restore():
    bak = CSPROJ + ".w13bak"
    if os.path.exists(bak):
        os.replace(bak, CSPROJ)


def build():
    cmd = [DOTNET, "build", CSPROJ, "-c", "Debug", "-p:Platform=x64",
           "-p:WindowsPackageType=None", "-nologo", "-v:m", "-clp:NoSummary"]
    t0 = time.time()
    with open(LOG, "wb") as f:
        rc = subprocess.call(cmd, env=ENV, stdout=f, stderr=subprocess.STDOUT)
    return rc, time.time() - t0


ERR_RE = re.compile(r"^(?P<file>[^\(]+)\((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*?) \[")
NAME_RE = re.compile(r"'([^']+)'")

def parse():
    txt = open(LOG, "rb").read().decode("utf-8", "replace")
    errors = []
    for line in txt.splitlines():
        m = ERR_RE.match(line.strip())
        if not m:
            continue
        errors.append({
            "file": os.path.basename(m.group("file")),
            "line": int(m.group("line")),
            "code": m.group("code"),
            "msg": m.group("msg")[:300],
        })
    return errors


def summarize(errors):
    by_code = {}
    by_name = {}
    by_file = {}
    for e in errors:
        by_code[e["code"]] = by_code.get(e["code"], 0) + 1
        by_file[e["file"]] = by_file.get(e["file"], 0) + 1
        m = NAME_RE.search(e["msg"])
        key = m.group(1) if m else e["msg"][:60]
        by_name.setdefault(key, []).append(f'{e["file"]}:{e["line"]}')
    print("=" * 70)
    print("错误总数=", len(errors))
    print("按错误码:", dict(sorted(by_code.items(), key=lambda kv: -kv[1])))
    print("按文件:", dict(sorted(by_file.items(), key=lambda kv: -kv[1])[:20]))
    print("=" * 70)
    print("缺失成员（按出现次数）：")
    for name, sites in sorted(by_name.items(), key=lambda kv: -len(kv[1])):
        uniq = sorted(set(sites))
        print(f"  {len(sites):4d}  {name}")
        print(f"        @ {', '.join(uniq[:6])}{' …' if len(uniq) > 6 else ''}")
    return {"total": len(errors), "by_code": by_code, "by_name": {k: sorted(set(v)) for k, v in by_name.items()}, "errors": errors}


def main():
    baseline = "--baseline" in sys.argv
    only = None
    for a in sys.argv[1:]:
        if a.startswith("--only="):
            only = [x.strip() for x in a[len("--only="):].split(",") if x.strip()]
    names = only if only else SHELL
    if not baseline:
        inject(names)
    try:
        rc, secs = build()
        print(f"build rc={rc} 耗时 {secs:.0f}s  日志={LOG}")
        errors = parse()
        report = summarize(errors)
        report["rc"] = rc
        report["excluded_count"] = 0 if baseline else len(names)
        with open(OUT, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=1)
        print(f"报告已写入 {OUT}")
    finally:
        if not baseline:
            restore()
            print("csproj 已还原")


if __name__ == "__main__":
    main()
