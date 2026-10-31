#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_prune_deadwrites.py —— 删除「只写不读」占位控件的赋值语句。

只处理满足下面全部条件的语句，其余一律跳过并上报：
  1. 目标字段在 MainWindow.LegacyControls.cs 里，且全项目（活文件）从未被读；
  2. 语句是属性赋值（`=`）/ 复合赋值，不是事件订阅（`+= lambda`）；
  3. 语句独占一行，或以该行为起点、能按分号/括号配平找到结尾。

默认 dry-run，只打印将删除的行；--apply 才真正改文件。
改完必须自己跑一次编译（脚本不做）。
"""
import os
import re
import sys
import glob

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(REPO, "CelesteMusicPlayer")
SHIM_PREFIX = "LegacyWinUiShim"
SHIM_EXTRA = {"MainWindow.LegacyControls.cs", "MainWindow.LegacyQueue.cs",
              "LegacyColorPalette.cs", "LegacyEnums.cs", "LegacyPropertyIdentities.cs",
              "LegacyViewState.cs", "LegacyLayoutState.cs"}
CTRL_KEYWORDS = ("if ", "if(", "else", "for ", "for(", "while ", "while(",
                 "switch", "case ", "default:", "using ", "lock ", "return ")


def load_fields():
    src = open(os.path.join(PROJ, "MainWindow.LegacyControls.cs"),
               encoding="utf-8-sig", errors="ignore").read()
    return sorted(set(re.findall(
        r"internal\s+[A-Za-z0-9_.<>?,\[\] ]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\(\)", src)))


def active_files():
    out = []
    for p in glob.glob(os.path.join(PROJ, "*.cs")):
        n = os.path.basename(p)
        if n.startswith(SHIM_PREFIX) or n in SHIM_EXTRA:
            continue
        out.append(p)
    return out


def balanced(text):
    """括号/引号配平检查（粗粒度，够用）"""
    depth = 0
    in_str = None
    esc = False
    for ch in text:
        if in_str:
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == in_str:
                in_str = None
            continue
        if ch in "\"'":
            in_str = ch
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
    return depth == 0 and in_str is None


def ends_stmt(line):
    """该行是否是语句结尾：去掉尾部 // 注释后以 ; 结束。"""
    s = re.sub(r"\s*//.*$", "", line.rstrip())
    return s.endswith(";")


GUARD = ("return", "throw", "break", "continue", "}", "{", "case ", "default")


def main():
    apply = "--apply" in sys.argv
    fields = load_fields()
    fset = set(fields)
    big = re.compile(r"\b(" + "|".join(re.escape(f) for f in fields) + r")\b")

    # ---- pass 1: 统计读/写，确定只写字段 ----
    rw = {}
    for p in active_files():
        lines = open(p, encoding="utf-8-sig", errors="ignore").read().splitlines()
        for line in lines:
            s = line.lstrip()
            if s.startswith("//"):
                continue
            for m in big.finditer(line):
                f = m.group(1)
                rec = rw.setdefault(f, [0, 0])
                pat = re.compile(re.escape(f) + r"(?:\.\w+){1,3}\s*(?:=|\+=|-=|\*=|/=|\+\+|--)")
                if pat.search(line):
                    rec[1] += 1
                else:
                    rec[0] += 1
    write_only = set(f for f, (r, w) in rw.items() if r == 0 and w > 0)

    # ---- pass 2: 收集待删行 ----
    plan = {}
    skipped = []
    for p in active_files():
        name = os.path.basename(p)
        raw = open(p, encoding="utf-8-sig", errors="ignore").read()
        lines = raw.splitlines(True)
        kill = set()
        i = 0
        while i < len(lines):
            line = lines[i]
            s = line.lstrip()
            if s.startswith("//"):
                i += 1
                continue
            hit = None
            for m in big.finditer(line):
                if m.group(1) in write_only:
                    hit = m.group(1)
                    break
            if not hit:
                i += 1
                continue
            # 该行必须形如 `F.X = ...` 或 `F.X.Y = ...`
            if not re.match(r"^\s*" + re.escape(hit) + r"(?:\.\w+){1,3}\s*(=|\+=|-=|\*=|/=|\+\+|--)", line):
                i += 1
                continue
            # 事件订阅跳过
            if "+=" in line and ("=>" in line or re.search(r"\b[A-Z]\w+\s*;", line)):
                skipped.append((name, i + 1, "event-subscription", line.strip()))
                i += 1
                continue
            # 控制语句同一行 -> 跳过
            low = s.lower()
            if any(low.startswith(k) for k in CTRL_KEYWORDS):
                skipped.append((name, i + 1, "inline-control", line.strip()))
                i += 1
                continue
            # 找语句结尾（行尾 ; 才算，尾部 // 注释不算语句内容）
            j = i
            buf = ""
            while j < len(lines) and j < i + 4:
                buf += lines[j]
                if ends_stmt(lines[j]) and balanced(re.sub(r"\s*//.*$", "", buf)):
                    break
                j += 1
            else:
                skipped.append((name, i + 1, "unterminated", line.strip()))
                i += 1
                continue
            # 多行时，中间任何一行被 guard 命中就整段跳过（防吞掉 return/大括号）
            if j > i:
                bad = [lines[k].strip() for k in range(i + 1, j + 1)
                       if any(lines[k].lstrip().startswith(g) for g in GUARD)]
                if bad:
                    skipped.append((name, i + 1, "guard:" + bad[0][:20], line.strip()))
                    i += 1
                    continue
            for k in range(i, j + 1):
                kill.add(k)
            i = j + 1
        if kill:
            plan[name] = (p, lines, kill)

    total = sum(len(k) for _, _, k in plan.values())
    print("只写字段 %d 个；将删除 %d 行，涉及 %d 个文件" % (len(write_only), total, len(plan)))
    for name, (_, _, k) in sorted(plan.items(), key=lambda kv: -len(kv[1][2])):
        print("  %4d  %s" % (len(k), name))
    if skipped:
        print("\n跳过 %d 处（需人工）：" % len(skipped))
        for name, ln, why, txt in skipped[:40]:
            print("  %-32s %5d  %-18s %s" % (name, ln, why, txt[:70]))

    if not apply:
        print("\n[dry-run] 加 --apply 才会写盘")
        return

    for name, (p, lines, kill) in plan.items():
        raw = open(p, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        out = [l for idx, l in enumerate(lines) if idx not in kill]
        data = "".join(out).encode("utf-8")
        if bom:
            data = b"\xef\xbb\xbf" + data
        open(p, "wb").write(data)
    print("\n已写盘 %d 个文件" % len(plan))


if __name__ == "__main__":
    main()
