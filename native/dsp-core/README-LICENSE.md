# native/dsp-core — 许可与代码来源说明

本目录产出 `celeste_dsp_core.dll`，是 CelesteMusicPlayer 的 DSP 运算内核
（EQ / 卷积 / ReplayGain / 压缩 / 交叉馈送 / 声场 / 声道矩阵 / balance /
Headroom / 限幅 / 变速 / 电平表）。

## ⚠ 代码来源：`audio-engine/` 主体与 ECHO 上游当前版本逐字节相同

2026-10-15 用 SHA-256 全目录复核：本目录 `audio-engine/` 共 30 个文件，
其中 **27 个与 ECHO 播放器（<https://github.com/Moekotori/ECHO>）当前版本
（26.10.7，AGPL-3.0-only）的 `native/audio-engine/` 同名文件完全相同**：

| 文件 | 状态 |
| --- | --- |
| CompressorProcessor / EqProcessor / SpatialDspProcessor / EqPresetStore / DspChain / DspRackOrder / ChannelBalanceProcessor / ReplayGainProcessor / LevelMeterProcessor / PlaybackRateProcessor / DspHeadroomProcessor / EqBand / EqTypes / buffer.h / DspSafetyLimiter 等 27 个 | **与上游逐字节相同（SHA-256 一致）** |
| `ConvolutionProcessor.cpp` / `.h` | 本地有改动（上游 19798 行 → 本地 12757 行，裁剪版） |
| `TruePeakLimiterProcessor.cpp` | 本地有改动（上游 3433 行 → 本地 4114 行） |

> **更正记录**：2026-10-08 曾记录"仅 5 个文件相同、取用的是上游 LGPL-3.0 时期的版本"。
> 2026-10-15 复核确认：**相同的是 27 个文件，对应的是上游当前的 AGPL-3.0 版本**。
> 本节与仓库根目录 `LICENSE` 均以复核结果为准，旧记录作废。

### 这意味着什么

1. **`audio-engine/` 主体不是本项目自写代码**，衍生自 ECHO 当前的 AGPL-3.0 版本。
2. **本目录随 CelesteMusicPlayer 整体按 AGPL-3.0 许可发布**（2026-10-16 定案），
   与上游当前许可一致；完整对应源码随本仓库公开可查。
3. 若 ECHO 作者或任何权利人提出异议，本项目将按其要求下架、改标许可或重写相关模块。
   本声明不构成任何对自己或他人的法律意见。

## 本项目自写的部分

| 文件 | 说明 |
| --- | --- |
| `celeste_dsp_bridge.cpp` | C ABI 桥接层。本项目原创，许可是 AGPL-3.0（与主项目一致）。 |
| `tests/` | 本项目回归测试。 |

## 已采取的合规动作

1. 完整对应源码随本仓库发布（本目录），公开可查；
2. `celeste_dsp_core.dll` 以动态链接、按名加载方式调用，未加壳，可用同名文件整体替换；
3. 来源、许可与现状在仓库根目录 `LICENSE`（「ECHO 派生代码：来源、许可与现状」）与
   软件「设置 → 关于 → 许可与开源声明」页同步披露。

## 构建

```bat
native\dsp-core\build.bat
```

产物为 `native\dsp-core\build\celeste_dsp_core.dll`。
DLL 本体暂不入库（.gitignore），随发布包分发；缺失时主程序自动回退
「DSP 全关」直通并记日志，不会崩播放。
