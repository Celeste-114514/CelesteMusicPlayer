# W13 战报：小件宿主四刀（批 6-9，2026-10-31 下午）

接 DSP 收官（232/98）后连打四刀，账面 **232 → 176 错 / 98 → ~88 成员**，
全天累计 **1,024 → 176（消化 83%）**。四刀全部独立提交、四道验证全绿。

## 批次一览

| 批 | 提交 | 内容 | 账面 |
|---|---|---|---|
| 6 | `d5982dd` | **输出设备域整体转活**：新 `MainWindow.AudioOutputHost.cs`（658 行），从壳 Features.cs 搬 28 个成员——输出模式/设备切换、AB 循环全套（A/B 点/开关/清除/提示/面板刷新）、链路摘要四兄弟（Build*Line）、ApplyLinkVisual、输出设备热插拔监听全家（watcher/回调/去抖/提示）；字段 9 个手工搬。WebAudioSettings(24) 下榜 | 232→206 |
| 7 | `db1ee42` | **设置实时应用三件**：新 `MainWindow.SettingsApplyHost.cs`，ApplyAlwaysOnTopFromSettings / ApplyOverlayPreferenceFromSettings / SyncHeaderColumnsFromState。媒体库监听三件试搬后**退回**（OnLibraryWatchDebounceTick 连着 RescanLocalLibraryButton_Click → Library.cs 重扫深水区） | 206→198 |
| 8 | `bbf7bb7` | **导航条目域 + 任务栏域**：新 `MainWindow.NavHost.cs`——NavItemRef 条目模型 + NavItems 懒构建（BuildNavItems/AddNavItem/BuildNavGeekGlyph）+ SetNavEntryVisibility + CollapseFolderAt + AddFolderToLibraryAsync；NativeTaskbar.cs 域内补全（_taskbarButtons + 缩略图泵三件从 xaml.cs/UiTheme.cs 搬入）。WebDav/WebFolders 下榜 | 190→178 |
| 9 | `aacbd5c` | **标签编辑器入口**：新 `MainWindow.MetadataHost.cs`，OpenTagEditForFiles（统一入口，原 TagEditorWindow 已删）。标签保存后刷新试搬后**退回**（连着 ApplyCategoryView 分类大 switch） | 178→176 |

## 关键判定

1. **「试搬→探针→退回」纪律**：批 7/9 各有一次试搬被探针抓住倒贴账（新活文件借壳成员），
   按「行为不变」原则整块退回壳里，留待对应深水域刀（媒体库重扫域 / 分类视图域）一起收。
   退回用 `_w13_move_method.py` 反向搬运，零手抄。
2. **字段手工搬铁律再次生效**：批 6 的 9 个字段（_abLoop 族 4 + _audioCombosLoading +
   _preferredOutputDeviceId 等）全部手工按行搬，配平工具不碰分号结尾成员。
3. **两次被工具尾部检查拦住**：dst 骨架尾部必须「body 末行以 } 或 ; 结尾」——
   `#endregion`、纯注释行、空类体（只有 `{`）都过不去；骨架先垫一个真实成员即可。
4. **using alias 要跟着方法走**：UiTheme.cs 的 `using Shapes = Microsoft.UI.Xaml.Shapes;`
   被 AddNavItem 隐式依赖，搬走后 NavHost.cs 编译报 CS0103，补 alias 解决。

## 四道验证（每刀同口径）

| 项 | 结果 |
|---|---|
| 全量重建（Debug x64, Rebuild） | 每刀 0 错误 |
| publish | DLL 3,018,240→3,018,752 字节、269 个文件（全部正常标志） |
| 接口回归 | W13InterfaceRegression：exitCode 0、jsErrors 空、failures 0 |
| 实播 | W13DspBatchPlaybackSmoke：ok=true，Diana pos 8.09 真实推进 |

## 剩余账面（176，全部连着深水区）

- 播放/列表视觉族（Playback 56 / Playback3 42 / Playback2 30 / Playback4 6 = 134）：
  ScheduleFitColumns 列宽族、多选视觉、极客行、文件夹搜索导航等——下一大战场；
- xaml.cs(24)：启动链（InitializeMusicPlayer2Features/MainWindow_Loaded/窗口钩子）；
- SettingsHost(6)：ApplySettingsLive → ApplyUiStyleMode(UiTheme) / ApplyExtendedSettingsLive(歌词/背景域)；
- ExternalFile(6)：启动加载 → UpdateStartupLoadingStatus/RestoreLastLibraryAsync（StartupLoading.cs/Library.cs）；
- AppFlow(2)：ToggleFavoriteForCurrent → ApplyCategoryView（分类域）；
- WebBrowseActions(2)：PlayCueFileAsync（Features.cs，CUE 播放链）。

下一仗建议：xaml.cs 启动链（24）是独立小域可整刀；Playback 族按「列宽/多选/极客行」分批。
