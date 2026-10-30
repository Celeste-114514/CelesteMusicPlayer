# RESULT — W13 压缩器参数模型

日期：2026-10-30。接手方案 A 的第三个模块（ReplayGain → SRC → **压缩器** → 声场/矩阵 → FIR → 机架/听音方案）。

## 这刀做了什么

压缩器的七个参数（阈值 / 比率 / 启动 / 释放 / 拐点 / 补偿增益 / 干湿比）与模块总开关，
从滑杆搬进 `DspCompParameters` 模型。

- `DspCompParameters.cs`（新）：唯一参数来源，自带滑杆元数据（范围/步长与旧滑杆逐条对齐）。
  `Load` 按旧滑杆的口径把越界值钳回范围内（例如存档里 threshold = -999 → -60、mix = 2.0 → 1.0）；
  干湿比对外仍按 **0..100 百分数**表达，模型内部存 0..1（与 `CompressorSettings.Mix` 一致）。
  **表头的「峰值 / GR」显示模式（`DspCompPeakToggle`）不在模型内** —— 它是表计显示选择，不是 DSP 参数，仍走旧适配。
- `MainWindow.DspRack.cs`：`PushDspRackToEngine` 的压缩器段改成 `rack.Compressor = CompParameters.Snapshot();`
  （声场/矩阵那两段仍从控件读，等它们各自的迁移刀）；`LoadDspRackUi` 先装载模型再投影控件；
  两条压缩器事件只导入被拖动的那个字段；新增 `ApplyCompParameters()` 给网页命令与页头电源开关共用
  （刷新读数 → 重画传递曲线 → 下发机架）。
- `MainWindow.DspComp.cs`：传递曲线与输入电平估算改读模型（原来回头读滑杆，属于"曲线跟着控件走"）。
- `MainWindow.DspPower.cs`：压缩器页头电源开关改由模型驱动。
- `MainWindow.DspRack.cs` 的 `DspCompActive()`（导航圆点/机架列表判据）：读模型，口径不变
  （启用 且 干湿比 > 0.1%）。
- `MainWindow.WebDsp.cs`：`comp` 页的 `get/module/setControl` 走模型；`setControl` 只重放压缩器这一路。

## 验证（本次在最终源码上重做）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功（第 1 次为环境性失败，0 条 `error CS`，第 2 次通过） | `_publish_retry_clean.py` 输出 |
| EqEditRegression | **96 项全过**（78 + **18 压缩器**） | 工具标准输出 |
| 压缩器真实网页 22 步 + 重启恢复 | 与迁移前二进制**逐字段 0 差异** | `work/comp-before.json` vs `work/comp-after.json` |
| SRC / RG 复跑 | 各自 0 差异（没碰坏旁边模块） | `work/comp-side-src.json`、`work/comp-side-rg.json` |
| W13InterfaceRegression | 冷启动/重载各 4041 首，0 失败，0 JS 异常，退出码 0 | 工具标准输出 |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → Time，队列下标 0→1→2→0 | `tools/W13TailAdvanceProbe.cjs` |
| `git diff --check` | 通过 | 本机 |

探针覆盖的语义点（每步都同时比网页回包与 `dsp-rack.json` 的 `Compressor` 段）：

- 七个滑杆逐条设值 → 回包与磁盘同步变化
- 电源开关关/开 → `Enabled` 跟着变，其它参数不动
- 越界：阈值 -999 → -60、比率 999 → 20、干湿 -5 → 0、拐点 -1 → 0、补偿 99 → 24
- 干湿比 50 → 磁盘 `Mix = 0.5`（回包仍是 50）
- 表头模式开关（`DspCompPeakToggle`，旧适配）仍可用且行为一致
- 关闭重开 → 七个参数 + 开关与盘里一致

## 产物核对

发布成功不等于产物正常（上一刀踩过 CsWinRT 生成器整批不产出的坑），所以每次都看体积 + 抽查关键类型：

- 体积 3,069,440（上一刀 3,065,344，+4 KB，符合一个模型类的量级），
  `DspCompParameters` 与 `WinRT.*GenericHelpers` 均在。
- publish 与 `dist/web-ui-20261028` 的 DLL SHA256 均为 `C07E90C34BB326A102D21903329C202F99A67E75C114B60929BFCFEB29247277`；
  同步脚本 `COPIED=2 / BAD=0 / STALE=0`。压缩器与旁证探针都在这份出货 DLL 上跑。

## 局限

- 主观听感、真实压缩行为（GR 深度/瞬态）不在本刀证据内：本刀只证明"参数来源"与"下发口径"没变。
- 声场 / 矩阵 / FIR 仍走控件适配，按方案 A 继续。
- `PushDspRackToEngine` 仍是"三模块一起下发"——等声场/矩阵也迁完，这一段才能收敛成一份机架快照。

## 回退

- 二进制：`dist/web-ui-20261028-pre-w13-comp-20261030/` 里的 `CelesteMusicPlayer.dll` / `.pdb`
  复制回 `dist/web-ui-20261028` 根目录（局部回退，不是整仓快照）。
- 源码：精确撤销本 commit 涉及的文件；不要 `git reset` / `git revert` 整仓。
