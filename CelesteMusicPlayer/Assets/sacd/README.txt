CelesteMusicPlayer — SACD (.iso) 解码工具说明
===============================================

本播放器支持直接打开 SACD 镜像（.iso）播放：播放时会在后台调用命令行工具
sacd_extract.exe 把镜像解成逐轨 DSD(DSF) 文件，再走项目既有的 DSD 解码 / DoP 直出 /
PCM 转码全链路（bit-perfect 不变）。

本目录已随仓库提供 sacd_extract.exe（hank/sacd-ripper 增强版 0.3.9.3，x64，约 2.2MB，
GPL 许可，与仓库内 ffmpeg.exe 同样作为开源工具二进制随包分发）。程序启动时会自动
发现并使用：
  - 本目录（构建/发布会自动复制到输出目录），以及
  - 与 CelesteMusicPlayer.exe 同目录的 sacd_extract.exe（回退查找）

如需更新/替换，可从以下来源获取新版本：
  - 源码编译：https://github.com/setmind/sacd-ripper（tools/sacd_extract，需 pthread + libiconv）
  - 或各 SACD 抓取/GUI 工具包中附带的 sacd_extract.exe（x64）

没有该工具时：打开 .iso 会在状态栏提示「无法读取 SACD：缺少 sacd_extract.exe 或镜像不支持」，
不会影响其它格式播放。

调用参数（程序内部使用，供参考）：
  sacd_extract.exe -2 -s -c -W -i"<iso路径>" -o"<输出目录>"
  （-2 立体声 / -s 输出 DSF / -c DST→DSD / -W 覆盖已存在；立体声为空时自动回退 -m 多声道）
