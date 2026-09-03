# 交接文档：CelesteMusicPlayer（Celeste 音乐播放器）

## 项目位置
- 仓库：`C:\Users\admin\source\repos\CelesteMusicPlayer`（`global-workspace` 下 `celeste-winui`/`CelesteDesktop` 是空壳勿用）
- 主工程：`CelesteMusicPlayer\CelesteMusicPlayer\CelesteMusicPlayer.csproj`；解决方案 `CelesteMusicPlayer.slnx`
- 技术栈：.NET 9 + WinUI 3（WindowsAppSDK 1.8）+ NAudio + ffmpeg；WinExe

## Git 状态
- 最近发布：**v26.9.2**（tag `f2cbbe7`）。待发布 HEAD = `a5ccb24`（合并 origin/main README 更新 + 9 个功能/修复提交：DSDIFF DST 解码、耳机 Crossfeed、歌词偏移校准、桌面歌词改进、内存暴涨修复）。
- 工作树：干净（均已提交）。接续会话建议先 `git fetch` 看远端是否有新提交，有则 `git merge origin/main` 再继续。
- **同步策略（铁律）**：不 rebase（沙箱 shallow 克隆 rebase 易被 SIGTERM 打断损坏仓库）；远端有新版时 `git merge origin/main` 合并后直接 `git push`（fast-forward）。`git push` 前先 `git fetch` 确认。
- ⚠️ **构建环境坑（2026-09 实测，关键）**：本机沙箱 bash **缺大半 Windows 环境变量**（`APPDATA` / `ProgramData` / `ProgramFiles(x86)` / `LOCALAPPDATA` 等）。直接跑 `dotnet` 会 `Value cannot be null. (Parameter 'path1')`（NuGet 启动即崩），`gh` 报"未登录"（其实已登录，只是找不到 `%APPDATA%` 下的配置）。**必须在跑 dotnet / gh 前补齐这些变量**。WorkBuddy 会话 `2026-09-02-14-12-25/` 下有 `winenv.py`（补齐环境后转发命令）与 `build_retry.py`（已内含环境修复 + XamlCompiler 随机崩溃自动重试），可直接复用；或手动 `export APPDATA='C:\Users\admin\AppData\Roaming' LOCALAPPDATA=... ProgramData=... ProgramFiles=... ProgramFiles(x86)=... USERPROFILE=C:\Users\admin HOME=C:\Users\admin` 后再跑。

## 编译命令
- Release 自包含（可双击，exe 需同目录 ffmpeg.exe）：
  `dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`
- Debug 非打包（调试用，需复制 ffmpeg.exe 到输出目录）：
  `dotnet build CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=false`
  输出：`bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\`
- 输出产物里手动 `cp Assets/ffmpeg/ffmpeg.exe` 到 win-x64。
- ⚠️ XamlCompiler 偶发崩溃 `-1073741819`（重试即过）；**非打包 Debug 一次全量重建偶发 `ms-appx:///.../themeresources.xaml` 资源失败**——遇到先 `rm -rf obj/x64 bin/x64` 彻底清后干净重建即可恢复（与代码无关）。
- ⚠️ **运行/调试正确档位（高频坑）**：Debug 直接 F5 必须选 **`CelesteMusicPlayer (Package)`** 档（首次 VS 会自动生成 `CelesteMusicPlayer_TemporaryKey.pfx` 自签名证书）；想免安装则用 **Release + `(Unpackaged)`**。**禁止 `Debug + (Unpackaged)`**——该组合不自带运行时，引导阶段死等，表现为"一直加载"或"加载一会就退"。
- ⚠️ **`0x80131124`「未找到索引」= WMC9999 构建错误**（来自 `Microsoft.UI.Xaml.Markup.Compiler`，**非运行时 COMException**）：表现为 Debug 一个 `.g.cs` 都生成不出、程序启不动（日志连 `Main begin` 都没有）。真因常是 XAML 里写了 `Window` 根不认的属性（如 `Width`/`Height`），编译器建崩索引级联成 WMC9999。修法：删 XAML 根的 Width/Height、改代码后置里 `AppWindow.Resize(new Windows.Graphics.SizeInt32(w,h))`；仍不行查 csproj `WindowsAppSDK` 版本与 `microsoft.windowsappsdk.winui` 是否对齐（1.8 实验版通病）。
- **ffmpeg 依赖（体积优化）**：`Assets/ffmpeg/ffmpeg.exe` 现为**精简版**（仅音频编解码器/容器，静态链接无外部 dll，~36MB；原完整版 ~103MB），覆盖项目全部音频用途（APE/WavPack/TTA/MusePack/TAK/DSD/Opus/模块文件 + PCM 输出 + 波形解码 + 格式探测 + ReplayGain/ebur128）。`FindFfmpeg` 优先 `Assets/ffmpeg/ffmpeg.exe`，回退 `Assets/ffmpeg-slim/ffmpeg.exe`。发布目录约省 66MB。

## 当前功能/架构
- **三模式统一输出**：共享=NAudio WasapiOut、独占=原生 WASAPI、ASIO；全部经 `ManagedDspSourceProvider`（EQ→声道平衡→限幅→ReplayGain）统一 DSP 链。共享折叠 `pcm_f32le` 到设备 MixFormat（全格式可播，含 16/44.1 ALAC、24/96 FLAC）。
- **DSP 面板**（左侧「音效处理」→右侧面板）：ECHO 式曲线 EQ（专业曲线+简单模式+自动增益+用户自定义预设保存/加载/删除）、声道平衡、安全限幅（soft-knee 软削波+自动峰值余量）、ReplayGain 响度归一化（track/album+preamp+防削波+10ms 平滑）。独占/共享/ASIO 都实时生效、暂停后保留。
  - **耳机 Crossfeed（声场交叉馈送）**（v26.9.3）：声道平衡模块新增子能力，缓解头中效应；已修复控件与原控件重叠的布局问题。
- **歌词**：滚动歌词（手动滚动+单击跳进度+翻译行不高亮主题色）、桌面歌词。
  - **歌词偏移校准**（v26.9.3）：LRC 与演唱不同步时手动微调偏移量（`_lyricOffset` 字段 + `OffsetLyricPosition` 方法），已补全接线。
- **无边框 + 自绘系统按钮**：窗口常驻无边框（`MakeWindowBorderless` 去 WS_CAPTION/THICKFRAME/BORDER），右上角自绘 最小化/最大化还原/关闭；右上角还有 全屏（`MoveAndResize` 所在监视器+置顶隐藏任务栏，无缝）、刷新、选项。窗口默认 1400×800、最小 1400×800。
- **分类**：新增「评分」（未评分+★1-★5，胶囊在搜索框右侧、排序按钮左侧，仅评分分类显示）；评分用 `TrackStatsStore.Rating`（0-5）。
- **排序**：歌曲面板排序字段扩到 8 个（标题/艺术家/专辑/年份/时长/流派/音轨号/文件路径）+ 升降序；用户列表/收藏/最近/流派/年份复用该排序。
- **设置审计**：清理了死选项（删除「无歌词时显示歌曲信息」`ShowSongInfoIfNoLyric` 开关，无歌词一律显示"该音频没有歌词"）；`MusicRateCustom` 已确认无残留。
- **封面优化**：封面解码缓存（ConcurrentDictionary）+ 并发限流（SemaphoreSlim(4)）+ 更严的行复用防护（`ContainerFromItem(song)==container` 才填图）。
- **时长**：`PlaylistItem.DurationText` 改为由 `Duration` 只读推导（不再可写覆盖），恒有值，修复"时长空白串"。
- **SACD (.iso) 播放**：播放 .iso 时由 `SacdIsoExtractor` 懒调用外部 `sacd_extract.exe`（`Assets/sacd/`，不随包发布，需自备 x64 二进制）把镜像解成逐轨 DSD(DSF)，**就地展开进播放队列**后走既有 DSD 全链路（DoP 直出 / PCM 转码 / 波形 / 队列恢复全部复用，bit-perfect 不变）。抽取结果按 ISO 内容指纹缓存到 `%LOCALAPPDATA%\CelesteMusicPlayer\SacdCache`，重复播放不重抽。缺工具时状态栏提示「无法读取 SACD：缺少 sacd_extract.exe 或镜像不支持」。接入点仅在 `PlayUserPlaylistAt`（播放时展开）与 `PrepareTrackPausedAsync`（.iso 不预载进 MediaPlayer），引擎层不感知 .iso。
- **重启恢复**：关闭再开记住**上次播放队列 + 当前曲**（`PlayQueueStore`/play-queue.json，默认开），恢复后定位到该曲**从头播**（不续播到具体秒数）。已移除逐曲续播书签（`TrackPositionStore`/track-positions.json）；旧单曲记忆 `PlaybackSessionStore`/last-playback.json 保留作无队列时的兼容回退（同样从头播）。

## ⚠️ 待办 / 已知现象
1. **任务栏图标外圈黑框**：已做常驻无边框（`MakeWindowBorderless` 去 WS_CAPTION/THICKFRAME/BORDER），并在**首次激活后强制再执行一次无边框 + `SetWindowPos(SWP_FRAMECHANGED)`** 让系统重新计算非客户区以清残留黑框。若仍存在，请在新会话用截图/观察确认（区分任务栏缩略图 vs 窗口角落），并按此排查：非客户区残留、`ExtendsContentIntoTitleBar`+`SetTitleBar` 是否保留 caption 阴影、`OverlappedPresenter` 投影。
2. **歌曲长时**：改只读推导后应修复；若个别仍空白，给具体文件名/格式再查。
3. 极端 EQ 参数仍有轻微爆音可能性（软削波已缓解）；独占高采样+EQ 受托管性能限制（较 ECHO native/SIMD 难完全丝滑）。
4. 非打包 Debug 全量重建的 ms-appx 资源偶发问题（见编译命令）。
5. **排序方案 A**：歌曲面板已扩字段；专辑墙/文件夹尚未接入多字段排序（如需继续）。
7. ④ 智能播放列表（Auto-DJ 规则生成）曾实现后又**移除**（用户确认不需要），代码 `git revert 6250df5` 可恢复。
6. 音频设置面板（AudioSettingsPanel）**已实现**（右上角音频图标 `E91F` 打开，滑出式面板含 输出模式/DSP 链/设备状态）。待做：对齐 ECHO 的 输出模式/音频链路/专业播放状态面板细节（用户曾点名的后续打磨）。
8. **启动偶发 `ArgumentException` 被吞**（已知旧问题，非本版引入）：设备枚举完成后 `ShowErrorAsync` 弹出的错误窗被静默吞掉，用户端看不到；v26.9.2 冒烟日志中该异常反复出现，根因待查。
9. **启动加载日志刷屏**（已知旧问题，非本版引入）：`Load 命中缓存 OutputMode` 在启动加载库时反复打印（设置缓存命中日志过频），非崩溃，仅日志噪音，待降频/收敛。

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

## ECHO 网易云/QQ 下载机制研究报告（去 bot 依赖选型依据，2026-08）

> 背景：用户目标=去掉对 WSL `streaming-plugin`(musicbot) 的音频下载依赖，把 ECHO 的网易云/QQ 下载套用进来。
> ECHO 源码位置：`C:\Users\admin\Downloads\Download\ECHO-main`（Electron + TS，非 C#）。以下均是**已核实**的结论。

### ECHO 的三条实现（分开看）
1. **[下载侧] `src/main/downloads/DownloadService.ts` + `DownloadAuthorization.ts`**
   - 用法：用户粘**分享 URL**（如 `music.163.com/#/song?id=xxx` / `y.qq.com/n/ryqq/songDetail/{songmid}`）→ `createUrlJob` → 调随包捆绑的 **yt-dlp**（`-f bestaudio/best --extract-audio`）提取并下载，再 `--extract-audio` 转码。
   - 头注入：Referer/Origin（`music.163.com` / `y.qq.com`）+ 平台 Cookie；受保护 provider（网易云/QQ/酷狗）需 `DownloadAuthorizationToken`(HMAC-SHA256 签名) + `DownloadFeatureUnlockService`(付费授权 Gate)。
   - 网易云下载产物是 `.ncm` 加密，再用随包捆绑的 **`NCMConverter.exe`** 解密。
   - ⚠️ **结论：ECHO 下载侧是"外部二进制集合"（yt-dlp / ffmpeg / NCMConverter.exe 三者随包分发），不是自研接口。**

2. **[网易云流媒体/直链侧] `NeteaseStreamingProvider.ts`**
   - 捆绑 **`@neteasecloudmusicapienhanced/api`**(Node 库，网易云开源 API 分支) 调官方 `song_url_v1`(→接口 `/song/url/v1`，按 level) 与 `song_url`(→`/song/url`，按 br)，可选带 `MUSIC_U` cookie。
   - 码率：`standard=128k` 到 `hires=flac/jymaster`；同一 URL 即可用于播放也可下载。
   - ⚠️ 底层是网易云 **weapi 接口**（AES-CBC + RSA 加密参数）。C# 移植需自研 weapi 加密，或改用更老的免加密 `api.song.url`。

3. **[QQ 流媒体/直链侧] `QQMusicStreamingProvider.ts`**
   - **纯 HTTP POST** `https://u.y.qq.com/cgi-bin/musicu.fcg`，模块 `music.vkey.GetVkey: UrlGetVkey`；构造 `guid/songmid/media_mid/uin/gtk` + `filename=${qualityPrefix}${mediaMid}.${ext}`，读回 `midurlinfo[0].purl` 拼 `sip`。
   - **128k(standard) 无 cookie 也可用**（UNLOGIN 时 qualities 限 `['standard']`）；高码率需 cookie(uin/guid/gtk)。
   - ✅ **该实现是纯自研 HTTP，C# 可 1:1 移植，无需任何外部库。这是 ECHO 里最容易套用的部分。**

### 播放器现状盘点（本仓库）
- 网易云：搜索已返回 `id`(→songId)；直接够用。
- QQ：搜索返回 `songmid`/`albummid`，但 **缺 `media_mid`**（取自 `song/url` 的 `file.media_mid`）——需补一个 QQ `song/detail` 接口或在搜索里多抓 `media_mid`（部分情况 songmid 即 media_mid，稳妥起见补齐）。
- cookie 已保存：`AppSettingsStore.NetEaseCookie/QqCookie`（本地明文，设置页输入）。
- 音频下载入口：`OnlineSearchWindow.DownloadAudio_Click` → `StreamingServiceClient.GetDownloadAsync`（即待绕过的 WSL 依赖）+ 返回 URL/Header 后本机 HttpClient 存盘。
- 现有 `OnlineMusicApi.cs` 已实现网易云/QQ 搜索+歌词+封面（public API），无下载接口。

### 三条备选路线对比（供用户拍板）
| 路线 | 是否去 bot | 外部依赖 | 网易云 | QQ | 工作量 | 失效/风险 |
|---|---|---|---|---|---|---|
| **A. 纯 C# 自研接口**（weapi song/url + QQ vkey） | ✅ 彻底去 | 无（仅需可选 cookie） | 需自研 weapi 加密；无 cookie 常拿不到 | 纯 HTTP 移植，128k 免登录 | 中~大（QQ 小、网易云大） | 网易云 weapi 接口/风控易变，需维护 |
| **B. 捆绑 yt-dlp+ffmpeg+NCMConverter**（复刻 ECHO 下载侧） | ✅ 去 | 3 个外部二进制随包 | yt-dlp 提取+ncm 解密 | yt-dlp 提取 | 小（接线即可） | 二进制体积大、更新维护、依赖外部工具 |
| **C. 保留/完善 WSL bot 服务** | ❌ 仍依赖 WSL | 无（服务端在 WSL） | bot 已有 | bot 已有 | 小 | 未真正"去 bot"，用户明确不要 |

**建议（推荐 A，分两期）**：
- **第一期（推荐立刻做）**：QQ vkey 纯 HTTP 直链移植（128k 免 cookie，工作量小、稳定）→ 改 `DownloadAudio_Click`：QQ 平台直接本机调 `musicu.fcg` 拿直链下载，不再走 WSL。
- **第二期**：网易云 weapi `song/url/v1`（先做免加密 `api.song.url` 兜底，再考虑 weapi+`MUSIC_U` 提码率）；QQ 补 `media_mid` 抓取与高码率(cookie)。
- 保留现有 `StreamingServiceClient` 仅用于 Apple Music（其需要登录态 wrapper，属既定保留项）。

**注意**：网易云/QQ 歌词、搜索接口已在线工作且非下载，不受下载改造影响；歌曲下载涉及平台授权，仅作个人/最低限度用途。

