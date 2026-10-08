# native/dsp-core — 许可与代码来源说明

本目录产出 `celeste_dsp_core.dll`，是 CelesteMusicPlayer 的 DSP 运算内核
（EQ / 卷积 / ReplayGain / 压缩 / 交叉馈送 / 声场 / 声道矩阵 / balance /
Headroom / 限幅 / 变速 / 电平表）。

## ⚠ 代码来源：`audio-engine/` 与 ECHO 上游逐字节相同

2026-10-08 核查发现，本目录 `audio-engine/` 下的以下文件与 ECHO 播放器
（<https://github.com/Moekotori/ECHO>）**当前** `native/audio-engine/` 中的同名文件
**逐字节完全相同**：

| 文件 | 本地行数 | ECHO 上游行数 | 结果 |
| --- | --- | --- | --- |
| `audio-engine/CompressorProcessor.cpp` | 176 | 176 | 完全相同 |
| `audio-engine/EqProcessor.cpp` | 321 | 321 | 完全相同 |
| `audio-engine/SpatialDspProcessor.cpp` | 206 | 206 | 完全相同 |
| `audio-engine/EqPresetStore.cpp` | 178 | 178 | 完全相同 |
| `audio-engine/DspChain.cpp` | 206 | 206 | 完全相同 |
| `audio-engine/TruePeakLimiterProcessor.cpp` | 138 | 127 | 有差异（本地多 11 行） |

其余文件（`ChannelBalanceProcessor`、`ConvolutionProcessor`、
`DspHeadroomProcessor`、`ReplayGainProcessor`、`PlaybackRateProcessor`、
`LevelMeterProcessor`、`buffer.h` 等）在 `native/echo-core/audio-engine/`
中也有同名文件，其中 `EqProcessor.cpp`、`buffer.h` 等与两者一致。

### 这意味着什么

1. **`audio-engine/` 不是本项目自写代码。** 仓库内此前的记录
   （"`native/dsp-core/` = ECHO audio-engine 源码裁剪后自己改写；含 5 个 Stage A
   自写模块：Compressor / SpatialDsp / TruePeakLimiter / DspRackOrder /
   EqPresetStore"）与逐字节比对的结果不符 —— 其中 Compressor / SpatialDsp /
   EqPresetStore 与上游完全相同。**以比对结果为准。**
2. **ECHO 上游现行许可是 AGPL-3.0-only。** 本项目取用的是上游在 LGPL-3.0 时期
   公开发布的版本（2026-09 采样），据此按 **LGPL-3.0** 使用；这些文件在上游改许可
   前后内容未变，本项目也未采用上游 2026-09 之后新增的代码。
3. **本项目维护者已知悉上述事实与许可风险，决定保留当前实现并自行承担相应风险。**
   上游许可已由 LGPL-3.0 变更为 AGPL-3.0-only；本目录仅取用其 LGPL-3.0 时期
   公开发布的版本（2026-09 采样），未采用该时点之后上游新增的任何代码。
   若权利人提出异议，本项目将按其要求下架、改标许可或重写模块。
4. `native/echo-core/README-LGPL.md` 里"本项目未使用上游新代码（AGPL 版本）"
   的声明**只覆盖 `native/echo-core/`**，不覆盖本目录。
   本目录此前**没有任何许可说明文件** —— 这是本次要补上的缺口。

## 本项目自写的部分

| 文件 | 说明 |
| --- | --- |
| `celeste_dsp_bridge.cpp` | C ABI 桥接层。本项目原创，许可是 GPL-3.0（与主项目一致）。 |
| `tests/` | 本项目回归测试。 |

## 当前处置：保留并如实披露

已采取的合规动作：

1. 完整对应源码随本仓库发布（本目录），公开可查；
2. `celeste_dsp_core.dll` 以动态链接、按名加载方式调用，未加壳，可用同名文件整体替换；
3. 来源、许可与现状在仓库根目录 `LICENSE`（「ECHO 派生代码：来源、许可与现状」）与
   软件「设置 → 关于 → 许可与开源声明」页同步披露。

这是本项目目前唯一尚未闭合的许可项。详见仓库根目录 `LICENSE`。

## 构建

```bat
native\dsp-core\build.bat
```

产物为 `native\dsp-core\build\celeste_dsp_core.dll`。
DLL 本体暂不入库（.gitignore），随发布包分发；缺失时主程序自动回退
「DSP 全关」直通并记日志，不会崩播放。
