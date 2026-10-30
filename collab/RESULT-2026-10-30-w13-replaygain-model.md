# RESULT — W13 ReplayGain 参数模型

日期：2026-10-30。按接手方案 A 的模块顺序（ReplayGain → SRC → 压缩器 → 声场/矩阵 → FIR）做的第一刀。
工作树里已有一份 20:26 落盘但未编译、未验证的半成品，本刀是在它基础上收口，不是推倒重写。

## 这刀做了什么

响度归一化（ReplayGain）的**用户可设参数**从控件搬进 `DspRgParameters` 模型：
模式（关闭/单曲/专辑）、前级增益、防削波，以及「电源断开前是哪一档」的记忆。

- `DspRgParameters.cs`（新）：唯一参数来源，自带网页元数据（滑杆 ±12 dB / 步长 0.5、三档模式项）。
  `Load` 跟旧滑杆一样把越界前级钳到 ±12 dB、非法模式落回「关闭」；`Set` 同值不产生变更。
  **曲目标签里的 Track/Album 增益与 peak 明确不进模型**——那是每首文件的事实，不是用户设置。
- `MainWindow.DspStateHost.cs`：模型实例 + 单向投影 `ProjectExtraParameters`（`rg` 页并入投影范围，
  `_audioFxLoading`/`_dspPowerSyncing` 双闸门防回写）+ 兼容输入只导入变化字段。
- `MainWindow.DspPower.cs`：RG 页头电源开关改由模型驱动（`SetModelControl("DspPowerRg")` → 投影 → 下发），
  不再靠改下拉框来间接改业务值；「断开前档位」从控件回填的记忆字段删除，由模型 `MemoryIndex` 持有。
- `MainWindow.Misc.cs`：读存档时先 `RgParameters.Load`，控件照模型投影；既有 `replaygain.json` 格式不变。
- `MainWindow.Playback.cs`：`ApplyReplayGainToEngine` 从模型取快照，删掉从控件重建参数的
  `CurrentAudioFxRgMode()`；持久化与下发仍是原来的两步。
- `MainWindow.WebDsp.cs`：`rg` 页的 `get/module/setControl` 走模型；RG 的 `setControl` 只重放 RG，
  不误触发整条 `ApplyDspToEngine`。
- `MainWindow.DspNav.cs`、`MainWindow.DspRack.cs`：RG 圆点/机架激活判据读模型（各一行）。

## 验证（本次在最终源码上重做）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `_publish_retry_clean.py` 输出 |
| EqEditRegression | **58 项全过**（25 EQ/下发 + 16 声道模型 + **17 RG 模型**） | 工具标准输出 |
| RG 真实网页 15 步 + 重启恢复 | 与迁移前二进制**逐字段 0 差异** | `work/rg-before.json` vs `work/rg-after.json` |
| 声道/安全 24 步复跑 | 与上一刀基线**0 差异**（本刀没碰坏旁边模块） | `work/rg-side-channel.json` vs `work/w13-verify-channel.json` |
| W13InterfaceRegression | 冷启动/重载各 4041 首，0 失败，0 JS 异常，退出码 0 | 工具标准输出 |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → Time，队列下标 0→1→2→0，底栏/详情页标题同步 | `tools/W13TailAdvanceProbe.cjs` |
| `git diff --check` | 通过；`DspNav.cs` 只有一行实质改动（历史 CRLF 未被顺手统一） | 本机 |

探针覆盖的语义点（每一步都同时比对网页回包与 `replaygain.json` 磁盘内容）：

- 前级 3.5 → 落盘 3.5；越界 +100/−100 → 12/−12（旧滑杆就是钳在这两端）
- 模式 直接选「单曲」+ 电源跟着亮；模式 选「关闭」+ 电源跟着灭
- 电源关 → 记忆当前档位；电源开 → 回到记忆档位（Album 场景与 Track 场景都过）
- 无效模式下标 999 / −1 → 忽略，不写盘、不改回包
- 防削波开关往返
- 关闭重开 → 模式/前级/防削波与盘里一致

迁移前后两次跑的是**同一份探针**（先跑迁移前二进制 `413EEE4A…`，再跑新二进制），所以 0 差异是等价性证据，
不是自证。探针会备份并还原 `replaygain.json`、`eq-curve.json`、`dsp-extra.json`，不碰歌单与标签。

产物对应关系：最终源码重编后 publish 与 `dist/web-ui-20261028` 的 `CelesteMusicPlayer.dll`
SHA256 均为 `086CE7FD0FB9F3391566E0B1A984911FAC9209A4635BD75C011E31D68FAAB509`，
RG 探针就是在**这份出货 DLL** 上复跑比对（`work/rg-final.json`，同样 0 差异）。
同步脚本报 `COPIED=2 / BAD=0 / STALE=0`（只换 DLL 与 PDB）。

## 方案点名的风险点核对

- 模型在填控件**之前**装载（`MainWindow.Misc.cs` 读档段），整段处于 `_audioFxLoading=true`，启动默认值不覆盖磁盘。
- 听音方案/设备配置重载走同一入口（`LoadAudioFxUiFromStore` → `RgParameters.Load`），不残留上一份模型。
- 旧控件仍会触发事件：`AudioFxRgMode_SelectionChanged` / `AudioFxRgPreamp_ValueChanged` /
  `AudioFxRgPreventClipping_Toggled` 顶部只导入 sender 那一个字段，不重建全部参数。
- 单独开/关 RG 不改变其它模块参数；电源开关的父模块联动语义保持原样。
- 前级允许小数显示（网页回包 3.5、1.5），落盘也保留小数——旧滑杆本来就没有取整，这里不新造取整规则。

## 局限

- 主观音质、各声卡输出模式、干净机验收不在本刀证据内。
- SRC / 压缩器 / 声场 / 矩阵 / FIR 仍走控件适配，按接手方案 A 逐模块迁移。
- `IsDspActiveForBadge` / 链路摘要这类**只读状态文字**仍读 `ReplayGainStore`（与 EQ、机架、声道同一口径）；
  业务路径（网页命令、下发引擎、圆点判据）已全部改为模型。存储层仍是 `replaygain.json`，格式未变。

## 回退

- 二进制：`dist/web-ui-20261028-pre-w13-replaygain-20261030/` 里的 `CelesteMusicPlayer.dll` / `.pdb`
  复制回 `dist/web-ui-20261028` 根目录（局部回退，不是整仓快照）。
- 源码：精确撤销本 commit 涉及的文件；不要 `git reset` / `git revert` 整仓。
  工作树里仍有大量与本刀无关的未跟踪文件与既有脏文件。
