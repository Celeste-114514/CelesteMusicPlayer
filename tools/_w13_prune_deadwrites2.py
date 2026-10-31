#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_prune_deadwrites.py —— 删除「只写不读」的占位控件属性赋值语句。

判定粒度是（字段, 第一个属性名）这一对：只要某个 `F.P` 在全项目活代码里
从没出现在赋值左侧以外的地方，就认为它的写入是纯空操作（垫片属性 setter 是空的）。

跳过并上报的情况：
  * 事件订阅（`+=` 且右侧是 lambda / 方法组）
  * 语句以控制关键字开头（`if (x) F.P = ...;` 这种单行形式）
  * 语句跨行且中间出现 return/throw/break/continue/大括号
  * 语句找不到结尾（>4 行）

默认 dry-run；--apply 才写盘。改完必须自己编译验证。
"""
import os
import re
import sys
import glob
import collections

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(REPO, "CelesteMusicPlayer")
SHIM_EXTRA = {"MainWindow.LegacyControls.cs", "MainWindow.LegacyQueue.cs",
              "LegacyColorPalette.cs", "LegacyEnums.cs", "LegacyPropertyIdentities.cs",
              "LegacyViewState.cs", "LegacyLayoutState.cs"}
CTRL_KEYWORDS = ("if ", "if(", "else", "for ", "for(", "while ", "while(",
                 "switch", "case ", "default:", "using ", "lock ", "return ", "throw ")
GUARD = ("return", "throw", "break", "continue", "}", "{", "case ", "default")


def load_fields():
    src = open(os.path.join(PROJ, "MainWindow.LegacyControls.cs"),
               encoding="utf-8-sig", errors="ignore").read()
    return sorted(set(re.findall(
        r"internal\s+[A-Za-z0-9_.<>?,\[\] ]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\(\)", src)))


def active_files():
    out = []
    for p in glob.glob(os.path.join(PROJ, "*.cs")):
        n = os.path.basename(p)
        if n.startswith("LegacyWinUiShim") or n in SHIM_EXTRA:
            continue
        out.append(p)
    return out


def ends_stmt(line):
    s = re.sub(r"\s*//.*$", "", line.rstrip())
    return s.endswith(";")


def balanced(text):
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


def main():
    apply = "--apply" in sys.argv
    fields = load_fields()
    pair_re = re.compile(r"\b(" + "|".join(re.escape(f) for f in fields) + r")\.(\w+)")
    uses = collections.Counter()   # (field, prop) -> 出现总次数
    writes = collections.Counter()

    for p in active_files():
        for line in open(p, encoding="utf-8-sig", errors="ignore").read().splitlines():
            s = line.lstrip()
            if s.startswith("//"):
                continue
            for m in pair_re.finditer(line):
                key = (m.group(1), m.group(2))
                uses[key] += 1
                if re.match(r"\s*(=|\+=|-=|\*=|/=|\+\+|--)", line[m.end():]):
                    writes[key] += 1

    write_only = {k for k in writes if uses[k] == writes[k]}

    plan = {}
    skipped = []
    for p in active_files():
        name = os.path.basename(p)
        lines = open(p, encoding="utf-8-sig", errors="ignore").read().splitlines(True)
        kill = set()
        i = 0
        while i < len(lines):
            line = lines[i]
            if line.lstrip().startswith("//"):
                i += 1
                continue
            hit = None
            for m in pair_re.finditer(line):
                if (m.group(1), m.group(2)) in write_only:
                    hit = (m.group(1), m.group(2), m.end())
                    break
            if not hit:
                i += 1
                continue
            # 整行必须从字段名开始（排除 `x = F.P;` 之类）
            if not re.match(r"^\s*" + re.escape(hit[0]) + r"\." + re.escape(hit[1]), line):
                i += 1
                continue
            low = line.lstrip().lower()
            if any(low.startswith(k) for k in CTRL_KEYWORDS):
                skipped.append((name, i + 1, "inline-control", line.strip()))
                i += 1
                continue
            if "+=" in line and ("=>" in line or re.search(r"\b[A-Z]\w+\s*;", line)):
                skipped.append((name, i + 1, "event-subscription", line.strip()))
                i += 1
                continue
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
    print("只写（字段,属性）对 %d 个；将删除 %d 行，涉及 %d 个文件"
          % (len(write_only), total, len(plan)))
    for name, (_, _, k) in sorted(plan.items(), key=lambda kv: -len(kv[1][2])):
        print("  %4d  %s" % (len(k), name))
    if skipped:
        print("\n跳过 %d 处（需人工）：" % len(skipped))
        for name, ln, why, txt in skipped[:40]:
            print("  %-34s %5d  %-18s %s" % (name, ln, why, txt[:70]))

    if not apply:
        # 输出明细供人工核对（尤其是 RHS 含方法调用的）
        det = []
        for name, (_, lines, kill) in sorted(plan.items()):
            det.append("--- %s ---" % name)
            for idx in sorted(kill):
                det.append("  %5d  %s" % (idx + 1, lines[idx].rstrip()))
        open(os.path.join(REPO, "_w13_prune_plan.txt"), "w", encoding="utf-8").write("\n".join(det))
        print("\n[dry-run] 明细写到 _w13_prune_plan.txt；加 --apply 才会写盘")
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
