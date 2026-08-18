# CelesteMusicPlayer

一个基于 **WinUI 3 / .NET 8** 的本地音乐播放器，功能参考 [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2) 设计开发。代码全部由AI生成，由Cursor和Reasonix辅助开发。

## ✨ 功能特性

- 🎵 本地音乐库：扫描文件夹 / 多选导入，自动读取标签（标题、艺术家、专辑、年份、封面）
- 🎧 广泛格式支持：MP3 / FLAC / WAV / M4A / APE / WavPack / TTA / DSD 等
  - 内置 [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) 转码引擎，系统无法解码的格式自动转码播放
- 📋 播放控制：顺序 / 单曲 / 随机播放、AB 重复、淡入淡出、倍速、进度拖动
- 🎚️ 均衡器（AudioGraph 预览）、音量条自绘、任务栏进度
- 📝 歌词：自动下载（网易云 / QQ / 酷狗）、在线搜索、桌面歌词窗口、卡拉 OK 高亮
- 🖼️ 封面：自动下载并嵌入标签、专辑 / 艺术家视图、封面文件夹
- 🎨 主题：主题预设 / 自定义主题色、毛玻璃背景、波形进度条（Poweramp 风格）
- 🪟 迷你播放器、当前播放列表窗口（拖拽排序）、睡眠定时器
- ⌨️ 全局快捷键、媒体键（SMTC）、系统托盘、开机自启
- 📊 播放统计（播放次数 / 收听时长 / 收藏 / 评分 / 最近播放）
- 🌐 在线搜索（网易云 / QQ / 酷狗）、Last.fm 记录
- 🏷️ 标签编辑器（批量编辑、按文件名填充、批量下载歌词封面）

## 🖥️ 截图

<img width="2255" height="1368" alt="QQ20260808-172857" src="https://github.com/user-attachments/assets/49b4e965-5f6e-4563-b9a4-8e2b547e885d" />

## 🛠️ 技术栈

- WinUI 3（Microsoft.WindowsAppSDK 1.8）
- .NET 8 / C#
- [TagLibSharp](https://github.com/mono/taglib-sharp) 标签读取
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) 系统托盘
- FFmpeg（内置，仅用于解码转码）

## 🔨 构建与发布

需要：.NET 8 SDK + Visual Studio 2022（含 WinUI 工作负载）

```bash
# 开发运行
dotnet build -c Debug

# 发布自包含（免安装，含 Windows App SDK 运行时）
dotnet publish CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -r win-x64 -o publish
```

发布产物为自包含目录：目标机器 **无需安装 .NET 运行时或 Windows App SDK**。

## 📦 安装包

使用 [Inno Setup](https://jrsoftware.org/isinfo.php) 将发布目录打包为 `setup.exe`（见 `installer/CelesteMusicPlayer.iss`）。

## 📄 许可证

[MIT](LICENSE)

## 🙏 致谢

- [FFmpeg](https://ffmpeg.org/)（gyan.dev 构建）
- [MusicPlayer2](https://github.com/zhongyang219/MusicPlayer2)（功能参考）


## 🙏 一些作者的碎碎念

这个播放器是因为目前网上的播放器没有能完全符合我需求的，几乎所有播放器都不支持我想要把专辑按照时间顺序排列（foobar和musicbee支持但是有点丑），musicplayer2在我高中时候就在用了，基本上就缺一个排序功能，外观也一般般，正好最近AI浪潮，就先借朋友的cursor账号试了一下改了一下musicplayer2，于是后续继续开发下去了，我自己对代码一无所知，只有最基础最基础的入门知识。这个播放器也算是我用AI花了蛮久开发的，用的开心就点个赞吧♥
