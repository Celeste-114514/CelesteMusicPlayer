# celeste_audio_core.dll — LGPL-3.0 合规说明

本目录是 CelesteMusicPlayer 「独占输出内核（ECHO 核心）」的源码，构建产物为
`celeste_audio_core.dll`，由 CelesteMusicPlayer 主程序（MIT 许可）以**动态链接 +
按名加载**的方式调用（C# P/Invoke，符号名固定，见 `CelesteMusicPlayer/EchoCoreAudio.cs`）。

## 代码来源与许可

| 组件 | 来源 | 许可 |
| --- | --- | --- |
| `audio-host/src/`（WASAPI 独占输出、PCM 环形缓冲、C 导出桥） | 改编自 [ECHO 播放器](https://github.com/takase1121/echo) 的 audio-host 模块（Rust 转写为 C++） | **LGPL-3.0** |
| `audio-engine/`（EQ / 卷积 / ReplayGain / 限幅 / 电平等 DSP 链） | 同上，ECHO audio-engine | **LGPL-3.0** |
| `audio-engine/third_party/nlohmann_json.hpp` | [nlohmann/json](https://github.com/nlohmann/json) v3.11 | MIT（与 LGPL-3.0 兼容，见 FSF 许可兼容列表） |

ECHO 原项目自述许可为 LGPL-3.0（仓库根 `LICENSE`，GPL-3.0 §7 附加许可形式）。
本目录代码按 LGPL-3.0 继续分发。

## 为什么 CelesteMusicPlayer（MIT）不受影响

1. **动态链接**：`celeste_audio_core.dll` 是独立的 Windows 动态库，通过
   `LoadLibrary` / 按名 P/Invoke 调用，满足 LGPL-3.0 §4(d1)「以共享库机制链接」。
2. **用户可替换**：本 DLL 未加壳、未合并、未做静态链接；用户可用兼容的替代库
   （同名文件、相同导出符号）整体替换，程序会继续尝试加载它。这满足 §6 对
   「Combined Work」动态链接形式的要求。
3. **不传染主程序**：主程序及其余模块保持 MIT 许可，不继承 LGPL-3.0 义务。

## 如何取得本 DLL 的源码（LGPL-3.0 §6 源码要约）

- **完整源码就在本仓库**：`native/echo-core/`（即本目录），随版本标签发布，
  与二进制 DLL 同版本对应。
- **自行构建**（需 Visual Studio 2022+ C++ 工具，无需 CMake/FFmpeg）：
  ```bat
  native\echo-core\build.bat
  ```
  产物为 `native\echo-core\build\celeste_audio_core.dll`。
- 如分发包中未随附源码，可根据上述仓库地址与版本号取得对应源码；
  亦可通过 SettingsWindow「关于」页的联系方式向作者索取书面源码拷盘。

## 许可声明

- LGPL-3.0 全文：<https://www.gnu.org/licenses/lgpl-3.0.html>
- GPL-3.0 全文（LGPL-3.0 §3 引用）：<https://www.gnu.org/licenses/gpl-3.0.html>
- ECHO 原项目：<https://github.com/takase1121/echo>
- nlohmann/json MIT 许可：<https://github.com/nlohmann/json/blob/develop/LICENSE.MIT>

## 修改说明（相对 ECHO 上游）

本目录代码对 ECHO 上游的主要修改（均为可验证的工程改动，不改变许可状态）：

- Rust → C++ 转写（`wasapi_exclusive` / `PcmRingAudioSource` / DSP 链）。
- `celeste_bridge.cpp`：新增 C ABI 导出面（`celeste_audio_start/write/replace/
  mark_input_ended/set_paused/stats/stop/destroy`），供 C# 侧调用。
- `wasapi_exclusive.cpp`：起播 primer 由「静音预填」改为预填真实音频；
  `wasapi_exclusive_start` 增加 `deviceId` 参数按设备 ID 精确选设备。
- `convert_float_to_endpoint`：取整由向零截断改为四舍五入 + 对称 2^n 系数 +
  钳位，保证 16/24/32bit 整数 PCM 与 float32 互转的 bit-perfect（±1 LSB 修复）。
- 删除 ECHO 原 Rust 侧的流媒体/歌单等与 Celeste 无关的部分（本目录只保留音频链路）。
