# RESULT — W13 SRC（采样率升频）参数模型

日期：2026-10-30。接手方案 A 的第二个模块（顺序 ReplayGain → **SRC** → 压缩器 → 声场/矩阵 → FIR → 机架/听音方案）。

## 这刀做了什么

升频的三个用户选择（目标采样率 / 质量档位 / 量化抖动）以及「电源断开前是哪一档」的记忆，
从三个下拉框搬进 `DspSrcParameters` 模型。

- `DspSrcParameters.cs`（新）：唯一参数来源，自带选项表与网页元数据
  （7 档采样率、3 档质量、4 档抖动，文案与顺序跟旧下拉框一字不差）。
  `Load` 复刻旧 `IndexOfHz` / `IndexOfKey` 的回退口径：未知采样率 → 关闭，未知质量 → 均衡，未知抖动 → 关闭。
  **源采样率、设备支持能力、自动退回原因仍然属于引擎的每次播放事实，不进模型。**
- `MainWindow.Src.cs`：`InitializeSrcUi` 先装载模型、再让三个下拉照模型投影；
  三个 `SelectionChanged` 只做「把控件改动导入模型 → 各自的原有动作」，值不再从控件读。
  新增 `ApplySrcSettings()`：网页命令与页头电源开关共用的一条「落盘 + 下发引擎（三项全给）+ 刷新读数」。
  删掉 `SrcRateOptions` / `SrcQualityOptions` / `SrcDitherOptions` 与 `SelectedHz/IndexOfHz/IndexOfKey/SelectedKey/SelectedHint` 六个旧辅助。
- `MainWindow.DspPower.cs`：SRC 页头电源开关改由模型驱动；`_dspSrcMemoryIndex` 字段删除，记忆进模型 `MemoryIndex`。
- `MainWindow.DspNav.cs`：SRC 圆点判据读模型（一处）。
- `MainWindow.DspCrossfeed.cs`：crossfeed 预览曲线的工作采样率读模型（显示用，不是真值）。
- `MainWindow.SrcView.cs`：链路图与示意曲线的采样率/档位文案读模型；「关闭 / 96k / 整数倍」三张卡片仍走控件手势，
  由控件事件导入模型（兼容输入路径，未改交互）。
- `MainWindow.WebDsp.cs`：`src` 页的 `get/module/setControl` 走模型；SRC 的 `setControl` 只重放 SRC，不误触发整条 `ApplyDspToEngine`。

## 验证（本次在最终源码上重做）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `_publish_retry_clean.py` 输出 |
| EqEditRegression | **78 项全过**（25 EQ/下发 + 16 声道 + 17 RG + **20 SRC**） | 工具标准输出 |
| SRC 真实网页 17 步 + 重启恢复 | 与迁移前二进制**逐字段 0 差异** | `work/src-before.json` vs `work/src-after.json` |
| RG 复跑 | 与上一刀基线 **0 差异**（没碰坏旁边模块） | `work/src-side-rg.json` vs `work/rg-before.json` |
| W13InterfaceRegression | 冷启动/重载各 4041 首，0 失败，0 JS 异常，退出码 0 | 工具标准输出 |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → Time，队列下标 0→1→2→0，标题同步 | `tools/W13TailAdvanceProbe.cjs` |
| `git diff --check` | 通过；`DspNav.cs` 只有一处实质改动（历史 CRLF 未被顺手统一） | 本机 |

探针覆盖的语义点（每步都同时比网页回包与 `app-settings.json` 里的三个字段）：

- 选 96 kHz → 电源亮；选「关闭」→ 电源灭；192 kHz 换档
- 电源关 → 记住当前档位；电源开 → 回到记忆档位（96k 与 192k 两种场景都过）
- 组合框直接选「关闭」后，再用电源开 → 仍回到断开前的档位
- 越界下标 999 / −1 / 质量 9 / 抖动 9 → 全部忽略，不写盘、不改回包
- 同值重复设置不产生新写入
- 关闭重开 → 目标采样率 / 质量 / 抖动与盘里一致

## 产物核对（这次多查了一步，因为踩到了怪现象）

本刀第一次 publish 出的 DLL 比上一刀**小 108 KB**。核对元数据发现少了一批
`WinRT.CelesteMusicPlayerGenericHelpers.*` / `WinRT.CelesteMusicPlayerVtableClasses.*`
（CsWinRT 生成的 ABI 辅助类型，共 431 个方法体），而业务类型一个没少 —— 说明是**生成器那一轮没产出**，
不是代码被裁。重编同一份源码后产物恢复正常体积，两类件都不再缺。

因此本刀额外做了一次类型表比对（`System.Reflection.Metadata` 读两版 DLL）：

- 相对上一刀：`+DspSrcParameters`（23 个方法）、`+RateOption`、`+KeyedOption` 两个嵌套记录、
  `MainWindow` −3 个方法（删掉的 5 个旧辅助 + 新增的 2 个），异步状态机编号整体位移。
- 除上述之外**没有任何类型或方法增减**。

最终产物：publish 与 `dist/web-ui-20261028` 的 `CelesteMusicPlayer.dll` SHA256 均为
`7DD9DB2277A0A178B71207BD411A3A15B4D1C4061B5AC5290812C9AB8B582D68`，体积 3,065,344；
同步脚本报 `COPIED=2 / BAD=0 / STALE=0`；SRC 与 RG 探针都是在这份出货 DLL 上跑的。

## 局限

- 主观听感、各声卡实际支持的采样率、DoP/Native DSD 边界不在本刀证据内。
  「设备不支持目标采样率自动退回」是引擎既有行为，本刀没有改它，也没有伪造证据。
- 压缩器 / 声场 / 矩阵 / FIR 仍走控件适配，按方案 A 继续。
- 引擎起播时仍从 `app-settings.json` 读这三项（`AudioPlaybackEngine` 既有入口），
  模型改动是**同步落盘**后再下发，两者不会分叉；把引擎改成直接吃模型快照属于后续「参数快照显式交给输出服务」那一刀。

## 回退

- 二进制：`dist/web-ui-20261028-pre-w13-src-20261030/` 里的 `CelesteMusicPlayer.dll` / `.pdb`
  复制回 `dist/web-ui-20261028` 根目录（局部回退，不是整仓快照）。
- 源码：精确撤销本 commit 涉及的文件；不要 `git reset` / `git revert` 整仓。
