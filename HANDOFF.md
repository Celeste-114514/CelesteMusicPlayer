# 交接文档：CelesteMusicPlayer（Celeste 音乐播放器）

## 项目位置
- 仓库：`C:\Users\admin\source\repos\CelesteMusicPlayer`（`global-workspace` 下 `celeste-winui`/`CelesteDesktop` 是空壳勿用）
- 主工程：`CelesteMusicPlayer\CelesteMusicPlayer\CelesteMusicPlayer.csproj`；解决方案 `CelesteMusicPlayer.slnx`
- 技术栈：.NET 8 + WinUI 3（WindowsAppSDK 1.8）+ NAudio 2.2.1 + ffmpeg；WinExe

## Git 状态
- 最近提交：`e455167`（feat(DSP) 三模式统一 DSP 链 + 共享全格式播放）
- **有大量未提交改动**（当前会话在 `0c233c2` 之后、`e455167` 基础上又改了很多，尚未再次 commit）。接续会话建议：`git status` 先看，必要时先 commit 一次再继续。

## 编译命令
- Release 自包含（可双击，exe 需同目录 ffmpeg.exe）：
  `dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`
- Debug 非打包（调试用，需复制 ffmpeg.exe 到输出目录）：
  `dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=false`
  输出：`bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\`
- 输出产物里手动 `cp Assets/ffmpeg/ffmpeg.exe` 到 win-x64。
- ⚠️ XamlCompiler 偶发崩溃 `-1073741819`（重试即过）；**非打包 Debug 一次全量重建偶发 `ms-appx:///.../themeresources.xaml` 资源失败**——遇到先 `rm -rf obj/x64 bin/x64` 彻底清后干净重建即可恢复（与代码无关）。

## 当前功能/架构
- **三模式统一输出**：共享=NAudio WasapiOut、独占=原生 WASAPI、ASIO；全部经 `ManagedDspSourceProvider`（EQ→声道平衡→限幅→ReplayGain）统一 DSP 链。共享折叠 `pcm_f32le` 到设备 MixFormat（全格式可播，含 16/44.1 ALAC、24/96 FLAC）。
- **DSP 面板**（左侧「音效处理」→右侧面板）：ECHO 式曲线 EQ（专业曲线+简单模式+自动增益+用户自定义预设保存/加载/删除）、声道平衡、安全限幅（soft-knee 软削波+自动峰值余量）、ReplayGain 响度归一化（track/album+preamp+防削波+10ms 平滑）。独占/共享/ASIO 都实时生效、暂停后保留。
- **歌词**：滚动歌词（手动滚动+单击跳进度+翻译行不高亮主题色）、桌面歌词。
- **无边框 + 自绘系统按钮**：窗口常驻无边框（`MakeWindowBorderless` 去 WS_CAPTION/THICKFRAME/BORDER），右上角自绘 最小化/最大化还原/关闭；右上角还有 全屏（`MoveAndResize` 所在监视器+置顶隐藏任务栏，无缝）、刷新、选项。窗口默认 1400×800、最小 1400×800。
- **分类**：新增「评分」（未评分+★1-★5，胶囊在搜索框右侧、排序按钮左侧，仅评分分类显示）；评分用 `TrackStatsStore.Rating`（0-5）。
- **排序**：歌曲面板排序字段扩到 8 个（标题/艺术家/专辑/年份/时长/流派/音轨号/文件路径）+ 升降序；用户列表/收藏/最近/流派/年份复用该排序。
- **设置审计**：清理了死选项（删除「无歌词时显示歌曲信息」`ShowSongInfoIfNoLyric` 开关，无歌词一律显示"该音频没有歌词"）；`MusicRateCustom` 已确认无残留。
- **封面优化**：封面解码缓存（ConcurrentDictionary）+ 并发限流（SemaphoreSlim(4)）+ 更严的行复用防护（`ContainerFromItem(song)==container` 才填图）。
- **时长**：`PlaylistItem.DurationText` 改为由 `Duration` 只读推导（不再可写覆盖），恒有值，修复"时长空白串"。

## ⚠️ 待办 / 已知现象
1. **任务栏图标外圈黑框**：已做常驻无边框（`MakeWindowBorderless` 去 WS_CAPTION/THICKFRAME/BORDER），并在**首次激活后强制再执行一次无边框 + `SetWindowPos(SWP_FRAMECHANGED)`** 让系统重新计算非客户区以清残留黑框。若仍存在，请在新会话用截图/观察确认（区分任务栏缩略图 vs 窗口角落），并按此排查：非客户区残留、`ExtendsContentIntoTitleBar`+`SetTitleBar` 是否保留 caption 阴影、`OverlappedPresenter` 投影。
2. **歌曲长时**：改只读推导后应修复；若个别仍空白，给具体文件名/格式再查。
3. 极端 EQ 参数仍有轻微爆音可能性（软削波已缓解）；独占高采样+EQ 受托管性能限制（较 ECHO native/SIMD 难完全丝滑）。
4. 非打包 Debug 全量重建的 ms-appx 资源偶发问题（见编译命令）。
5. **排序方案 A**：歌曲面板已扩字段；专辑墙/文件夹尚未接入多字段排序（如需继续）。
6. 音频设置下拉（右上角耳机图标 → 对齐 ECHO 的 输出模式/音频链路/专业播放状态面板）**尚未开始**——用户曾点名的后续大工程。

## 给接续会话的建议
- 先 `git commit` 当前未提交改动存档，再继续。
- 若要继续 DSP：独占性能/爆音、音频设置面板（ECHO 对齐）。
- 若要继续 UI：黑框根因、排序扩展、专辑墙等。

## 插件化服务（跨会话大工程，用户已确认方向，待实施）
**目标**：把 `musicbot-go`（WSL 侧，见交接文档上半部用户提供的 bot 说明）的能力独立打包成"可直接运行的 WSL 服务包"，从播放器发布渠道（release）下载包 → 安装 → 播放器内即可使用对应服务（Apple Music/网易云/QQ 等的**在线歌词加载 + 最低限度下载**）。若用户缺运行环境（WSL/Go），安装时提示（安装提示在别的会话做）。

**可行性（已核实）**：
- WSL `musicbot-go` 的 `bot/platform/manager.go` 已提供 `Search(ctx, platform, query, limit)`、`GetLyrics(ctx, platform, trackID)`、`GetDownloadInfo`、`GetTrack` 等成体系接口（含 Apple Music `plugins/applemusic` → wrapper 10021/20021/30021 解密）。**缺的只是为它包一个 HTTP 服务做"桥接"**。
- Apple Music 歌词来自 `https://api.music.apple.com/v1/catalog/{sf}/songs/{id}/lyrics`（MusicKit，需 Apple 订阅 cookie/身份）——播放器无法免登录拿，必须依赖 bot wrapper 登录态。
- iTunes Search API（播放器现有 `OnlineMusicApi.SearchItunesSongsAsync`）**不返回歌词**（已实测），故歌词需登录/其他平台兜底。

**实施蓝图**：
1. **bot 侧（WSL/Go）**：新增一个独立 HTTP 服务（可复用 platform.Manager），暴露：
   - `GET /api/ping`（健康检查）、`GET /api/platforms`（可用平台）
   - `GET /api/search?platform=&q=&limit=`
   - `GET /api/lyric?platform=&trackId=`（返回 LRC/明文；Apple Music 返回 TTML 需在 bot `bot/lyric` 转 LRC）
   - `POST /api/download?platform=&trackId=&out=`（或返回下载流；Apple Music 走 wrapper 解密）
   - 打包：tar + systemd/启动脚本，独立于 Telegram bot 可单独跑，端口约定（如 21010）。
2. **播放器侧（本仓库）**：
   - 设置→流媒体板块：显示各平台登录状态；配置 WSL 服务地址（IP:port）。
   - 把"下载歌词/在线标签/Apple Music 无歌词提示"改为：优先调该服务拿 Apple Music 真歌词；服务未配置/未登录时保留当前"按歌名艺人从网易云等兜底"与"请登录个人账号以加载歌词"提示。
   - 安装包下载：播放器 release 附 WSL 服务包；安装器做环境检测（wsl 是否可用），缺失则在安装时提示（提示 UI 在别的会话做）。
3. **本会话已完成的铺垫**：设置已加"流媒体"板块骨架（PanelStreaming + AmLoginButton 占位→跳转安装/配置）、Apple Music 未登录时歌词提示改为"需登录个人账号加载"、登录按钮目前弹"待接入"对话框。

**注意**：cmake/Go/网络/版权——下载功能仅作"最低限度"；Apple 歌词依赖订阅账号有效性/风控；wrapper 有已知 segfault（冷却自动拉起，见 bot 说明）。
