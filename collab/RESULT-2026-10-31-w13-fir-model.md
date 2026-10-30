# RESULT — W13 FIR / 房间校正参数模型

日期：2026-10-31。方案 A 第六个模块（ReplayGain → SRC → 压缩器 → 声场 + 矩阵 → **FIR / 房间校正** → 机架/听音方案）。

## 这刀做了什么

- `DspFirParameters.cs`（新）：房间校正的参数搬进模型，实现 `IDspParameterModel`。
  - 开关：`DspPowerFir`（页头电源）/ `RoomIrEnabledToggle`（模块开关）
  - 滑杆：`RoomIrGainSlider`（-12..12，步长 0.5）、`RoomTrimSlider`（-24..6，步长 0.5）
  - IR 不归网页管：文件选择器 / 清除走 `SetIr(path)` + `ClearIr()` 写进模型；
    taps / 源采样率 / 声道数由 `SetIrMetadata()` 在**落盘那一刻**回写（旧版是保存时从 `_roomIrLoaded` 取，口径照搬）。
  - 启用校验进模型：没有可用 IR（没选 / 文件不在盘上）时，模块开关**明确拒绝**而不是先画成开再报错。
- `MainWindow.RoomIr.cs`：三件事 —— 选文件（写模型）、`ProjectFirParameters()`（模型→控件）、
  `ApplyFirParameters()`（落盘 + 下发 + 刷新 + 同步电源开关）。面板自己不再持有任何业务值。
  IR 路径框、增益/微调读数也都从模型读。
- `MainWindow.DspStateHost.cs`：`ModelOf` 加一行；`ProjectedControls` 加 fir 组；新增 `NumberMessage` 助手。
- `MainWindow.WebDsp.cs`：`setControl` 走模型后按模块重放（多一个 `ApplyFirParameters` 分支）；
  删掉原来「FIR 四个控件改完就 `WebDspSaveRoom()`」那条尾巴。
- `MainWindow.WebDspTools.cs`：`WebDspSaveRoom()` 整个删掉；`firImport` / `firClear` / `firApply` 走模型；
  `info` 回包从模型取。
- `MainWindow.DspPower.cs`：FIR 页头电源开关改由模型驱动（读 `Effective` / `HasIr`）。
- `MainWindow.DspNav.cs`：导航圆点、trim 快捷预设、「安全启用」、「IR 信息与微调」全部改读模型。
- `MainWindow.DspRack.cs`：机架行状态里卷积那一行改读模型。
- `MainWindow.DspProfiles.cs`：**切听音方案时补一次 FIR 模型重载** —— FIR 不进 `dsp-rack.json`，
  原来的 `ReloadDspPanelFromStores()` 没管它，方案切过去后模型会停在旧值上。

## 验证（全部在最终源码 / 最终出货 DLL 上跑）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `_publish_retry_clean.py`（3 次，最后一次 18s） |
| 参数模型回归 | **142 项全过**（原 115 + 新增 27 项 FIR） | tools/EqEditRegression |
| FIR 真实网页 11 步 + 重启恢复 | 与迁移前二进制**逐字段 0 差异**（一处除外，见下） | `work/fir-before.json` vs `work/fir-after.json` |
| IR 不存在 / 加载失败 | 见下「三处有意变化」 | 同上的 `noIr` 段 |
| 禁用后保留路径 | 关掉卷积后 `IrPath` 仍在盘中 | 同上 step 8 |
| 声场 + 矩阵旁证复跑 | 0 差异 | `work/fm-before.json` vs `work/fm-side-fir.json` |
| 接口回归 | 冷启动 / 重载各 4041 首，0 失败，0 JS 异常，退出码 0 | tools/W13InterfaceRegression |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → 回到 Time，队列 0→1→2→0 | tools/W13TailAdvanceProbe |

探针覆盖的语义点（每步同时比 `fir` 模块回包与 `room-correction.json`）：
启用 → 增益 4.5 → trim -6 → 增益越界 999→12 → trim 越界 99→6 / -99→-24 →
电源关 / 开 → 关掉卷积后路径是否还在 → 收尾回中性值 → 关闭重开（重启恢复）→ IR 文件被删后的三个动作。

## 三处有意的行为变化（不是回归，是修旧毛病）

1. **网页里 FIR 页头电源开关以前点了不算数。** 旧代码改完 `DspPowerFir` 后调 `WebDspSaveRoom()`，
   而它保存的是**模块开关** `RoomIrEnabledToggle` 的值 —— 等于把页头开关的改动立刻覆盖掉
   （探针 step 6：before 设 `false` 后盘里仍是 `true`，after 正常变 `false`）。现在两者同源，拨哪个都生效。
2. **没有可用 IR 时启用卷积，不再出现「界面显示已开、盘里没变」的半改状态。** 旧版先画成开、
   再在保存时报错；现在模型直接拒绝，界面保持关、报错文案不变（「请先导入有效的脉冲响应文件。」）。
3. **IR 文件被删之后，trim 不再是"改不动"。** 旧版因为保存前统一校验 IR 文件是否存在，
   文件一丢连微调滑杆都会报「请先导入有效的脉冲响应文件」；现在 trim 照改照存，只有"启用卷积"会被拒。

`main` 段除第 1 条外逐字段 0 差异；`noIr` 段的差异就是第 2、3 条（探针里已单独分组，不混进主对比）。

## 产物

- DLL 3,076,096 字节（上一刀 3,074,560，+1.5 KB ≈ 一个模型类），
  sha256 前 16 位 `AD02B7F39064184A`。
- publish 与 `dist/web-ui-20261028` 的 DLL 完全一致；同步脚本 `COPIED=2 / BAD=0 / STALE=0`。
- ⚠ 我误把默认目标跑了一次同步：`dist/web-shell-dsp-tags-complete/` 也被更新成同一份最新 DLL。
  那个目录是早期交付目录，不在 git 里，内容与这次发布一致，但没有专门验证过（脚本当时报 STALE=24 残留文件）。

## 局限

- 主观听感（卷积后的声场/音色）不在本刀证据内；本刀只证参数来源、落盘与下发口径没变。
- 「卷积实际下发」只验到宿主侧：播放会话建立时 `SetRoomCorrection(RoomCorrectionStore.Load())`
  与实时 `ApplyRoomCorrection` 两条路径未改动；探针未播放，`_audioEngine` 为空时只落盘不进内核。
- 遗留：`RoomCorrectionWindow.xaml.cs`（独立房间校正窗口，2026-10-12 起已不再弹）仍直接写
  `RoomCorrectionStore`，不经过模型。它现在没有入口，暂未动。
- 机架顺序 `RackOrder`、旁路与听音方案协调仍是方案 A 的最后一小步。

## 回退

- 源码回退：`git checkout da81f5b -- CelesteMusicPlayer` 后重新 publish（该提交已验证可编）。
- 现成二进制回退：`dist/web-ui-20261028-pre-w13-fir-20261030/`（`324BA15C…`，= 声场/矩阵那一刀，
  也是本刀 before 基线）；`dist/web-ui-20261028-pre-w13-comp-20261030/` 更早一档。
- 本刀提交的出货 DLL 已存 `dist/web-ui-20261028-pre-w13-rack-20261031/`（`AD02B7F3…`），
  下一刀（机架顺序 / 听音方案）开始时就有现成回退件。
