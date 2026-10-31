#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_dep_closure.py —— 估算「把某个壳成员搬到活文件」会连锁拉进多少壳成员。

做法（粗粒度但够用）：
  1) 扫所有 .cs，用声明正则收集 MainWindow 的成员 -> {文件, 起始行, 结束行, 是否壳}
  2) 对指定成员，取其方法块文本，找其中出现过的「其它壳成员名」
  3) 递归展开，输出闭包（按壳文件分组）

用法：
  python tools/_w13_dep_closure.py SetupMinSizeHooks MainWindow_Loaded
"""
import os
import re
import sys
from collections import defaultdict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "CelesteMusicPlayer")
PROBE = os.path.join(REPO, "tools", "_w13_shell_exclude_probe.py")

MODS = r"(?:private|public|internal|protected|static|sealed|partial|async|readonly|const|event|override|virtual|extern|unsafe|new|required)"
DECL_RE = re.compile(
    r"(?m)^(?P<indent>[ \t]*)(?:" + MODS + r"\s+)+(?:[\w\.<>,\[\]\?]+\s+)*(?P<name>[A-Za-z_]\w*)\s*(?:\(|;|\{|=>|=)")


def load_shell():
    names = []
    for line in open(PROBE, encoding="utf-8"):
        m = re.match(r'\s*"(MainWindow\.[^"]+\.cs)"', line)
        if m:
            names.append(m.group(1))
    return set(names)


def split_lines(raw):
    return raw.split(b"\r\n") if b"\r\n" in raw else raw.split(b"\n")


def block_end(lines, i):
    """从声明行 i 起，配平花括号；expression-bodied 到分号。"""
    head = ""
    for k in range(i, min(i + 10, len(lines))):
        t = lines[k].decode("utf-8", "replace")
        head += t
        if "{" in t or ";" in t or "=>" in t:
            break
    if "=>" in head and "{" not in head:
        for k in range(i, len(lines)):
            if lines[k].decode("utf-8", "replace").rstrip().endswith(";"):
                return k
        return i
    depth, seen = 0, False
    for j in range(i, len(lines)):
        code = re.sub(r"//.*$", "", lines[j].decode("utf-8", "replace"))
        depth += code.count("{") - code.count("}")
        if "{" in code:
            seen = True
        if seen and depth == 0:
            return j
    return i


def build_index():
    shell = load_shell()
    idx = {}
    for fn in sorted(os.listdir(SRC)):
        if not fn.endswith(".cs"):
            continue
        path = os.path.join(SRC, fn)
        lines = split_lines(open(path, "rb").read())
        for i, ln in enumerate(lines):
            m = DECL_RE.match(ln.decode("utf-8", "replace"))
            if not m:
                continue
            name = m.group("name")
            if name in idx:
                continue
            idx[name] = {"file": fn, "start": i, "end": block_end(lines, i),
                         "shell": fn in shell, "lines": lines}
    return idx, shell


def text_of(info):
    return "\n".join(x.decode("utf-8", "replace") for x in info["lines"][info["start"]:info["end"] + 1])


def closure(roots, idx, shell, maxdepth=6):
    seen = {}
    frontier = [(r, 0) for r in roots]
    while frontier:
        name, d = frontier.pop()
        if name in seen or d > maxdepth:
            continue
        info = idx.get(name)
        if info is None:
            continue
        seen[name] = (info, d)
        if not info["shell"]:
            continue  # 活成员不再往下展开（它已经不需要壳）
        body = text_of(info)
        for other, oi in idx.items():
            if other == name or not oi["shell"] or other in seen:
                continue
            if re.search(r"\b" + re.escape(other) + r"\b", body):
                frontier.append((other, d + 1))
    return seen


def main():
    idx, shell = build_index()
    roots = sys.argv[1:]
    if not roots:
        raise SystemExit("给出成员名")
    allseen = {}
    for r in roots:
        info = idx.get(r)
        if not info:
            print(f"[?] 找不到 {r}")
            continue
        ch = closure([r], idx, shell)
        allseen.update(ch)
        print(f"\n=== {r}  ({info['file']}:{info['start']+1}) ===")
        shell_members = {k: v for k, v in ch.items() if v[0]["shell"]}
        if len(shell_members) == 1:
            print("  ✅ 自足：只涉及自己，无壳依赖")
        else:
            for k, (i2, d) in sorted(shell_members.items(), key=lambda kv: kv[1][0]["file"]):
                if k == r:
                    continue
                print(f"  → {k:42s} {i2['file']}:{i2['start']+1}  (深度{d})")
    groups = defaultdict(set)
    for k, (i2, d) in allseen.items():
        if i2["shell"]:
            groups[i2["file"]].add(k)
    print("\n--- 闭包合计（壳文件 -> 成员数）---")
    for f, ns in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        print(f"  {len(ns):3d}  {f}")


if __name__ == "__main__":
    main()
