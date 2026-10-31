#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""_w13_deadwrite_scan.py —— 扫描「只写不读」的占位控件字段与其写入语句。

背景：W13 之后 MainWindow 上还挂着 706 个 `= new()` 的假控件（LegacyControls.cs），
活代码里有两千多处对它们的引用。其中「只写、从不读」的字段，删除写入语句在语义上
完全等价（垫片对象的属性 setter 都是空实现）。

产出写到 _w13_deadwrite_scan.txt：
  * 只写字段列表（安全集）
  * 每个文件里属于这些字段的写入语句行号与原文
  * 被读字段列表（留给下一步人工处理）

用法：
  python tools/_w13_deadwrite_scan.py            # 汇总
  python tools/_w13_deadwrite_scan.py --detail   # 附每条写入语句
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


def load_fields():
    path = os.path.join(PROJ, "MainWindow.LegacyControls.cs")
    src = open(path, encoding="utf-8-sig", errors="ignore").read()
    return sorted(set(re.findall(
        r"internal\s+[A-Za-z0-9_.<>?,\[\] ]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\(\)", src)))


def active_files():
    out = []
    for p in glob.glob(os.path.join(PROJ, "*.cs")):
        name = os.path.basename(p)
        if name.startswith(SHIM_PREFIX) or name in SHIM_EXTRA:
            continue
        out.append(p)
    return out


def main():
    fields = load_fields()
    field_set = set(fields)
    # 一次编译一个大 alternation，用 finditer 取命中词
    big = re.compile(r"\b(" + "|".join(re.escape(f) for f in fields) + r")\b")
    # 写入形态：字段名后紧跟 .X（可再一层）再跟赋值/自增
    wr = re.compile(r"^\s*(?:\w+\s*=\s*)?FIELD(?:\.\w+){1,3}\s*(?:=|\+=|-=|\*=|/=|\+\+|--)")
    wr2 = re.compile(r"\bFIELD(?:\.\w+){1,3}\s*(?:=|\+=|-=|\*=|/=|\+\+|--)")

    use = {}
    for p in active_files():
        name = os.path.basename(p)
        lines = open(p, encoding="utf-8-sig", errors="ignore").read().splitlines()
        for i, line in enumerate(lines, 1):
            stripped = line.lstrip()
            if stripped.startswith("//") or stripped.startswith("///"):
                continue
            hits = set(m.group(1) for m in big.finditer(line))
            if not hits:
                continue
            for f in hits:
                rec = use.setdefault(f, {"w": [], "r": []})
                pat = re.compile(re.escape(f) + r"(?:\.\w+){1,3}\s*(?:=|\+=|-=|\*=|/=|\+\+|--)")
                if pat.search(line):
                    rec["w"].append((name, i, line.strip()))
                else:
                    rec["r"].append((name, i, line.strip()))

    write_only = sorted(f for f, r in use.items() if not r["r"])
    read_some = sorted(f for f, r in use.items() if r["r"])

    w_stmts = sum(len(use[f]["w"]) for f in write_only)
    r_stmts = sum(len(use[f]["r"]) for f in read_some)

    out = []
    out.append("占位控件字段总数: %d" % len(fields))
    out.append("被引用的字段: %d" % len(use))
    out.append("只写不读字段: %d  -> 可删写入语句 %d 条" % (len(write_only), w_stmts))
    out.append("含读操作字段: %d  -> 涉及读引用 %d 条（下一步人工）" % (len(read_some), r_stmts))
    out.append("")

    per_file = {}
    for f in write_only:
        for (name, line_no, text) in use[f]["w"]:
            per_file.setdefault(name, []).append((line_no, text))
    out.append("=== 只写语句按文件分布（top 40） ===")
    for name, items in sorted(per_file.items(), key=lambda kv: -len(kv[1]))[:40]:
        out.append("  %4d  %s" % (len(items), name))
    out.append("")
    out.append("=== 只写不读字段（全部 %d 个） ===" % len(write_only))
    for i in range(0, len(write_only), 6):
        out.append("  " + "  ".join(write_only[i:i + 6]))
    out.append("")
    out.append("=== 含读字段（top 80，按读引用数） ===")
    for f in sorted(read_some, key=lambda x: -len(use[x]["r"]))[:80]:
        out.append("  %4d 读 / %4d 写   %s" % (len(use[f]["r"]), len(use[f]["w"]), f))

    if "--detail" in sys.argv:
        out.append("")
        out.append("=== 只写语句明细 ===")
        for name in sorted(per_file):
            out.append("--- %s ---" % name)
            for (line_no, text) in sorted(per_file[name]):
                out.append("  %5d  %s" % (line_no, text))

    txt = "\n".join(out)
    open(os.path.join(REPO, "_w13_deadwrite_scan.txt"), "w", encoding="utf-8").write(txt)
    print(txt[:2500])
    print("...\n写出 _w13_deadwrite_scan.txt（%d 行）" % len(out))


if __name__ == "__main__":
    main()
