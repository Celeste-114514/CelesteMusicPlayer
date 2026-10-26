# Celeste 新界面外壳 · 部分后端接入版

直接用 Edge 打开本目录 `index.html` 即可预览。无需安装依赖、无需联网。

真实播放请运行最新的 `dist/web-shell-library-preview/启动网页界面.cmd`。启动前退出旧播放器，避免单实例机制激活旧窗口。浏览器中的文件页只用于外观预览，不直接控制音频。

功能、源码依据、已验收/待验收项目和缺陷记录统一见上一层的 `后端功能与WebView2接入台账.md`，后续优先更新台账。

## 本轮范围

- 圆润浅色外壳、顶部工具栏、精简侧边快捷入口、底部固定播放栏。
- 顶部水平居中的半圆双圈导航，圆心为展开/收起按钮，主页位于圆盘内部；仅保留图标及悬停名称。导航浮在内容上方，展开/收起不改变工具栏和页面抬头的尺寸或位置。
- 轮盘有缩放、旋转、淡入淡出的收放动画。首次展开；选择分类、点击页面或滚动时收起，之后仅通过圆心按钮重新展开。收起时内部按钮不进入键盘焦点序列。
- 内圈：歌曲、专辑、艺术家、专辑艺人、文件夹。
- 外圈：喜欢的音乐、评分、播放最多、播放列表、播放队列、最近播放。
- 左上角原品牌标志与文字已移除，等待用户后续设计。
- 内容区取消外围框线；轮盘缩为 250px（小窗口 230px），主页按钮采用与分类一致的平面风格。
- 底部进度条与播放按钮按整个窗口中心对齐；右上角六个按钮统一为 34×34px，依次为标签、音效处理、设置、最小化、最大化、关闭。
- 主页用于观察整体风格；歌曲页已按原 UI 制作序号、封面、标题、艺术家与专辑、格式参数及右侧时长。其他分类页仍为占位。
- 歌曲页提供即时搜索、按标题/艺术家/专辑/时长排序、升降序、选中高亮、双击或键盘播放、播放全部和无结果状态。演示播放队列按照当前筛选及排序生成。
- 队列和歌词抽屉、搜索入口、返回、键盘焦点、减少动态效果偏好。
- 浏览器下播放按钮、切歌、进度、音量、喜欢和播放模式使用演示状态，不播放真实音频。

**已创建 WebShellWindow 并部分连接原 C# 播放链路。** 真实曲库、歌曲双击、基本播放控制、状态和封面已接线。旧界面继续承载业务，原音频引擎源码和已有未提交功能保留；本轮只新增过渡窗口与少量入口。独占/ASIO 的网页配置页面尚未接入。

## 文件

- `index.html`：界面骨架及本地 SVG 图标。
- `shell.css`：外观、间距、圆角和动效；主要色彩在顶部 `:root`。
- `corner.css`：四分之一圆轮盘及为它留出的主内容空间。
- `songs.css`：歌曲列表、轮盘收放动画及页面空间适配。
- `shell.js`：导航、预览状态与 WebView2 消息入口。
- `preview.png`、`preview-quarter.png`、`preview-songs.png`：当前主页、轮盘和歌曲页截图。`preview-orbit.png` 是上一版弹出圆盘的历史截图。

## 当前宿主协议

新协议有明确版本，与历史 WebHost 不直接兼容。历史代码可参考，但需要适配器，不能只复制旧文件便宣称完成接入。

网页只发命令；真正处于 WebView2 时，以 C# 推送的状态为准，不启用模拟播放时钟。

网页发送示例：

```json
{"kind":"celeste.command","version":1,"command":"seek","seconds":96}
```

已接命令：`ready`、`trackInfo`（trackIds）、`playTrack`（trackId）、`playAll`（trackIds，按网页顺序）、`play`、`togglePlay`、`next`、`previous`、`seek`（seconds）、`setVolume`（volume，0–100）、`favorite`、`window`（action=minimize/maximize/close）。搜索和排序在完整曲库索引上由网页完成。`shuffle / repeat` 编辑暂未接入，会提示使用旧界面；不再使用历史 `playIndex` 命令。

C# 使用 `CoreWebView2.PostWebMessageAsJson` 回传：

```json
{
  "kind":"celeste.state",
  "version":1,
  "state":{
    "title":"Priestess",
    "artist":"Periphery · Juggernaut: Omega",
    "duration":303,
    "position":96,
    "playing":true,
    "volume":65,
    "favorite":false,
    "shuffle":false,
    "repeat":false,
    "quality":"ALAC · 16bit / 44.1kHz"
  }
}
```

曲库消息 `celeste.library` 按 begin/batch/end（revision）传输，每批 100 首。音频参数由可见行发 `trackInfo` 请求后经 `celeste.metadata` 回传。封面使用独立只读资源地址，两个后台读取并发。未知值不补造格式。

2026-10-25 播放栏：保留旧版按钮排列。`transport` 的 `action` 白名单为 `seekBack` / `seekForward` / `cycleOrder` / `desktopLyrics` / `unlockLyrics`；`setPlaybackOrder` 接受原来的五个枚举名。快进退复用原始源时间逻辑，设置持久化仍由旧逻辑负责。队列、更多与音频设置使用 HTML 面板；桌面歌词仍是原有独立桌面功能。

音频设置已使用网页 `<dialog>`，四页为输出、AB 循环、速度、睡眠定时。采用非模态 `show()`，在按钮上方定位的小浮窗，音频设置 430×540、队列和歌单选择 380×480，并根据窗口尺寸约束位置。点击外部、再次点击入口或按 Escape 收起；调整窗口大小时跟随按钮重新定位。标题/页签固定，内容区复用主界面的 `scrollbar-width:thin` 与 `scrollbar-color:#ded9dd transparent`，无全屏遮罩。移除借用原生 Flyout / 新建原生设置窗口的过渡实现。主窗口仅保留唯一音频会话和服务适配；两套界面共用 `ChangeOutputModeAsync` / `ChangeOutputDeviceAsync`、AB 循环业务方法、原实时变速和定时器逻辑。设置模式/设备按原行为在下次起播生效，面板分开显示偏好与实际链路，不自动重播。

带 `requestId` 的 `audioSettings` / `queue` / `more` 请求使用 `celeste.reply`（`ok`、`data` 或 `error`）回应。音频操作包括 get / refresh / setMode / setDevice / setRate / setPitch / setSleep / ab / deviceMemory / saveDeviceProfile；参数范围在 C# 再验证。设置串行守卫拒绝重入；设备保存前重新枚举并检查所属模式，ASIO 驱动为空时保留现有配置。进入/退出 ASIO 时重置不兼容的设备标识。设备枚举不激活 ASIO 驱动 COM。

队列每页 200 首，含 revision/index/trackId；播放时三项联合校验，避免列表改变后误播，保留 CUE 起点标识。更多菜单的加入已有歌单、在文件夹显示、复制歌名已接线；编辑标签、定位专辑灰显待接入。独占/ASIO 音量按原策略回传 `volumeEnabled` 并在宿主再次守卫；不在网页绕过硬件音量限制。

动效采用用户选定的 BiliPai Continuity `cubic-bezier(.20,.90,.22,1)` 与小幅阻尼弹簧（网页数学重建，非直接移植 Compose 渲染器）。参考本机 `BiliPai-main/design-tokens/.../AppMotionEasing.kt`、`design-system/.../AppMotionTokens.kt` 的连续性和轻弹簧参数；面板进入 420ms，页签 180ms，退出 140ms，系统减少动画时关闭位移/缩放。液态玻璃尚未制作。

状态增加 `playbackOrder`、`desktopLyrics`、`desktopLyricsLocked`、`coverUrl`。当前曲目即使不在曲库中也可读取封面。前端保留旧封面直到新图 `decode()` 成功，再叠加淡入；递增请求标记丢弃过期结果，避免快速切歌串图；加载失败显示占位，不反复重试或误显示其他专辑。适配系统减少动画偏好。

2026-10-25 专辑页与波形：曲库行新增 albumArtist / year / genre / track / disc / sortPath；歌曲排序补齐旧版八项（标题、艺术家、专辑、年份、时长、流派、音轨号、文件路径）。库的 end 消息新增 albums，复用旧 `BuildAlbumEntriesFromTracks` 的专辑聚合口径、主艺术家和代表封面。网页不自行猜测真实年份或添加时间，专辑“添加顺序”对应旧 SortIndex；七种专辑排序含名称、艺术家、年份、添加顺序、随机、曲目数、总时长。

`albums.js` 负责专辑网格、80 张一批分页、搜索、详情、多碟分组、整张播放、双击/Enter 单曲播放；“更多→定位到专辑”已指向网页详情。整张播放仍发送稳定 ID 数组，经过原曲库校验后使用既有音频会话。封面资源 `?size=480` 返回专辑缩略图，普通歌曲行仍用 160 档，均按需读取；修复播放栏淡入 opacity 样式污染歌曲列表/专辑图片的问题。

`waveform` 请求仅接受当前歌曲稳定 ID，回应 `celeste.waveform {trackId,peaks}`。同旧版复用 WaveformDataProvider 的 120 桶 RMS 波形与缓存，延迟 1200ms 错开发声/转码高峰；CUE 按 start/duration 限定区间。切歌/关闭取消旧请求并终止该请求的解码进程，缓存改用 ConcurrentDictionary 兼容双界面访问。波形画布覆在原 range 下方，鼠标拖动、点击定位、键盘操作和无障碍语义均保留，粉色为已播放部分。实际数据未就绪或读取失败时显示中性基线，不伪造真实音频波形；浏览器演示模式有明确的演示波形状态。所有 range 控件的滑块移除白边和外圈阴影。

宿主需验证消息来源、版本、命令和参数范围，只允许应用自己的本地页面调用。发布时将页面资源随包带上，限制导航至可信应用来源；窗口拖动需要宿主支持，CSS 标记自身不完成拖动。

## 已验证

2026-10-26：新增 `artists.js` 与 `collections.css`，艺术家/专辑艺人分别按 Artist / AlbumArtist 完整标签聚合（合作艺人不自动拆分），支持名称、歌曲数、专辑数、总时长、最新发行年份排序和 80 项分页。卡片显示歌曲及专辑数量，图片取曲库专辑封面，不冒充艺人照片。详情提供扩大概览、八项统计、歌曲/专辑切换、全部播放、双击/Enter 单曲播放及跳转网页专辑详情。专辑详情继续保留分碟与原音轨顺序，补充概览信息。

`collectionOverview {category: artists|albumArtists|albums, name}` 使用带 requestId 的 RPC。后端只从实际库条目选择数据，先复用编码缓存，再在后台用 TagLib 读取本地文件头；不启动额外 ffmpeg、网络下载或全文件解码。相同文件/CUE 分轨复用文件信息，统计按歌曲计数；文件时间戳缓存最多 8192 项，切换详情取消旧请求，前端丢弃过期响应。远程曲目没有已知缓存时标未知。无损占比为已识别编码中的比例，未知曲数明确提示；最高规格按 PCM 采样率、位深排序，DSD 单独显示；活跃年份仅为库内发行年份范围。

排序控件保留原字段和排序行为，外观改为统一网页浮层，支持键盘、选中标记、外部点击/Escape 关闭。轮盘展开不再改变任何页头位置。1.00× 刻度位于 0.5–2 范围的三分之一处，并计入 8px 滑块宽度。睡眠新增 selection=8、minutes=1–1440，输入后点击开始计时；沿用原 Dispatcher 定时暂停，前后端均验证数值，旧预设和曲目计数保持原语义。

新增 `tools/WebShellArtistsSmoke.cjs` 覆盖六页标题几何位置、排序浮层、两种艺人分组/计数、八项概览、详情播放稳定 ID、专辑跳转、速度刻度、自定义定时 RPC 和响应式布局。与现有两套网页检查一同通过；模拟宿主检查不等于真实音频输出或计时到期实测。

界面预览在 1440 / 1024 / 760 像素宽度下无横向溢出。模拟宿主验证了排序后稳定 ID、播放全部顺序和实际输出状态显示。真实程序载入 4031 首、真实封面显示、WASAPI 共享下双击播放与暂停/切歌/定位通过，用户确认声音正常。详见台账；本轮没有安装覆盖用户的正式播放器。
