"""W13: 删除播放信息页的布局数学（只对无视觉树的占位控件做 Grid.Set* / Margin 计算）。

依据：NowPlayingPane 等控件全部来自 MainWindow.LegacyControls.cs 的 `= new()`，
从未挂进任何视觉树 → ActualWidth/ActualHeight 恒 0 → UpdateNowPlayingCardLayout
在 `paneWidth <= 0 || paneHeight <= 0` 处立即返回，方法体是永不执行的死代码。
"""
import os
import sys

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "CelesteMusicPlayer")

PLAYBACK = os.path.join(SRC, "MainWindow.Playback.cs")
FEATURES = os.path.join(SRC, "MainWindow.Features.cs")
LAYOUT = os.path.join(SRC, "MainWindow.NowPlayingLayout.cs")

DROP_LINES = {
    FEATURES: [b"                DispatcherQueue.TryEnqueue(() => UpdateNowPlayingCardLayout());"],
    LAYOUT: [b"                    UpdateNowPlayingCardLayout();"],
}


def load(path):
    with open(path, "rb") as f:
        raw = f.read()
    nl = b"\r\n" if b"\r\n" in raw else b"\n"
    return raw.split(nl), nl


def save(path, lines, nl):
    with open(path, "wb") as f:
        f.write(nl.join(lines))


def find_line(lines, exact):
    hits = [i for i, l in enumerate(lines) if l.rstrip() == exact]
    if len(hits) != 1:
        raise SystemExit(f"期望唯一命中，实际 {len(hits)}：{exact!r}")
    return hits[0]


def brace_end(lines, start):
    depth = 0
    opened = False
    for i in range(start, len(lines)):
        depth += lines[i].count(b"{") - lines[i].count(b"}")
        if b"{" in lines[i]:
            opened = True
        if opened and depth <= 0:
            return i
    raise SystemExit(f"未找到闭合花括号，起点 {start}")


def main():
    lines, nl = load(PLAYBACK)
    before = len(lines)

    # 1) 删重入守卫字段（只被待删方法使用）
    i = find_line(lines, b"        private bool _isUpdatingNowPlayingLayout;")
    if lines[i - 1].strip() == b"":
        del lines[i - 1:i + 1]
    else:
        del lines[i]

    # 2) 删 UpdateNowPlayingCardLayout 整段（含 XML 注释），精确止于 PlayBarFullWidth 的闭合括号
    i = find_line(lines, b"        private void UpdateNowPlayingCardLayout()")
    end = brace_end(lines, find_line(lines, b"        private void PlayBarFullWidth(int columnSpan)"))
    start = i
    while start > 0 and lines[start - 1].strip().startswith(b"///"):
        start -= 1
    # 补齐中间两个只在被删段里调用的方法，一并带走：确认它们落在同一区间
    seg = b"\n".join(lines[start:end + 1])
    for name in (b"UpdateNowPlayingBodyColumns", b"UpdateNowPlayingBodyRows", b"PlayBarFullWidth"):
        if name not in seg:
            raise SystemExit(f"{name.decode()} 不在待删区间内，放弃")
    del lines[start:end + 1]
    if lines[start].strip() == b"":
        del lines[start]

    # 3) 删三处调用点
    for exact in (
        b"            NowPlayingPane.SizeChanged += (_, _) => UpdateNowPlayingCardLayout();",
        b"                        UpdateNowPlayingCardLayout();",
        b"                UpdateNowPlayingCardLayout();",
    ):
        j = find_line(lines, exact)
        del lines[j]

    save(PLAYBACK, lines, nl)
    print(f"Playback.cs {before} -> {len(lines)} (-{before - len(lines)})")

    for path, needles in DROP_LINES.items():
        ls, n2 = load(path)
        b4 = len(ls)
        for needle in needles:
            j = find_line(ls, needle)
            del ls[j]
        save(path, ls, n2)
        print(f"{os.path.basename(path)} {b4} -> {len(ls)}")


if __name__ == "__main__":
    main()
