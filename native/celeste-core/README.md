# celeste-core — Celeste 自研原生音频内核（PCM / WASAPI 独占）

MIT 许可。全新编写，**不派生 ECHO 任何代码**（ECHO 是 LGPL-3.0，另住 `native/echo-core/`）。
架构上参照了 ECHO audio-host 的 C ABI 外形与自研 `NativeWasapiExclusiveOut`
（C# 版）的调度修复经验——经验不受版权约束，代码零共享。

## 为什么要有这个内核

自研 C# 渲染线程的卡顿根因已实锤（2026-09-23 用户实机日志）：.NET GC 的
STW（暂停所有托管线程）会把渲染线程冻住几十毫秒，MMCSS/优先级/缓冲都救不了
——优先级再高也躲不过运行时冻线程。把渲染环路搬进原生 C++ 线程 = 进程内拿到
"没有 GC"这一半收益（另一本是独立进程，UI 崩不拖垮播放，暂不需要）。

##  Phase 1 范围（本目录当前状态）

- 只接 PCM 独占输出。DSD/DoP 不走这里（沿用自研 C# 内核）。
- 上层（C#）负责所有决策：格式探测、采样率/位深协商、转码。
  本内核**只执行**：端点容器必须与源布局一致才 Init 成功，否则返回错误让上层
  切回自研内核。**不做跨格式转换回退**（与 C# 自研内核 SameLayout 策略一致）。
  DSP（EQ/限幅/ReplayGain 等）仍在 C# 链，feeder 灌进来的就是最终字节。

## 关键设计

| 项 | 口径 |
|---|---|
| C ABI | 8 个导出：`celeste_core_start/write/replace/mark_input_ended/set_paused/stats/stop/destroy` |
| 格式标签 | 1=pcm16(2B) 2=pcm24 packed(3B) 3=pcm24in32(4B,高位24位) 4=pcm32(4B) 5=float32(4B)；`write` 带标签做一致性校验 |
| bit-perfect | 端点容器 == 源布局 → 整数字节从 feeder 一路 memcpy 直通 DAC，不过 float |
| 缓冲 | 设备真实周期（GetDevicePeriod，下限 10ms），周期=缓冲（MSDN 独占铁律） |
| ring | 字节级 SPSC，mutex+cv；容量 = 1 周期 + 1.5s 存货（吸收 feeder 被冻 ≤64ms，余量 20×+） |
| 调度三件套 | MMCSS "Pro Audio" + `AvSetMmThreadPriority(CRITICAL)` + 播放期 `timeBeginPeriod(1)`（finally 成对归还）。⚠️ 只注册 MMCSS 不设频段内优先级 = NORMAL = 频段最底层——这是 2026-09-23 实机定论的卡顿根因之一 |
| 补货 | **事件驱动**（b0e8a73）：`WaitForMultipleObjects({stop,render}, INFINITE)`（2s 兜底仅防驱动完全不发事件），每次 `GetBuffer(bufferFrameCount)` 填满**整个**设备缓冲。判据基准 = 1 个缓冲周期。⚠️ 早期版本是"缓冲/8 轮询 + 只填空闲"——实机证明与设备周期无关的不规则小块写入会让 USB DAC（FiiO KA13）抖动，统计全绿也听得出卡 |
| 预填 | C# 起播前读 ≤3 个周期真实字节（硬顶 1.2s，防超 ring 容量卡死 Init）；start 阶段 prime 首缓冲，余量留 ring 兜 feeder 线程创建+JIT 窗口 |
| 统计 | `celeste_core_stats_t` 首字段 StructSize 防前向兼容越界；framesPlayed/underrun 累计（C# 取差分）；maxGapMs/spikeCount/padMaxFrames/lateWakeups 是窗口值（读走即清零，⚠️ 每次 stats 调用都会清窗口）；devicePosition = IAudioClock::GetPosition 设备实际播放游标（I 轮探针：我们写入帧 vs 设备实际消费帧）；**晚醒逐次明细**（J 轮）：lateWakeTotal/Base/Count/StartMs/GapMs，「开演以来」单调序号语义**不清零**——渲染线程每次"晚 8ms+"醒即记一条（环形 8 槽），C# 按序号只打印未读的新事件。⚠️ 5s 统计行只报窗口 max，同窗口第二次晚醒会被整个掩盖（实机：用户听得见 2 次、统计只显示 1 个 129ms），明细行才是对齐用户听感的口径 |
| 超时包装 | Activate/GetDevicePeriod/Initialize/Start 各包 3000ms 超时（std::async + wait_for + 坟墓区统一 drain，绝不在超时路径析构 std::future）；Start 挂住 → `client_leaked_on_timeout`（-3），join 超时 → `engine_leaked`（进程退出收尸，绝不碰野指针） |
| replace | seek/切歌 = clear ring + 重灌 + 统计归零 + 10ms 线性淡入（仅 2/4 字节布局） |

## 构建

### 沙箱（WorkBuddy bash，无 vcvars）

```bash
./build.sh        # 编译+链接+重试循环，产物 build/celeste_core.dll
```

要点（都踩过坑，别改）：
- 所有参数走 cl/link **响应文件**（`build_args.rsp` / `build/link_args.rsp`），
  不经 shell 传参（Git Bash 会把 `/DLL`、`/OUT:` 转成 `DLL.obj` 毁掉调用）。
- bash 的 `export` **不传播**到 Windows 子进程，`LIB` 必须用 `env` 前缀；
  且 LIB 里的路径**必须反斜杠**（正斜杠 link 不认，报 LNK1104 假象）。
- 显式输入库（ole32/avrt/winmm/uuid + CRT）从 `build/libs` 用相对路径给——
  那个目录里的 .lib 是从 VS/Windows Kits 拷贝的无空格副本，**要入库**。

### 有 vcvars 的机器

```bash
cl @build_args.rsp && link @build/link_args.rsp
```

依赖：MSVC（C++17）+ Windows SDK。无需 cmake/ffmpeg。

## 许可

MIT，见文件头声明。与 LGPL 的 `native/echo-core/` 相互独立、互不派生。
