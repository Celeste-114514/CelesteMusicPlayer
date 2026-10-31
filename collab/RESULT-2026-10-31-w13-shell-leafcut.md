# W13 旧壳收容第一刀：用编译器量账，先砍掉五个整文件（2026-10-31）

> 这一轮不再用正则猜「哪些是死代码」。做法是：临时把旧界面壳文件排除出编译，
> 让 csc 把「还活着的东西到底借了什么」一条条报出来，再照单处理。

## 一、先纠正一条旧结论

`PLAN-2026-10-30-w13-legacy-shell.md` 第四节说「第 2 层：独立 WinUI 窗口还拴着壳，等用户重构」。
**这条已经过期**：17 个旧窗口/面板的 `.xaml` 与 `.xaml.cs` 现在全部在 csproj 里
`Page Remove` + `Compile Remove`（`ArtistAvatar*`、`AudioChainPanel`、`ColorPicker`、
`CurrentPlaylist`、`CustomSortOrder`、`Equalizer`、`FileAssociationPanel`、`MiniPlayer`、
`PlayQueue`、`ReplayGainScan`、`RoomCorrection`、`ScatteredPlay`、`SettingsPanel`、
`SettingsWindow`、`TagPanel`）。编译内只剩 `MainWindow`（`Content` 是一个空 Grid）和 `App`。

所以挡住「整批排除壳文件」的只剩两层：

1. 活代码（播放域 / 网页桥 / 宿主接线）还在借壳里的成员 —— **259 个成员**；
2. `LegacyControls` 的 463 个控件字段被活代码直接写 —— 第③步才收。

## 二、方法（可复跑）

```
python tools/_w13_shell_exclude_probe.py            # 排除 45 个壳文件后编译，聚合缺失成员
python tools/_w13_shell_exclude_probe.py --only=A.cs,B.cs   # 只排除指定文件
python tools/_w13_shell_exclude_probe.py --baseline          # 只编译，不动排除
```

脚本临时往 csproj 插入 `Compile Remove` → 编译 → 按「缺失成员名」聚合 → **无条件还原 csproj**
（字节级，保留 CRLF）。产出 `_w13_shell_exclude_errors.json` 与构建日志。

⚠ 进程被 kill 时 `finally` 不执行，csproj 会残留排除块。同目录 `.w13bak` 是注入前的原文件，
`os.replace` 回去即可。今天踩过一次，已确认还原后 `git diff` 为空。

## 三、账（2026-10-31 实测）

| 口径 | 错误数 | 说明 |
|---|---|---|
| 基线（不排除任何文件） | **0** | 树本身是干净的，25 秒一次编译 |
| 只排除 45 个壳文件 | 1,164 | 含 `LegacyBusinessState` 自己那 142 处互调 |
| 连 `LegacyBusinessState` 一起排除 | **1,024** | 1,018 个「名字不存在」+ 6 个类型/推断类 |

按文件（错误都出在**仍然编译**的文件里）：

`Playback3` 234、`Playback` 226、`WebDsp` 138、`Playback2` 98、`MainWindow.xaml.cs` 56、
`WebAudioSettings` 46、`DspStateHost` 26、`WebDspTools` 22、`WebOpra` 20、`Playback4` 18、
`ExternalFile` 14、`SettingsHost` 14、`WebDav` 12、`NativeTaskbar` 8、`WebTagService` 8、
`WebBrowseActions` 8、`MiniPlayerBridge` 6、`TrayCommandAdapter` 6、`WebShell` 6、`AudioChain` 4。

成员共 **259 个**。前 30 名（声明位置已逐个核对）：

| 次数 | 成员 | 定义在 | 性质 |
|---|---|---|---|
| 26 | `FormatTime` | `Misc.cs:3260` | 纯工具 |
| 22 | `_layoutIsWater` | `NowPlayingLayout.cs:43` | 旧布局旗标 |
| 20 | `ShowErrorAsync` | `Misc.cs:3278` | 原生错误弹窗 |
| 20 | `ContainsIgnoreCase` | `Misc.cs:1903` | 纯工具 |
| 18 | `IsHiFiModeSelected` | `Misc.cs:62` | UI 模式查询 |
| 18 | `ExitMultiSelectMode` | `Misc.cs:2517` | 旧列表多选 |
| 16 | `ApplyCategoryView` | `Misc.cs:1318` | 旧曲库分类页 |
| 14 | `_layoutIsLyrics` | `NowPlayingLayout.cs:52` | 旧布局旗标 |
| 14 | `ResolveAccentBrush` | `UiTheme.cs:1956` | 主题色 |
| 12 | `ExtractCoverBytes` | `Library2.cs:1007` | 封面取字节 |
| 12 | `FindLibraryIndex` | `Library2.cs:485` | 曲库查找 |
| 12 | `_opra` | `Opra.cs:16` | Opra 服务字段 |
| 10 | `PickerHostHandle` | `Misc.cs:184` | 选择器句柄 |
| 10 | `GeekUiStyleCached` | `UiTheme.cs:435` | UI 风格缓存 |
| 10 | `_audioFxLoading` | `EqDsp.cs:101` | DSP 装载闸门 |
| 10 | `IsHiFiSoftVolumeUiUnlocked` | `Misc.cs:75` | UI 模式查询 |
| 10 | `IsGeekUiStyleActive` | `UiTheme.cs:410` | UI 风格查询 |
| 10 | `LoadWaveformForCurrentAsync` | `Misc.cs:2969` | 波形 |
| 10 | `CommitLibraryNavigation` | `EqDsp.cs:2061` | 旧曲库导航 |
| 10 | `CreateBitmapFromBytesAsync` | `Misc.cs:2190` | 位图 |
| 10 | `CoverDecodeTargetPx` | `Misc.cs:2245` | 封面档位 |
| 10 | `ResetRowRunColors` | `UiTheme.cs:3100` | 旧列表行色 |
| 8 | `OpenFileLocationInExplorer` | `Misc.cs:2713` | 资源管理器 |
| 8 | `SetNowPlayingPaneVisible` | `Features.cs:1098` | 旧详情页 |
| 8 | `UpdateFavoriteButtonUi` | `Features.cs:967` | 旧收藏按钮 |
| 8 | `UpdateLibraryNavHighlight` | `Library2.cs:1200` | 旧左栏高亮 |
| 8 | `_layoutIsStage` | `NowPlayingLayout.cs:49` | 旧布局旗标 |
| 8 | `ApplyForegroundToDescendants` | `UiTheme.cs:1916` | 旧视觉树刷色 |
| 8 | `ClearForegroundOnDescendants` | `UiTheme.cs:1936` | 旧视觉树刷色 |
| 8 | `UpdateSelectAllMultiSelectButtonState` | `Misc.cs:2478` | 旧列表多选 |

## 四、这一刀砍了什么（提交 `161f68c`）

五个文件全部 `Compile Remove`，共 **700 行**：

| 文件 | 行 | 它原来在干什么 |
|---|---|---|
| `MainWindow.LegacyBusinessState.cs` | 228 | **生成代码**：给 100+ 个占位控件设默认值（Maximum/Minimum/Value/IsOn） |
| `MainWindow.LevelMeter.cs` | 217 | 建 33ms 定时器，往占位 Border/Rectangle 画电平条 |
| `MainWindow.DspStatus.cs` | 136 | 往三个占位 TextBlock 写「设备 / 输出模式 / 会话格式」 |
| `MainWindow.Crossfade.cs` | 59 | 把设置里的交叉淡化时长回填到占位滑块 |
| `MainWindow.FullScreen.cs` | 60 | 旧标题栏全屏按钮 + Esc 控制器 |

判断依据（不是「看起来没人用」）：

- `DspDeviceNameText` / `DspOutputModeText` / `DspSessionFormatText` 全项目**只有**
  `LegacyControls.cs` 的声明和 `DspStatus.cs` 的写入，**没有任何读点**；
- 全屏真正的实现是 `WebShellWindow`（网页命令走宿主 presenter），旧 `FullScreenController`
  只服务旧标题栏按钮；
- 电平表的控件由 `LegacyControls` 造，没有视觉树，定时器画完就丢。

接线一并删掉：构造函数里 4 次初始化调用、`UpdateDspDeviceStatus()` 3 个调用点、
`SetGeekFullScreenKey(_fullScreen…)` 1 处、`_levelMeterTimer?.Stop()` 的收尾 try。

顺手省掉的启动开销：一个 33ms 定时器、一次设置读盘 + 滑块赋值、一个全屏控制器、100+ 次占位控件赋值。

## 五、验证

| 关 | 结果 |
|---|---|
| 编译 | `rc=0`，`error CS` = 0 |
| 接口回归（publish） | 冷启动 4041、重载 4041、0 失败、0 JS 异常、正常退出 |
| 接口回归（dist 出货目录） | 同上 |
| 出声 | `W13PlaybackLedgerProbe` 实播两首：`HiFi播放 mode=WasapiShared ok=True`、`[journal] 起播记录`、`波形解码完成` |
| 启动日志 | 本次运行窗口内无 ERR/异常行 |
| 产物 | DLL **3,063,296**（改前 3,090,432），在 3.0–3.1MB 正常区间 |

出货已同步 `dist/web-ui-20261028`；回退件 `dist/web-ui-20261028-w13-leafcut-20261031/`；
上一版仍在 `dist/web-ui-20261028-pre-w13-audiosettings-20261031/`。

## 六、下一刀候选（按「能不能自动验」排）

1. **纯工具六件套**：`FormatTime`(26) `ContainsIgnoreCase`(20) `CoverDecodeTargetPx`(10)
   `ExtractCoverBytes`(12) `PickerHostHandle`(10) `FindAncestorListView`(2)
   → 搬进已有的纯函数/静态类，86 个调用点，零行为变化，回包+编译即可验。
2. **`ShowErrorAsync`(20)**：内部已经是原生弹窗那套，搬成服务；失败路径要真人点一次。
3. **布局旗标 58 处**（`_layoutIsWater/Lyrics/Stage/Center/Terminal`）：网页单界面下恒为 false，
   但每个分支要逐个确认「不执行会不会少做事」，不能整批删。
4. **DSP 状态机**（`_audioFxLoading` / `_opra` / `ApplyFirParameters` / `SetAudioFxEqFromSnapshot` /
   `PushEqUndoSnapshot`）：碰出声，放最后，按 `dsp get` 回包 + 实听验。
5. `LegacyControls`（463 个控件字段）：等上面几刀把写点降到可以按快照泵统一接管时再收。
