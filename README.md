# CelesteMusicPlayer

一个基于 **WinUI 3 / .NET 8** 的本地音乐播放器，功能参考 [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2) 和[ECHO-NEXT](https://github.com/moekotori/ECHO)设计开发。代码全部由AI生成，主要模型为deepseek，由Cursor和Reasonix辅助开发。

## ✨ 功能特性

- 🎵 本地音乐库：扫描文件夹 / 多选导入，自动读取标签（标题、艺术家、专辑、年份、封面）
- 🎧 广泛格式支持：MP3 / FLAC / WAV / M4A / APE / WavPack / TTA / DSD（DSD由解码器转为高质量pcm后播放，Windows Shared模式下不支持dsd） 等
  - 内置 [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) 
- 📋 输出模式：AUDIO 输出设备可选择，WASAPI 共享 / WASAPI 独占 / ASIO输出
- 🎯 HiFi 独占输出：基于 NAudio / 原生 WASAPI 从 PCM WAV 流式输出，兼顾音质；独占设备音量可调、切歌音量不再重置
- 🎚️ **DSP 三模式统一信号链**（共享 / WASAPI 独占 / ASIO）：曲线 EQ（专业 / 简单模式 / 预设保存加载）、10 段均衡器、声道平衡、安全限幅（soft-knee 软削波 + 自动峰值余量）防爆音
- 🔊 **ReplayGain 响度归一化**：单曲 / 专辑统一响度、10ms 平滑渐变、peak 防削波、额外增益可调
- 🎚️ 音量条自绘、任务栏进度
- 📝 歌词：自动下载（网易云 / QQ / 酷狗）、在线搜索、桌面歌词窗口、卡拉 OK 高亮
- 🖼️ 封面：自动下载并嵌入标签、专辑 / 艺术家视图、封面文件夹
- 🎨 主题：主题预设 / 自定义主题色、毛玻璃背景、波形进度条
- 🪟 迷你播放器、当前播放列表窗口（拖拽排序）、独立播放队列窗口、睡眠定时器
- 🗂️ 播放列表墙：命名播放列表网格浏览、创建/重命名/删除、批量多选、封面、导入/导出
- 🏷️ 标签分类浏览：按艺术家 / 专辑艺术家 / 专辑 / 流派 / 年份分类浏览曲库
- 🔤 自定义排序：1–5 个元数据字段排序链 + 整体升降序（如"专辑按时间顺序排列"）；播放列表内按标题 / 艺术家 / 专辑 / 音轨号 / 年份 / 时长 / 路径排序
- ⌨️ 全局快捷键、媒体键（SMTC）、系统托盘、开机自启
- 📊 播放统计（播放次数 / 收听时长 / 收藏 / 评分 / 最近播放）
- 🌐 在线搜索（网易云 / QQ / 酷狗）、Last.fm 记录
- 🏷️ 标签编辑器（包括批量编辑、按文件名填充、批量下载歌词封面）

## 🖥️ 截图

<img width="2022" height="1153" alt="QQ20260822-022810" src="https://github.com/user-attachments/assets/5f09a6e1-64bb-4996-bc67-8e7c6648d8da" />
<img width="2028" height="1181" alt="QQ20260822-023000" src="https://github.com/user-attachments/assets/ad398e2b-e2e1-4941-a4b6-d389e5d2cdb2" />
<img width="2022" height="1153" alt="QQ20260822-022927" src="https://github.com/user-attachments/assets/b5bdd631-2bfa-4e90-9770-c6bb0ab9239b" />
<img width="2022" height="1153" alt="QQ20260822-022920" src="https://github.com/user-attachments/assets/222b74b0-1fe6-45c8-a073-60ab6cae1d84" />
<img width="2022" height="1153" alt="QQ20260822-022901" src="https://github.com/user-attachments/assets/f3524832-e282-4915-bff7-b2cf4982b550" />
<img width="2022" height="1153" alt="QQ20260822-022840" src="https://github.com/user-attachments/assets/e462445d-48d1-45bc-bbe5-7ca3a5159436" />
<img width="2022" height="1153" alt="QQ20260822-022831" src="https://github.com/user-attachments/assets/87aee0fc-873d-458e-a667-46cd5265d12e" />
<img width="2022" height="1153" alt="QQ20260822-022817" src="https://github.com/user-attachments/assets/a32ef812-fc88-4fb1-aa6b-53defc41c1d8" />




## 🛠️ 技术栈

- WinUI 3（Microsoft.WindowsAppSDK 1.8）
- .NET 8 / C#
- [TagLibSharp](https://github.com/mono/taglib-sharp) 标签读取
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) 系统托盘
- FFmpeg（内置）

## 🔨 构建与发布

需要：.NET 8 SDK + Visual Studio 2022（含 WinUI 工作负载）

```bash
# 开发运行
dotnet build -c Debug

# 发布自包含（免安装，含 Windows App SDK 运行时）
dotnet publish CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -r win-x64 -o publish
```

发布产物为自包含目录：目标机器 **无需安装 .NET 运行时或 Windows App SDK**。

## 📝 更新日志

### v26.8.22（2026-08-22）
- 🎛️ **三模式统一 DSP 信号链**（共享 / WASAPI 独占 / ASIO 统一走 EQ→声道→限幅）；共享模式折叠输出修复 ALAC / 高采样 FLAC 无法播放
- 📈 **曲线 EQ**（ECHO 式）：专业曲线 / 简单模式 / 自动增益 / 用户自定义预设保存加载删除；覆盖 10 段均衡
- 🎚️ **声道平衡 + 安全限幅**（soft-knee 软削波 + 自动峰值余量补偿防爆音）
- 🔊 **ReplayGain 响度归一化**：单曲 / 专辑统一响度、10ms 平滑渐变、peak 防削波、额外增益面板
- 🎼 **DSD（DSF/DFF）播放支持**：由解码器转为高质量 PCM 后播放（WASAPI 独占 / ASIO 下 pcm_s32le@352800Hz 高采样直出；Windows Shared 模式下不支持 DSD）；解析器插件化、整曲内存预加载 / 环形预读根治卡顿、电流音、雪花、后半段无声；专辑全 DSD 封面角标
- 🖥️ **媒体库 / 详情页重构**（阶段 4/5）：媒体库面板（多根文件夹树 + 详情区）、专辑 / 艺术家详情页左右分栏重构、播放歌曲信息整页（网易云式）、播放队列拖拽重排、歌曲面板全新三行式行设计、详情页 / 自定义排序、播放列表墙封面加大
- 📝 对照歌词翻译行展示、无歌词提示、歌词 / 播放信息超长换行修复、播放信息面板布局重构
- 📐 UI/体验：WM_GETMINMAXINFO 锁定最小窗口、默认 1600×900、播放条压缩、采样率统一整数 Hz、全格式编码器显示、左侧扁平图标导航
- ⚙️ 其他：播放信息面板进出动画、艺术家 / 专辑超链接跳转、收藏按钮、媒体库刷新、右键歌曲选项菜单、多选界面优化

### v26.8.20（2026-08-20）
- 🏷️ **标签分类浏览**：按艺术家 / 专辑艺术家 / 专辑 / 流派 / 年份分类浏览曲库
- 🔤 **自定义排序**：1–5 个元数据字段排序链 + 整体升 / 降序（如"专辑按时间顺序排列"）；播放列表详情内按标题 / 艺术家 / 专辑 / 音轨号 / 年份 / 时长 / 文件路径排序
- 🔊 HiFi 独占输出设备音量可调，切歌音量不再重置为 100%
- 🖥️ 信号链面板与初始化日志、Pro Audio 线程优先级
- 🔔 切换输出模式时提醒；ReplayGain 支持关闭
- 🖼️ 艺术家头像缓存

### v26.8.19（2026-08-19）
- 🎯 **HiFi 独占输出**：新增 WASAPI 独占模式（绕开系统混音器），基于 NAudio 及原生 WASAPI 从 PCM WAV 流式输出；ASIO 后续更新
- 🔊 **音频输出设备选择**：可在设置中选择目标声卡，留空则使用系统默认
- 🔀 **输出模式切换**：共享 / 独占（HiFi）一键切换
- 🗂️ **播放列表墙**：命名播放列表网格浏览，支持创建 / 重命名 / 删除、批量多选、本地 / 在线列表封面、导入 / 导出
- 🪟 **独立播放队列窗口**：随时查看并管理当前播放队列
- ⚙️ 音频信息展示优化（采样率 / 位深 / 输出格式等）

### v2.0.0（2026-08-14）
- ReplayGain 响度归一化（优先读内嵌标签，缺失时用内置 ffmpeg 计算并缓存）
- 逐字卡拉OK歌词（AMLL 风格，主窗口 + 桌面歌词）
- 睡眠定时器增强：当前曲目播完后停止 / 再播放指定曲目数后停止
- 修复系统托盘图标点击无反应

### v1.0.0（2026-08-08）
- 首个正式版本

## 📦 安装包

使用 [NSIS](https://nsis.sourceforge.io/) 将发布目录打包为 `setup.exe`（见 `installer/CelesteMusicPlayer.nsi`）。安装为**用户级**安装到 `%LOCALAPPDATA%\Programs\CelesteMusicPlayer`，无需管理员权限，自动创建开始菜单 / 桌面快捷方式与卸载入口。

```bash
# 发布自包含（win-x64）后
makensis.exe installer\CelesteMusicPlayer.nsi
```

## 📄 许可证

[MIT](LICENSE)

## 🙏 致谢

- [FFmpeg](https://ffmpeg.org/)（gyan.dev 构建）
- [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2)（功能参考）
- [ECHO-NEXT](https://github.com/moekotori/ECHO)（功能参考）


## 🙏 一些作者的碎碎念

这个播放器是因为目前网上的播放器没有能完全符合我需求的，几乎所有播放器都不支持我想要把专辑按照时间顺序排列（foobar和musicbee支持但是有点丑），musicplayer2在我高中时候就在用了，基本上就缺一个排序功能，外观也一般般，正好最近AI浪潮，就先借朋友的cursor账号试了一下改了一下musicplayer2，于是后续继续开发下去了，我自己对代码一无所知，只有最基础最基础的入门知识。这个播放器也算是我用AI花了蛮久开发的，希望各位能用的舒服用的开心，那我也不枉花钱买那么多token了（
