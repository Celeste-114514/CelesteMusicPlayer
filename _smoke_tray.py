# -*- coding: utf-8 -*-
"""托盘改版冒烟：启动 publish 产物，看日志里托盘相关行是否正常。

只看三件事：
1) 进程没有秒退（秒退=0x80008082 说明是沙箱低完整性标签问题，不是代码）；
2) 日志出现「托盘: Win32 通知图标已创建 (成功=True)」；
3) 日志出现「[托盘菜单] WebView2 菜单已就绪」（预热成功，右键才是即开的）。
"""
import os
import re
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.abspath(__file__))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    REPO,
    r"CelesteMusicPlayer\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\CelesteMusicPlayer.exe",
)
SECONDS = int(sys.argv[2]) if len(sys.argv) > 2 else 25
LOGS_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", r"C:\Users\MSI\AppData\Local"),
    "CelesteMusicPlayer", "logs",
)


def read_all_logs(offset):
    """把 logs 目录（含 app/audio/dsp/lib 等子目录）下所有 .log 里新增的行拼起来。"""
    out = []
    if not os.path.isdir(LOGS_DIR):
        return out
    for root, _dirs, files in os.walk(LOGS_DIR):
        for name in sorted(files):
            if not name.lower().endswith(".log"):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, LOGS_DIR)
            start = offset.get(rel, 0)
            try:
                size = os.path.getsize(path)
                if size < start:
                    start = 0
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    fh.seek(start)
                    chunk = fh.read()
                offset[rel] = size
                for line in chunk.splitlines():
                    if line.strip():
                        out.append("[%s] %s" % (rel, line))
            except OSError:
                pass
    return out


def snapshot_offsets():
    offsets = {}
    if not os.path.isdir(LOGS_DIR):
        return offsets
    for root, _dirs, files in os.walk(LOGS_DIR):
        for name in files:
            if not name.lower().endswith(".log"):
                continue
            path = os.path.join(root, name)
            rel = os.path.relpath(path, LOGS_DIR)
            try:
                offsets[rel] = os.path.getsize(path)
            except OSError:
                pass
    return offsets


def main():
    if not os.path.isfile(EXE):
        print("EXE_NOT_FOUND", EXE)
        return 2
    offsets = snapshot_offsets()
    print("EXE=", EXE)
    proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    time.sleep(SECONDS)
    died_early = proc.poll() is not None
    code = proc.poll()
    if not died_early:
        proc.kill()
        try:
            proc.communicate(timeout=10)
        except Exception:
            pass
    else:
        _, err = proc.communicate(timeout=10)
        tail = (err or b"").decode("utf-8", "replace").strip().splitlines()[-4:]
        print("EARLY_EXIT code=%s (0x%08X)" % (code, code & 0xFFFFFFFF if code > 0 else 0))
        for line in tail:
            print("  stderr:", line)

    lines = read_all_logs(offsets)
    print("NEW_LOG_LINES=", len(lines))

    hits = [l for l in lines if "托盘" in l or "Tray" in l or "tray" in l]
    print("--- tray lines ---")
    for line in hits:
        print(line)
    if not hits:
        print("(none)")

    bad = re.compile(r"RPC_E_WRONG_THREAD|UnhandledException|Fatal|AccessViolation|"
                     r"0x8000|System\.[A-Za-z.]*Exception")
    noise = re.compile(r"XamlRoot|PrimaryLanguageOverride|全透明|rate limit exceeded")
    suspects = [l for l in lines if bad.search(l) and not noise.search(l)]
    print("SUSPECT_LINES=", len(suspects))
    for line in suspects[:15]:
        print("  !", line)

    relay = [l for l in lines if "已有实例在运行" in l]
    if relay:
        print("RELAYED=1 说明这次启动被已在运行的实例挡了，没验证到新产物")

    ok = (not died_early) and any("Win32 通知图标已创建 (成功=True)" in l for l in lines)
    print("RESULT=", "OK" if ok else "CHECK")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
