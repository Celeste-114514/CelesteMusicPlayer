#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_delete_dead_methods.py —— 删除「只摆弄假控件、无人调用」的方法。

安全前提：目标方法在全项目（剔除字符串与注释后）没有任何调用形式
（`Name(`、`+= Name`、`= Name;`）。这类方法在 W13 之后永远不会执行，
删掉不改变运行时行为，只是把旧界面残留的入口移除。

默认 dry-run；--apply 才写盘。
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

# 需要人工保留的（含真业务 / 名字对不上签名猜测），先列在这里，脚本不会动
KEEP = set()


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


def find_methods(lines):
    out = []
    i, n = 0, len(lines)
    while i < n:
        m = SIG.match(lines[i])
        if not m:
            i += 1
            continue
        name = m.group(1)
        if name in NOT_METHOD or re.search(r"\b(class|struct|record|interface|enum)\s+" + re.escape(name), lines[i]):
            i += 1
            continue
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
            out.append((i, k, name))
            i = k + 1
            continue
        depth, started, k = 0, False, j
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
        out.append((i, min(k, n - 1), name))
        i = k + 1
    return out


def main():
    apply = "--apply" in sys.argv
    fields = shim_fields()
    big = re.compile(r"\b(" + "|".join(map(re.escape, fields)) + r")\b")
    files = active_files()

    call = collections.Counter()
    ident = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]*\b")
    for p in files:
        code = strip_noise(open(p, encoding="utf-8-sig", errors="ignore").read())
        for m in ident.finditer(code):
            call[m.group(0)] += 1

    targets = {}
    all_kills = []
    for p in files:
        raw = open(p, encoding="utf-8-sig", errors="ignore").read()
        lines = raw.splitlines(True)
        code_lines = strip_noise(raw).splitlines()
        kills = []
        for (s, e, name) in find_methods(code_lines):
            body = "\n".join(code_lines[s:e + 1])
            if not big.search(body):
                continue
            if call[name] - 1 > 0:
                continue
            if name in KEEP:
                continue
            # 往前吃掉紧邻的注释/特性行
            st = s
            while st > 0:
                prev = lines[st - 1].strip()
                if prev.startswith("///") or prev.startswith("//") or prev.startswith("["):
                    st -= 1
                else:
                    break
            kills.append((st, e, name))
        if kills:
            targets[os.path.basename(p)] = kills
            all_kills.extend(kills)

    total = sum(e - s + 1 for s, e, _ in all_kills)
    print("待删方法 %d 个，共 %d 行" % (len(all_kills), total))
    for fname in sorted(targets):
        for s, e, name in targets[fname]:
            print("  %-32s %5d-%-5d %s" % (fname, s + 1, e + 1, name))

    if not apply:
        print("\n[dry-run] 加 --apply 才写盘")
        return

    # 按文件分组，从后往前删
    per_file = collections.defaultdict(list)
    for p in files:
        raw = open(p, encoding="utf-8-sig", errors="ignore").read()
        lines = raw.splitlines(True)
        code_lines = strip_noise(raw).splitlines()
        ks = []
        for (s, e, name) in find_methods(code_lines):
            body = "\n".join(code_lines[s:e + 1])
            if not big.search(body) or call[name] - 1 > 0 or name in KEEP:
                continue
            st = s
            while st > 0 and (lines[st - 1].strip().startswith(("///", "//", "["))):
                st -= 1
            ks.append((st, e))
        if ks:
            per_file[p] = (lines, ks)

    removed = 0
    for p, (lines, ks) in per_file.items():
        raw = open(p, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        crlf = b"\r\n" in raw
        for s, e in sorted(ks, reverse=True):
            del lines[s:e + 1]
            removed += e - s + 1
        data = "".join(lines).encode("utf-8")
        if bom:
            data = b"\xef\xbb\xbf" + data
        if crlf:
            data = data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
        open(p, "wb").write(data)
    print("\n已删 %d 行，涉及 %d 个文件" % (removed, len(per_file)))


if __name__ == "__main__":
    main()
