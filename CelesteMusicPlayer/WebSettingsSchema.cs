using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页设置页的字段目录（单一真相源）。
    ///
    /// 设计要点（来自 2026-10-26 设置调查）：
    /// 1. 只登记后端真实读过写法这一侧的字段；没找到消费者的字段（StreamingServiceUrl、
    ///    LastFmSharedSecret 之类）不进表，避免"看起来能改、改了没反应"。
    /// 2. 每项都带 scope（作用面）与 note（生效时机/依赖），网页原样显示，不替用户脑补。
    /// 3. Readers/Writers 是显式登记的读写白名单，不是反射：写哪个字段、怎么夹取值都在明面上。
    /// 4. 密码/密钥不在这里读写，走 <see cref="MainWindow.WebSettingsAsync"/> 的 credentials 命令
    ///    ——空输入保留旧值，不会被一次误提交抹掉。
    /// 新增字段：Defs、Readers、Writers 三处都要加，否则该项在网页上不会出现。
    /// </summary>
    internal static class WebSettingsSchema
    {
        internal sealed class Def
        {
            public string id { get; set; } = "";
            public string group { get; set; } = "";
            public string label { get; set; } = "";
            /// <summary>bool / slider / select / text / color / folder / file</summary>
            public string kind { get; set; } = "bool";
            public string? note { get; set; }
            /// <summary>app=全程序；native=只影响旧 MainWindow / 独立原生窗；web=只影响当前网页窗口</summary>
            public string scope { get; set; } = "app";
            /// <summary>select 的候选值，格式 "值|显示名"</summary>
            public string[]? options { get; set; }
            public double min { get; set; }
            public double max { get; set; }
            public double step { get; set; }
            public string? unit { get; set; }
            /// <summary>低频项，默认折叠</summary>
            public bool advanced { get; set; }
        }

        internal sealed class Group
        {
            public string id { get; set; } = "";
            public string label { get; set; } = "";
            public string description { get; set; } = "";
            public string icon { get; set; } = "";
        }

        internal static readonly Group[] Groups =
        {
            new() { id = "general", label = "常规与启动", description = "开机自启、关闭行为，以及程序启动后恢复什么。", icon = "power" },
            new() { id = "audio", label = "音频输出", description = "输出方式、缓冲与 DSD 高级兼容。具体音效在「音效处理」页。", icon = "wave" },
            new() { id = "playback", label = "播放行为", description = "换曲、淡化、变速与队列插入位置。", icon = "play" },
            new() { id = "appearance", label = "外观", description = "界面风格、配色、背景与列表密度。", icon = "disc" },
            new() { id = "mini", label = "迷你播放器", description = "选择样式、制作自己的配色，或导入与导出样式文件。", icon = "play" },
            new() { id = "desktopLyrics", label = "桌面歌词", description = "WebView2 歌词：显示、字体、竖排与音乐响应。", icon = "lyrics" },
            new() { id = "lyrics", label = "歌词与封面", description = "歌词来源与样式、桌面歌词、自动获取与保存规则。", icon = "lyrics" },
            new() { id = "library", label = "媒体库", description = "监视目录、扫描规则与删除保护。", icon = "folder" },
            new() { id = "network", label = "网络与在线服务", description = "WebDAV 网络音乐库与在线账号凭据。", icon = "cloud" },
            new() { id = "system", label = "快捷键与系统", description = "全局热键、系统媒体控制、任务栏与文件关联。", icon = "settings" },
            new() { id = "storage", label = "存储与维护", description = "三类缓存、偏好备份与恢复范围。", icon = "shield" },
            new() { id = "about", label = "关于", description = "版本信息与更新检查。", icon = "clock" }
        };

        // —— 构造辅助：把 group 一次性打到一组 def 上 ——
        private static Def[] In(string group, params Def[] items)
        {
            foreach (Def item in items) item.group = group;
            return items;
        }

        private static Def Bool(string id, string label, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = "bool", note = note, scope = scope, advanced = advanced };

        private static Def Slider(string id, string label, double min, double max, double step, string? unit = null, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = "slider", min = min, max = max, step = step, unit = unit, note = note, scope = scope, advanced = advanced };

        private static Def Sel(string id, string label, string options, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = "select", note = note, scope = scope, advanced = advanced, options = options.Split(';', StringSplitOptions.RemoveEmptyEntries) };

        private static Def Txt(string id, string label, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = "text", note = note, scope = scope, advanced = advanced };

        private static Def Col(string id, string label, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = "color", note = note, scope = scope, advanced = advanced };

        private static Def Path(string id, string label, bool file, string? note = null, string scope = "app", bool advanced = false) =>
            new() { id = id, label = label, kind = file ? "file" : "folder", note = note, scope = scope, advanced = advanced };

        internal static readonly Def[] Defs = Build().Where(d => d.scope != "native").ToArray();

        private static Def[] Build() => In("general",
                Bool("AutoRun", "随 Windows 启动", "写入当前用户的启动项；取消勾选会立刻删掉启动项，不是只改数字。"),
                Sel("StartupCategory", "启动后进入的分类", "UserPlaylist|播放队列;Songs|歌曲;Albums|专辑;Artists|艺术家;AlbumArtists|专辑艺人;Favorites|我喜欢的音乐;Ratings|评分;Recent|最近播放;PlaylistWall|播放列表",
                    "只控制旧主窗口启动时停在哪个分类；网页界面启动时固定进入主页。", scope: "native"),
                Bool("RestoreLibrary", "启动时恢复音乐库"),
                Bool("RestorePlayback", "启动时恢复到上次曲目与位置"),
                Bool("AutoPlayWhenStart", "启动后自动开始播放", "只有在成功恢复出上次的曲目时才会起播。" + Requires("RestorePlayback")),
                Bool("AlwaysOnTop", "主窗口置顶", "只影响旧主窗口；网页窗口的置顶请看本页底部的「网页窗口置顶」按钮。", scope: "native")
            ).Concat(In("audio",
                Sel("OutputMode", "输出模式", "Shared|WASAPI 共享;WasapiExclusive|WASAPI 独占;Asio|ASIO",
                    "切换设备请到底栏「音频设置」面板操作 —— 那里和这里是同一套后端，不清成两份。"),
                Bool("HiFiSoftwareVolume", "HiFi 软件音量", "独占 / ASIO 下用 DSP 做数字衰减；音量不等于 100% 时不再 bit-perfect。DSD/DoP 直出时无效。"),
                Slider("OutputBufferMs", "输出缓冲", 20, 500, 10, "ms", "越小越跟手，越大越抗卡顿。下一首歌或重新起播后生效。", advanced: true),
                Sel("DsdOutputMode", "DSD 输出方式", "Pcm|转成 PCM;Dop|DoP 直出",
                    "DoP 直出要求：DSF、DSD64–256、1–2 声道、独占或 ASIO、DSP 全关；不满足会自动回退 PCM。", advanced: true),
                Sel("DopContainerMode", "DoP 容器摆位", "Packed24|24 位紧凑;Container32|32 位标准集装箱",
                    "仅在真正走 DoP 直出时起作用；预生成的缓存文件固定是 24 位紧凑。", scope: "app", advanced: true),
                Bool("DsdPreloadEnabled", "DSD 整轨预载", "第一次播放会先花一点时间生成缓存，之后再播同一首是顺序读文件。", advanced: true),
                Bool("AsioFeederEnabled", "ASIO 后台喂料", "后台线程备货、驱动回调只拷贝。下一首歌或重新起播后生效。", advanced: true),
                Path("DsdCachePath", "DSD 预载缓存目录", false, "留空用默认位置（D 盘存在就用 D 盘，否则用系统数据目录）。", advanced: true),
                Slider("TranscodeCacheLimitMb", "转码缓存上限", 256, 102400, 256, "MB", "ffmpeg 转出的 PCM 缓存上限，超出后按最久未用温和清理。", advanced: true)
            )).Concat(In("playback",
                Slider("CrossfadeMs", "交叉淡化", 0, 12000, 250, "ms", "0 = 关闭。只对自动连播的自然换曲生效，手动切歌不淡化。"),
                Bool("StopWhenError", "播放出错时停止", "目前挂在 MediaPlayer 的失败回调上；自持引擎的失败路径还没接到同一个开关。"),
                Bool("ContinueWhenSwitchPlaylist", "切换列表时继续播放", "当前实现是暂停播放；换成新播放链路后这里要重新安线，别当成所有场景都成立了。"),
                Bool("InsertPlaylistAtBegin", "新加入的曲目插到队列最前"),
                Slider("PlaybackRate", "播放速度", 0.5, 2, 0.05, "×", "实际生效区间 0.5–2；旧存档里更大的值会被夹到这里。"),
                Bool("PlaybackRatePreservePitch", "变速时保持音调", "关掉后音调随倍率升降（nightcore / daycore 效果）。")
            )).Concat(In("appearance",
                Sel("CelesteThemeName", "主题文件", "$(themes)", "读 Themes\\*.celeste-theme 里的配色。只作用于旧主窗口的令牌。", scope: "native"),
                Sel("AccentSource", "强调色来源", "System|跟随系统;Custom|自定义"),
                Col("CustomAccentColor", "自定义强调色", "只有「强调色来源」选了自定义才起作用。"),
                Bool("EnableBackground", "显示背景", "网页背景暂由网页自己的样式决定，此项目前作用于旧界面。", scope: "native"),
                Bool("AlbumCoverAsBackground", "用专辑封面做背景", scope: "native"),
                Bool("BackgroundGaussBlur", "背景高斯模糊", scope: "native"),
                Slider("GaussBlurRadius", "模糊半径", 1, 8, 1, "级", scope: "native"),
                Sel("BackgroundPreset", "内置背景预设", "|不使用预设;Aurora|极光;Sunset|日落;Midnight|午夜", scope: "native"),
                Bool("BackgroundPresetMotion", "预设背景缓慢移动", scope: "native"),
                Path("CustomBackgroundPath", "自定义背景图片", true, "留空表示不用自定义图片。", scope: "native"),
                Bool("ShowNavFavorites", "导航栏显示「我喜欢的音乐」", scope: "native"),
                Bool("ShowNavRecent", "导航栏显示「最近播放」", scope: "native"),
                Bool("ShowNavGenre", "导航栏显示「流派」", scope: "native"),
                Bool("ShowNavYear", "导航栏显示「年份」", scope: "native"),
                Bool("NavEmojiIcons", "导航栏用 emoji 图标", scope: "native"),
                Sel("PlaylistDensity", "列表行距", "Compact|紧凑;Comfortable|宽松", "只作用于旧主窗口的播放列表。", scope: "native"),
                Bool("ShowPlaylistTitle", "列表显示标题", scope: "native"),
                Bool("ShowPlaylistArtist", "列表显示艺术家", scope: "native"),
                Bool("ShowPlaylistAlbum", "列表显示专辑", scope: "native"),
                Bool("ShowPlaylistYear", "列表显示年份", scope: "native"),
                Bool("ShowPlaylistDuration", "列表显示时长", scope: "native"),
                Sel("NowPlayingLayout", "播放页布局", "Classic|经典;Water|水面倒影", "沉浸式播放页（旧界面）的布局。", scope: "native"),
                Sel("NowPlayingVisual", "播放页可视化", "Waveform|波形;Bars|频谱柱;Radial|径向频谱;Scope|示波器;Lissajous|李萨如", "沉浸式播放页（旧界面）的可视化元素。", scope: "native"),
                Sel("MiniPlayerBackground", "迷你播放器底色", "Cover|跟随封面;Dark|深色;Frosted|毛玻璃", "只影响独立迷你窗口。", scope: "native"),
                Bool("MiniPlayerAlwaysOnTop", "迷你播放器置顶", "只影响独立迷你窗口。", scope: "native"),
                Bool("OpenMiniPlayerOnStartup", "启动时打开迷你播放器", "只影响独立迷你窗口。", scope: "native"),
                Sel("ScatteredPlayBackground", "零散播放窗口背景", "Cover|跟随封面;Dark|深色;Light|浅色;Frosted|毛玻璃", "双击音乐文件打开的那个独立播放窗。", scope: "native"),
                Bool("TaskbarPlayerEnabled", "任务栏播放条", "在主显示器任务栏里显示播放控制。自动隐藏的任务栏、全屏应用或空间不足时会自动让位。", scope: "app")
            )).Concat(In("lyrics",
                Sel("LyricSourcePriority", "歌词来源优先级", "ExternalLrc|外挂 LRC 优先;EmbeddedSynced|内嵌同步歌词优先;EmbeddedUnsynced|内嵌纯文本歌词优先",
                    "选中的来源找不到时，仍会按 外挂 → 内嵌同步 → 内嵌纯文本 的顺序继续找。"),
                Bool("LyricFuzzyMatch", "歌词模糊匹配", "按标题/艺术家近似匹配本地歌词文件。"),
                Bool("HideBlankLyricLines", "隐藏空歌词行"),
                Sel("LyricAlign", "歌词对齐", "Left|左;Center|居中;Right|右"),
                Slider("LyricLineSpacing", "歌词行距", 0, 40, 1, "px"),
                Slider("LyricOffsetMs", "歌词时间偏移", -5000, 5000, 50, "ms", "正数延后、负数提前，用来校正和演唱对不上。"),
                Slider("MainLyricFontSize", "主歌词字号", 12, 48, 1, "px"),
                Txt("MainLyricFontFamily", "主歌词字体", "留空跟随系统默认。"),
                Bool("ShowLyricTranslate", "显示在线歌词译文", "只对在线获取的歌词起作用，不代表能筛掉本地文件中自带的译文。"),
                Sel("LyricSavePolicy", "歌词保存策略", "None|不自动保存;Auto|自动保存;Ask|每次询问"),
                // —— 桌面歌词（独立原生窗）——
                Bool("OpenDesktopLyricsOnStartup", "启动桌面歌词", "这是个偏好；勾选/取消会立刻开关那个覆盖层，不只是改下次。", scope: "native"),
                Bool("DesktopLyricHideWithoutLyric", "没有歌词时隐藏桌面歌词", scope: "native"),
                Bool("DesktopLyricHideWhenPaused", "暂停时隐藏桌面歌词", scope: "native"),
                Bool("DesktopLyricLockOnStart", "桌面歌词启动即锁定", scope: "native"),
                Bool("DesktopLyricShowUnlockWhenLocked", "锁定后显示解锁提示", scope: "native"),
                Bool("DesktopLyricClickThrough", "桌面歌词鼠标穿透", scope: "native", advanced: true),
                Bool("DesktopLyricShowRomanization", "显示罗马音", "读取歌曲同名 .romaji.lrc 或 .romanized.lrc，按时间戳关联。"),
                Bool("DesktopLyricHideTranslation", "桌面歌词不显示译文行", scope: "native"),
                Bool("DesktopLyricShowBackgroundBar", "桌面歌词显示底色条", scope: "native"),
                Slider("DesktopLyricVisibleLines", "桌面歌词显示行数", 1, 3, 1, "行", scope: "native"),
                Slider("DesktopLyricLineSpacing", "桌面歌词行距", 0, 24, 1, "px", scope: "native"),
                Slider("DesktopLyricOpacity", "桌面歌词不透明度", 20, 100, 1, "%", scope: "native"),
                Slider("DesktopLyricFontSize", "桌面歌词字号", 14, 64, 1, "px", scope: "native"),
                Sel("DesktopLyricDirection", "文字方向", "Horizontal|横排;Vertical|竖排"),
                Path("DesktopLyricFontFile", "自定义字体文件", true, "支持 TTF、OTF、WOFF、WOFF2；失败时回退系统字体。"),
                Bool("DesktopLyricMusicReactive", "音乐响应光晕", "使用真实输出采样；无采样时不显示。"),
                Slider("DesktopLyricScale", "整体缩放", 50, 150, 5, "%"),
                Sel("DesktopLyricColorMode", "桌面歌词配色模式", "Theme|跟随主程序（ECHO 风格）;Solid|自定义纯色;Gradient|自定义渐变", "当前歌词使用主题渐变或自定义颜色；未唱部分仍可单独设置。"),
                Col("DesktopLyricGradientStart", "歌词渐变起点"),
                Col("DesktopLyricGradientEnd", "歌词渐变终点"),
                Slider("DesktopLyricSecondaryFontSize", "前后歌词字号", 12, 48, 1, "px"),
                Txt("DesktopLyricFontFamily", "桌面歌词字体", scope: "native"),
                Col("DesktopLyricPlayedColor", "已唱部分颜色", scope: "native"),
                Col("DesktopLyricUnplayedColor", "未唱部分颜色", scope: "native"),
                Col("DesktopLyricOutlineColor", "描边颜色", scope: "native"),
                Slider("DesktopLyricOutlineWidth", "描边宽度", 0, 4, 0.5, "px", "0 = 不描边。", scope: "native", advanced: true),
                Slider("DesktopLyricShadowStrength", "阴影强度", 0, 3, 1, "级", scope: "native", advanced: true),
                Sel("DesktopLyricAlign", "桌面歌词对齐", "Left|左;Center|居中;Right|右", scope: "native"),
                Col("DesktopLyricBackgroundColor", "底色条颜色", scope: "native", advanced: true),
                // —— 歌词 / 封面的获取与保存 ——
                Bool("AutoDownloadLyrics", "缺歌词时自动下载"),
                Bool("AutoDownloadCover", "缺封面时自动下载"),
                Bool("AutoDownloadOnlyWhenTagFull", "只在标签完整时才自动下载", "标签残缺时宁可不下载，避免张冠李戴。"),
                Bool("SaveLyricToSongFolder", "歌词存到歌曲所在文件夹", "没指定歌词目录时生效；指定了歌词目录就用那个目录。"),
                Bool("SaveCoverToSongFolder", "封面存到歌曲所在文件夹"),
                Path("LyricFolder", "歌词目录", false, "留空表示存走歌曲所在文件夹的规则。"),
                Path("CoverFolder", "封面目录", false, "留空表示存走歌曲所在文件夹的规则。"),
                Bool("UseInnerCoverFirst", "优先用文件内嵌封面"),
                Sel("LyricPageSource", "标签工具 · 歌词下载源", "LRClib|LRClib（免 key）;QQ|QQ 音乐", "标签页下载歌词时的默认来源。"),
                Sel("LyricBatchPageSource", "标签工具 · 批量下载源", "LRClib|LRClib（免 key）;QQ|QQ 音乐"),
                Sel("LyricPageWriteTarget", "标签工具 · 歌词写入口径", "Sidecar|外挂 .lrc;TagPlain|标签纯文本;TagTimestamped|标签带时间轴;Both|.lrc + 标签纯文本"),
                Bool("LyricPageIncludeTranslation", "标签工具 · 下载歌词含译文"),
                Bool("WriteId3v23", "写入 ID3v2.3 标签", "兼容性更好；有些新字段需要 v2.4，有需要再关。", advanced: true)
            )).Concat(In("library",
                Bool("AutoUpdateLibrary", "文件夹变化时自动更新音乐库", "改完会重启目录监视服务，不是只改一个开关。"),
                Bool("AutoApplyCueOnScan", "自动展开同名 CUE 分轨"),
                Bool("RemoveMissingOnUpdate", "更新时移除失效曲目"),
                Bool("IgnoreTooShortOnUpdate", "忽略过短的音频文件"),
                Slider("FileTooShortSec", "过短判定阈值", 0, 600, 1, "秒", "勾选「忽略过短」后才起作用。"),
                Slider("RecentPlayedRangeDays", "最近播放显示范围", 0, 3650, 1, "天", "0 = 不限时间。这只决定列表里显示多少天内听过的曲目，不会删除播放历史。"),
                Bool("DisableDeleteFromDisk", "禁止从磁盘删除文件", "关掉后删除才是真的删除文件；从列表里移除不受这项影响。")
            )).Concat(In("network",
                Txt("WebDavUrl", "WebDAV 服务器地址", "例如 https://dav.example.com/dav"),
                Txt("WebDavDisplayName", "显示名称", "留空自动用网址主机名。"),
                Txt("WebDavUser", "用户名"),
                Sel("WebDavProxyMode", "代理", "System|跟随系统;None|不使用;Manual|手动指定"),
                Txt("WebDavProxyHost", "手动代理主机", "代理选「手动」时生效。"),
                Slider("WebDavProxyPort", "手动代理端口", 1, 65535, 1, "", "代理选「手动」时生效。", advanced: true),
                Path("WebDavCacheDir", "网络音乐缓存目录", false, "留空用默认位置。", advanced: true),
                Slider("WebDavCacheLimitMb", "网络音乐缓存上限", 128, 102400, 128, "MB"),
                Sel("OnlineSearchDefaultSource", "在线搜索默认平台", "MusicBrainz|MusicBrainz;iTunes|iTunes"),
                Bool("EnableLastFm", "启用 Last.fm 收听记录提交"),
                Bool("LastFmNowPlaying", "提交「正在收听」"),
                Slider("LastFmLeastSeconds", "最少收听秒数", 1, 600, 1, "秒", "和下面的百分比要同时满足才会提交，不是二选一。"),
                Slider("LastFmLeastPercent", "最少收听比例", 1, 100, 1, "%", "和上面的秒数要同时满足才会提交。")
            )).Concat(In("system",
                Bool("EnableGlobalHotkeys", "启用全局热键", "热键注册失败会在本页的「注册结果」里写出来，不会闷声失败。"),
                Bool("EnableSmtc", "接入系统媒体控制", "任务栏预览 / 键盘媒体键的那套系统控件。"),
                Bool("ShowTaskbarProgress", "任务栏显示播放进度", "作用于旧主窗口的任务栏按钮。", scope: "native")
            )).ToArray();

        private static string Requires(string dependency) => "需要同时开启「" + dependency + "」。";

        // —— 读（Get 用）——
        internal static readonly Dictionary<string, Func<AppSettingsState, object?>> Readers = new(StringComparer.Ordinal)
        {
            // general
            ["AutoRun"] = s => s.AutoRun,
            ["StartupCategory"] = s => s.StartupCategory,
            ["RestoreLibrary"] = s => s.RestoreLibrary,
            ["RestorePlayback"] = s => s.RestorePlayback,
            ["AutoPlayWhenStart"] = s => s.AutoPlayWhenStart,
            ["AlwaysOnTop"] = s => s.AlwaysOnTop,
            // audio
            ["OutputMode"] = s => s.OutputMode,
            ["HiFiSoftwareVolume"] = s => s.HiFiSoftwareVolume,
            ["OutputBufferMs"] = s => s.OutputBufferMs,
            ["DsdOutputMode"] = s => s.DsdOutputMode,
            ["DopContainerMode"] = s => s.DopContainerMode,
            ["DsdPreloadEnabled"] = s => s.DsdPreloadEnabled,
            ["AsioFeederEnabled"] = s => s.AsioFeederEnabled,
            ["DsdCachePath"] = s => s.DsdCachePath,
            ["TranscodeCacheLimitMb"] = s => s.TranscodeCacheLimitMb,
            // playback
            ["CrossfadeMs"] = s => s.CrossfadeMs,
            ["StopWhenError"] = s => s.StopWhenError,
            ["ContinueWhenSwitchPlaylist"] = s => s.ContinueWhenSwitchPlaylist,
            ["InsertPlaylistAtBegin"] = s => s.InsertPlaylistAtBegin,
            ["PlaybackRate"] = s => s.PlaybackRate,
            ["PlaybackRatePreservePitch"] = s => s.PlaybackRatePreservePitch,
            // appearance
            ["CelesteThemeName"] = s => s.CelesteThemeName,
            ["AccentSource"] = s => s.AccentSource,
            ["CustomAccentColor"] = s => s.CustomAccentColor,
            ["EnableBackground"] = s => s.EnableBackground,
            ["AlbumCoverAsBackground"] = s => s.AlbumCoverAsBackground,
            ["BackgroundGaussBlur"] = s => s.BackgroundGaussBlur,
            ["GaussBlurRadius"] = s => s.GaussBlurRadius,
            ["BackgroundPreset"] = s => s.BackgroundPreset,
            ["BackgroundPresetMotion"] = s => s.BackgroundPresetMotion,
            ["CustomBackgroundPath"] = s => s.CustomBackgroundPath,
            ["ShowNavFavorites"] = s => s.ShowNavFavorites,
            ["ShowNavRecent"] = s => s.ShowNavRecent,
            ["ShowNavGenre"] = s => s.ShowNavGenre,
            ["ShowNavYear"] = s => s.ShowNavYear,
            ["NavEmojiIcons"] = s => s.NavEmojiIcons,
            ["PlaylistDensity"] = s => s.PlaylistDensity,
            ["ShowPlaylistTitle"] = s => s.ShowPlaylistTitle,
            ["ShowPlaylistArtist"] = s => s.ShowPlaylistArtist,
            ["ShowPlaylistAlbum"] = s => s.ShowPlaylistAlbum,
            ["ShowPlaylistYear"] = s => s.ShowPlaylistYear,
            ["ShowPlaylistDuration"] = s => s.ShowPlaylistDuration,
            ["NowPlayingLayout"] = s => s.NowPlayingLayout,
            ["NowPlayingVisual"] = s => s.NowPlayingVisual,
            ["MiniPlayerBackground"] = s => s.MiniPlayerBackground,
            ["MiniPlayerAlwaysOnTop"] = s => s.MiniPlayerAlwaysOnTop,
            ["OpenMiniPlayerOnStartup"] = s => s.OpenMiniPlayerOnStartup,
            ["ScatteredPlayBackground"] = s => s.ScatteredPlayBackground,
            ["TaskbarPlayerEnabled"] = s => s.TaskbarPlayerEnabled,
            // lyrics
            ["LyricSourcePriority"] = s => s.LyricSourcePriority,
            ["LyricFuzzyMatch"] = s => s.LyricFuzzyMatch,
            ["HideBlankLyricLines"] = s => s.HideBlankLyricLines,
            ["LyricAlign"] = s => s.LyricAlign,
            ["LyricLineSpacing"] = s => s.LyricLineSpacing,
            ["LyricOffsetMs"] = s => s.LyricOffsetMs,
            ["MainLyricFontSize"] = s => s.MainLyricFontSize,
            ["MainLyricFontFamily"] = s => s.MainLyricFontFamily,
            ["ShowLyricTranslate"] = s => s.ShowLyricTranslate,
            ["LyricSavePolicy"] = s => s.LyricSavePolicy,
            ["OpenDesktopLyricsOnStartup"] = s => s.OpenDesktopLyricsOnStartup,
            ["DesktopLyricHideWithoutLyric"] = s => s.DesktopLyricHideWithoutLyric,
            ["DesktopLyricHideWhenPaused"] = s => s.DesktopLyricHideWhenPaused,
            ["DesktopLyricLockOnStart"] = s => s.DesktopLyricLockOnStart,
            ["DesktopLyricShowUnlockWhenLocked"] = s => s.DesktopLyricShowUnlockWhenLocked,
            ["DesktopLyricClickThrough"] = s => s.DesktopLyricClickThrough,
            ["DesktopLyricShowRomanization"] = s => s.DesktopLyricShowRomanization,
            ["DesktopLyricHideTranslation"] = s => s.DesktopLyricHideTranslation,
            ["DesktopLyricShowBackgroundBar"] = s => s.DesktopLyricShowBackgroundBar,
            ["DesktopLyricVisibleLines"] = s => s.DesktopLyricVisibleLines,
            ["DesktopLyricLineSpacing"] = s => s.DesktopLyricLineSpacing,
            ["DesktopLyricOpacity"] = s => s.DesktopLyricOpacity,
            ["DesktopLyricDirection"] = s => s.DesktopLyricDirection,
            ["DesktopLyricFontFile"] = s => s.DesktopLyricFontFile,
            ["DesktopLyricMusicReactive"] = s => s.DesktopLyricMusicReactive,
            ["DesktopLyricScale"] = s => s.DesktopLyricScale,
            ["DesktopLyricColorMode"] = s => s.DesktopLyricColorMode,
            ["DesktopLyricGradientStart"] = s => s.DesktopLyricGradientStart,
            ["DesktopLyricGradientEnd"] = s => s.DesktopLyricGradientEnd,
            ["DesktopLyricSecondaryFontSize"] = s => s.DesktopLyricSecondaryFontSize,
            ["DesktopLyricFontSize"] = s => s.DesktopLyricFontSize,
            ["DesktopLyricFontFamily"] = s => s.DesktopLyricFontFamily,
            ["DesktopLyricPlayedColor"] = s => s.DesktopLyricPlayedColor,
            ["DesktopLyricUnplayedColor"] = s => s.DesktopLyricUnplayedColor,
            ["DesktopLyricOutlineColor"] = s => s.DesktopLyricOutlineColor,
            ["DesktopLyricOutlineWidth"] = s => s.DesktopLyricOutlineWidth,
            ["DesktopLyricShadowStrength"] = s => s.DesktopLyricShadowStrength,
            ["DesktopLyricAlign"] = s => s.DesktopLyricAlign,
            ["DesktopLyricBackgroundColor"] = s => s.DesktopLyricBackgroundColor,
            ["AutoDownloadLyrics"] = s => s.AutoDownloadLyrics,
            ["AutoDownloadCover"] = s => s.AutoDownloadCover,
            ["AutoDownloadOnlyWhenTagFull"] = s => s.AutoDownloadOnlyWhenTagFull,
            ["SaveLyricToSongFolder"] = s => s.SaveLyricToSongFolder,
            ["SaveCoverToSongFolder"] = s => s.SaveCoverToSongFolder,
            ["LyricFolder"] = s => s.LyricFolder,
            ["CoverFolder"] = s => s.CoverFolder,
            ["UseInnerCoverFirst"] = s => s.UseInnerCoverFirst,
            ["LyricPageSource"] = s => s.LyricPageSource,
            ["LyricBatchPageSource"] = s => s.LyricBatchPageSource,
            ["LyricPageWriteTarget"] = s => s.LyricPageWriteTarget,
            ["LyricPageIncludeTranslation"] = s => s.LyricPageIncludeTranslation,
            ["WriteId3v23"] = s => s.WriteId3v23,
            // library
            ["AutoUpdateLibrary"] = s => s.AutoUpdateLibrary,
            ["AutoApplyCueOnScan"] = s => s.AutoApplyCueOnScan,
            ["RemoveMissingOnUpdate"] = s => s.RemoveMissingOnUpdate,
            ["IgnoreTooShortOnUpdate"] = s => s.IgnoreTooShortOnUpdate,
            ["FileTooShortSec"] = s => s.FileTooShortSec,
            ["RecentPlayedRangeDays"] = s => s.RecentPlayedRangeDays,
            ["DisableDeleteFromDisk"] = s => s.DisableDeleteFromDisk,
            // network
            ["WebDavUrl"] = s => s.WebDavUrl,
            ["WebDavDisplayName"] = s => s.WebDavDisplayName,
            ["WebDavUser"] = s => s.WebDavUser,
            ["WebDavProxyMode"] = s => s.WebDavProxyMode,
            ["WebDavProxyHost"] = s => s.WebDavProxyHost,
            ["WebDavProxyPort"] = s => s.WebDavProxyPort,
            ["WebDavCacheDir"] = s => s.WebDavCacheDir,
            ["WebDavCacheLimitMb"] = s => s.WebDavCacheLimitMb,
            ["OnlineSearchDefaultSource"] = s => s.OnlineSearchDefaultSource,
            ["EnableLastFm"] = s => s.EnableLastFm,
            ["LastFmNowPlaying"] = s => s.LastFmNowPlaying,
            ["LastFmLeastSeconds"] = s => s.LastFmLeastSeconds,
            ["LastFmLeastPercent"] = s => s.LastFmLeastPercent,
            // system
            ["EnableGlobalHotkeys"] = s => s.EnableGlobalHotkeys,
            ["EnableSmtc"] = s => s.EnableSmtc,
            ["ShowTaskbarProgress"] = s => s.ShowTaskbarProgress
        };

        // —— 写（Patch 用）——
        internal static readonly Dictionary<string, Action<AppSettingsState, JsonElement>> Writers = new(StringComparer.Ordinal)
        {
            ["AutoRun"] = (s, v) => s.AutoRun = Bool(v),
            ["StartupCategory"] = (s, v) => s.StartupCategory = One(v, "UserPlaylist", "Songs", "Albums", "Artists", "AlbumArtists", "Favorites", "Ratings", "Recent", "PlaylistWall"),
            ["RestoreLibrary"] = (s, v) => s.RestoreLibrary = Bool(v),
            ["RestorePlayback"] = (s, v) => s.RestorePlayback = Bool(v),
            ["AutoPlayWhenStart"] = (s, v) => s.AutoPlayWhenStart = Bool(v),
            ["AlwaysOnTop"] = (s, v) => s.AlwaysOnTop = Bool(v),
            ["OutputMode"] = (s, v) => s.OutputMode = One(v, "Shared", "WasapiExclusive", "Asio"),
            ["HiFiSoftwareVolume"] = (s, v) => s.HiFiSoftwareVolume = Bool(v),
            ["OutputBufferMs"] = (s, v) => s.OutputBufferMs = Int(v, 20, 500),
            ["DsdOutputMode"] = (s, v) => s.DsdOutputMode = One(v, "Pcm", "Dop"),
            ["DopContainerMode"] = (s, v) => s.DopContainerMode = One(v, "Packed24", "Container32"),
            ["DsdPreloadEnabled"] = (s, v) => s.DsdPreloadEnabled = Bool(v),
            ["AsioFeederEnabled"] = (s, v) => s.AsioFeederEnabled = Bool(v),
            ["DsdCachePath"] = (s, v) => s.DsdCachePath = Text(v, 512),
            ["TranscodeCacheLimitMb"] = (s, v) => s.TranscodeCacheLimitMb = Int(v, 256, 102400),
            ["CrossfadeMs"] = (s, v) => s.CrossfadeMs = Int(v, 0, 12000),
            ["StopWhenError"] = (s, v) => s.StopWhenError = Bool(v),
            ["ContinueWhenSwitchPlaylist"] = (s, v) => s.ContinueWhenSwitchPlaylist = Bool(v),
            ["InsertPlaylistAtBegin"] = (s, v) => s.InsertPlaylistAtBegin = Bool(v),
            ["PlaybackRate"] = (s, v) => s.PlaybackRate = Math.Clamp(Num(v), 0.5, 2),
            ["PlaybackRatePreservePitch"] = (s, v) => s.PlaybackRatePreservePitch = Bool(v),
            ["CelesteThemeName"] = (s, v) => s.CelesteThemeName = Text(v, 260),
            ["AccentSource"] = (s, v) => s.AccentSource = One(v, "System", "Custom"),
            ["CustomAccentColor"] = (s, v) => s.CustomAccentColor = Color(v),
            ["EnableBackground"] = (s, v) => s.EnableBackground = Bool(v),
            ["AlbumCoverAsBackground"] = (s, v) => s.AlbumCoverAsBackground = Bool(v),
            ["BackgroundGaussBlur"] = (s, v) => s.BackgroundGaussBlur = Bool(v),
            ["GaussBlurRadius"] = (s, v) => s.GaussBlurRadius = Int(v, 1, 8),
            ["BackgroundPreset"] = (s, v) => s.BackgroundPreset = One(v, "", "Aurora", "Sunset", "Midnight"),
            ["BackgroundPresetMotion"] = (s, v) => s.BackgroundPresetMotion = Bool(v),
            ["CustomBackgroundPath"] = (s, v) => s.CustomBackgroundPath = Text(v, 512),
            ["ShowNavFavorites"] = (s, v) => s.ShowNavFavorites = Bool(v),
            ["ShowNavRecent"] = (s, v) => s.ShowNavRecent = Bool(v),
            ["ShowNavGenre"] = (s, v) => s.ShowNavGenre = Bool(v),
            ["ShowNavYear"] = (s, v) => s.ShowNavYear = Bool(v),
            ["NavEmojiIcons"] = (s, v) => s.NavEmojiIcons = Bool(v),
            ["PlaylistDensity"] = (s, v) => s.PlaylistDensity = One(v, "Compact", "Comfortable"),
            ["ShowPlaylistTitle"] = (s, v) => s.ShowPlaylistTitle = Bool(v),
            ["ShowPlaylistArtist"] = (s, v) => s.ShowPlaylistArtist = Bool(v),
            ["ShowPlaylistAlbum"] = (s, v) => s.ShowPlaylistAlbum = Bool(v),
            ["ShowPlaylistYear"] = (s, v) => s.ShowPlaylistYear = Bool(v),
            ["ShowPlaylistDuration"] = (s, v) => s.ShowPlaylistDuration = Bool(v),
            ["NowPlayingLayout"] = (s, v) => s.NowPlayingLayout = One(v, "Classic", "Water"),
            ["NowPlayingVisual"] = (s, v) => s.NowPlayingVisual = One(v, "Waveform", "Bars", "Radial", "Scope", "Lissajous"),
            ["MiniPlayerBackground"] = (s, v) => s.MiniPlayerBackground = One(v, "Cover", "Dark", "Frosted"),
            ["MiniPlayerAlwaysOnTop"] = (s, v) => s.MiniPlayerAlwaysOnTop = Bool(v),
            ["OpenMiniPlayerOnStartup"] = (s, v) => s.OpenMiniPlayerOnStartup = Bool(v),
            ["ScatteredPlayBackground"] = (s, v) => s.ScatteredPlayBackground = One(v, "Cover", "Dark", "Light", "Frosted"),
            ["TaskbarPlayerEnabled"] = (s, v) => s.TaskbarPlayerEnabled = Bool(v),
            ["LyricSourcePriority"] = (s, v) => s.LyricSourcePriority = One(v, "ExternalLrc", "EmbeddedSynced", "EmbeddedUnsynced"),
            ["LyricFuzzyMatch"] = (s, v) => s.LyricFuzzyMatch = Bool(v),
            ["HideBlankLyricLines"] = (s, v) => s.HideBlankLyricLines = Bool(v),
            ["LyricAlign"] = (s, v) => s.LyricAlign = One(v, "Left", "Center", "Right"),
            ["LyricLineSpacing"] = (s, v) => s.LyricLineSpacing = Int(v, 0, 40),
            ["LyricOffsetMs"] = (s, v) => s.LyricOffsetMs = Int(v, -5000, 5000),
            ["MainLyricFontSize"] = (s, v) => s.MainLyricFontSize = Num(v, 12, 48),
            ["MainLyricFontFamily"] = (s, v) => s.MainLyricFontFamily = Text(v, 160),
            ["ShowLyricTranslate"] = (s, v) => s.ShowLyricTranslate = Bool(v),
            ["LyricSavePolicy"] = (s, v) => s.LyricSavePolicy = One(v, "None", "Auto", "Ask"),
            ["OpenDesktopLyricsOnStartup"] = (s, v) => s.OpenDesktopLyricsOnStartup = Bool(v),
            ["DesktopLyricHideWithoutLyric"] = (s, v) => s.DesktopLyricHideWithoutLyric = Bool(v),
            ["DesktopLyricHideWhenPaused"] = (s, v) => s.DesktopLyricHideWhenPaused = Bool(v),
            ["DesktopLyricLockOnStart"] = (s, v) => s.DesktopLyricLockOnStart = Bool(v),
            ["DesktopLyricShowUnlockWhenLocked"] = (s, v) => s.DesktopLyricShowUnlockWhenLocked = Bool(v),
            ["DesktopLyricClickThrough"] = (s, v) => s.DesktopLyricClickThrough = Bool(v),
            ["DesktopLyricShowRomanization"] = (s, v) => s.DesktopLyricShowRomanization = Bool(v),
            ["DesktopLyricHideTranslation"] = (s, v) => s.DesktopLyricHideTranslation = Bool(v),
            ["DesktopLyricShowBackgroundBar"] = (s, v) => s.DesktopLyricShowBackgroundBar = Bool(v),
            ["DesktopLyricVisibleLines"] = (s, v) => s.DesktopLyricVisibleLines = Int(v, 1, 3),
            ["DesktopLyricLineSpacing"] = (s, v) => s.DesktopLyricLineSpacing = Num(v, 0, 24),
            ["DesktopLyricOpacity"] = (s, v) => s.DesktopLyricOpacity = Int(v, 20, 100),
            ["DesktopLyricDirection"] = (s,v) => s.DesktopLyricDirection = Text(v,20)=="Vertical"?"Vertical":"Horizontal",
            ["DesktopLyricFontFile"] = (s,v) => s.DesktopLyricFontFile = Text(v,2000),
            ["DesktopLyricMusicReactive"] = (s,v) => s.DesktopLyricMusicReactive = Bool(v),
            ["DesktopLyricScale"] = (s,v) => s.DesktopLyricScale = Int(v,50,150),
            ["DesktopLyricColorMode"] = (s, v) => s.DesktopLyricColorMode = Text(v, 20) is "Solid" ? "Solid" : Text(v, 20) is "Gradient" ? "Gradient" : "Theme",
            ["DesktopLyricGradientStart"] = (s, v) => s.DesktopLyricGradientStart = Color(v),
            ["DesktopLyricGradientEnd"] = (s, v) => s.DesktopLyricGradientEnd = Color(v),
            ["DesktopLyricSecondaryFontSize"] = (s, v) => s.DesktopLyricSecondaryFontSize = Num(v, 12, 48),
            ["DesktopLyricFontSize"] = (s, v) => s.DesktopLyricFontSize = Num(v, 14, 64),
            ["DesktopLyricFontFamily"] = (s, v) => s.DesktopLyricFontFamily = Text(v, 160),
            ["DesktopLyricPlayedColor"] = (s, v) => s.DesktopLyricPlayedColor = Color(v),
            ["DesktopLyricUnplayedColor"] = (s, v) => s.DesktopLyricUnplayedColor = Color(v),
            ["DesktopLyricOutlineColor"] = (s, v) => s.DesktopLyricOutlineColor = Color(v),
            ["DesktopLyricOutlineWidth"] = (s, v) => s.DesktopLyricOutlineWidth = Num(v, 0, 4),
            ["DesktopLyricShadowStrength"] = (s, v) => s.DesktopLyricShadowStrength = Int(v, 0, 3),
            ["DesktopLyricAlign"] = (s, v) => s.DesktopLyricAlign = One(v, "Left", "Center", "Right"),
            ["DesktopLyricBackgroundColor"] = (s, v) => s.DesktopLyricBackgroundColor = Color(v, allowArgb: true),
            ["AutoDownloadLyrics"] = (s, v) => s.AutoDownloadLyrics = Bool(v),
            ["AutoDownloadCover"] = (s, v) => s.AutoDownloadCover = Bool(v),
            ["AutoDownloadOnlyWhenTagFull"] = (s, v) => s.AutoDownloadOnlyWhenTagFull = Bool(v),
            ["SaveLyricToSongFolder"] = (s, v) => s.SaveLyricToSongFolder = Bool(v),
            ["SaveCoverToSongFolder"] = (s, v) => s.SaveCoverToSongFolder = Bool(v),
            ["LyricFolder"] = (s, v) => s.LyricFolder = Text(v, 512),
            ["CoverFolder"] = (s, v) => s.CoverFolder = Text(v, 512),
            ["UseInnerCoverFirst"] = (s, v) => s.UseInnerCoverFirst = Bool(v),
            ["LyricPageSource"] = (s, v) => s.LyricPageSource = One(v, "LRClib", "QQ"),
            ["LyricBatchPageSource"] = (s, v) => s.LyricBatchPageSource = One(v, "LRClib", "QQ"),
            ["LyricPageWriteTarget"] = (s, v) => s.LyricPageWriteTarget = One(v, "Sidecar", "TagPlain", "TagTimestamped", "Both"),
            ["LyricPageIncludeTranslation"] = (s, v) => s.LyricPageIncludeTranslation = Bool(v),
            ["WriteId3v23"] = (s, v) => s.WriteId3v23 = Bool(v),
            ["AutoUpdateLibrary"] = (s, v) => s.AutoUpdateLibrary = Bool(v),
            ["AutoApplyCueOnScan"] = (s, v) => s.AutoApplyCueOnScan = Bool(v),
            ["RemoveMissingOnUpdate"] = (s, v) => s.RemoveMissingOnUpdate = Bool(v),
            ["IgnoreTooShortOnUpdate"] = (s, v) => s.IgnoreTooShortOnUpdate = Bool(v),
            ["FileTooShortSec"] = (s, v) => s.FileTooShortSec = Int(v, 0, 600),
            ["RecentPlayedRangeDays"] = (s, v) => s.RecentPlayedRangeDays = Int(v, 0, 3650),
            ["DisableDeleteFromDisk"] = (s, v) => s.DisableDeleteFromDisk = Bool(v),
            ["WebDavUrl"] = (s, v) => s.WebDavUrl = Text(v, 1024),
            ["WebDavDisplayName"] = (s, v) => s.WebDavDisplayName = Text(v, 120),
            ["WebDavUser"] = (s, v) => s.WebDavUser = Text(v, 256),
            ["WebDavProxyMode"] = (s, v) => s.WebDavProxyMode = One(v, "System", "None", "Manual"),
            ["WebDavProxyHost"] = (s, v) => s.WebDavProxyHost = Text(v, 256),
            ["WebDavProxyPort"] = (s, v) => s.WebDavProxyPort = Int(v, 1, 65535),
            ["WebDavCacheDir"] = (s, v) => s.WebDavCacheDir = Text(v, 512),
            ["WebDavCacheLimitMb"] = (s, v) => s.WebDavCacheLimitMb = Int(v, 128, 102400),
            ["OnlineSearchDefaultSource"] = (s, v) => s.OnlineSearchDefaultSource = One(v, "MusicBrainz", "iTunes"),
            ["EnableLastFm"] = (s, v) => s.EnableLastFm = Bool(v),
            ["LastFmNowPlaying"] = (s, v) => s.LastFmNowPlaying = Bool(v),
            ["LastFmLeastSeconds"] = (s, v) => s.LastFmLeastSeconds = Int(v, 1, 600),
            ["LastFmLeastPercent"] = (s, v) => s.LastFmLeastPercent = Int(v, 1, 100),
            ["EnableGlobalHotkeys"] = (s, v) => s.EnableGlobalHotkeys = Bool(v),
            ["EnableSmtc"] = (s, v) => s.EnableSmtc = Bool(v),
            ["ShowTaskbarProgress"] = (s, v) => s.ShowTaskbarProgress = Bool(v)
        };

        // —— 取值校验：写坏的值宁可直接报错退回，也不能悄悄落盘 ——
        private static bool Bool(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0,
            _ => false
        };

        private static string One(JsonElement v, params string[] allowed)
        {
            string value = Text(v, 256);
            if (!allowed.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException("取值不在允许范围内：" + (value.Length == 0 ? "(空)" : value));
            return value;
        }

        private static string Text(JsonElement v, int max)
        {
            string value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            value = value.Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static double Num(JsonElement v) => Num(v, double.MinValue, double.MaxValue);

        private static double Num(JsonElement v, double min, double max)
        {
            double value;
            if (v.ValueKind == JsonValueKind.Number) value = v.GetDouble();
            else if (!double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : "", out value))
                throw new ArgumentException("需要一个数字。");
            if (!double.IsFinite(value)) throw new ArgumentException("数值无效。");
            return Math.Clamp(value, min, max);
        }

        private static int Int(JsonElement v, int min, int max) => (int)Math.Clamp(Math.Round(Num(v)), min, max);

        private static string Color(JsonElement v, bool allowArgb = false)
        {
            string value = Text(v, 32);
            if (value.Length == 0) throw new ArgumentException("颜色不能为空。");
            // #RRGGBB，底色条额外允许 #AARRGGBB
            int digits = allowArgb && value.Length == 9 ? 8 : 6;
            if (value.Length != digits + 1 || value[0] != '#') throw new ArgumentException("颜色格式应为 #RRGGBB。");
            foreach (char c in value.Substring(1))
            {
                if (!Uri.IsHexDigit(c)) throw new ArgumentException("颜色格式应为 #RRGGBB。");
            }
            return value;
        }
    }
}

