#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_move_method.py —— 把 partial class 里的方法整块搬到另一个 partial 文件。

为什么不用手抄：这些方法动辄几十行，手抄一次就等于引入一次错字风险。
脚本按「签名行 → 花括号配平」定位，连紧邻的 /// 注释一起搬，全程按字节处理保持 CRLF。

用法：
  python tools/_w13_move_method.py --src A.cs --dst B.cs --sig "private int Foo\\(" [--sig ...]
                                   [--note "块注释"]
每次搬运都会打印实际删掉的行号范围，便于 git diff 核对。
"""
import argparse
import io
import os
import re
import sys


def split_keep_crlf(raw):
    return raw.split(b"\r\n")


def find_method(lines, pattern):
    """返回 (start, end) —— start 是注释/签名的第一行，end 是方法闭合花括号所在行。"""
    rx = re.compile(pattern)
    for i, line in enumerate(lines):
        if not rx.search(line.decode("utf-8", "replace")):
            continue
        # 往上吸注释行
        start = i
        while start > 0:
            prev = lines[start - 1].strip()
            if prev.startswith(b"///") or prev.startswith(b"//"):
                start -= 1
            else:
                break
        # 从签名往下找第一个 {，再配平；若是 expression-bodied（=> ... ;），到分号行结束
        # ⚠ 这个分支必须存在：GeekPhosphorColor 就是 `=> PhosphorColor(...)` 单行体，
        # 没有它脚本会一路跑到下一个方法的 { 并把整段同时代走（2026-10-31 实测踩过）。
        head = ""
        for k in range(i, min(i + 12, len(lines))):
            text_k = lines[k].decode("utf-8", "replace")
            head += text_k
            if "{" in text_k or ";" in text_k:
                break
        if "=>" in head:
            for k in range(i, len(lines)):
                if lines[k].decode("utf-8", "replace").rstrip().endswith(";"):
                    return start, k
            raise SystemExit(f"expression-bodied 方法没找到分号结尾：{pattern}")

        depth = 0
        seen_brace = False
        for j in range(i, len(lines)):
            text = lines[j].decode("utf-8", "replace")
            # 去掉行注释里的花括号，避免误配平（这些方法里没有字符串含大括号的情况）
            code = re.sub(r"//.*$", "", text)
            depth += code.count("{") - code.count("}")
            if "{" in code:
                seen_brace = True
            if seen_brace and depth == 0:
                return start, j
        raise SystemExit(f"花括号没配平：{pattern}")
    raise SystemExit(f"没找到方法：{pattern}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--dst", required=True)
    ap.add_argument("--sig", action="append", required=True)
    ap.add_argument("--note", default="")
    a = ap.parse_args()

    src_raw = open(a.src, "rb").read()
    dst_raw = open(a.dst, "rb").read()
    src = split_keep_crlf(src_raw)

    blocks = []
    ranges = []
    for pattern in a.sig:
        s, e = find_method(src, pattern)
        blocks.append(src[s:e + 1])
        ranges.append((s, e))
        print(f"  找到 {pattern!r}: 行 {s + 1}..{e + 1}（{e - s + 1} 行）")

    # ⚠ 顺序很重要（2026-10-31 踩过）：先准备好目标内容并写目标，最后才动源文件。
    # 反过来一旦目标侧失败，源文件已经被删了方法，内容直接丢失。
    nl_t = b"\r\n" if b"\r\n" in dst_raw[-4000:] else b"\n"
    tail_marker = nl_t + b"    }" + nl_t + b"}" + nl_t
    if not dst_raw.endswith(tail_marker):
        raise SystemExit("目标文件尾部不是预期的 `    }\\n}\\n`，先确认结构")
    body = dst_raw[: -len(tail_marker)]
    body = body.replace(b"\r\n", nl_t)
    chunk = b""
    if a.note:
        chunk += nl_t
        for ln in a.note.split("\n"):
            chunk += ("        // " + ln if ln.strip() else "        //").encode("utf-8") + nl_t
    for blk in blocks:
        chunk += nl_t
        chunk += nl_t.join(blk) + nl_t
    open(a.dst, "wb").write(body + chunk + b"    }" + nl_t + b"}" + nl_t)
    print(f"已写入 {a.dst}（换行 {'CRLF' if nl_t == b'\\r\\n' else 'LF'}），开始删源文件")

    # 从后往前删，避免行号漂移
    for s, e in sorted(ranges, reverse=True):
        # 连同方法后面的两个空行一起删（保持段落间距不变）
        tail = e + 1
        blanks = 0
        while tail < len(src) and src[tail].strip() == b"" and blanks < 2:
            tail += 1
            blanks += 1
        del src[s:tail]

    open(a.src, "wb").write(b"\r\n".join(src))
    print(f"已搬 {len(blocks)} 个方法 -> {a.dst}")


if __name__ == "__main__":
    main()
