# CelesteMusicPlayer

一个基于 **WinUI 3 / .NET 8** 的本地音乐播放器，功能参考 [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2) 和[ECHO-NEXT](https://github.com/moekotori/ECHO)设计开发。代码全部由AI生成，主要模型为deepseek，由Cursor和Reasonix辅助开发。

## ✨ 功能特性

- 🎵 本地音乐库：扫描文件夹 / 多选导入，自动读取标签（标题、艺术家、专辑、年份、封面）
- 🎧 广泛格式支持：MP3 / FLAC / WAV / M4A / APE / WavPack / TTA / DSD 等
  - 内置 [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) 
- 📋 输出模式：AUDIO 输出设备可选择，WASAPI 共享 / WASAPI 独占（HiFi，绕开系统混音器；ASIO 后续更新）
- 🎯 HiFi 独占输出：基于 NAudio / 原生 WASAPI 从 PCM WAV 流式输出，兼顾音质
- 🎚️ 均衡器（AudioGraph 预览）、音量条自绘、任务栏进度
- 📝 歌词：自动下载（网易云 / QQ / 酷狗）、在线搜索、桌面歌词窗口、卡拉 OK 高亮
- 🖼️ 封面：自动下载并嵌入标签、专辑 / 艺术家视图、封面文件夹
- 🎨 主题：主题预设 / 自定义主题色、毛玻璃背景、波形进度条
- 🪟 迷你播放器、当前播放列表窗口（拖拽排序）、独立播放队列窗口、睡眠定时器
- 🗂️ 播放列表墙：命名播放列表网格浏览、创建/重命名/删除、批量多选、封面、导入/导出
- ⌨️ 全局快捷键、媒体键（SMTC）、系统托盘、开机自启
- 📊 播放统计（播放次数 / 收听时长 / 收藏 / 评分 / 最近播放）
- 🌐 在线搜索（网易云 / QQ / 酷狗）、Last.fm 记录
- 🏷️ 标签编辑器（包括批量编辑、按文件名填充、批量下载歌词封面）

## 🖥️ 截图

<img width="2255" height="1368" alt="QQ20260808-172857" src="https://github.com/user-attachments/assets/49b4e965-5f6e-4563-b9a4-8e2b547e885d" />

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

使用 [Inno Setup](https://jrsoftware.org/isinfo.php) 将发布目录打包为 `setup.exe`（见 `installer/CelesteMusicPlayer.iss`）。

## 📄 许可证

[MIT](LICENSE)

## 🙏 致谢

- [FFmpeg](https://ffmpeg.org/)（gyan.dev 构建）
- [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2)（功能参考）
- [ECHO-NEXT](https://github.com/moekotori/ECHO)（功能参考）


## 🙏 一些作者的碎碎念

这个播放器是因为目前网上的播放器没有能完全符合我需求的，几乎所有播放器都不支持我想要把专辑按照时间顺序排列（foobar和musicbee支持但是有点丑），musicplayer2在我高中时候就在用了，基本上就缺一个排序功能，外观也一般般，正好最近AI浪潮，就先借朋友的cursor账号试了一下改了一下musicplayer2，于是后续继续开发下去了，我自己对代码一无所知，只有最基础最基础的入门知识。这个播放器也算是我用AI花了蛮久开发的，希望各位能用的舒服用的开心，那我也不枉花钱买那么多token了（
