# RESULT — W13 机架顺序 + 总旁路 + 听音方案协调

日期：2026-10-31。**方案 A 的最后一项**，至此六个模块（ReplayGain / SRC / 压缩器 / 声场 / 矩阵 / FIR）
加上链路级状态（机架顺序、总旁路）与听音方案判定全部离开 MainWindow 控件。

## 这一刀的来历（要记一笔）

开工时发现工作区里已经躺着两个未提交的新文件（`DspRackParameters.cs`、`DspProfileCoordinator.cs`）
和 9 个改了一半的宿主文件 —— 是**上一次会话被中断的残留**，不是别人的并行改动。
判断依据：它们引用的符号与上一刀留下的 `RESULT` 文档里"留给最后一小步"的清单完全对得上。
所以我接着把它做完，而不是重写。残留状态：编译不过（`_dspProfileRows` 重复声明），
25 处旧字段（`_dspProfileActiveName` / `_dspProfileLeftName` / `_dspProfileRefKey`）没改完。

## 做了什么

- `DspRackParameters.cs`（新）：链路级状态 —— 8 个内核模块的排列 + 全局旁路。
  它不按控件名读写，所以**没有**实现 `IDspParameterModel`（那套是"按控件名读写"的契约），
  只暴露 `Load / Move / Reset / SetBypass / Snapshot / Order / IsDefault / Bypassed`。
  旁路是 A/B 对比的内存态，不落盘（与旧行为一致）；顺序由 `DspRackStore` 持久化。
- `DspProfileCoordinator.cs`（新）：「当前这套设置属于哪份听音方案」的判定搬出 MainWindow。
  持有 `ActiveName` / `LeftName` / `RefKey`，提供 `Load / Apply / Clear / Refresh / Fingerprint`。
  判定仍然靠内容指纹比对（不靠各模块 handler 上报），指纹刻意不含 IR 的 taps / 采样率 / 声道数
  —— 那些是播放时回写的，放进来会让刚应用完的方案立刻被误判成"已改动"。
- 宿主 `MainWindow.DspRack.cs`：`_dspRackOrderDraft`（草稿数组）和 `DefaultDspRackOrder()` 删掉，
  上移 / 下移 / 复位 / 行渲染全部走模型；`DspRackGlobalBypassed` 改读模型。
- 宿主 `MainWindow.DspProfiles.cs`：三个私有字段 + `DspProfileFingerprint` / `DspProfileDrifted` 删掉，
  改由协调器持有；保存 / 应用 / 更新走 `ProfileCoordinator.Apply(...)`，删除走 `Clear()`。
- `MainWindow.WebDsp.cs`：方案命令（保存 / 应用 / 更新 / 删除）与机架命令（rackMove / rackReset）
  都改走模型；`setBypass` 仍然借道原生开关（它会触发既有 handler 下发引擎），读取一律走模型。
- `MainWindow.DspNav.cs`：导航圆点里"方案页是否点亮"改读协调器。

## 验证（全部在最终源码 / 最终出货 DLL 上跑）

| 项目 | 结果 | 证据 |
|---|---|---|
| 自包含 publish | 成功，0 条 `error CS` | `_publish_retry_clean.py` |
| 参数模型回归 | **164 项全过**（上一刀 142 + 新增 22） | tools/EqEditRegression |
| 机架 / 旁路 / 方案真机 14 步 + 重启 | 与迁移前二进制**逐字段 0 差异**（时间戳除外） | `work/rp-before.json` vs `work/rp-after2.json` |
| FIR 旁证复跑 | 与上一刀 after 完全一致（同样 7 处 = 上一刀已知的那 1 个修复点） | `work/fir-side-rack2.json` |
| 声场 + 矩阵旁证复跑 | 0 差异 | `work/fm-side-rack.json` |
| 接口回归 | 冷启动 / 重载各 4041 首，0 失败，0 JS 异常 | tools/W13InterfaceRegression |
| 真实起播 + 自然续播 | Time → Hi! → Vitamin C → Time | tools/W13TailAdvanceProbe |

探针轨迹（证明真的动到了，不是空跑）：初始 `[0..7]` → 移动 → `[1,0,2..7]` → 再动 → `[1,0,3,2,4..7]`
→ 三次越界都报"这一行不能再移动了。" → 复位回默认 → 旁路开 / 读 / 关 → 存方案「探针方案A」
（状态变"正在生效"）→ 改压缩器阈值（**自动脱离，状态变回手动设置**）→ 再应用（回到方案）
→ 更新 → 删除（状态变回手动设置）→ 重启：顺序、方案、旁路（内存态 = 关）全部与盘中一致。

## ⚠ 这一刀踩到的坑：publish 成功但产物是坏的

第一次 publish（exit=0、`error CS`=0）出来的 DLL **只有 2,970,112 字节**，比上一刀少了 106 KB。
这正是记忆里记过的那个坑：CsWinRT 生成的 `WinRT.*GenericHelpers/*VtableClasses` 没编进去。
**同一份源码重编一次就恢复到 3,077,632。**

教训（这次真的兑现了一次）：**同步 dist 之前必须先看体积**。我第一次没看，把坏产物同步进了
`dist/web-ui-20261028`，还拿它跑完了全部验证（好在那些验证都过了）；发现后重编、重跑全部验证、
重新同步。现在 dist 里是重编后的 `09E74A41…`。

## 产物

- DLL 3,077,632 字节，sha256 前 16 位 `09E74A41A8A39327`，publish 与 `dist/web-ui-20261028` 一致
  （同步脚本 `COPIED=2 / BAD=0 / STALE=0`）。
- 回退件：`dist/web-ui-20261028-w13-planA-done-20261031/`（同一份 DLL）；
  上一刀的 `AD02B7F3…` 在 `dist/web-ui-20261028-pre-w13-rack-20261031/`。

## 局限

- 机架顺序的**听感**（顺序不同会不会有可闻差异）不在本刀证据内；本刀只证顺序的编辑、落盘、下发与恢复。
- `setBypass` 仍借道原生开关触发下发（控件 handler → `RackParameters.SetBypass` + `SetBypassAll`）。
  模型已经是唯一真值，但写入路径还没完全脱开控件，留待以后。
- 方案协调器只管"属于哪份方案"的判定与降级；清盘上 `ActiveProfile`、刷界面仍由宿主做。

## 回退

- 源码：`git checkout 3d7874d -- CelesteMusicPlayer` 后重新 publish（该提交已验证可编）。
- 二进制：`dist/web-ui-20261028-pre-w13-rack-20261031/`（`AD02B7F3…` = FIR 那一刀）。
