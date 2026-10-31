# W13 DSP 域收官：五批连打，WebDsp 借用清零（2026-10-31）

用户指令「DSP 一起做好」。接并行会话的 DSP 批 1（`11750a6`，422→378），本会话五批连打：
**378 → 234 错 / 156 → 98 成员**。DSP 音效域借用清零收官（`WebDsp.cs` 82 / `WebDspTools.cs` 20 /
`WebOpra.cs` 4 全部下榜），全天累计 **1,024 → 234（消化 77%）**。

## 批次一览

| 批 | 提交 | 内容 | 账面 |
|---|---|---|---|
| 2 | `537fd31` | RoomIr.cs 整文件转活（FIR/房间校正全家）；EqDsp 的 ApplyRoomCorrection/UpdateDspBitPerfectUi/IsDspActiveForBadge、DspNav 的 RefreshRoomCorrectionInfo、DspPower 的 SyncDspPowerSwitches+DspPowerEntry+记忆字段、Features 的 IsBitPerfectPure/EvaluateBitPerfectChain/RefreshMainBitPerfectBadge → DspStateHost | 378→348 |
| 3 | `6aaf20a` | EQ 撤销/快照/A-B/自动增益核心 + ApplyDspToEngine + DescribeDspChainSummary + ApproxBandGain/Log2/IsShiftDown/EqCurve* 常量 → DspStateHost | 348→~324 |
| 4+5 | `76328bc` | **13 个模块文件整文件转活**（DspNav/DspPower/DspRack/DspProfiles/DspCrossfeed/DspComp/DspField/DspMatrix/Src/EqApo/SrcView/Opra/OpraWall）+ EqDsp/Misc/GeekDsp/Features 外科手术（LoadAudioFxUiFromStore/ApplySimpleTones/EnsureAudioFxUiBuilt/极客强调色四件/ApplyOpraCurve/ApplyEqCurveToPlayer/SaveAudioFxEqPresetCoreAsync/ApplyDeviceDspProfileIfEnabled 等）+ DspNav 剥两处死画布调用 | →234 |

收尾小件（`ApplyDeviceDspProfileIfEnabled` + 死画布事件挂接剥离）随后补入批 4+5 同一提交前的重建验证。

## 关键判定

1. **整文件转活的依据**：面板初始化链经 `xaml.cs → MainWindow_Loaded → InitDspNav → InitDspPowerEntries/LoadDspRackUi` 活可达，
   网页命令 → Apply*Parameters → PushDspRackToEngine 也活可达——这些模块文件终局必须整体转活，
   逐成员搬家既慢又会倒贴账。残余的旧面板 poke（目标全是 LegacyControls 占位控件）无害，
   留待后续死代码清理。
2. **混合打法**：内聚模块文件转活（13 个）；EqDsp(2172 行)/Misc/Features 是混合大文件，仍走外科手术，
   死面板同步按第十三/十四刀口径剥离（撤销/重做/A-B/自动增益/外部曲线入口只保留引擎+撤销栈行为）。

## 工具三修（`tools/_w13_move_method.py`）

1. 并行会话的混合换行尾部检查改进随批 2 入库；
2. **批 3 期间二修**：`body.endswith(b'    }')` 会把 8 空格的方法闭合也当类闭合剥掉
   （DspMatrixActive 的 `}` 被吃，编译器把后面整片吞进方法体）；且对文件级命名空间
   （`namespace X;`，结尾只有 `    }`）硬补假 `}` → 改为按整行匹配两种尾部；
3. **批 5 期间三修**：`split_keep_crlf` 永远按 `\r\n` 切——LF-only 文件（GeekDsp.cs）
   被切成一整行，配平必炸 → 按文件实际换行风格切/拼。
   另：表达式体成员结尾是 `;` 不是 `}`，尾部校验放宽为 `}` 或 `;`。

## 过程坑（均已修复）

- 分号结尾的字段/属性绝不能用配平工具（`_dspHeadroomMemory` 单行属性被当成表达式体，
  一路吃到 51 行，吞掉 DspPowerEntry+InitDspPowerEntries 开头）→ 字段/属性一律手工按行搬；
- 文件级命名空间结构认知错误导致花括号加减各错一次（CS1513/CS1022）→ 用「剥字符串后按行数层级」定位；
- DspPower 修复流程：dst 截污染尾 → src 从 git HEAD 恢复 → 工具重搬方法 + 手工搬字段 → 去重。

## 四道验证

| 项 | 结果 |
|---|---|
| 全量重建（Debug x64, Rebuild） | 0 错误 |
| publish | DLL 3,018,240 字节、PDB 774,164、269 个文件（全部正常标志） |
| 接口回归 | W13InterfaceRegression：exitCode 0、jsErrors 空、正常退出 |
| DSP 探针 8 连 | DspParameters/Fir/EqState/RackProfile/Rg/Src/Comp/FieldMatrix 全部落盘，无意外错误
  （仅 noIR 场景「请先导入有效 IR」与机架行到边界「这一行不能再移动了」两个**预期**业务拒绝） |
| 实播 | 新探针 `W13DspBatchPlaybackSmoke.cjs`：ABYSS → Diana 两首真实推进（pos 67.3 / 75.28） |

## 交付

- 出货已同步：`dist/web-ui-20261028/`（COPIED=2，BAD=0；STALE=293 为已知 W13 前残留）。
- 回退件：`dist/web-ui-20261028-pre-w13-dspbatch-20261031/`（= 批 1 出货版 DLL+PDB）。
- 探针结果：`work/dspbatch-verify/*.json`；实播：`_w13_dspbatch_playback.json`。

## 剩余账面（234 / 98，非 DSP）

- 播放/列表视觉族（Playback/Playback2/Playback3/xaml.cs ~150）：列宽、多选、极客行、歌词视觉等；
- WebAudioSettings(24)：AB 循环字段 + 输出设备切换族；
- SettingsHost(12)：设置实时应用族（ApplySettingsLive 大 switch）；
- ExternalFile(8)/NativeTaskbar(8)/WebDav(6)/WebBrowseActions(4)/AppFlow(2)/WebFolders(2)/WebTags(2)。

下一仗建议：播放/列表视觉族按第十一刀「小件清完」的路子逐个甄别，或先打 WebAudioSettings 的 AB 循环+输出设备族（用户天天用）。
