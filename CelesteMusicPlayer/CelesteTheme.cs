// 主题令牌模型（第 1 步 · 基础设施）
//
// 目标：把「皮肤」从代码逻辑变成数据。
// 现状痛点：MainWindow.UiTheme.cs 近 3000 行里大量是遍历控件树改属性，
//           换主题等于改代码；且没有主题文件格式 → 无法导入导出、无法让 AI 生成。
// 本文件只做「数据 + 解析 + 校验 + 与 WinUI 令牌的映射」，不碰任何 UI 逻辑。
//
// 两套渲染层共用同一份数据（这是整套方案的核心）：
//   WinUI 原生页 → 填进 ResourceDictionary 的 Celeste.* 键
//   WebView2 页 → 转成 CSS 变量 --celeste-* 注入网页
// 命名转换规则：Celeste.Panel.Background → --celeste-panel-background
//
// 规范文档：仓库根目录 celeste-theme-ai-guide.md（给 AI 读，AI 按它吐主题 JSON）

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
    /// 主题文件（celeste.theme）。结构刻意做成扁平字典 + light/dark 两组，
    /// 这样它同时对应 WinUI 的 ThemeDictionaries（切浅色/深色系统自动切换）
    /// 和 CSS 的 prefers-color-scheme。
    /// </summary>
    public sealed class CelesteThemeFile
    {
        /// <summary>格式版本号。以后加字段时 +1，老文件仍能读。</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("name")]
        public string Name { get; set; } = "默认";

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>浅色模式令牌。键是点分令牌名（不含 Celeste. 前缀），如 "Panel.Background"。</summary>
        [JsonPropertyName("light")]
        public Dictionary<string, string> Light { get; set; } = new();

        /// <summary>深色模式令牌。缺省时回退用 Light。</summary>
        [JsonPropertyName("dark")]
        public Dictionary<string, string> Dark { get; set; } = new();

        /// <summary>
        /// 非颜色维度：圆角、间距、字号、动效时长、模糊强度。
        /// 这是 WinUI 相对纯 CSS 的强项——CSS 主题多半只换颜色，这里连形状和节奏一起换。
        /// 值为字符串，按 <see cref="CelesteTokenCatalog"/> 里登记的类型解析。
        /// </summary>
        [JsonPropertyName("metrics")]
        public Dictionary<string, string> Metrics { get; set; } = new();
    }

    /// <summary>令牌元数据：名字、类型、默认值、取值范围、作用域。</summary>
    public sealed record CelesteTokenDef(
        string Name,
        string Type,          // color / length / number / duration / fontFamily
        string DefaultLight,
        string DefaultDark,
        string Group,
        double? Min = null,
        double? Max = null,
        string? Note = null);

    /// <summary>
    /// 令牌目录。改外观只改这里 + 主题 JSON，UI 代码不再写死数值。
    /// ★ 标了「最小可用集」——主题只覆盖这一组，界面不会出现「换了一半」的割裂感。
    /// </summary>
    public static class CelesteTokenCatalog
    {
        // ---- 令牌名常量（代码里引用请用这些，别写字符串字面量）----
        public const string AppBackground = "App.Background";
        public const string PanelBackground = "Panel.Background";
        public const string CardBackground = "Card.Background";
        public const string CardBackgroundHover = "Card.BackgroundHover";
        public const string CardStroke = "Card.Stroke";
        public const string Divider = "Divider";
        public const string TextPrimary = "Text.Primary";
        public const string TextSecondary = "Text.Secondary";
        public const string TextDisabled = "Text.Disabled";
        public const string AccentSolid = "Accent.Solid";
        public const string AccentText = "Accent.Text";
        public const string InputBackground = "Input.Background";
        public const string InputStroke = "Input.Stroke";
        public const string OverlayBackground = "Overlay.Background";
        public const string SelectionBackground = "Selection.Background";

        public const string RadiusSmall = "Radius.Small";
        public const string RadiusMedium = "Radius.Medium";
        public const string RadiusLarge = "Radius.Large";
        public const string RadiusControl = "Radius.Control";
        public const string RadiusCover = "Radius.Cover";
        public const string SpaceTight = "Space.Tight";
        public const string SpaceNormal = "Space.Normal";
        public const string SpaceLoose = "Space.Loose";
        public const string MotionFast = "Motion.Fast";
        public const string MotionNormal = "Motion.Normal";
        public const string BlurPanel = "Blur.Panel";
        public const string CoverShadow = "Cover.Shadow";
        public const string FontFamilyUi = "Font.FamilyUi";
        public const string FontFamilyMono = "Font.FamilyMono";

        public static readonly IReadOnlyList<CelesteTokenDef> All = new[]
        {
            // ---------------- 颜色 · 最小可用集（★） ----------------
            new CelesteTokenDef(AppBackground,  "color", "#f3f3f3", "#101216", "颜色"),
            new CelesteTokenDef(PanelBackground, "color", "#fcfcfc", "#181b22", "颜色"),
            new CelesteTokenDef(CardBackground,  "color", "#ffffff", "#1e222b", "颜色"),
            new CelesteTokenDef(CardBackgroundHover, "color", "#f6f6f6", "#262b36", "颜色"),
            new CelesteTokenDef(CardStroke,      "color", "#e5e5e5", "#2b303b", "颜色"),
            new CelesteTokenDef(Divider,         "color", "#e0e0e0", "#2b303b", "颜色"),
            new CelesteTokenDef(TextPrimary,     "color", "#1a1a1a", "#e7e9ef", "颜色"),
            new CelesteTokenDef(TextSecondary,   "color", "#5c5c5c", "#a8b0c0", "颜色"),
            new CelesteTokenDef(TextDisabled,    "color", "#a0a0a0", "#6b7280", "颜色"),
            new CelesteTokenDef(AccentSolid,     "color", "#0067c0", "#f0b429", "颜色"),
            new CelesteTokenDef(AccentText,      "color", "#ffffff", "#101216", "颜色"),
            new CelesteTokenDef(InputBackground,  "color", "#ffffff", "#151920", "颜色"),
            new CelesteTokenDef(InputStroke,      "color", "#d0d0d0", "#333a46", "颜色"),
            new CelesteTokenDef(OverlayBackground,"color", "#ffffff", "#1a1e26", "颜色"),
            new CelesteTokenDef(SelectionBackground,"color","#e5f1fb", "#2a3648", "颜色"),

            // ---------------- 圆角 ----------------
            new CelesteTokenDef(RadiusSmall,  "length", "4",  "4",  "圆角", 0, 40, "行内小元素"),
            new CelesteTokenDef(RadiusMedium, "length", "8",  "8",  "圆角", 0, 40, "卡片/面板"),
            new CelesteTokenDef(RadiusLarge,  "length", "12", "12", "圆角", 0, 40, "弹窗"),
            new CelesteTokenDef(RadiusControl,"length", "4",  "4",  "圆角", 0, 40, "按钮/输入框"),
            new CelesteTokenDef(RadiusCover,  "length", "14", "14", "圆角", 0, 60, "专辑封面。★现状是 XAML 写死值，主题盖不掉，令牌化时必须一并改引用"),

            // ---------------- 间距 ----------------
            new CelesteTokenDef(SpaceTight,  "length", "4",  "4",  "间距", 0, 40),
            new CelesteTokenDef(SpaceNormal, "length", "12", "12", "间距", 0, 60),
            new CelesteTokenDef(SpaceLoose,  "length", "24", "24", "间距", 0, 96),

            // ---------------- 动效 ----------------
            new CelesteTokenDef(MotionFast,   "duration", "0",    "0",    "动效", 0, 2000, "悬停反馈"),
            new CelesteTokenDef(MotionNormal, "duration", "0",    "0",    "动效", 0, 4000, "页面切换"),

            // ---------------- 材质 ----------------
            new CelesteTokenDef(BlurPanel,    "length", "0",  "0",  "材质", 0, 200, "毛玻璃强度。0=不用模糊"),
            new CelesteTokenDef(CoverShadow,  "length", "0",  "12", "材质", 0, 100, "封面投影"),

            // ---------------- 字体 ----------------
            new CelesteTokenDef(FontFamilyUi,   "fontFamily", "", "", "字体", null, null, "空=跟随系统"),
            new CelesteTokenDef(FontFamilyMono, "fontFamily", "Consolas, Cascadia Mono, monospace", "Consolas, Cascadia Mono, monospace", "字体",
                               null, null, "⚠ 绝不能解析成 null 赋给 FontFamily：WinRT 会当 \"Unknown\" 解析 XAML 抛 0x800F1001"),
        };

        private static readonly Dictionary<string, CelesteTokenDef> ByNameMap =
            All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        public static CelesteTokenDef? Find(string name) =>
            ByNameMap.TryGetValue(name, out var d) ? d : null;

        public static bool IsKnown(string name) => ByNameMap.ContainsKey(name);

        /// <summary>「最小可用集」——只覆盖这一组就够换出一套完整观感。</summary>
        public static readonly string[] MinimalSet = BuildMinimalSet();

        private static string[] BuildMinimalSet()
        {
            var names = new List<string>
            {
                AppBackground, PanelBackground, CardBackground, CardBackgroundHover,
                CardStroke, Divider, TextPrimary, TextSecondary, AccentSolid, AccentText,
                RadiusMedium, RadiusControl, RadiusCover, SpaceNormal,
            };
            return names.ToArray();
        }
    }

    /// <summary>
    /// 主题加载 / 校验 / 导出 / 跨渲染层映射。
    /// 无任何 UI 依赖，可单独测试。
    /// </summary>
    public static class CelesteThemeStore
    {
        public static readonly string FileExtension = ".celeste-theme";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,      // 属性名由 JsonPropertyName 定，别让策略改写
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string ThemesDirectory =>
            Path.Combine(AppSettingsStore.GetConfigDirectory(), "Themes");

        // ---------------- 校验 ----------------

        public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
        {
            public static ValidationResult Good() => new(true, Array.Empty<string>(), Array.Empty<string>());
        }

        /// <summary>
        /// 校验一份主题文件。目的是让「AI 生成的 JSON」和「用户手改的 JSON」都能安全导入——
        /// 坏主题绝不能把界面改成不可用状态。
        /// </summary>
        public static ValidationResult Validate(CelesteThemeFile? theme)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (theme == null) return new ValidationResult(false, new[] { "主题文件为空或无法解析" }, warnings);

            if (theme.Version <= 0) theme.Version = 1;
            if (theme.Version > 1)
                warnings.Add($"主题版本 {theme.Version} 比程序新（程序支持 1），未��字段会被忽略");

            void CheckDict(Dictionary<string, string> dict, string which)
            {
                if (dict.Count == 0)
                {
                    if (which == "light") errors.Add("light 组是空的，至少要有它");
                    else warnings.Add("dark 组是空的，将回退用 light");
                    return;
                }
                foreach (var (key, raw) in dict)
                {
                    var def = CelesteTokenCatalog.Find(key);
                    if (def == null) { warnings.Add($"[{which}] 未知令牌「{key}」已忽略"); continue; }

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (def.Type == "fontFamily") continue;   // 字体允许空 = 跟随系统
                        errors.Add($"[{which}] 令牌「{key}」值为空");
                        continue;
                    }

                    switch (def.Type)
                    {
                        case "color":
                            if (!IsColorLike(raw))
                                errors.Add($"[{which}] 「{key}」不是合法颜色：{raw}");
                            break;
                        case "length":
                        case "duration":
                        case "number":
                            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            {
                                errors.Add($"[{which}] 「{key}」不是数字：{raw}");
                            }
                            else if (def.Min.HasValue && d < def.Min.Value)
                            {
                                errors.Add($"[{which}] 「{key}」={raw} 超出下限 {def.Min.Value}");
                            }
                            else if (def.Max.HasValue && d > def.Max.Value)
                            {
                                errors.Add($"[{which}] 「{key}」={raw} 超出上限 {def.Max.Value}");
                            }
                            else if (def.Type == "duration" && d > 0 && raw.IndexOf("ms", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                warnings.Add($"[{which}] 「{key}」是时长，建议带单位（{raw}ms）");
                            }
                            break;
                        case "fontFamily":
                            if (raw.Trim().Equals("null", StringComparison.OrdinalIgnoreCase) ||
                                raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                                errors.Add($"[{which}] 「{key}」不能是 null/none——赋给 FontFamily 会让 XAML 解析失败（0x800F1001）");
                            break;
                    }
                }
            }

            CheckDict(theme.Light, "light");
            CheckDict(theme.Dark, "dark");

            if (theme.Light.Count > 0)
            {
                var covered = new HashSet<string>(theme.Light.Keys, StringComparer.OrdinalIgnoreCase);
                int missing = CelesteTokenCatalog.MinimalSet.Count(k => !covered.Contains(k));
                if (missing > 0)
                    warnings.Add($"最小可用集有 {missing} 个令牌没覆盖，界面会「换了一半」（建议先只覆盖最小集试手）");
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// 颜色接受 #RGB / #RRGGBB / #AARRGGBB / rgb(...) / 常见 CSS 颜色名。
        /// 只做「像不像颜色」的判断，不做色彩空间转换。
        /// </summary>
        private static bool IsColorLike(string raw)
        {
            var s = raw.Trim();
            if (s.Length == 0) return false;
            if (s[0] == '#')
            {
                var hex = s[1..];
                if (hex.Length != 3 && hex.Length != 6 && hex.Length != 8) return false;
                return hex.All(c => char.IsAsciiHexDigit(c));
            }
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("hsl", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("oklch", StringComparison.OrdinalIgnoreCase))
                return s.Contains('(') && s.Contains(')');
            return s.All(c => char.IsAsciiLetter(c)) && s.Length >= 3;   // 颜色名
        }

        // ---------------- 读 / 写 ----------------

        public static string? FilePathFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (safe.Length == 0) return null;
            return Path.Combine(ThemesDirectory, safe + FileExtension);
        }

        public static IReadOnlyList<string> ListThemes()
        {
            try
            {
                if (!Directory.Exists(ThemesDirectory)) return Array.Empty<string>();
                return Directory.GetFiles(ThemesDirectory, "*" + FileExtension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CelesteThemeStore.ListThemes", ex);
                return Array.Empty<string>();
            }
        }

        public static CelesteThemeFile? Load(string name)
        {
            var path = FilePathFor(name);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                var theme = JsonSerializer.Deserialize<CelesteThemeFile>(json, JsonOpts);
                var v = Validate(theme);
                if (!v.Ok)
                {
                    StartupLog.Write("[Theme] 主题文件校验失败，已拒绝加载：" + name + " / " + string.Join("; ", v.Errors));
                    return null;
                }
                foreach (var w in v.Warnings) StartupLog.Write("[Theme] " + w);
                return theme;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CelesteThemeStore.Load:" + name, ex);
                return null;
            }
        }

        public static bool Save(CelesteThemeFile theme, out string? error)
        {
            error = null;
            try
            {
                var v = Validate(theme);
                if (!v.Ok) { error = string.Join("; ", v.Errors); return false; }

                var path = FilePathFor(theme.Name);
                if (path == null) { error = "主题名为空或含非法字符"; return false; }

                Directory.CreateDirectory(ThemesDirectory);
                // 先写临时文件再替换：中途崩了不会留下半个坏主题
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(theme, JsonOpts));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CelesteThemeStore.Save", ex);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>把内置默认主题写一份到磁盘，当作用户上手的模板（也是 AI 生成的参考样板）。</summary>
        public static CelesteThemeFile CreateDefault()
        {
            var t = new CelesteThemeFile { Name = "默认", Description = "Celeste 内置配色，WinUI Fluent 观感的等价物" };
            foreach (var d in CelesteTokenCatalog.All)
            {
                t.Light[d.Name] = d.DefaultLight;
                t.Dark[d.Name] = d.DefaultDark;
            }
            return t;
        }

        // ---------------- 跨渲染层映射 ----------------

        /// <summary>Celeste.Panel.Background → --celeste-panel-background</summary>
        public static string ToCssVariableName(string tokenName)
        {
            var sb = new System.Text.StringBuilder("--celeste-");
            bool lastLower = false;
            foreach (var c in tokenName)
            {
                if (char.IsUpper(c))
                {
                    if (lastLower) sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                    lastLower = false;
                }
                else
                {
                    sb.Append(c == '.' ? '-' : c);
                    lastLower = char.IsLetterOrDigit(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>取某模式下的实际令牌值，未覆盖则回退默认。</summary>
        public static string Resolve(CelesteThemeFile theme, string tokenName, bool dark)
        {
            var dict = dark ? theme.Dark : theme.Light;
            if (dict.Count == 0) dict = theme.Light;
            if (dict.TryGetValue(tokenName, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();

            var def = CelesteTokenCatalog.Find(tokenName);
            if (def != null) return dark ? def.DefaultDark : def.DefaultLight;
            return "";
        }

        /// <summary>生成整套 CSS 变量声明块，直接塞进网页的 &lt;style&gt; 或 style 标签。</summary>
        public static string BuildCssVariables(CelesteThemeFile theme, bool dark)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(":root{");
            foreach (var d in CelesteTokenCatalog.All)
            {
                var v = Resolve(theme, d.Name, dark);
                if (v.Length == 0) continue;   // 字体为空 = 跟随系统，网页侧不声明
                sb.Append(ToCssVariableName(d.Name)).Append(':').Append(v).Append(';');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>CSS 变量值转 WinUI 可用的值（长度加 px、时长加 ms）。</summary>
        public static string ToWinUiValue(string tokenName, string cssValue)
        {
            var def = CelesteTokenCatalog.Find(tokenName);
            var type = def?.Type ?? "color";
            var s = cssValue.Trim();
            switch (type)
            {
                case "length":
                    // 允许 "0" 之外的裸数字当 px；带单位的原样保留
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? d.ToString("F0", CultureInfo.InvariantCulture)
                        : s;
                case "duration":
                    return s.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? s : s + "ms";
                default:
                    return s;
            }
        }
    }
}
