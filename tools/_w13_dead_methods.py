#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_dead_methods.py —— 找出「只摆弄假控件、没人调用」的方法。

W13 账本归零后仍然残留的，是「只为旧界面服务的方法」：它们把值写进空壳控件、
从空壳控件读值，自己没有任何调用方。这类方法整块删掉，比逐条删语句划算得多。

判定：
  * 方法体内对 MainWindow.LegacyControls.cs 那批假控件的引用 > 0
  * 全项目（活文件、剔除字符串字面量与注释）里找不到对它的调用
  * 不是 obvious 入口（Main / OnLaunched / 构造函数 / 事件重写）

输出 _w13_dead_methods.txt：
"""
import os
import re
import sys
import glob
import collections

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(REPO, "CelesteMusicPlayer")
SHIM_SKIP = {"MainWindow.LegacyControls.cs", "MainWindow.LegacyQueue.cs",
             "LegacyColorPalette.cs", "LegacyEnums.cs", "LegacyPropertyIdentities.cs",
             "LegacyViewState.cs", "LegacyLayoutState.cs"}
MODIFIERS = ("public", "private", "internal", "protected", "static", "async",
             "override", "virtual", "sealed", "unsafe", "extern", "partial")
NOT_METHOD = ("if", "for", "foreach", "while", "switch", "catch", "using", "lock",
              "return", "new", "get", "set", "else", "do", "try", "fixed", "throw")


def shim_fields():
    src = open(os.path.join(PROJ, "MainWindow.LegacyControls.cs"),
               encoding="utf-8-sig", errors="ignore").read()
    return sorted(set(re.findall(
        r"internal\s+[A-Za-z0-9_.<>?,\[\] ]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\(\)", src)))


def active_files():
    out = []
    for p in glob.glob(os.path.join(PROJ, "*.cs")):
        n = os.path.basename(p)
        if n.startswith("LegacyWinUiShim") or n in SHIM_SKIP:
            continue
        out.append(p)
    return out


def strip_noise(text):
    text = re.sub(r'"(?:[^"\\]|\\.)*"', '""', text)
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return text


SIG = re.compile(
    r"^[ \t]*(?:(?:" + "|".join(MODIFIERS) + r")\s+)+"
    r"(?:[A-Za-z0-9_.<>?,\[\] ]+?)\s+"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*\(")


def find_methods(lines, fname):
    """返回 [(start, end, name, sig_text)]，行号 0 基"""
    out = []
    i = 0
    n = len(lines)
    while i < n:
        m = SIG.match(lines[i])
        if not m:
            i += 1
            continue
        name = m.group(1)
        if name in NOT_METHOD:
            i += 1
            continue
        # 排除类/结构体声明（`class Foo(` 很少见，但 `record Foo(` 会命中）
        if re.search(r"\b(class|struct|record|interface|enum)\s+" + re.escape(name), lines[i]):
            i += 1
            continue
        # 表达式体
        j = i
        while j < n and "{" not in lines[j] and ";" not in lines[j] and "=>" not in lines[j]:
            j += 1
            if j - i > 6:
                break
        if j >= n:
            break
        if "=>" in lines[j] and "{" not in lines[j]:
            k = j
            while k < n and ";" not in lines[k]:
                k += 1
            out.append((i, k, name, lines[i].strip()))
            i = k + 1
            continue
        # 块体：花括号配平
        depth = 0
        started = False
        k = j
        while k < n:
            for ch in lines[k]:
                if ch == "{":
                    depth += 1
                    started = True
                elif ch == "}":
                    depth -= 1
            if started and depth <= 0:
                break
            k += 1
        out.append((i, min(k, n - 1), name, lines[i].strip()))
        i = k + 1
    return out


def main():
    fields = shim_fields()
    big = re.compile(r"\b(" + "|".join(map(re.escape, fields)) + r")\b")
    files = active_files()

    # 全项目调用计数（剔除字符串与注释后）
    all_text = {}
    call_count = collections.Counter()
    for p in files:
        raw = open(p, encoding="utf-8-sig", errors="ignore").read()
        code = strip_noise(raw)
        all_text[p] = raw
        for f in fields:
            pass
        # 粗略：`Name(` 出现 + 方法组引用（`+= Name` / `= Name;`）
        for m in re.finditer(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", code):
            call_count[m.group(1)] += 1
        for m in re.finditer(r"(?:\+=|=)\s*([A-Za-z_][A-Za-z0-9_]*)\s*[;,)]", code):
            call_count[m.group(1)] += 1

    rows = []
    for p in files:
        raw = all_text[p]
        lines = raw.splitlines()
        code_lines = strip_noise(raw).splitlines()
        for (s, e, name, sig) in find_methods(code_lines, os.path.basename(p)):
            body = "\n".join(code_lines[s:e + 1])
            shim_hits = len(big.findall(body))
            if shim_hits == 0:
                continue
            # 扣除声明行自身的一次 `Name(`
            calls = call_count[name] - 1
            rows.append((os.path.basename(p), s + 1, e + 1, name, shim_hits, calls, sig))

    dead = [r for r in rows if r[5] <= 0]
    alive = [r for r in rows if r[5] > 0]

    dead.sort(key=lambda r: -r[4])
    out = []
    out.append("含假控件引用的方法: %d（其中零调用 %d）" % (len(rows), len(dead)))
    out.append("零调用方法合计引用点数: %d" % sum(r[4] for r in dead))
    out.append("")
    out.append("=== 零调用方法（候选删除，按引用点数排序） ===")
    for f, s, e, name, hits, calls, sig in dead:
        out.append("  %4d点  %-34s %5d-%-5d %s" % (hits, f, s, e, name))
    out.append("")
    out.append("=== 有调用的方法（top 40） ===")
    alive.sort(key=lambda r: -r[4])
    for f, s, e, name, hits, calls, sig in alive[:40]:
        out.append("  %4d点 调用%3d  %-34s %5d-%-5d %s" % (hits, calls, f, s, e, name))

    txt = "\n".join(out)
    open(os.path.join(REPO, "_w13_dead_methods.txt"), "w", encoding="utf-8").write(txt)
    print(txt[:3000])
    print("\n... 写出 _w13_dead_methods.txt")


if __name__ == "__main__":
    main()
