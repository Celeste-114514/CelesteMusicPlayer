# W13 第三刀：纯工具函数离开旧壳（2026-10-31）

提交：`b463dce`

## 1. 这刀删的是什么

不是死代码，是**住错了地方**的活代码：13 个成员被播放域/网页桥/标签服务大量借用，
定义却躺在旧壳文件里 —— 只要它们不搬走，壳就永远拆不掉。全部**逐行原样搬家**，行为零变化，
落点是新文件 `CelesteMusicPlayer/MainWindow.Utilities.cs`（`public sealed partial class MainWindow`）。

| 成员 | 原文件 | 借用处约数 |
|---|---|---|
| `ShowErrorAsync` + `GetXamlRootWhenReadyAsync` | Misc | 22 |
| `FormatTime` | Misc | 26 |
| `ContainsIgnoreCase` / `SearchNormalize` / `IsAsciiLetters` / `ContainsHan` / `PinyinInitials` / 繁简表 | Misc / Search | 20 |
| `IsHiFiModeSelected` / `IsHiFiSoftVolumeUiUnlocked` | Misc | 18+10 |
| `ExtractCoverBytes` + `CoverBytesCache` / `CoverBytesCacheMax` | Library2 | 12 |
| `CoverDecodeTargetPx` / `CoverRasterizationScale` | Misc | 12 |
| `PickerHostHandle` | Misc | 10 |
| `FindAncestorListView` | Search | 少量 |
| `FindLibraryIndex` | Library2 | 12 |
| `OpenFileLocationInExplorer` | Misc | 8 |
| `IsGeekUiStyleActive` + `UiStyleGeek` 常量 | UiTheme | 8 |
| `ResolveAccentBrush` | UiTheme | 14 |
| `PhosphorColor` / `GeekPhosphorColor` + 三档磷光色常量 | GeekTransport | （被 ResolveAccentBrush 带出） |

**`OpenFileButton_Click` 特意没搬**：它不是纯工具（落点是壳里的 `LoadAndAddFiles`），
搬走会给工具文件引入壳依赖，已退回 Misc.cs 原位。

## 2. 量的变化

- 探针借用量：**916 → 744 处**（成员 234 → 228）。榜首从 `FormatTime`(26) 换成了 `ExitMultiSelectMode`(18)。
- DLL：3,020,800 → **3,020,288** 字节（正常区间内；搬家不减重，重的是后面几刀）。
- 新工具：`tools/_w13_move_method.py` —— 按「签名行 → 花括号配平」整块搬方法，支持 expression-bodied。

## 3. 两个真踩了才写进代码的坑

### 3.1 静态字段声明顺序 = 启动崩溃

繁简对照表搬家时把两个声明的先后写反了：

```csharp
// ❌ 反了：cctor 先跑 _tradToSimp = BuildTradToSimpMap()，那时 _tradSimpPairs 还是 null
private static readonly Dictionary<char,char> _tradToSimp = BuildTradToSimpMap();
private static readonly (char,char)[] _tradSimpPairs = new[] { ... };

// ✅ 原顺序（HEAD 的 Misc.cs 就是这么排的）
```

症状：程序**秒崩，日志只有一句 `TypeInitializationException: MainWindow 类型初始化器`
（`set_Instance` 处），不报 InnerException**。第一次 publish 后实跑才发现，已修复并加警示注释。

> 教训：**搬静态字段必须连声明顺序一起搬，且验证必须包含"真的启动一次"** ——
> 本轮 Rebuild 0 错误、接口回归都没接住它，因为是类型初始化器炸在第一次 new MainWindow。

### 3.2 shim 遮蔽类型名

`LegacyWinUiShim.cs`（W13 过渡层）在 `Microsoft.UI.Xaml` 命名空间里声明了空类 `XamlRoot`，
同名声明会**遮住**真 WinUI 类型。在 Utilities.cs 里显式写 `XamlRoot? root = ...` 会选中 shim
（没有 `RasterizationScale`）→ CS1061。规避：跟原代码一样用 `var`/`dynamic` 语义，不写裸类型名。

## 4. 顺手核实的现状（不是本轮改动，写下来免得再怀疑一次）

- **错误对话框现在不弹窗**：`ShowErrorAsync` 依赖 `Content?.XamlRoot`，而旧控件全是 shim 纯数据对象，
  dynamic 链运行时恒 null → 40 次轮询后放弃 → 只写日志 `[对话框] ...`。弹窗功能随"界面脱离 WinUI"自然失效，
  报错信息仍完整落在 `logs/app/` 里。要恢复弹窗需走网页壳的 toast，另立任务。
- **封面缩放恒 1.0**：`CoverRasterizationScale` 旧实现走 dynamic，每次运行时抛 `RuntimeBinderException`
  被 catch 成 1.0。本轮把它改成明确 `return 1.0` 并写明原因；要支持高 DPI 另立任务（用 `GetDpiForWindow`）。

## 5. 验证

| 项 | 结果 |
|---|---|
| 全量重建（Debug Rebuild） | 0 错误 |
| publish DLL 体积 | 3,020,288（`GenericHelpers`=1、`VtableClasses`=5，符号齐） |
| 启动 | 进程存活、CDP 9347 开（修完 3.1 之后） |
| 接口回归（publish 目录） | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出 |
| 实播两首（`W13PlaybackLedgerProbe`） | Epilogue → Prologue，`[journal] 起播记录`、`波形解码完成 buckets=120`，正常退出 |
| 启动日志 | 无新增 ERR（两条 ERR 均为改动前既有：`RefreshAudioChainPanelIfOpen` 全天有、`未知的播放栏操作` 来自探针本身） |

## 6. 发布坑（第三次了，按流程走的）

第一次 publish 报 `PUBLISH_OK` 但 DLL = 2,913,280（`GenericHelpers=0`）。按老办法把 `obj`、
`bin/Release`、`bin/x64` **整个** `os.replace` 搬走（存到 `C:\Users\MSI\_objbak_w13s3\124908\`），
重新 publish 才得到正常产物。`_publish_retry_clean.py` 清不动 `bin/Release`，别信它的 OK。

## 7. 交付

- 出货目录已同步：`dist/web-ui-20261028/`（DLL 3,020,288，`COPIED=2`，`BAD=0`；`STALE=293` 为 W13 前残留，忽略）。
- 回退件：`dist/web-ui-20261028-pre-w13-utils-20261031/`（3,020,800 = 本轮改前）。
  ⚠ 注意：上一轮的改后产物存的是 `web-ui-20261028-w13-npcluster-20261031/`（不带 `pre-` 名字但内容正确），
  本轮回退件就是从它拷的 —— **同步出货前务必先拷回退件，否则旧 DLL 会被覆盖丢失**（本轮差点发生）。

## 8. 下一刀

榜首都是**界面动作**（`ExitMultiSelectMode` 18、`ApplyCategoryView` 16、`CommitLibraryNavigation` 10），
不能照这刀直接搬：要先判它们是「只刷旧控件（死）→ 删调用点」还是「顺手更新活状态（活）→ 搬」。
判断方法：看调用点所在方法本身是网页命令还是旧 XAML 事件（旧 XAML 已移除，事件链全死）。
`ApplyCategoryView` 里有 `ApplySort()`（真排序 `_playlist`），动它之前必须先画清这条链。
