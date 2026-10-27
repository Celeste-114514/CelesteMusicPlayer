using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页外观：语义变量 + 预设库 + 个人覆盖。
    ///
    /// 为什么单独一套而不是复用旧 .celeste-theme：
    /// 旧主题文件管的是旧主窗口那 39 个原生令牌（CelesteTokenCatalog），和网页这一层消费的
    /// CSS 变量不是同一套消费者；硬绑在一起的结果是「改了这边没反应」。所以这里：
    ///   - 网页外观走独立存储 web-appearance.json（语义变量名的字典，前端直接消费）
    ///   - 旧 .celeste-theme 继续只管旧窗口，两边互不覆盖（UI 上也这么写清楚）
    /// 预设只存「基色 + 数值参数」，完整变量表由 <see cref="Derive"/> 按明暗各算一份，
    /// 保证每套预设都有配对的浅色/深色，不会出现某套夜里刺眼。
    /// </summary>
    internal static class WebAppearance
    {
        // —— 语义变量目录：网页 CSS 只认这些名字 ——
        internal static readonly string[] VarNames =
        {
            "bg", "shell", "surface", "panel", "panel-soft", "line",
            "ink", "heading", "muted", "on-muted",
            "accent", "accent-strong", "accent-soft", "on-accent",
            "row", "row-hover", "row-active", "chip", "focus",
            // 交互状态与浮层：悬停/按下/输入框/菜单/遮罩/投影。
            // 分开建变量而不是复用 panel-soft，是因为"选中"和"悬停"必须能分别调 ——
            // 两者共用一支值时，用户想加大选中对比，悬停也会跟着变重。
            "hover", "pressed", "input-bg", "input-line", "menu-bg", "overlay", "shadow",
            // 底栏底色不在目录里：它由 theme.css 直接引用 --shell，
            // 否则用户改了外壳色、底栏还留着旧值，看着就是"半截没变"
            "wave-idle", "wave-played",
            // 底栏的图标与文字单独一支：底栏底色跟外壳走，但图标色用户可能要更淡或更重
            "player-ink", "player-muted",
            // 半圆导航：轨道线色与普通项目色（悬停/选中仍走强调色 —— 那是状态，不该各存一份）
            "nav-track", "nav-ink",
            // 背景层的三色渐变（纯色模式不用它们）
            "bg-grad-1", "bg-grad-2", "bg-grad-3",
            "danger", "success", "warning"
        };

        internal static readonly string[] NumNames =
        {
            "radius", "radius-sm", "blur", "panel-opacity", "sat", "motion-speed", "motion-strength", "density",
            "player-opacity", "player-blur", "cover-radius", "cover-size",
            "player-gap", "player-pad", "player-height",
            // 排版的两个数值：字号整体缩放与行高。做成数值参数而不是开关，
            // 是因为它们和圆角/间距一样属于「预设自带 + 用户可覆盖」，跟着明暗各存一份。
            "text-scale", "line-height",
            // 禁用态透明度：各处的 .3/.5 太散，统一成一支
            "disabled-opacity",
            // 半圆导航几何：整体尺寸、图标大小、两圈半径（后两个是 SVG viewBox 单位，
            // 跟容器一起缩放，所以改整体尺寸不会让图标圈跑偏）
            "nav-size", "nav-icon", "nav-ring-1", "nav-ring-2",
            // 背景层：模糊 / 亮度 / 遮罩浓度
            "bg-blur", "bg-brightness", "bg-scrim",
            "material-style", "material-transparency", "material-opacity", "material-blur", "material-highlight", "material-shadow"
        };

        /// <summary>
        /// 字符串型的排版变量（字体族名）。单独一支，不混进颜色目录：
        /// 颜色选择器会按 hex 校验，字体名不是 hex，混进去只会互相干扰。
        /// </summary>
        internal static readonly string[] TextNames = { "font-ui", "font-cjk", "font-mono", "bg-mode", "bg-image", "bg-video", "bg-fit", "bg-pos" };

        internal const string DefaultFontUi = "Segoe UI";
        internal const string DefaultFontCjk = "Microsoft YaHei UI";
        internal const string DefaultFontMono = "Cascadia Mono";

        internal const string Light = "light";
        internal const string Dark = "dark";

        /// <summary>底栏一个可配置按钮。分组决定它落在播放键左侧、右侧，还是最右的工具区。</summary>
        internal sealed class PlayerButton
        {
            public string id { get; set; } = "";
            public string label { get; set; } = "";
            /// <summary>left / right / tools</summary>
            public string group { get; set; } = "left";
            /// <summary>核心能力，不允许隐藏（播放键本身不在目录里，它永远是正中间那个）</summary>
            public bool core { get; set; }
        }

        internal static readonly PlayerButton[] PlayerButtonCatalog =
        {
            new() { id = "favorite", label = "喜欢", group = "left" },
            new() { id = "playback-order", label = "播放顺序", group = "left" },
            new() { id = "previous", label = "上一首", group = "left", core = true },
            new() { id = "seek-back", label = "快退 5 秒", group = "left" },
            new() { id = "seek-forward", label = "快进 5 秒", group = "right" },
            new() { id = "next", label = "下一首", group = "right", core = true },
            new() { id = "queue", label = "播放队列", group = "right" },
            new() { id = "transport-more", label = "更多功能", group = "right" },
            new() { id = "lyrics", label = "桌面歌词", group = "tools" },
            new() { id = "volume", label = "音量", group = "tools" },
            new() { id = "audio-settings", label = "音频设置", group = "tools" }
        };

        /// <summary>默认构成。存成一段可读的字符串，用户改了也直接看得懂（加 ! 表示隐藏）。</summary>
        internal const string DefaultPlayerButtons =
            "left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings";

        /// <summary>构成串里的分组顺序（也决定存档格式的顺序，别依赖字典枚举顺序）。</summary>
        internal static readonly string[] PlayerGroups = { "left", "right", "tools" };

        /// <summary>
        /// 把用户存的构成收敛成合法值：去掉不认识的、去重、漏掉的补回默认组末尾、核心按钮强制显示。
        /// 放在宿主做，是为了网页那侧不用再写一套校验 —— 坏值存进文件后也不会让底栏少按键。
        /// </summary>
        internal static string NormalizePlayerButtons(string? raw)
        {
            string text = (raw ?? "").Trim();
            if (text.Length == 0) text = DefaultPlayerButtons;

            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["left"] = new(), ["right"] = new(), ["tools"] = new()
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string chunk in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int split = chunk.IndexOf(':');
                if (split <= 0) continue;
                string items = chunk.Substring(split + 1);
                foreach (string token in items.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = token.Trim();
                    bool hidden = value.StartsWith("!", StringComparison.Ordinal);
                    string id = hidden ? value.Substring(1) : value;
                    PlayerButton? def = PlayerButtonCatalog.FirstOrDefault(button => button.id == id);
                    if (def == null || !seen.Add(id)) continue;
                    groups[def.group].Add(def.core || !hidden ? id : "!" + id);
                }
            }

            foreach (PlayerButton def in PlayerButtonCatalog)
            {
                if (seen.Contains(def.id)) continue;
                groups[def.group].Add(def.id);
            }

            // 分组顺序写死 left|right|tools：字典枚举顺序不写进契约，将来加组也不想让存档格式跟着漂
            return string.Join("|", PlayerGroups.Select(group => group + ":" + string.Join(",", groups[group])));
        }

        /// <summary>半圆导航里的一个项目。group 决定它落在内圈还是外圈。</summary>
        internal sealed class NavItem
        {
            public string id { get; set; } = "";
            public string label { get; set; } = "";
            /// <summary>ring1（内圈）/ ring2（外圈）</summary>
            public string group { get; set; } = "ring1";
        }

        internal static readonly NavItem[] NavItemCatalog =
        {
            new() { id = "songs", label = "歌曲", group = "ring1" },
            new() { id = "albums", label = "专辑", group = "ring1" },
            new() { id = "artists", label = "艺术家", group = "ring1" },
            new() { id = "albumArtists", label = "专辑艺人", group = "ring1" },
            new() { id = "folders", label = "文件夹", group = "ring1" },
            new() { id = "favorites", label = "喜欢的音乐", group = "ring2" },
            new() { id = "ratings", label = "评分", group = "ring2" },
            new() { id = "mostPlayed", label = "播放最多", group = "ring2" },
            new() { id = "playlists", label = "播放列表", group = "ring2" },
            new() { id = "queue", label = "播放队列", group = "ring2" },
            new() { id = "recent", label = "最近播放", group = "ring2" }
        };

        internal const string DefaultNavItems =
            "ring1:songs,albums,artists,albumArtists,folders|ring2:favorites,ratings,mostPlayed,playlists,queue,recent";

        internal static readonly string[] NavGroups = { "ring1", "ring2" };

        /// <summary>
        /// 导航构成收敛：去重、丢弃不认识的、漏掉的补回组末尾，**每圈至少留一个可见项**。
        /// 最后这条是硬约束：整圈都收起来的话，JS 算角度要除以 (n-1)，会直接算出 NaN，
        /// 项目会全叠在左上角 —— 与其让用户踩，不如宿主先把至少一个顶回来。
        /// </summary>
        internal static string NormalizeNavItems(string? raw)
        {
            string text = (raw ?? "").Trim();
            if (text.Length == 0) text = DefaultNavItems;

            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["ring1"] = new(), ["ring2"] = new()
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string chunk in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int split = chunk.IndexOf(':');
                if (split <= 0) continue;
                foreach (string token in chunk.Substring(split + 1).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = token.Trim();
                    bool hidden = value.StartsWith("!", StringComparison.Ordinal);
                    string id = hidden ? value.Substring(1) : value;
                    NavItem? def = NavItemCatalog.FirstOrDefault(item => item.id == id);
                    if (def == null || !seen.Add(id)) continue;
                    groups[def.group].Add(hidden ? "!" + id : id);
                }
            }

            foreach (NavItem def in NavItemCatalog)
            {
                if (seen.Contains(def.id)) continue;
                groups[def.group].Add(def.id);
            }

            foreach (string group in NavGroups)
            {
                if (groups[group].Any(entry => !entry.StartsWith("!", StringComparison.Ordinal))) continue;
                // 全被收起来了：把该圈目录里的第一个顶回来（先删掉它原来的隐藏项，别留重复）
                string? first = NavItemCatalog.FirstOrDefault(item => item.group == group)?.id;
                if (first != null)
                {
                    groups[group].Remove("!" + first);
                    groups[group].Insert(0, first);
                }
            }

            return string.Join("|", NavGroups.Select(group => group + ":" + string.Join(",", groups[group])));
        }

        internal sealed class Preset
        {            public string id { get; set; } = "";
            public string name { get; set; } = "";
            public string group { get; set; } = "";
            /// <summary>浅色底</summary>
            public string lightBg { get; set; } = "";
            /// <summary>深色底</summary>
            public string darkBg { get; set; } = "";
            /// <summary>强调色（深浅共用色相）</summary>
            public string accent { get; set; } = "";
            public double radius { get; set; } = 26;
            public double blur { get; set; } = 25;
            public double sat { get; set; } = 1;
            public double motionSpeed { get; set; } = 1;
            public double motionStrength { get; set; } = 1;
            /// <summary>compact / normal / relaxed</summary>
            public string density { get; set; } = "normal";
            /// <summary>字号整体缩放（1 = 设计原尺寸）</summary>
            public double textScale { get; set; } = 1;
            /// <summary>基准行高（无单位，1.5 = 字号的 1.5 倍）</summary>
            public double lineHeight { get; set; } = 1.5;
            /// <summary>个人预设，可删除</summary>
            public bool custom { get; set; }
            public int materialStyle { get; set; }
        }

        internal sealed class State
        {
            /// <summary>light / dark / system</summary>
            public string mode { get; set; } = Light;
            /// <summary>theme（跟随预设）/ system（跟随系统）/ custom（自定义）</summary>
            public string accentSource { get; set; } = "theme";
            public string accentCustom { get; set; } = "#e84865";
            public string presetId { get; set; } = "sakura";
            /// <summary>在当前预设之上改过的变量（键是 "light:accent" 这种带明暗前缀的写法）</summary>
            public Dictionary<string, string> overrides { get; set; } = new(StringComparer.Ordinal);
            public List<Preset> customPresets { get; set; } = new();
            /// <summary>底栏按钮的显隐与顺序（个人布局偏好，不随预设切换）</summary>
            public string playerButtons { get; set; } = DefaultPlayerButtons;
            /// <summary>半圆导航的项目分组与顺序（个人布局偏好，不随预设切换）</summary>
            public string navItems { get; set; } = DefaultNavItems;
            /// <summary>背景模式：solid / gradient / cover / image / video</summary>
            public string backgroundMode { get; set; } = "solid";
            /// <summary>背景图片地址（由宿主拷进受控目录后给的虚拟主机地址，不是用户原始路径）</summary>
            public string backgroundImage { get; set; } = "";
            public string backgroundVideo { get; set; } = "";
            /// <summary>cover / contain</summary>
            public string backgroundFit { get; set; } = "cover";
            /// <summary>center / top / bottom</summary>
            public string backgroundPos { get; set; } = "center";
            /// <summary>字体族（个人偏好，不随预设切换 —— 字体是这台机器上装了什么的问题，不是配色方案的一部分）</summary>
            public string fontUi { get; set; } = DefaultFontUi;
            public string fontCjk { get; set; } = DefaultFontCjk;
            public string fontMono { get; set; } = DefaultFontMono;
            /// <summary>动效模式：system（跟随系统）/ full（完整）/ lite（轻量）/ off（关闭）</summary>
            public string motionMode { get; set; } = "system";
            /// <summary>动画速度：越大越快（1 = 基准）。null = 沿用预设自带的性格</summary>
            public double? motionSpeed { get; set; }
            /// <summary>位移/回弹幅度：0 = 只保留颜色与状态变化，不做几何运动。null = 沿用预设</summary>
            public double? motionStrength { get; set; }
            /// <summary>回弹风格：calm（克制）/ soft（柔和）/ lively（灵动）</summary>
            public string motionBounce { get; set; } = "soft";
            /// <summary>被关掉的动效类别（逗号分隔）。空 = 全开。</summary>
            public string motionOff { get; set; } = "";
            public int revision { get; set; }
        }

        // —— 预设库：29 套（含两套系统风格、三套极客）。每套只给基色与数值，其余由 Derive 派生 ——
        private static Preset P(string id, string name, string group, string accent, string lightBg, string darkBg,
                                double radius = 26, double blur = 25, double sat = 1, double motionSpeed = 1, double motionStrength = 1, string density = "normal",
                                double textScale = 1, double lineHeight = 1.5) =>
            new() { id = id, name = name, group = group, accent = accent, lightBg = lightBg, darkBg = darkBg,
                    radius = radius, blur = blur, sat = sat, motionSpeed = motionSpeed, motionStrength = motionStrength, density = density,
                    textScale = textScale, lineHeight = lineHeight };

        internal static readonly Preset[] BuiltIn =
        {
            // 清透柔和：大圆角、半透明、动效柔和
            P("sakura", "樱雾", "清透柔和", "#e84865", "#fbf7f8", "#1b1719", 26, 25, 1.0, 1.0, 0.9),
            P("cream", "奶油白", "清透柔和", "#d98b3f", "#fdf8f1", "#1e1a16", 24, 18, 0.95, 0.95, 0.85),
            P("peach", "蜜桃苏打", "清透柔和", "#ef6f6c", "#fff6f4", "#201917", 28, 22, 1.05, 1.05, 1.0),
            P("lavender", "薰衣草", "清透柔和", "#8b72d1", "#f8f6fd", "#1a1824", 26, 24, 1.0, 0.95, 0.9),
            // 录音室：低饱和、紧凑、动效克制
            P("graphite", "石墨", "录音室", "#6f7d8c", "#f4f5f6", "#15171a", 14, 10, 0.7, 0.8, 0.6, "compact", 0.94, 1.35),
            P("navy", "海军蓝", "录音室", "#3f6fb5", "#f2f5f9", "#121722", 14, 12, 0.8, 0.8, 0.6, "compact", 0.94, 1.35),
            P("coolgrey", "冷灰", "录音室", "#7c8a90", "#f5f6f7", "#161819", 12, 8, 0.65, 0.75, 0.55, "compact", 0.92, 1.3),
            P("titanium", "钛银", "录音室", "#8e9aa3", "#f7f8f9", "#191b1d", 16, 14, 0.75, 0.85, 0.7, "normal", 0.96, 1.4),
            // 自然
            P("mint", "薄荷", "自然", "#3f9e86", "#f3faf7", "#121e1b", 22, 18, 0.95, 1.0, 0.85),
            P("bamboo", "竹青", "自然", "#6f9a4a", "#f7faf2", "#171d14", 20, 16, 0.9, 0.95, 0.8),
            P("seasalt", "海盐", "自然", "#4c8fa8", "#f2f8fb", "#111e23", 24, 20, 0.95, 1.0, 0.9),
            P("autumn", "秋木", "自然", "#b3733f", "#fbf5ee", "#1f1813", 18, 14, 1.0, 0.9, 0.75),
            // 复古
            P("amber-crt", "琥珀终端", "复古", "#e0a33c", "#f8f1e2", "#1b1608", 8, 4, 1.15, 0.7, 0.5, "compact", 1.02, 1.7),
            P("green-crt", "绿磷终端", "复古", "#6fdc8c", "#f1f7f1", "#0d1710", 8, 4, 1.2, 0.7, 0.5, "compact", 1.02, 1.7),
            P("paperink", "纸墨", "复古", "#b4472f", "#faf6ef", "#1c1917", 10, 6, 0.85, 0.8, 0.6, "normal", 1.06, 1.8),
            P("warmfilm", "暖胶片", "复古", "#d97b5a", "#fdf3ec", "#1e1714", 16, 20, 1.05, 0.9, 0.8, "normal", 1.04, 1.7),
            // 多彩
            P("neon", "霓虹", "多彩", "#c840ff", "#faf4ff", "#170f22", 22, 26, 1.25, 1.2, 1.2),
            P("berry", "莓果", "多彩", "#d63a6a", "#fdf3f6", "#1d1119", 24, 22, 1.15, 1.1, 1.1),
            P("candy", "彩虹糖", "多彩", "#4aa3f0", "#f6f9fd", "#121a26", 26, 24, 1.2, 1.15, 1.15),
            P("indigo", "靛紫", "多彩", "#5b53c9", "#f6f6fd", "#141327", 20, 20, 1.1, 1.0, 1.0),
            // 专注与清晰
            P("pure-white", "纯净白", "专注与清晰", "#2f6fd0", "#ffffff", "#1a1a1a", 12, 6, 1.0, 0.85, 0.7),
            P("pure-black", "纯净黑", "专注与清晰", "#7aa7ff", "#e9e9e9", "#000000", 10, 4, 1.0, 0.8, 0.65),
            P("clear-light", "清晰浅色", "专注与清晰", "#1f7a6b", "#f7fbfb", "#132020", 14, 8, 0.9, 0.85, 0.7, "relaxed"),
            P("clear-dark", "清晰深色", "专注与清晰", "#6fd3bf", "#eef2f2", "#0e1414", 14, 10, 0.95, 0.85, 0.7, "relaxed"),
            // 平台风格：使用本机已有字体；浅色/深色成对，几何和动效一并切换。
            P("windows-fluent", "Windows · Fluent", "系统风格", "#0067c0", "#f3f3f3", "#202020", 8, 24, 0.95, 0.85, 0.55, "normal", 1, 1.45),
            P("apple-silver", "Apple · 银白", "系统风格", "#007aff", "#f5f5f7", "#1c1c1e", 20, 32, 0.9, 1.05, 0.75, "relaxed", 1, 1.55),
            // 极客：近黑底 + 单一磷光强调色，小圆角、几乎不模糊、几乎没有动效位移，
            // 走派生规则得到冷调中灰边框与文字（制度上仍是「只给基色，其余派生」，不逐值硬写）。
            // ⚠ 这一步只做配色层：等宽 numerals、顶栏 OUT/FMT/FLAG/BUF 之类信息架构是另一件事，见方案第 5 节，尚未动。
            P("geek-amber", "极客 · 琥珀", "极客", "#ffbe28", "#f2f4f7", "#0b0e12", 6, 2, 1.15, 0.7, 0.35, "compact", 0.98, 1.45),
            P("geek-green", "极客 · 磷绿", "极客", "#57e389", "#eff3f0", "#080d0a", 6, 2, 1.15, 0.7, 0.35, "compact", 0.98, 1.45),
            P("geek-cyan", "极客 · 磷青", "极客", "#4fd6e0", "#eef4f6", "#070e10", 6, 2, 1.15, 0.7, 0.35, "compact", 0.98, 1.45),
        };

        // —— 颜色工具：预设只存基色，其余按 HSL 派生，明暗各一套 ——
        private static (double H, double S, double L) ToHsl(string hex)
        {
            (int r, int g, int b) = ParseHex(hex);
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
            double l = (max + min) / 2;
            double d = max - min;
            if (d < 0.0001) return (0, 0, l);
            double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            double h;
            if (Math.Abs(max - rf) < 0.0001) h = ((gf - bf) / d + (gf < bf ? 6 : 0));
            else if (Math.Abs(max - gf) < 0.0001) h = (bf - rf) / d + 2;
            else h = (rf - gf) / d + 4;
            return (h * 60, s, l);
        }

        private static string FromHsl(double h, double s, double l)
        {
            s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
            if (s < 0.0001)
            {
                int grey = (int)Math.Round(l * 255);
                return ToHex(grey, grey, grey);
            }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            return ToHex(Hue(p, q, h / 360 + 1.0 / 3), Hue(p, q, h / 360), Hue(p, q, h / 360 - 1.0 / 3));
        }

        private static int Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            double value = t < 1.0 / 6 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
            return (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
        }

        private static (int R, int G, int B) ParseHex(string hex)
        {
            string value = (hex ?? "").Trim().TrimStart('#');
            if (value.Length == 8) value = value.Substring(2);           // 万一是 #AARRGGBB
            if (value.Length == 3) value = string.Concat(value.Select(c => new string(c, 2)));
            if (value.Length != 6 || !value.All(Uri.IsHexDigit)) return (128, 128, 128);
            return (Convert.ToInt32(value.Substring(0, 2), 16), Convert.ToInt32(value.Substring(2, 2), 16), Convert.ToInt32(value.Substring(4, 2), 16));
        }

        private static string ToHex(int r, int g, int b) => "#" + r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture) + b.ToString("X2", CultureInfo.InvariantCulture);

        private static string Mix(string a, string b, double ratio)
        {
            (int r1, int g1, int b1) = ParseHex(a);
            (int r2, int g2, int b2) = ParseHex(b);
            ratio = Math.Clamp(ratio, 0, 1);
            return ToHex((int)Math.Round(r1 + (r2 - r1) * ratio), (int)Math.Round(g1 + (g2 - g1) * ratio), (int)Math.Round(b1 + (b2 - b1) * ratio));
        }

        /// <summary>感知亮度，用来决定强调色上该压白字还是深字。</summary>
        private static double Luma(string hex)
        {
            (int r, int g, int b) = ParseHex(hex);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }

        /// <summary>由基色派生一整套语义变量。深浅两套走同一套规则，方向相反。</summary>
        internal static Dictionary<string, string> Derive(Preset preset, string mode)
        {
            bool dark = mode == Dark;
            string baseBg = dark ? preset.darkBg : preset.lightBg;
            (double h, double s, double l) = ToHsl(baseBg);
            double sat = Math.Clamp(s * preset.sat, 0, 1);

            string accent = preset.accent;
            (double ah, double @as, double al) = ToHsl(accent);
            // 深色下把强调色提亮、浅色下压暗，避免两种模式下都撞上不可读的组合
            double accentL = dark ? Math.Clamp(Math.Max(al, 0.58), 0, 0.78) : Math.Clamp(Math.Min(al, 0.55), 0.18, 0.55);

            string bg = FromHsl(h, sat, dark ? l : l);
            string shell = FromHsl(h, sat, dark ? l + 0.045 : Math.Min(0.985, l + 0.03));
            string surface = dark ? FromHsl(h, sat, l + 0.075) : "#ffffff";
            string panel = dark ? FromHsl(h, sat, l + 0.055) : FromHsl(h, sat * 0.8, l - 0.022);
            string panelSoft = dark ? FromHsl(h, sat, l + 0.09) : FromHsl(h, sat * 0.7, l - 0.045);
            string line = dark ? FromHsl(h, sat * 0.6, l + 0.12) : FromHsl(h, sat * 0.5, l - 0.075);
            string ink = dark ? FromHsl(h, Math.Min(0.12, sat), 0.93) : FromHsl(h, Math.Min(0.18, sat * 1.6), 0.16);
            string heading = dark ? FromHsl(h, Math.Min(0.14, sat), 0.97) : FromHsl(h, Math.Min(0.2, sat * 1.6), 0.11);
            string muted = dark ? FromHsl(h, Math.Min(0.1, sat), 0.7) : FromHsl(h, Math.Min(0.1, sat), 0.58);
            string accentValue = FromHsl(ah, Math.Clamp(@as * preset.sat, 0.15, 1), accentL);
            string accentStrong = dark ? FromHsl(ah, Math.Clamp(@as, 0.2, 1), Math.Min(0.85, accentL + 0.12)) : FromHsl(ah, Math.Clamp(@as, 0.2, 1), Math.Max(0.14, accentL - 0.12));
            string accentSoft = Mix(dark ? bg : surface, accentValue, dark ? 0.22 : 0.14);
            string onAccent = Luma(accentValue) > 0.6 ? "#1a1a1a" : "#ffffff";
            string row = surface;
            string rowHover = Mix(surface, accentValue, dark ? 0.16 : 0.07);
            string rowActive = Mix(surface, accentValue, dark ? 0.26 : 0.12);
            string chip = dark ? panelSoft : FromHsl(h, sat * 0.6, l - 0.03);
            string focus = accentValue;

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["bg"] = bg, ["shell"] = shell, ["surface"] = surface, ["panel"] = panel, ["panel-soft"] = panelSoft, ["line"] = line,
                ["ink"] = ink, ["heading"] = heading, ["muted"] = muted, ["on-muted"] = dark ? FromHsl(h, sat * 0.5, 0.8) : FromHsl(h, sat * 0.4, 0.45),
                ["accent"] = accentValue, ["accent-strong"] = accentStrong, ["accent-soft"] = accentSoft, ["on-accent"] = onAccent,
                ["row"] = row, ["row-hover"] = rowHover, ["row-active"] = rowActive, ["chip"] = chip, ["focus"] = focus,
                ["wave-idle"] = Mix(dark ? surface : "#ffffff", muted, dark ? 0.5 : 0.28),
                ["wave-played"] = accentValue,
                // 底栏图标比正文再重一档（底栏底色略透，按正文色会发灰），次要文字沿用 muted
                ["player-ink"] = dark ? FromHsl(h, Math.Min(0.14, sat), 0.95) : FromHsl(h, Math.Min(0.2, sat * 1.6), 0.18),
                ["player-muted"] = muted,
                ["nav-track"] = dark ? Mix(bg, line, 0.55) : Mix(surface, line, 0.85),
                ["nav-ink"] = muted,
                // 背景层渐变：从底色往强调色/面板色各偏一点，三色叠起来才不是一块死色
                ["bg-grad-1"] = dark ? FromHsl(h, sat * 0.9, Math.Min(0.99, l + 0.06)) : FromHsl(h, sat * 0.55, Math.Min(0.995, l + 0.04)),
                ["bg-grad-2"] = Mix(bg, accentValue, 0.16),
                ["bg-grad-3"] = dark ? FromHsl(h, Math.Max(0.04, sat * 0.7), Math.Max(0.03, l - 0.03)) : Mix(surface, accentValue, 0.06),
                ["danger"] = dark ? "#ff8a80" : "#c0454f", ["success"] = dark ? "#6fd3a8" : "#3f8f6f", ["warning"] = dark ? "#e8b962" : "#b8802a",
                // 字体族默认值。真正生效的值由 Resolve 用人选的字体覆盖，
                // 这里给出兜底，保证没接主题（或用户没选过）时不至于 font-family 为空
                ["font-ui"] = QuoteFamily(DefaultFontUi), ["font-cjk"] = QuoteFamily(DefaultFontCjk), ["font-mono"] = QuoteFamily(DefaultFontMono),
                // 交互状态：悬停比选中浅一档（选中是"已在此"，应该更实）；
                // 输入框底色在深色下比页面亮一点才看得出是个框；遮罩与投影自带透明度
                ["hover"] = rowHover,
                ["pressed"] = rowActive,
                ["input-bg"] = dark ? FromHsl(h, sat, l + 0.1) : "#ffffff",
                ["input-line"] = line,
                ["menu-bg"] = dark ? FromHsl(h, sat, l + 0.085) : "#ffffff",
                ["overlay"] = dark ? "#000000A6" : "#30252d4d",
                ["shadow"] = dark ? "#0000008C" : "#43343126"
            };
        }

        internal static Dictionary<string, string> Numbers(Preset preset)
        {
            double radius = preset.radius;
            string density = preset.density;
            double densityScale = density switch { "compact" => 0.82, "relaxed" => 1.18, _ => 1 };
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["radius"] = radius.ToString("0.##", CultureInfo.InvariantCulture) + "px",
                ["radius-sm"] = Math.Max(6, radius * 0.42).ToString("0.##", CultureInfo.InvariantCulture) + "px",
                ["blur"] = preset.blur.ToString("0.##", CultureInfo.InvariantCulture) + "px",
                ["panel-opacity"] = "1",
                ["sat"] = preset.sat.ToString("0.##", CultureInfo.InvariantCulture),
                // 动效速度：这里换过一次语义。
                // 预设里存的是旧的"时长倍率"（大 = 慢），网页/动效控制器要的是"速度"（大 = 快），
                // 所以这里取倒数。用户自己在外观页调的值会盖掉这个基准。
                ["motion-speed"] = (1.0 / Math.Max(0.05, preset.motionSpeed)).ToString("0.##", CultureInfo.InvariantCulture),
                ["motion-strength"] = preset.motionStrength.ToString("0.##", CultureInfo.InvariantCulture),
                ["density"] = densityScale.ToString("0.##", CultureInfo.InvariantCulture),
                ["player-opacity"] = "0.88",   // 与原设计一致：底栏略透，不是全实心
                ["player-blur"] = "0px",       // 默认不模糊：每帧重绘太费，需要时在设置里开
                ["cover-radius"] = Math.Max(0, radius * 0.46).ToString("0.##", CultureInfo.InvariantCulture) + "px",
                ["cover-size"] = "62px",
                // 底栏的三围：左右留白、中间空隙、整体高度。原来写死在 transport.css 里，
                // 用户要"底栏更紧凑一点"只能改代码，所以提成参数。
                ["player-gap"] = "24px",
                ["player-pad"] = "16px",
                ["player-height"] = "120px",
                // 排版：预设自带一套（录音室更紧、复古更松），用户可以再覆盖
                ["text-scale"] = preset.textScale.ToString("0.##", CultureInfo.InvariantCulture),
                ["line-height"] = preset.lineHeight.ToString("0.##", CultureInfo.InvariantCulture),
                ["disabled-opacity"] = "0.35",
                // 导航几何：300×145 的轮盘、两圈半径 150/238（原来写死在 shell.js 与 navigation.css 里）
                ["nav-size"] = "300px",
                ["nav-icon"] = "28px",
                ["nav-ring-1"] = "150",
                ["nav-ring-2"] = "238",
                // 背景层：默认不模糊、亮度 1、遮罩 55%（图片上直接放文字会看不清）
                ["material-style"] = (preset.id == "windows-fluent" ? 1 : preset.id == "apple-silver" ? 2 : Math.Clamp(preset.materialStyle, 0, 2)).ToString(CultureInfo.InvariantCulture),
                ["material-transparency"] = "1", ["material-opacity"] = "0.72", ["material-blur"] = "28px",
                ["material-highlight"] = "0.36", ["material-shadow"] = "0.16",
                ["bg-blur"] = "0px",
                ["bg-brightness"] = "1",
                ["bg-scrim"] = "0.55"
            };
            if (preset.id == "windows-fluent" || preset.id == "apple-silver" || preset.materialStyle > 0)
            {
                bool apple = preset.id == "apple-silver" || preset.materialStyle == 2;
                values["player-opacity"] = apple ? "0.76" : "0.82";
                values["material-opacity"] = apple ? "0.74" : "0.62";
                values["material-blur"] = apple ? "36px" : "28px";
                values["material-highlight"] = apple ? "0.52" : "0.3";
                values["material-shadow"] = apple ? "0.12" : "0.16";
                values["player-blur"] = apple ? "24px" : "16px";
                values["cover-radius"] = apple ? "10px" : "4px";
                values["player-height"] = apple ? "124px" : "112px";
            }
            return values;
        }

        // —— 存储 ——
        private static readonly JsonSerializerOptions Store = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string FilePath => Path.Combine(AppSettingsStore.GetConfigDirectory(), "web-appearance.json");

        private static State? _cache;
        private static readonly object Gate = new();

        internal static State Load()
        {
            lock (Gate)
            {
                if (_cache != null) return Clone(_cache);
                try
                {
                    string path = FilePath;
                    if (File.Exists(path))
                    {
                        State? parsed = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Store);
                        if (parsed != null) { Normalize(parsed); _cache = parsed; return Clone(parsed); }
                    }
                }
                catch (Exception caught) { StartupLog.WriteException("WebAppearance.Load", caught); }
                _cache = new State();
                return Clone(_cache);
            }
        }

        internal static void Save(State state)
        {
            lock (Gate)
            {
                Normalize(state);
                state.revision++;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Store));
                _cache = Clone(state);
                Changed?.Invoke(state.revision);
            }
        }

        /// <summary>主题快照修订号变化时通知网页，避免每秒全量拉取。</summary>
        internal static event Action<int>? Changed;

        internal static int CurrentRevision => Load().revision;

        private static void Normalize(State state)
        {
            if (state.mode != Light && state.mode != Dark && state.mode != "system") state.mode = Light;
            if (state.accentSource != "theme" && state.accentSource != "system" && state.accentSource != "custom") state.accentSource = "theme";
            if (state.accentCustom.Length > 0 && !IsHex(state.accentCustom)) state.accentCustom = "#e84865";
            state.overrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            state.customPresets ??= new List<Preset>();
            state.playerButtons = NormalizePlayerButtons(state.playerButtons);
            state.navItems = NormalizeNavItems(state.navItems);
            // 背景模式只认这四个；旧存档没这几项就回到"纯色"
            if (state.backgroundMode != "gradient" && state.backgroundMode != "cover" && state.backgroundMode != "image" && state.backgroundMode != "video") state.backgroundMode = "solid";
            if (state.backgroundFit != "contain") state.backgroundFit = "cover";
            if (state.backgroundPos != "top" && state.backgroundPos != "bottom") state.backgroundPos = "center";
            state.backgroundImage ??= "";
            state.backgroundVideo ??= "";
            if (!IsBackgroundUrl(state.backgroundImage)) state.backgroundImage = "";
            if (!IsBackgroundUrl(state.backgroundVideo)) state.backgroundVideo = "";
            // 选了"图片"但没有图片地址 = 等于没选，回落纯色，免得界面上"选着图片却什么都没有"
            if (state.backgroundMode == "image" && state.backgroundImage.Length == 0) state.backgroundMode = "solid";
            if (state.backgroundMode == "video" && state.backgroundVideo.Length == 0) state.backgroundMode = "solid";
            // 字体名留空（旧存档没这两项）就回到默认，别让 font-family 变成空值
            if (string.IsNullOrWhiteSpace(state.fontUi)) state.fontUi = DefaultFontUi;
            if (string.IsNullOrWhiteSpace(state.fontCjk)) state.fontCjk = DefaultFontCjk;
            if (string.IsNullOrWhiteSpace(state.fontMono)) state.fontMono = DefaultFontMono;
            // 动效偏好单独保存，不随外观预设切换 —— 换配色不该把用户关掉的动画又打开
            if (state.motionMode != "system" && state.motionMode != "full" && state.motionMode != "lite" && state.motionMode != "off")
                state.motionMode = "system";
            if (state.motionBounce != "calm" && state.motionBounce != "soft" && state.motionBounce != "lively")
                state.motionBounce = "soft";
            if (state.motionSpeed.HasValue) state.motionSpeed = Math.Clamp(state.motionSpeed.Value, 0.6, 1.6);
            if (state.motionStrength.HasValue) state.motionStrength = Math.Clamp(state.motionStrength.Value, 0, 1.25);
            state.motionOff = NormalizeMotionOff(state.motionOff);
            if (FindPreset(state.presetId, state) == null) state.presetId = BuiltIn[0].id;
        }

        private static bool IsHex(string value)
        {
            string text = value.Trim().TrimStart('#');
            return (text.Length == 6 || text.Length == 8) && text.All(Uri.IsHexDigit);
        }

        /// <summary>
        /// 字体族名包一层引号。名字里带空格（Microsoft YaHei UI）或不常见字符时，
        /// 不加引号的 font-family 会整条失效 —— 浏览器直接忽略这一项，看着就是"选了没反应"。
        /// </summary>
        internal static string QuoteFamily(string name)
            => "\"" + (name ?? "").Replace("\"", "").Trim() + "\"";

        internal static State Clone(State state)
        {
            var copy = new State
            {
                mode = state.mode, accentSource = state.accentSource, accentCustom = state.accentCustom,
                presetId = state.presetId, revision = state.revision,
                playerButtons = state.playerButtons,
                navItems = state.navItems,
                backgroundMode = state.backgroundMode, backgroundImage = state.backgroundImage, backgroundVideo = state.backgroundVideo,
                backgroundFit = state.backgroundFit, backgroundPos = state.backgroundPos,
                fontUi = state.fontUi, fontCjk = state.fontCjk, fontMono = state.fontMono,
                // ⚠ 动效偏好也要逐字段拷贝：漏一个就是「存得进文件、Load() 恒默认」
                motionMode = state.motionMode, motionSpeed = state.motionSpeed,
                motionStrength = state.motionStrength, motionBounce = state.motionBounce,
                motionOff = state.motionOff,
                overrides = new Dictionary<string, string>(state.overrides ?? new(), StringComparer.Ordinal),
                customPresets = (state.customPresets ?? new()).Select(p => new Preset
                {
                    id = p.id, name = p.name, group = p.group, lightBg = p.lightBg, darkBg = p.darkBg, accent = p.accent,
                    radius = p.radius, blur = p.blur, sat = p.sat, motionSpeed = p.motionSpeed,
                    motionStrength = p.motionStrength, density = p.density,
                    // ⚠ 逐字段拷贝：漏一个就会出现「存得进文件、Load() 恒默认」
                    textScale = p.textScale, lineHeight = p.lineHeight, materialStyle = p.materialStyle,
                    custom = p.custom
                }).ToList()
            };
            return copy;
        }

        internal static Preset? FindPreset(string id, State state)
            => BuiltIn.Concat(state.customPresets ?? new List<Preset>()).FirstOrDefault(p => p.id == id);

        // —— 预览草稿：改参数先落在这里，点保存才写文件；关页面/取消就整体丢掉 ——
        private static State? _draft;
        private static readonly object DraftGate = new();

        internal static State Draft()
        {
            lock (DraftGate) { _draft ??= Load(); return Clone(_draft); }
        }

        internal static void SetDraft(State state)
        {
            Normalize(state);
            bool unchanged = JsonSerializer.Serialize(EditState(state)) == JsonSerializer.Serialize(EditState(Load()));
            lock (DraftGate) { _draft = unchanged ? null : Clone(state); }
        }

        // 历史保存原始覆盖表，而非派生 CSS。恢复时可以真正移除新增覆盖。
        internal static State EditState(State state)
        {
            State copy = Clone(state);
            copy.revision = 0;
            copy.customPresets.Clear();
            return copy;
        }

        internal static bool IsBackgroundUrl(string? value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != "https" || uri.Host != "celeste-asset.internal"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            string name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return name.Length > 0 && name != "." && name != ".."
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        internal static void ClearDraft()
        {
            lock (DraftGate) { _draft = null; }
        }

        internal static bool HasDraft()
        {
            lock (DraftGate) { return _draft != null; }
        }

        /// <summary>「跟随系统」读的是系统应用色（WinRT 的 UISettings），不是某个窗口当前的主题，
        /// 也不拿 Application.RequestedTheme 当全局状态 —— 混合主题时那个值不可靠。</summary>
        internal static string ResolveMode(string mode)
        {
            if (mode != "system") return mode == Dark ? Dark : Light;
            try
            {
                var settings = new Windows.UI.ViewManagement.UISettings();
                Windows.UI.Color background = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                double luma = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
                return luma < 0.5 ? Dark : Light;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("WebAppearance.ResolveMode", caught);
                return Light;
            }
        }

        // —— 解析：预设 → 覆盖 → 强调色策略 ——
        /// <summary>当前生效的动画速度（越大越快）。用户没自己定过，就用预设自带的性格。</summary>
        internal static double EffectiveMotionSpeed(State state)
        {
            Preset? preset = FindPreset(state.presetId, state) ?? BuiltIn[0];
            double baseSpeed = 1.0 / Math.Max(0.05, preset.motionSpeed);
            return Math.Clamp(state.motionSpeed ?? baseSpeed, 0.6, 1.6);
        }

        /// <summary>当前生效的位移幅度。0 = 只保留颜色与状态变化，不做几何运动。</summary>
        internal static double EffectiveMotionStrength(State state)
        {
            Preset? preset = FindPreset(state.presetId, state) ?? BuiltIn[0];
            return Math.Clamp(state.motionStrength ?? preset.motionStrength, 0, 1.25);
        }

        /// <summary>可单独关掉的动效类别。分类是为了让用户能"少动一点"，而不是给几十个参数。</summary>
        internal static readonly string[] MotionCategories = { "nav", "popup", "list", "control", "theme", "cover" };

        internal static readonly Dictionary<string, string> MotionCategoryLabels = new(StringComparer.Ordinal)
        {
            ["nav"] = "导航与页面转场",
            ["popup"] = "弹窗与浮层",
            ["list"] = "列表重排",
            ["control"] = "控件反馈",
            ["theme"] = "主题与背景",
            ["cover"] = "封面转场"
        };

        /// <summary>只保留认识的类别，去重并按目录顺序排列。</summary>
        internal static string NormalizeMotionOff(string? raw)
        {
            var wanted = new HashSet<string>(
                (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            return string.Join(",", MotionCategories.Where(item => wanted.Contains(item)));
        }

        /// <summary>当前被关掉的类别清单。</summary>
        internal static string[] MotionOffList(State state)
            => state.motionOff.Length == 0
                ? Array.Empty<string>()
                : state.motionOff.Split(',', StringSplitOptions.RemoveEmptyEntries);

        internal static Dictionary<string, string> Resolve(State state, string mode)
        {
            Preset? preset = FindPreset(state.presetId, state) ?? BuiltIn[0];
            var values = Derive(preset, mode);
            var numbers = Numbers(preset);

            // 字体是「这台机器装了什么」决定的个人偏好，明暗共用一套，所以先于覆盖值铺上底
            if (!string.IsNullOrWhiteSpace(state.fontUi)) values["font-ui"] = QuoteFamily(state.fontUi);
            if (!string.IsNullOrWhiteSpace(state.fontCjk)) values["font-cjk"] = QuoteFamily(state.fontCjk);
            if (!string.IsNullOrWhiteSpace(state.fontMono)) values["font-mono"] = QuoteFamily(state.fontMono);

            // 背景层。bg-image 为空时必须显式给 none：applyCss 只覆盖不清理，
            // 省略不写会让上一张图的地址一直挂在根元素上 —— 用户"关掉背景"却还看得见图。
            values["bg-mode"] = string.IsNullOrWhiteSpace(state.backgroundMode) ? "solid" : state.backgroundMode;
            values["bg-image"] = string.IsNullOrWhiteSpace(state.backgroundImage)
                ? "none"
                : "url(\"" + state.backgroundImage + "\")";
            values["bg-video"] = string.IsNullOrEmpty(state.backgroundVideo) ? "none" : state.backgroundVideo;
            values["bg-fit"] = string.IsNullOrWhiteSpace(state.backgroundFit) ? "cover" : state.backgroundFit;
            values["bg-pos"] = string.IsNullOrWhiteSpace(state.backgroundPos) ? "center" : state.backgroundPos;

            foreach (KeyValuePair<string, string> pair in state.overrides ?? new())
            {
                int split = pair.Key.IndexOf(':');
                if (split <= 0) continue;
                if (pair.Key.Substring(0, split) != mode) continue;
                string name = pair.Key.Substring(split + 1);
                if (VarNames.Contains(name)) values[name] = pair.Value;
                else if (TextNames.Contains(name)) values[name] = pair.Value;
                else if (NumNames.Contains(name)) numbers[name] = pair.Value;
            }

            // 动效偏好：用户自己定过的值盖过预设自带的速度与幅度，也盖过旧版留下的覆盖值。
            // 放在覆盖之后是有意的 —— 换外观预设不该把用户关掉的动画又打开。
            if (state.motionSpeed.HasValue) numbers["motion-speed"] = state.motionSpeed.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (state.motionStrength.HasValue) numbers["motion-strength"] = state.motionStrength.Value.ToString("0.##", CultureInfo.InvariantCulture);

            // 强调色策略：跟随系统时只换 accent 一支，不动其余语义色（避免整站跳色）
            if (state.accentSource == "custom" && IsHex(state.accentCustom))
                ApplyAccent(values, state.accentCustom);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in VarNames) result[name] = values.TryGetValue(name, out string? v) ? v : "#888888";
            foreach (string name in NumNames) result[name] = numbers.TryGetValue(name, out string? v) ? v : "";
            foreach (string name in TextNames) result[name] = values.TryGetValue(name, out string? v) ? v : "";
            return result;
        }

        private static void ApplyAccent(Dictionary<string, string> values, string hex)
        {
            (double h, double s, double l) = ToHsl(hex);
            bool dark = Luma(values["bg"]) < 0.5;
            double accentL = dark ? Math.Clamp(Math.Max(l, 0.58), 0, 0.78) : Math.Clamp(Math.Min(l, 0.55), 0.18, 0.55);
            string accent = FromHsl(h, Math.Clamp(s, 0.2, 1), accentL);
            values["accent"] = accent;
            values["accent-strong"] = dark ? FromHsl(h, s, Math.Min(0.85, accentL + 0.12)) : FromHsl(h, s, Math.Max(0.14, accentL - 0.12));
            values["accent-soft"] = Mix(values[dark ? "bg" : "surface"], accent, dark ? 0.22 : 0.14);
            values["on-accent"] = Luma(accent) > 0.6 ? "#1a1a1a" : "#ffffff";
            values["focus"] = accent;
            values["row-hover"] = Mix(values["surface"], accent, dark ? 0.16 : 0.07);
            values["row-active"] = Mix(values["surface"], accent, dark ? 0.26 : 0.12);
            // 悬停/按下是从选中色派生的，换了强调色要一起跟上，否则会出现"选中变粉、悬停还是旧的"
            values["hover"] = values["row-hover"];
            values["pressed"] = values["row-active"];
        }

        /// <summary>把变量表拼成一段 CSS，网页直接塞进 style 标签。原生 ARGB 不参与，这里全是 CSS 顺序。</summary>
        internal static string ToCss(Dictionary<string, string> values)
        {
            var lines = new List<string>();
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (pair.Value.Length == 0) continue;
                lines.Add("--" + pair.Key + ":" + pair.Value);
            }
            return string.Join(";", lines);
        }
    }
}
