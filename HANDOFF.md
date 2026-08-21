# 交接文档：CelesteMusicPlayer（Celeste 音乐播放器）

## 项目信息
- 代码仓库（含 git）：`C:\Users\admin\source\repos\CelesteMusicPlayer`（真实项目；`global-workspace` 下的 `celeste-winui`/`CelesteDesktop` 是空壳模板，勿用）
- 主工程：`CelesteMusicPlayer\CelesteMusicPlayer\CelesteMusicPlayer.csproj`；解决方案 `CelesteMusicPlayer.slnx`
- 技术栈：.NET 8 + WinUI 3（WindowsAppSDK 1.8）+ NAudio 2.2.1 + ffmpeg；WinExe；Release 自包含、Debug 走 MSIX 打包（VS F5）
- 输出 exe：`bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CelesteMusicPlayer.exe`（自包含可直接双击；需同目录 `ffmpeg.exe`）
- 编译（自包含 Release）：`dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`
- 编译（非打包 Debug，供调试/找错）：`dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=false`，产物在 `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\`（记得 `Assets\ffmpeg\ffmpeg.exe` 已复制）。XamlCompiler 偶发崩溃 -1073741819，重试即过。

## 当前状态（最近一轮迭代，尚未 git commit）
工作树含大量未提交改动，集中在：
- DSP 音效板块加分（曲线 EQ / 声道平衡 / 安全限幅）
- 三模式统一走 HiFi 后端输出链
- 共享格式兼容修复、音量切换修复、独占 DSP 优化

## 播放/输出架构（重要变化）
- **三模式统一走 HiFiOutputBackend 输出**：共享 = NAudio `WasapiOut(Shared)`，独占 = 原生 `NativeWasapiExclusiveOut`，ASIO = `AsioOut`。所有输出都包一个 `ManagedDspSourceProvider`（EQ→声道平衡→限幅），DSP 实时生效、暂停后继续保留。
- `AudioPlaybackEngine.IsHiFiMode` 恒为 true（统一走引擎/DSP）；`AudioGraph` 路径（`PlayFileAsync`/`BuildGraphInputNode`）不再作为主播放入口（保留作试听等）。
- 主播放入口 `MainWindow.StartPlayback` → 无条件走 `PlayExtendedWithEngineAsync`（ffmpeg 转 PCM 后交给引擎）。

## 共享模式全格式播放（关键修复）
- **根因**：ffmpeg 输出 `pcm_s32le/s24le` 会写 `WAVE_FORMAT_EXTENSIBLE` → NAudio `WaveFileReader` 返回 `Extensible` → `WasapiOut(Shared).Init` 抛 `E_INVALIDARG`（"value does not fall within the expected range"），导致 16/44.1 ALAC、24/96 FLAC 等都无法播放。
- **修复**：共享模式统一折叠输出为 **`pcm_f32le`（IEEE float）**（采样率/声道按设备 MixFormat，设备探测失败兜底 48k/2ch），规避 Extensible 问题。缓存 key 含转码参数指纹，参数变更自动失效重转。
- 效果：除 DSD 外的所有格式在共享都可播（非 bit-perfect，可听优先）。

## DSP 板块（左侧「音效处理」按钮 → 右侧面板）
- **EQ**：ECHO 式曲线均衡。专业模式（对数频率曲线 + 每段频率/增益/Q/滤波器类型 Peak/LowShelf/HighShelf/LowPass/HighPass/Notch + 增删段 + preamp + 自动增益 + 预设）+ 简单模式（低音/人声/空气/温暖滑杆）。**用户自定义预设**：保存/加载/删除（`EqUserPresetStore`，存 `eq-user-presets.json`）。
- **声道平衡**：左右增益/平衡/反相/交换/单声道。
- **安全限幅**：headroom 余量 + soft-knee 软削波（`SoftLimit`：|x|≤0.9 线性，0.9~∞ 渐近压缩到 ±1，消除硬削爆音）。另加 **自动峰值余量补偿**：估算所有 band 在频域最大叠加增益，超 0dB 自动施加负 preamp（≤-12dB）压回，防极端参数爆音。
- DSP 状态持久化：`eq-curve.json`（曲线 EQ）、`dsp-extra.json`（声道/限幅）、`eq-user-presets.json`（用户预设）。
- 使用任何 DSP → 输出非 bit-perfect，DSP 面板顶部 + 左上角 `NowPlayingText` 提示。

## 音量
- 共享 = NAudio 软件增益（0..1）；独占/ASIO = 设备主音量（slider²）。
- 修复"独占/ASIO 切共享后换歌音量巨大"：设置保存后同步 `MainWindow.ApplySettingsLive`；`FadeInEngineAsync` 共享用持久化真实音量，而非被锁 100% 的滑块值。

## 独占 DSP 性能
- `ManagedDspSourceProvider.ProcessBlock` 用**整块批量解码/编码 + 池化 float 缓冲**，降低 352800Hz 下 per-sample 字节编解码开销。
- 独占 render 缓冲 200ms → **100ms**，降低 render 单次 DSP 块/实时峰值。
- 关 DSP 时 `_active=false` → `Read` 纯直通零开销。
- 说明：独占 352800Hz × EQ 段数 × 托管 float 有物理吞吐上限，较 ECHO（native C++/SIMD）仍可能不够丝滑；是否引入 native DSP 是待定的大决定。

## DSD
- 放弃 DoP 原生直出，统一 ffmpeg 转 PCM：共享 44.1k；独占/ASIO `pcm_s32le @ 352800Hz`（已恢复，不再 176.4k 降档——降不降都卡，且已确认与采样率无关）。
- DoP 直出代码保留但不启用。
- **KA13 硬件时钟/固件问题仍存在**（高采样 DoP 固定位置掉帧/黄灯），这是设备端问题，非软件可修；安卓 USB Audio Player Pro 能 bit-perfect 是平台（USB 独占底层）差异。

## 已知待办/现象
- 独占开 EQ 在极端参数下已加自动余量，听感待用户确认。
- 用户自定义预设的删除入口已接（预设下拉「管理（删除）我的预设…」）。
- 左上角三横菜单已移除「均衡器…」入口（并入 DSP 板块）。

## 其它
- 音频独占 render：`GetCurrentPadding` 在 KA13 上会非托管崩溃，别用（已回滚留注释）。
- 认证/打包：Debug x64 需自签名 `CelesteMusicPlayer_TemporaryKey.pfx`（密码 `celeste123`）。
- 若继续 DSD：优先确认是否为设备时钟问题，别回音频层盲试（已多轮证明非软件音频链路）。
