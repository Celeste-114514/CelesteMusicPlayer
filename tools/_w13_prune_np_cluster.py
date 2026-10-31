"""W13 第二刀：把「原生播放信息页布局 / 终端页 / 可视化」的接线全部剪掉。

这四个壳文件（NowPlayingLayout / Terminal / TerminalLyrics / Visualizer）只对
MainWindow.LegacyControls.cs 里的占位控件做 Grid.Set* / Visibility / 画布绘制，
那些控件从未挂进视觉树（`= new()`），ActualWidth 恒 0，所以整条路早就是死路。
本脚本删掉活文件里对它们的引用，随后四个文件整批退出编译。
"""
import os
import sys

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "CelesteMusicPlayer")


def load(name):
    path = os.path.join(SRC, name)
    with open(path, "rb") as f:
        raw = f.read()
    nl = b"\r\n" if b"\r\n" in raw else b"\n"
    return path, raw.split(nl), nl


def save(path, lines, nl):
    with open(path, "wb") as f:
        f.write(nl.join(lines))


def idx_of(lines, text, what, start=0):
    """定位一行；给了 start 就只在该行之后找第一处（结束锚点常重复，起点已足够定位）。"""
    b = text.encode("utf-8")
    hits = [i for i, l in enumerate(lines) if i >= start and l.rstrip() == b]
    if not hits:
        raise SystemExit(f"{what}: 未找到 -> {text!r}")
    if len(hits) > 1 and start == 0:
        raise SystemExit(f"{what}: 期望 1 处，实际 {len(hits)} -> {text!r}")
    return hits[0]


def cut(lines, first, last, back=0, fwd=0, eat_blank=True):
    a = idx_of(lines, first, "first")
    b = idx_of(lines, last, "last")
    a -= back
    b += fwd
    if b < a:
        raise SystemExit(f"区间反了: {first!r}")
    if eat_blank and b + 1 < len(lines) and lines[b + 1].strip() == b"":
        b += 1
    del lines[a:b + 1]


# (文件, first, last, back, fwd)
CUTS = [
    ("MainWindow.xaml.cs",
     '            // 播放信息页布局（经典/水面…）：订阅设置变更让"改设置即时生效"，并先按当前设置套用一次',
     '            ApplyNowPlayingLayout();', 0, 0),
    ("MainWindow.Settings.cs",
     '                // 极客界面只给终端这一个播放页布局，风格刚切过来要让布局立刻跟上',
     '                ApplyNowPlayingLayout();', 0, 0),
    ("MainWindow.Features.cs",
     '                // 按当前可视化档位重绘（波形 / 频谱柱 / 示波器 / 径向），',
     '                StartupLog.Write("主题色应用-波形重绘异常: " + waveEx.Message);', 2, 1),
    ("MainWindow.Features.cs",
     '                ApplyNowPlayingTitleColor();', '                ApplyNowPlayingTitleColor();', 0, 0),
    ("MainWindow.Features.cs",
     '            ApplyNowPlayingVisual();', '            ApplyNowPlayingVisual();', 0, 0),
    ("MainWindow.Features.cs",
     '                // 进入播放页时按当前设置套用布局（经典/水面），并重算倒影尺寸',
     '                UpdateNowPlayingReflectionGeometry();', 0, 0),
    ("MainWindow.GeekNowPlaying.cs",
     '            // 终端布局歌词面板：换磷光色时重染（主歌词页空/非空都不影响它）',
     '                RetintTerminalLyrics();', 0, 1),
    ("MainWindow.Lyrics.cs",
     '                // 终端布局歌词面板同步进入空态（无词可显示）',
     '                    ResetTerminalLyrics("NO LYRICS");', 0, 1),
    ("MainWindow.Lyrics.cs",
     '            // 终端布局歌词面板：歌词就绪，按 _lyricLines 重建行（不看布局时这里不进）',
     '                BuildTerminalLyricsRows();', 0, 1),
    ("MainWindow.Misc.cs",
     '        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)',
     '            DrawVisualSlot();', 0, 1),
    ("MainWindow.Playback.cs",
     '            // 黑胶布局的唱片/唱臂状态轮询（内部有布局判断，非黑胶布局时等于空转一次布尔比较）',
     '            EnsureVinylMotionTimer();', 0, 0),
    ("MainWindow.Playback3.cs",
     '            else if (!_waveformTimer.IsRunning)',
     '                DrawVisualSlot();', 0, 1),
    ("MainWindow.Playback3.cs",
     '            UpdateVinylArt(null);', '            SyncNowPlayingReflection(null);', 0, 0),
    ("MainWindow.Playback3.cs",
     '            // 未播放时也填充静态频谱，保证信息卡波形始终可见',
     '            DrawVisualSlot();', 0, 0),
    ("MainWindow.Playback3.cs",
     '                // 终端布局的左栏用的是同一批数据，这里顺带刷一次',
     '                UpdateTerminalInfo();', 0, 0),
    ("MainWindow.Playback3.cs",
     '            UpdateVinylArt(coverImage);', '            SyncNowPlayingReflection(coverBytes);', 0, 0),
    ("MainWindow.Playback3.cs",
     '            // 终端布局歌词面板：与主歌词页同一节拍推进（独立高亮，互不干扰）。',
     '                SyncTerminalLyricsToPosition(adj);', 0, 1),
    ("MainWindow.Playback3.cs",
     '                // 「水面」布局：非当前行按距离连续衰减，离当前句越远越淡（像没入水里）。',
     '                    row.Opacity = Math.Max(0.10, 1.0 - dist * 0.16);', 0, 1),
    ("MainWindow.Playback3.cs",
     '            if (changed || playing)',
     '                DrawVisualSlot();', 0, 1),
    ("MainWindow.Playback3.cs",
     '            // 终端布局：自己的频谱/电平/相位/播放头（非终端布局里这行直接返回）',
     '            UpdateTerminalTick();', 0, 0),
    ("MainWindow.UiTheme.cs",
     '                    // 首次激活兜底：确保信息卡波形已绘制（无论是否播放）',
     '                        DrawVisualSlot();', 0, 1),
    ("MainWindow.UiTheme.cs",
     '                // 终端页底板配色跟模式走；播放页布局菜单也要重建（极客下只有终端一项）',
     '                RebuildLayoutFlyout();', 0, 0),
    ("MainWindow.UiTheme.cs",
     '                DrawVisualSlot();', '                DrawVisualSlot();', 0, 0),
]

# 双击歌词跳转的日志：去掉终端面板那两个字段
LOG_OLD = [
    '                $"[终端歌词] 双击跳转 target={target.TotalSeconds:F2}s → 强制同步(force=true, applyOffset=false) " +',
    '                $"layout={_layoutIsTerminal} rows={_terminalLyricTexts.Count}");',
]
LOG_NEW = ['                $"[歌词] 双击跳转 target={target.TotalSeconds:F2}s → 强制同步(force=true, applyOffset=false)");']

LIB2_OLD = "            bool immersiveLayout = _nowPlayingLayoutName != LayoutClassic;"
LIB2_NEW = [
    "            // 布局名直接读设置：求值 _nowPlayingLayoutName 的原生播放页已下线（W13），这里只要那个值。",
    '            bool immersiveLayout = !string.Equals(AppSettingsStore.Load().NowPlayingLayout, "Classic", StringComparison.OrdinalIgnoreCase);',
]


def main():
    # 1) 按文件分组，组内从后往前删
    per_file = {}
    for name, first, last, back, fwd in CUTS:
        per_file.setdefault(name, []).append((first, last, back, fwd))

    for name, items in per_file.items():
        path, lines, nl = load(name)
        before = len(lines)
        # 先定位再倒序删，避免行号漂移
        spans = []
        for first, last, back, fwd in items:
            a = idx_of(lines, first, "first")
            b = idx_of(lines, last, "last", start=a)
            spans.append((a - back, b + fwd))
        for a, b in sorted(spans, reverse=True):
            if b + 1 < len(lines) and lines[b + 1].strip() == b"":
                b += 1
            del lines[a:b + 1]
        save(path, lines, nl)
        print(f"{name}: {before} -> {len(lines)}  (-{before - len(lines)})")

    # 2) 特殊：日志与 immersiveLayout
    path, lines, nl = load("MainWindow.Playback3.cs")
    a = idx_of(lines, LOG_OLD[0], "log")
    if lines[a + 1].rstrip() != LOG_OLD[1].encode("utf-8"):
        raise SystemExit("日志第二行不匹配")
    lines[a:a + 2] = [LOG_NEW[0].encode("utf-8")]
    save(path, lines, nl)
    print("Playback3.cs 日志已改一行为单行")

    path, lines, nl = load("MainWindow.Library2.cs")
    i = idx_of(lines, LIB2_OLD, "immersiveLayout")
    lines[i:i + 1] = [x.encode("utf-8") for x in LIB2_NEW]
    save(path, lines, nl)
    print("Library2.cs immersiveLayout 改为直接读设置")

    # 3) DrawWaveformBars：整段按花括号配对删除
    path, lines, nl = load("MainWindow.Misc.cs")
    start = None
    for i, l in enumerate(lines):
        if l.startswith(b"        private void DrawWaveformBars("):
            start = i
            break
    if start is None:
        raise SystemExit("找不到 DrawWaveformBars")
    depth = 0
    opened = False
    end = None
    for i in range(start, len(lines)):
        depth += lines[i].count(b"{") - lines[i].count(b"}")
        if b"{" in lines[i]:
            opened = True
        if opened and depth <= 0:
            end = i
            break
    if end is None:
        raise SystemExit("DrawWaveformBars 未闭合")
    # 带上紧邻上方的 XML 注释
    while lines[start - 1].strip().startswith(b"///"):
        start -= 1
    if end + 1 < len(lines) and lines[end + 1].strip() == b"":
        end += 1
    print(f"DrawWaveformBars: 删除 {start + 1}-{end + 1} 行（{end - start + 1} 行）")
    del lines[start:end + 1]
    save(path, lines, nl)


if __name__ == "__main__":
    main()
