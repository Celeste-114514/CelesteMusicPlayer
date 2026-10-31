"""删掉 LegacyControls.cs 里「活代码一句都不提」的垫片控件字段。

判据：字段名在除本文件外的所有 .cs 里整词出现 0 次（去注释后再数）。
字段类型全是垫片、初始化 `= new()`，没有构造副作用，所以没人提就是纯噪音。

用法：
  python tools/_w13_drop_unused_shim_fields.py            # dry-run
  python tools/_w13_drop_unused_shim_fields.py --apply
"""

import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "CelesteMusicPlayer")
TARGET = os.path.join(PROJ, "MainWindow.LegacyControls.cs")

DECL = re.compile(r"^\s*internal\s+[\w\.<>\[\],\? ]+\s+(\w+)\s*=\s*new\(\);\s*$")


def strip_comments(text, strip_strings=False):
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            if strip_strings:
                # 字符串里的名字不是字段引用（DSP 参数字符串表里就有大量控件名）
                i += 1
                while i < n:
                    if text[i] == "\\" and i + 1 < n:
                        i += 2; continue
                    if text[i] == '"':
                        i += 1; break
                    i += 1
                out.append('""')
                continue
            out.append(c); i += 1
            while i < n:
                out.append(text[i])
                if text[i] == "\\" and i + 1 < n:
                    out.append(text[i + 1]); i += 2; continue
                if text[i] == '"':
                    i += 1; break
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue
        out.append(c); i += 1
    return "".join(out)


def main():
    apply = "--apply" in sys.argv

    others = ""
    for p in glob.glob(os.path.join(PROJ, "*.cs")):
        if os.path.abspath(p) == os.path.abspath(TARGET):
            continue
        if os.sep + "obj" + os.sep in p:
            continue
        others += strip_comments(open(p, encoding="utf-8-sig", errors="ignore").read(), strip_strings=True) + "\n"

    raw = open(TARGET, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    crlf = raw.count(b"\n") == raw.count(b"\r\n")
    lines = raw.decode("utf-8-sig").splitlines(True)

    keep, drop = [], []
    for l in lines:
        m = DECL.match(l)
        if not m:
            keep.append((l, None))
            continue
        name = m.group(1)
        used = re.search(r"\b" + re.escape(name) + r"\b", others) is not None
        if used:
            keep.append((l, name))
        else:
            drop.append(name)

    print("字段总数 %d：活代码仍在用 %d，没人提 %d" % (len(keep), len([k for k in keep if k[1]]), len(drop)))
    print("保留：")
    for l, name in keep:
        if name:
            print("  " + name)
    if not apply:
        print("\n[dry-run] 加 --apply 才写盘")
        return

    out = [l for l, _ in keep]
    data = "".join(out).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    if crlf:
        data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(TARGET, "wb").write(data)
    print("\n已写盘，文件行数 %d -> %d" % (len(lines), len(out)))


if __name__ == "__main__":
    main()
