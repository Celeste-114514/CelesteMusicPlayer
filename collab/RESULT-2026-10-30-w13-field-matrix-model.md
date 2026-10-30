# RESULT — W13 声场 + 声道矩阵参数模型

日期：2026-10-30。接手方案 A 的第四、第五个模块（ReplayGain → SRC → 压缩器 → **声场 + 矩阵** → FIR → 机架/听音方案）。
两模块共用同一个 `PushDspRackToEngine`，所以合成一刀做。

## 这刀做了什么

- `DspParameterModel.cs`（新）：五个参数模型的统一契约 —— `Owns(name)` / `Describe(name)` / `Set(name, msg)`。
  宿主侧不再按"哪个模型"写一长串三元判断，改成 `ModelOf(name)` 一次定位。以后加模块只加一行。
  为了满足接口，前四刀的四个模型把这三个方法从 `internal` 改成 `public`（类本身仍是 `internal`）。
- `DspFieldParameters.cs`（新）：声场宽度（对外 0..200%，模型存 0..2）、中置增益（-18..18 dB）、
  侧向增益（-18..18 dB）+ 模块开关。`Load` 按旧滑杆范围钳制。
- `DspMatrixParameters.cs`（新）：四个系数（-2..2，步长 0.05）+ 模块开关。
- `MainWindow.DspRack.cs`：
  - `PushDspRackToEngine` 现在三个模块**全部**从模型取快照 —— 这段终于没有"从控件重建参数"了；
  - `LoadDspRackUi` 先装载两个模型再投影控件；
  - 声场/矩阵的开关与滑杆事件只导入被改动的那一个字段；新增 `ApplyFieldParameters()` / `ApplyMatrixParameters()`；
  - `DspFieldActive()` / `DspMatrixActive()` 读模型（圆点判据口径不变：开关开 且 参数偏离中性值）。
  - ⚠ 圆环拖动期间 `_dspFieldSuppressPush` 的"不落盘"逻辑保持原样：拖动中只更新模型，松手才下发。
- `MainWindow.DspPower.cs`：声场/矩阵两个页头电源开关改由模型驱动。
- `MainWindow.WebDsp.cs`：`stereo` / `matrix` 两页的 `get/module/setControl` 走模型，`setControl` 只重放对应模块。

## 验证（本次在最终源码上重做）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `_publish_retry_clean.py` 输出 |
| EqEditRegression | **115 项全过**（96 + **19 声场/矩阵**） | 工具标准输出 |
| 声场+矩阵真实网页 27 步 + 重启恢复 | 与迁移前二进制**逐字段 0 差异** | `work/fm-before.json` vs `work/fm-after.json` |
| 压缩器 / SRC / RG 复跑 | 各自 0 差异 | `work/fm-side-{comp,src,rg}.json` |
| W13InterfaceRegression | 冷启动/重载各 4041 首，0 失败，0 JS 异常，退出码 0 | 工具标准输出 |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → Time，队列下标 0→1→2→0 | `tools/W13TailAdvanceProbe.cjs` |
| `git diff --check` | 通过 | 本机 |

探针覆盖的语义点（每步同时比 `stereo`、`matrix` 两个模块回包与 `dsp-rack.json` 的 `StereoField`/`Matrix` 段）：

- 声场：宽度 130%、中置 -3.5 dB、侧向 4.5 dB；电源关/开；越界 999→200、-99→-18、99→18；收尾回中性值
- 矩阵：Ll/Rl/Lr/Rr 四项逐条改；电源关/开；越界 -9→-2、9→2；收尾回单位矩阵
- 关闭重开：两组参数与盘里一致

## 产物

- 体积 3,074,560（上一刀 3,069,440，+5 KB ≈ 两个模型 + 一个接口），
  `DspFieldParameters`/`DspMatrixParameters`/`IDspParameterModel` 与 `WinRT.*GenericHelpers` 均在。
- publish 与 `dist/web-ui-20261028` 的 DLL SHA256 均为 `324BA15CCFF691D60B7FF88357F7CA5C72E254D825BA7BBA3B33FBD6EBAD422B`；
  同步脚本 `COPIED=2 / BAD=0 / STALE=0`。声场/矩阵与三个旁证探针都在这份出货 DLL 上跑。

## 局限

- 主观听感（声场宽度/矩阵串音的实际效果）不在本刀证据内；本刀只证参数来源与下发口径没变。
- `PushDspRackToEngine` 仍是"读整份 RackState → 覆盖三模块 → 保存 + 下发"，
  机架顺序 `RackOrder`、旁路与听音方案协调留给方案 A 的最后一小步。

## 回退

- **本刀没有预留二进制回退目录**：动手前没先把上一版 DLL 拷出来，过程中三次 publish 把压缩器那一版覆盖了。
  要回退到压缩器那一刀：`git checkout 993b258 -- CelesteMusicPlayer` 后重新 publish（该提交已验证可编，约 20 秒）。
- 更早的稳定点仍有二进制可回退：`dist/web-ui-20261028-pre-w13-comp-20261030/`（`7DD9DB22…` = 声场/矩阵之前的 SRC 版）。
- 源码回退：精确撤销本 commit 涉及的文件；不要 `git reset` / `git revert` 整仓。
