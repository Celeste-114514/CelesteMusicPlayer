# RESULT — W13 声道/安全参数模型（收口自 Codex 交接）

日期：2026-10-30。接手自 `Documents/Codex/2026-10-30/nowplayingpath-13-01-13-01-15/outputs/W13-完整接手方案-WorkBuddy-2026-10-30.md`。

## 这刀做了什么

声道平衡（含交叉馈送）与安全限幅的**业务参数所有权**从控件搬进 `DspExtraParameters` 模型：

- `DspExtraParameters.cs`（新）：唯一参数来源，自带网页元数据（滑杆范围/步长、开关、单声道选项），
  不依赖任何控件。`Snapshot()` 才把交叉馈送强度按原逻辑取整落盘；`Channel.Enabled` 原值保留，
  「单独开 crossfeed 也要生效」只在快照里折算。
- `MainWindow.DspStateHost.cs`（新）：统一模型实例 + 单向显示投影（`ProjectExtraParameters`，
  `_audioFxLoading`/`_dspPowerSyncing` 双闸门防回写，退出时恢复外层原值）+ 兼容输入只导入变化字段
  （`ImportExtraParameterInput`）。
- `MainWindow.EqDsp.cs`：声道/安全输入先入模型；`ApplyDspToEngine` 从模型快照取参，**不再从控件重建**。
- `MainWindow.Misc.cs`：读存档时先 `ExtraParameters.Load` 再填控件；单声道/交换/余量输入适配。
- `MainWindow.WebDsp.cs`：声道与 headroom 的 `get/module/setControl` 描述与修改走模型，其余模块暂留控件适配。
- `MainWindow.WebDspTools.cs`、`MainWindow.DspNav.cs`：信息与激活圆点判据读模型。
- `MainWindow.DspPower.cs`：余量/crossfeed 的「断开前档位」记忆由模型持有；同步嵌套时保留外层 syncing 标记。
- 前置已提交：`b3cbb8a`（EqEditSession：EQ 曲线/撤销重做/40 条历史/400ms 合并/A/B）、
  `871f12b`（DspApplyService：持久化与 `IDspParameterTarget` 下发分离）。

## 验证（本次在最终源码上重做，不只沿用交接数字）

交接文档称「最后一次源码调整只修正交叉馈送模型内部整数字段的同步，之后重新跑过」。核对时间戳发现
**`MainWindow.DspNav.cs` 在 20:01:26 才落盘，晚于 20:00:58 的最终回归和 20:01:04 的 publish**，
即当次出货的 DLL 并不包含这处改动。用当前源码重编，DLL 从 `A444BEC8…` 变为 `413EEE4A…`（体积也差 1 KB），
证实确有未验证的差异。故整套证据在最终源码上重跑：

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `work/w13-verify-publish.log` |
| EqEditRegression | **41 项全过**（25 EQ/下发 + 16 参数模型） | `work/w13-verify-tests.log` |
| 24 步真实网页修改 + 逐字段对比 | 与迁移前基线 **0 差异** | `work/channel-final.json` vs `work/w13-verify-channel.json` |
| 关闭重开恢复 | channel/headroom 回包一致 | 同上 `restarted` 段 |
| W13InterfaceRegression | 冷启动/重载各 4041 首，0 失败，0 JS 异常，退出码 0 | 工具标准输出 |
| 真实起播 + 自然续播 | 底栏/主页/详情页标题与队列下标同步（Vitamin C ↔ Hi! 循环两轮） | `tools/W13TailAdvanceProbe.cjs` |
| `git diff --check` | 通过，DspNav 仅两行实质改动（历史 CRLF 未被顺手统一） | 本机 |

产物对应关系：publish 与 `dist/web-ui-20261028` 的 `CelesteMusicPlayer.dll` SHA256 现均为
`413EEE4A16D7B86269DCA724DFB096AA2B75246F9CFB4F926814B7DC7034126E`；同步脚本报 `COPIED=2 / BAD=0 / STALE=0`
（只换 DLL 与 PDB，其余文件先前已一致）。

方案点名的风险点逐条核对结果：

- `LoadAudioFxUiFromStore` 在填控件**之前**装载模型（`MainWindow.Misc.cs:1101`），整段处于 `_audioFxLoading=true`，
  启动默认值不会覆盖磁盘。
- 听音方案（`ReloadDspPanelFromStores`）与设备配置（`ApplyDeviceDspProfileIfEnabled`）都经同一入口重载，
  不残留上一份模型；后者重载后再用模型快照下发引擎。
- `ProjectExtraParameters` 进出保留外层 `_audioFxLoading`／`_dspPowerSyncing`；旧输入只导入 sender 对应的那一个字段。
- 单独开 crossfeed 时快照 `Enabled=true`，`Channel.Enabled` 原值不被强改；`DspPowerXfeed` 的父模块联动
  仍按「声道工具没开则禁用」呈现。
- 强度保留小数（网页回包 33.3），落盘仍取整（33）。
- 限幅按钮圆点判据只看余量，单独开限幅不会点亮「正在处理」。

## 局限

主观音质、真实声卡各输出模式、干净机验收不在本刀证据内。其他 DSP 模块（RG / SRC / 压缩器 / 声场 / FIR）
仍走控件适配，按接手方案 A 的路线逐模块迁移。

## 回退

- 二进制：`dist/web-ui-20261028-pre-w13-channel-20261030/` 里的 `CelesteMusicPlayer.dll` / `.pdb`
  复制回 `dist/web-ui-20261028` 根目录（局部二进制回退，不是整仓快照）。
- 源码：精确撤销本 commit 涉及的文件；不要 `git reset`／`git revert` 整仓，工作树里有大量与本次无关的未跟踪文件。
