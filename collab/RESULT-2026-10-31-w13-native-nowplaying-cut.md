# W13 第二刀：原生播放信息页 / 终端页 / 可视化退出编译（2026-10-31）

提交：`7ad24f8`

## 1. 为什么这批是死的（证据，不是推测）

`MainWindow.LegacyControls.cs` 里所有控件都是 `internal Xxx Xxx = new();` ——
**独立 new 出来，从未挂进任何视觉树**（`MainWindow.Content` 只有一个空 `Grid`）。

于是：

- `NowPlayingPane.ActualWidth / ActualHeight` 恒为 `0`；
- `UpdateNowPlayingCardLayout()` 的第一道守卫就是 `paneWidth <= 0 || paneHeight <= 0 → return`；
- **那 500 行布局数学一行都没执行过**（改窗口大小、切布局、启动都不会）。

同理 `WaveformCanvas` 尺寸恒 0，`DrawWaveformBars` 里的柱子几何全是退化值，
`WaveformCanvas_SizeChanged` 更是**从未被订阅**（全项目只有声明，没有 `+=`）。

## 2. 删了什么

### 2.1 方法本体（795 行）

| 方法 | 行数 | 说明 |
|---|---|---|
| `UpdateNowPlayingCardLayout` | ~490 | 按布局摆封面/信息/歌词/播放条，只在占位控件上 `Grid.Set*` |
| `UpdateNowPlayingBodyColumns` | 35 | 列宽 Star 计算 |
| `UpdateNowPlayingBodyRows` | 21 | 行高计算 |
| `PlayBarFullWidth` | 12 | 播放条跨列 |
| `WaveformCanvas_SizeChanged` | 5 | 从未被订阅 |
| `DrawWaveformBars` | 102 | 向 0 尺寸画布塞 Border 柱子 |

### 2.2 接线（12 个文件，共 800 行删除 / 11 行新增）

| 文件 | 删除 | 内容 |
|---|---|---|
| `MainWindow.Playback.cs` | 590 | 上面的布局数学 + `EnsureVinylMotionTimer()` 调用 |
| `MainWindow.Playback3.cs` | 46 | `DrawVisualSlot` ×3、`UpdateVinylArt`/`SyncNowPlayingReflection` ×2、`UpdateTerminalInfo`、`SyncTerminalLyricsToPosition`、`UpdateTerminalTick`、水面透明度、终端歌词日志字段 |
| `MainWindow.Misc.cs` | 108 | `DrawWaveformBars` + `WaveformCanvas_SizeChanged` |
| `MainWindow.Features.cs` | 17 | 主题色重绘块、`ApplyNowPlayingTitleColor`、`ApplyNowPlayingVisual`、`ApplyNowPlayingLayout` + `UpdateNowPlayingReflectionGeometry` |
| `MainWindow.UiTheme.cs` | 16 | 首次激活的静态频谱兜底、`ApplyTerminalChrome`、`RebuildLayoutFlyout`、`OnThemeColorChanged` 里的重绘 |
| `MainWindow.Lyrics.cs` | 10 | 两处终端歌词面板同步 |
| `MainWindow.GeekNowPlaying.cs` | 6 | `RetintTerminalLyrics` |
| `MainWindow.xaml.cs` | 4 | `HookNowPlayingLayoutSettings` + `ApplyNowPlayingLayout` |
| `MainWindow.Settings.cs` | 2 | `ApplyNowPlayingLayout` |
| `MainWindow.Library2.cs` | 3(+1) | `_nowPlayingLayoutName` 改为直接读 `AppSettingsStore.Load().NowPlayingLayout` |
| `MainWindow.NowPlayingLayout.cs` | 1 | 残留的 `UpdateNowPlayingCardLayout()` 调用 |

### 2.3 整文件退出编译（`<Compile Remove>`，2,566 行）

```
MainWindow.NowPlayingLayout.cs   848
MainWindow.Terminal.cs           760
MainWindow.TerminalLyrics.cs     345
MainWindow.Visualizer.cs         613
```

**合计 3,261 行**（第一刀是 700 行）。

## 3. 一个必须记住的坑

`Library2.cs` 里 `bool immersiveLayout = _nowPlayingLayoutName != LayoutClassic;`
决定专辑背景模糊半径。它读的字段只由被删的 `ApplyNowPlayingLayout()` 更新，
所以**改成直接读设置**而不是删掉：

```csharp
bool immersiveLayout = !string.Equals(
    AppSettingsStore.Load().NowPlayingLayout, "Classic", StringComparison.OrdinalIgnoreCase);
```

判断一个成员能不能删，除了「谁调用它」，还要问「它有没有顺手更新别人在用的状态」。

## 4. 验证

| 项 | 结果 |
|---|---|
| 全量重建 | 0 错误（`--baseline`，15s） |
| 接口回归（publish） | `cold=4041 reload=4041`、0 失败、0 JS 异常、正常退出 |
| 接口回归（dist） | 同上 |
| 实播两首 | `HiFi播放 ok=True`、`[journal] 起播记录`、`波形解码完成`，正常退出 |
| 启动日志 | 无新增 ERR（两条 ERR 均为改动前既有：`RefreshAudioChainPanelIfOpen` 全天有、`未知的播放栏操作` 来自探针本身） |
| DLL | 3,020,800 字节（改前 3,063,296；正常区间 3.0–3.1 MB） |

## 5. 发布时踩的坑（重复出现，必须按流程）

第一次 publish 出 **2,913,280** 字节坏产物（`GenericHelpers=0` / `VtableClasses=0`），
`_publish_retry_clean.py` 报 `PUBLISH_OK` —— `exit=0` 完全不可信。

`_publish_retry_clean.py` 只清 `obj/x64` 与 `bin/x64`，**不清 `bin/Release`**。
按老办法把 `obj`、`bin/Release`、`bin/x64` 三个目录整体 `os.replace` 移走后重编，得 3,020,800 字节正常产物。

> 附带发现：这次 `publish` 报 `move bin\x64 failed: [WinError 5] 拒绝访问`
> 但 `tasklist` 查不到 `CelesteMusicPlayer.exe`。目录被谁占着不影响结论，移走后即恢复。

## 6. 新的借用量账（供下一刀）

`python tools/_w13_shell_exclude_probe.py`（SHELL 清单已去掉 9 个已退出编译的文件）：

- 排除 35 个壳文件 → **916 个错误 / 234 个成员**（上一刀前是 1,024 / 259）
- 调用点最多的活文件：Playback3 **200**、Playback **164**、WebDsp **138**、Playback2 **98**、
  WebAudioSettings 46、xaml.cs 44、DspStateHost 26、WebDspTools 22
- 借用榜首：`FormatTime` 26、`ShowErrorAsync` 20、`ContainsIgnoreCase` 20、`IsHiFiModeSelected` 18、
  `ExitMultiSelectMode` 18、`ApplyCategoryView` 16、`ResolveAccentBrush` 14、`ExtractCoverBytes` 12

> ⚠ 探针本身有一次 Roslyn 崩溃（`AccessViolationException` in `NullableWalker`），
> `rc=1` 但 `error CS` 数为 0。**看到「0 错误」先确认 `rc` 与错误码**，重跑一次即可，不是代码问题。
