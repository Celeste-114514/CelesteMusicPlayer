#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_drop.py —— 小工具：删除方法定义 / 调用行。

用法：
  python tools/_w13_drop.py method <file.cs> <MethodName> [...]
  python tools/_w13_drop.py call   <file.cs> <MethodName> [...]
  python tools/_w13_drop.py callall <MethodName> [...]     # 全项目扫调用行

会保留原文件 BOM 与换行风格。
"""
import os
import re
import sys
import glob

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(REPO, "CelesteMusicPlayer")


def read(path):
    raw = open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    crlf = b"\r\n" in raw
    text = raw.decode("utf-8-sig")
    return text, bom, crlf


def write(path, text, bom, crlf):
    data = text.encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    if crlf:
        data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    open(path, "wb").write(data)


def drop_method(path, name):
    text, bom, crlf = read(path)
    lines = text.splitlines(True)
    for i, l in enumerate(lines):
        if re.match(r"\s*(private|internal|public|protected)", l) and \
           re.search(r"\b" + re.escape(name) + r"\s*\(", l):
            # 表达式体（`... => Xxx();`）没有花括号：只删这一行，
            # 否则花括号配平会一路吞到下一个 `}`（曾经把后面整个方法+类删掉）。
            if "=>" in l and l.rstrip().endswith(";"):
                st = i
                while st > 0 and (lines[st - 1].strip().startswith("//") or lines[st - 1].strip() == ""):
                    st -= 1
                del lines[st:i + 1]
                write(path, "".join(lines), bom, crlf)
                print("删表达式体方法 %-24s %s 行 %d" % (name, os.path.basename(path), st + 1))
                return True

            depth, started = 0, False
            for k in range(i, len(lines)):
                for ch in lines[k]:
                    if ch == "{":
                        depth += 1
                        started = True
                    elif ch == "}":
                        depth -= 1
                if started and depth <= 0:
                    st = i
                    while st > 0 and (lines[st - 1].strip().startswith("//") or lines[st - 1].strip() == ""):
                        st -= 1
                    # 自检：范围必须收在右括号上，否则说明配平跑飞了，拒绝删
                    if lines[k].strip() not in ("}", "};"):
                        print("!! 自检失败 %s：结束行不是右括号 -> %s" % (name, lines[k].strip()[:50]))
                        return False
                    del lines[st:k + 1]
                    write(path, "".join(lines), bom, crlf)
                    print("删方法 %-30s %s 行 %d-%d (%d 行)" % (name, os.path.basename(path), st + 1, k + 1, k - st + 1))
                    return True
    print("!! 未找到方法", name, path)
    return False


def drop_call(path, name):
    text, bom, crlf = read(path)
    lines = text.splitlines(True)
    out, n = [], 0
    for l in lines:
        if l.strip() in (name + "();", "await " + name + "();"):
            n += 1
            continue
        out.append(l)
    if n:
        write(path, "".join(out), bom, crlf)
    print("删调用 %-30s %s %d 行" % (name, os.path.basename(path), n))
    return n


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    mode = sys.argv[1]
    if mode in ("method", "call"):
        path = sys.argv[2]
        if not os.path.isabs(path):
            path = os.path.join(PROJ, path)
        for name in sys.argv[3:]:
            (drop_method if mode == "method" else drop_call)(path, name)
    elif mode == "callall":
        total = 0
        for name in sys.argv[2:]:
            for p in glob.glob(os.path.join(PROJ, "*.cs")):
                total += drop_call(p, name)
        print("合计删除调用", total)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
