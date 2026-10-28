# -*- coding: utf-8 -*-
"""更新助手事务模式冒烟：只在临时目录里跑，不碰真实安装。

A 事务文件不存在        -> 退出码 2，不写握手文件（主程序必须留在原地）
B 安装包哈希对不上      -> 退出码 5，写握手前中止、事务目录被清掉、结果文件记 checksum
C 完整链路（替身安装包）-> 退出码 0，文件铺好、版本通过、主程序被拉起、事务目录清空

路径布局刻意贴近真实：事务目录在别处，安装目录不在事务目录里面。
（真实环境里事务目录是 %LOCALAPPDATA%\\CelesteMusicPlayer\\update\\<id>，安装目录是程序所在处。）
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PUBLISH = ROOT.parent / "CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish"
UPDATER = PUBLISH / "CelesteUpdater.exe"
FAKE = ROOT / "fake/bin/single/CelesteMusicPlayer.exe"   # 单文件发布，免去 runtimeconfig 搬运
DATA = Path(os.environ["LOCALAPPDATA"]) / "CelesteMusicPlayer"
RESULT = DATA / "update-last-result.json"
LOG = DATA / "CelesteUpdater.log"

failures = []


def check(name, ok, detail=""):
    print(("  PASS " if ok else "  FAIL ") + name + (("  -> " + detail) if detail and not ok else ""))
    if not ok:
        failures.append(name)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run(args):
    proc = subprocess.run([str(UPDATER)] + args, capture_output=True, text=True, timeout=180)
    return proc.returncode


def fresh(path):
    if path.exists():
        shutil.rmtree(path, ignore_errors=True)
    path.mkdir(parents=True)
    return path


def write_transaction(work, payload):
    (work / "transaction.json").write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")


def main():
    if not UPDATER.exists():
        print("找不到 CelesteUpdater.exe：" + str(UPDATER))
        return 1
    if not FAKE.exists():
        print("找不到替身安装包，先编译 _updater_probe/fake：" + str(FAKE))
        return 1

    # 把真实环境里的结果文件/日志先挪开，跑完再放回去，别污染用户现场。
    backup = ROOT / "_env_backup"
    backup.mkdir(exist_ok=True)
    for path in (RESULT, LOG):
        if path.exists():
            shutil.copy2(path, backup / path.name)
            path.unlink()
    started = time.time()

    try:
        print("A 事务文件不存在")
        work = fresh(ROOT / "case-a")
        ready = ROOT / "case-a.ready"
        if ready.exists():
            ready.unlink()
        code = run(["--transaction", str(work / "nope/transaction.json"), "--pid", "0", "--ready", str(ready)])
        check("退出码 2（参数不可用）", code == 2, "实际 " + str(code))
        check("没有写握手文件", not ready.exists())

        print("B 安装包哈希对不上")
        work = fresh(ROOT / "case-b")
        package = work / "CelesteMusicPlayer-Setup-26.11.1.exe"
        package.write_bytes(b"not a real installer")
        install_b = fresh(ROOT / "case-b-install")
        write_transaction(work, {
            "Id": "case-b", "Version": "26.11.1.0", "PackageKind": "fd",
            "ExpectedSha256": "0" * 64, "PackageFileName": package.name,
            "InstallDirectory": str(install_b), "RestartPath": "",
            "BytesTotal": package.stat().st_size, "Stage": "Installing",
        })
        ready = ROOT / "case-b.ready"
        if ready.exists():
            ready.unlink()
        code = run(["--transaction", str(work / "transaction.json"), "--pid", "0", "--ready", str(ready)])
        check("退出码 5（哈希不一致）", code == 5, "实际 " + str(code))
        check("没有写握手文件（主程序不该退出）", not ready.exists())
        check("坏包被清掉，事务目录已删除", not (work / "transaction.json").exists())
        result = json.loads(RESULT.read_text(encoding="utf-8")) if RESULT.exists() else {}
        check("结果文件记下 checksum 失败", result.get("ErrorCode") == "checksum", str(result))

        print("C 完整链路（替身安装包）")
        work = fresh(ROOT / "case-c")
        install = fresh(ROOT / "case-c-install")
        package = work / "CelesteMusicPlayer-Setup-26.11.1.exe"
        shutil.copy2(FAKE, package)
        write_transaction(work, {
            "Id": "case-c", "Version": "26.11.1.0", "PackageKind": "fd",
            "ExpectedSha256": sha256(package), "PackageFileName": package.name,
            "InstallDirectory": str(install),
            "RestartPath": str(install / "CelesteMusicPlayer.exe"),
            "BytesTotal": package.stat().st_size, "Stage": "Installing",
        })
        ready = ROOT / "case-c.ready"
        if ready.exists():
            ready.unlink()
        code = run(["--transaction", str(work / "transaction.json"), "--pid", "0", "--ready", str(ready)])
        check("退出码 0（装完并已重启）", code == 0, "实际 " + str(code))
        check("握手文件已写", ready.exists())
        check("主程序装到位", (install / "CelesteMusicPlayer.exe").exists())
        check("主界面资源装到位", (install / "WebShell/index.html").exists())
        check("安装包已删", not package.exists())
        check("事务目录已清空", not (work / "transaction.json").exists())
        result = json.loads(RESULT.read_text(encoding="utf-8")) if RESULT.exists() else {}
        check("结果文件记成功", bool(result.get("Succeeded")), str(result))
        check("结果文件记已重启", bool(result.get("Restarted")), str(result))
        launched = install / "app-launched.txt"
        for _ in range(20):
            if launched.exists():
                break
            time.sleep(0.25)
        check("主程序确实被拉起来了", launched.exists())
        if ready.exists():
            ready.unlink()
    finally:
        for path in (RESULT, LOG):
            if (backup / path.name).exists():
                shutil.copy2(backup / path.name, path)
            elif path.exists():
                path.unlink()
        shutil.rmtree(backup, ignore_errors=True)
        # 助手日志里留一份副本，方便人工核对整条时间线
        try:
            if LOG.exists():
                shutil.copy2(LOG, ROOT / "updater-run.log")
        except OSError:
            pass

    print()
    if failures:
        print("FAIL: " + "，".join(failures))
        return 1
    print("PASS: 事务缺失/哈希不符/完整安装三条路径都符合预期（耗时 %.1fs）。" % (time.time() - started))
    return 0


if __name__ == "__main__":
    sys.exit(main())
