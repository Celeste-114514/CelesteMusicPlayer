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
2. **ECHO 上游现行许可是 AGPL-3.0-only。** AGPL-3.0 与 GPL-3.0 可按
   GPL-3.0 §13 组合，但组合后的整体必须同时满足 AGPL-3.0 条款
   （含"通过网络提供服务即构成分发"）。
3. `native/echo-core/README-LGPL.md` 里"本项目未使用上游新代码（AGPL 版本）"
   的声明**只覆盖 `native/echo-core/`**，不覆盖本目录。
   本目录此前**没有任何许可说明文件** —— 这是本次要补上的缺口。

## 本项目自写的部分

| 文件 | 说明 |
| --- | --- |
| `celeste_dsp_bridge.cpp` | C ABI 桥接层。本项目原创，许可是 GPL-3.0（与主项目一致）。 |
| `tests/` | 本项目回归测试。 |

## 处置方式（用户选定前不要对外发布包含本 DLL 的版本）

- **甲**：把 `audio-engine/` 中与上游相同的文件换成本项目自写实现。
- **乙**：保留这些代码，在仓库与分发包中提供源码，并把整体许可改标 **AGPL-3.0**。
- **丙**：保留这些代码，仅在 `LICENSE` 中如实披露来源，自担合规风险
  （不推荐用于公开发布）。

当前状态：**未选定**。详见仓库根目录 `LICENSE` 的
「待解决的代码来源问题」章节。

## 构建

```bat
native\dsp-core\build.bat
```

产物为 `native\dsp-core\build\celeste_dsp_core.dll`。
DLL 本体暂不入库（.gitignore），随发布包分发；缺失时主程序自动回退
「DSP 全关」直通并记日志，不会崩播放。
