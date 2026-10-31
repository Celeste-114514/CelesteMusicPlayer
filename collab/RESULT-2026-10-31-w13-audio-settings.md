# W13 音频设置搬出 MainWindow

日期：2026-10-31　提交：`53a3c6b`

## 一、这次搬了什么

`audioSettings` 是 `MainWindow.WebTransport.cs` 里剩下的最后一整块业务（11 个操作）：

| 操作 | 干什么 | 现位置 |
|---|---|---|
| `get` / `refresh` | 读快照 / 强制重枚举设备 | `WebAudioSettingsService.HandleAsync` |
| `setMode` / `setDevice` | 切输出模式 / 切输出设备 | 同上（真正动手仍走宿主） |
| `setRate` / `setPitch` | 播放倍速 / 音调不变开关 | 同上 |
| `setSleep` | 睡眠定时（0/1..5/6/7/8 八档） | 同上 |
| `ab` | AB 循环 a / b / toggle / clear | 同上 |
| `deviceMemory` / `saveDeviceProfile` | 设备 DSP 配置记忆 | 同上 |

`MainWindow.WebTransport.cs`：**164 行 → 32 行**，只剩 `waveform` 与 `setVolume` 两个一行转发。
新增 `WebAudioSettingsService.cs`（202 行）与 `MainWindow.WebAudioSettings.cs`（90 行，转发 + 宿主实现）。

### 宿主接口 `IAudioSettingsHost` 给了十样

`IsDeviceListRefreshing` / `EngineDeviceId` / `ReadFacts` / `ChangeOutputModeAsync` / `ChangeOutputDeviceAsync` /
`SetPlaybackRateAsync` / `SetPreservePitch` / `ApplySleep` / `ApplyAbLoop` / `RefreshPanel`。

**为什么这十样搬不动**：切模式与切设备要重开输出链路；倍速要落盘并即时下发到引擎；
睡眠定时器要一个 `DispatcherQueue` 计时器；AB 循环要写原生面板的读数与按钮态；
`RefreshPanel` 是界面活。**没给**：`_webOutputDevices` / `_webDevicesMode` / `_webSettingsBusy` 三个窗口字段
—— 设备缓存与并发闸门都收进服务实例里了。

**设备枚举没占宿主口子**：`HiFiOutputBackend.EnumerateWasapiDevices` / `EnumerateAsioDrivers` /
`GetDefaultWasapiDeviceId` 都是静态方法，服务直接调。

## 二、验证

新增探针 `tools/W13AudioSettingsWireProbe.cjs`。覆盖 14 组、逐字段比对：

| 组 | 内容 |
|---|---|
| `get0` / `refresh` / `get1` | 18 个快照字段（含设备 id 与显示名逐条） |
| `rateUp` / `rateBack` | 1 → 1.25 → 1 |
| `pitchFlipped` / `pitchRestored` | 翻转再翻回 |
| `sleep` | 7（再 3 首）/ 8（45 分钟）/ 6（本曲后）/ 0（关） |
| `abLoop` | a / b / toggle / clear 四个动作的循环点与提示文案 |
| `deviceMemoryFlipped` / `deviceMemoryRestored` | 翻转再翻回 |
| `setModeSame` | 用当前模式重设一次（不跨命名空间，不重开链路） |
| `rejects` | 11 条拒绝路径（未知操作 / 未知模式 / 倍速越界 ×2 / 定时越界 ×2 / 未知 AB 动作 / 设备模式不符 / 设备不存在 / 未起播存配置） |

结果：**改动前 vs 改动后 = 0 字段差异**；`dist` 里的 exe 再跑一遍接口回归 **4041 首 × 2 轮、0 失败、0 JS 异常**。

## 三、⚠ 探针第一版把用户的输出模式切走了

第一版把「本机没有 ASIO 驱动」当成必然，用 `setMode('Asio')` 去测那条拒绝分支。
**这台机器装着实 FiiO ASIO 驱动**，于是请求成功，输出模式真的从 `Shared` 切到了 `Asio`、
设备变成 `FiiO ASIO Driver`。是探针末尾的 `stateDrift` 把它抓出来的。

处理：
1. 写 `tools/_w13_audio_restore.cjs`，用 `setMode Shared` 改回（跨命名空间会自动把设备重置成系统默认）。
2. 删掉那条用例，并在代码里写明**为什么不能测**：要测「没有驱动」得先卸驱动。
3. 探针末尾加硬断言：`stateDrift` 非空就 `exitCode = 2`。**碰用户输出设置的探针必须自证已归位。**

另外 `setDevice` 的「设备不存在」分支在第一版里也测歪了（模式已被切成 Asio，请求先撞上「模式已变化」）；
去掉 ASIO 用例之后才真正走到「设备已断开或不属于当前输出模式」。

## 四、现场

- 出货 DLL `B704E1AA…` 3,095,040 字节，已同步 `dist/web-ui-20261028`（`COPIED=2 BAD=0`；`STALE=293` 是 W13 之前的旧残留，非本次）
- 回退件：`dist/web-ui-20261028-pre-w13-audiosettings-20261031/`（`ABFEB582`，3,090,432）
- 全量重建：`obj`、`bin/Release`、`bin/x64` 整体移走后 publish，`GenericHelpers` / `VtableClasses` 两个 CsWinRT 符号齐全
- 测试后用户数据：`app-settings.json` **全字段零差异**（`OutputMode=Shared`、`OutputDeviceId=""`、`PlaybackRate=1`、保调开），
  `library.db` / `play-queue.json` / `track-stats.json` 逐字节未变

## 五、没验到的

1. **切输出模式的成功路径（跨 Shared ↔ WasapiExclusive ↔ Asio）**：只测了「用当前模式重设」。
   真跨模式切换要重开输出链路，探针里没有播放做兜底，风险大于收益。**这条必须人手点一次。**
2. **切输出设备的成功路径**：同上，只钉住了两条拒绝路径。
3. **`saveDeviceProfile` 的成功分支**：要求引擎真在用具体设备，探针不起播，只测到拒绝路径。
4. **`_webSettingsBusy` 的并发闸门**：单个探针发不出并发请求，没有证据。
