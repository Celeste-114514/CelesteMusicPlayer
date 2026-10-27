using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 外观通道（网页）。
    ///
    /// 三条纪律：
    /// 1. 预览是内存草稿，不写文件；点保存才落盘。改崩了点「取消预览」就完整回到进入时的样子。
    /// 2. 只回语义变量，不回「请自己去改 CSS」这类空话；网页拿到的是已经算好的颜色/数值。
    /// 3. 不碰音频、DSP、账号、曲库目录 —— 换主题绝不重建播放引擎，也不动音量和设备。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal async Task<object> WebAppearanceAsync(JsonElement msg)
        {
            string operation = ReadString(msg, "operation");
            StartupLog.Write("[WebShell] theme op=" + operation);
            switch (operation)
            {
                case "get": return Snapshot();
                case "select": return SelectPreset(ReadString(msg, "presetId"));
                case "mode": return SetMode(ReadString(msg, "mode"));
                case "accent": return SetAccent(ReadString(msg, "source"), ReadString(msg, "color"));
                case "preview": return Preview(msg);
                case "restoreDraft": return RestoreDraft(msg);
                case "commit": return Commit();
                case "cancel": return Cancel();
                case "reset": return Reset(msg);
                case "savePreset": return SavePreset(ReadString(msg, "name"));
                case "deletePreset": return DeletePreset(ReadString(msg, "presetId"));
                case "playerButtons": return SetPlayerButtons(ReadString(msg, "value"));
                case "typography": return SetTypography(msg);
                case "motion": return SetMotion(msg);
                case "navItems": return SetNavItems(ReadString(msg, "value"));
                case "background": return SetBackground(msg);
                case "pickBackground": return await PickBackgroundAsync(ReadString(msg, "media")).ConfigureAwait(true);
                case "clearBackground": return ClearBackground();
                default: throw new ArgumentException("未知的外观操作：" + operation);
            }
        }

        // —— 快照：预设库 + 当前两套明暗的完整变量 ——
        private static object Snapshot()
        {
            WebAppearance.State state = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
            string resolved = WebAppearance.ResolveMode(state.mode);
            return new
            {
                revision = WebAppearance.CurrentRevision,
                draft = WebAppearance.HasDraft(),
                mode = state.mode,
                resolved,
                accentSource = state.accentSource,
                accentCustom = state.accentCustom,
                presetId = state.presetId,
                overrideCount = state.overrides.Count,
                editState = WebAppearance.EditState(state),
                presets = PresetCards(state),
                light = Bundle(WebAppearance.Resolve(state, WebAppearance.Light)),
                dark = Bundle(WebAppearance.Resolve(state, WebAppearance.Dark)),
                // 可继续编辑的参数清单：类型/边界/单位都在这里，网页不另存一份默认值
                numbers = NumberSchema(state),
                // 底栏按钮的现状 + 可选目录：显隐/顺序存在宿主，网页不自己编一套按钮表
                playerButtons = state.playerButtons,
                playerButtonCatalog = WebAppearance.PlayerButtonCatalog.Select(item => new { id = item.id, label = item.label, group = item.group, core = item.core }).ToArray(),
                // 字体选择器：族名清单来自宿主（网页拿不到 Windows 字体表），当前选中值也在这一起给
                fonts = FontCatalog.Families(),
                typography = new { fontUi = state.fontUi, fontCjk = state.fontCjk, fontMono = state.fontMono },
                // 动效：模式/速度/幅度/回弹风格。速度是"越大越快"，幅度 0 = 不做几何运动。
                // 这几项单独存，不随外观预设切换 —— 换配色不该把用户关掉的动画又打开。
                motion = new
                {
                    mode = state.motionMode,
                    speed = WebAppearance.EffectiveMotionSpeed(state),
                    strength = WebAppearance.EffectiveMotionStrength(state),
                    bounce = state.motionBounce,
                    speedMin = 0.6, speedMax = 1.6, speedStep = 0.05,
                    strengthMin = 0.0, strengthMax = 1.25, strengthStep = 0.05,
                    // 被关掉的类别 + 全部可选类别（含中文名，网页直接拿来画开关）
                    off = WebAppearance.MotionOffList(state),
                    categories = WebAppearance.MotionCategories
                        .Select(id => new { id, label = WebAppearance.MotionCategoryLabels.TryGetValue(id, out string? label) ? label : id })
                        .ToArray()
                },
                // 导航构成：哪些项、在哪一圈、什么顺序
                navItems = state.navItems,
                navItemCatalog = WebAppearance.NavItemCatalog.Select(item => new { id = item.id, label = item.label, group = item.group }).ToArray()
            };
        }

        private static object Bundle(Dictionary<string, string> values) => new
        {
            css = WebAppearance.ToCss(values),
            values
        };

        /// <summary>预设卡片只带够用的缩略色：底、面板、强调、文字、线。</summary>
        private static object[] PresetCards(WebAppearance.State state)
        {
            var list = new List<object>();
            foreach (WebAppearance.Preset preset in WebAppearance.BuiltIn.Concat(state.customPresets))
            {
                var light = WebAppearance.Derive(preset, WebAppearance.Light);
                var dark = WebAppearance.Derive(preset, WebAppearance.Dark);
                string[] keys = { "bg", "shell", "surface", "panel", "accent", "ink", "muted", "line" };
                list.Add(new
                {
                    id = preset.id, name = preset.name, group = preset.group, custom = preset.custom,
                    radius = preset.radius, density = preset.density,
                    light = keys.ToDictionary(key => key, key => light[key]),
                    dark = keys.ToDictionary(key => key, key => dark[key])
                });
            }
            return list.ToArray();
        }

        /// <summary>数值参数目录：给网页画滑杆用，边界和单位在这里定，避免两端各写一份。</summary>
        private static object[] NumberSchema(WebAppearance.State state)
        {
            WebAppearance.Preset preset = WebAppearance.FindPreset(state.presetId, state) ?? WebAppearance.BuiltIn[0];
            double Get(string key)
            {
                // 覆盖值里带单位（"70px"），直接 TryParse 会失败 →
                // 滑杆回弹到预设值，但页面上生效的还是覆盖值，看着就是"滑了没记住"。先剥单位。
                if (state.overrides.TryGetValue("light:" + key, out string? raw)
                    && double.TryParse((raw ?? "").Replace("px", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
                var numbers = WebAppearance.Numbers(preset);
                string text = numbers.TryGetValue(key, out string? value) ? value : "";
                return double.TryParse(text.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double fallback) ? fallback : 0;
            }

            return new object[]
            {
                new { id = "radius", label = "圆角", min = 0, max = 34, step = 1, unit = "px", value = Get("radius") },
                new { id = "blur", label = "玻璃模糊", min = 0, max = 60, step = 1, unit = "px", value = Get("blur") },
                new { id = "sat", label = "饱和度", min = 0.4, max = 1.6, step = 0.05, unit = "×", value = Get("sat") },
                // ⚠ 动效速度/幅度不在这里：它们有独立的一份用户偏好（motion 字段），
                // 走覆盖表的话一换外观预设就被清掉了 —— 换配色不该把用户调过的动效重置。
                new { id = "density", label = "间距密度", min = 0.8, max = 1.25, step = 0.01, unit = "×", value = Get("density") },
                new { id = "material-opacity", label = "磨砂底色浓度", min = 0.4, max = 1, step = 0.02, unit = "×", value = Get("material-opacity") },
                new { id = "material-blur", label = "浮层磨砂强度", min = 0, max = 48, step = 1, unit = "px", value = Get("material-blur") },
                new { id = "material-highlight", label = "玻璃边缘高光", min = 0, max = 0.8, step = 0.02, unit = "×", value = Get("material-highlight") },
                new { id = "material-shadow", label = "浮层阴影强度", min = 0, max = 0.3, step = 0.01, unit = "×", value = Get("material-shadow") },
                // 底栏单独一组：它要的语义和别处不一样（可以略透、可以毛玻璃、封面圆角自成一档）
                new { id = "player-opacity", label = "底栏不透明度", min = 0.5, max = 1, step = 0.02, unit = "×", value = Get("player-opacity") },
                new { id = "player-blur", label = "底栏毛玻璃", min = 0, max = 40, step = 1, unit = "px", value = Get("player-blur") },
                new { id = "cover-radius", label = "底栏封面圆角", min = 0, max = 27, step = 1, unit = "px", value = Get("cover-radius") },
                new { id = "cover-size", label = "底栏封面大小", min = 42, max = 88, step = 1, unit = "px", value = Get("cover-size") },
                new { id = "player-height", label = "底栏高度", min = 88, max = 180, step = 1, unit = "px", value = Get("player-height") },
                new { id = "player-gap", label = "底栏区块间距", min = 8, max = 56, step = 1, unit = "px", value = Get("player-gap") },
                new { id = "player-pad", label = "底栏上下留白", min = 6, max = 40, step = 1, unit = "px", value = Get("player-pad") },
                // 排版：1 就是设计原尺寸，往两边各留一点余量；行高按字号倍数算
                new { id = "text-scale", label = "字号整体缩放", min = 0.8, max = 1.4, step = 0.01, unit = "×", value = Get("text-scale") },
                new { id = "line-height", label = "行高", min = 1.2, max = 2, step = 0.05, unit = "×", value = Get("line-height") },
                new { id = "disabled-opacity", label = "禁用态透明度", min = 0.15, max = 0.7, step = 0.05, unit = "×", value = Get("disabled-opacity") },
                // 导航几何。两圈半径是 SVG viewBox 单位，跟整个轮盘一起缩放
                new { id = "nav-size", label = "导航整体尺寸", min = 240, max = 420, step = 2, unit = "px", value = Get("nav-size") },
                new { id = "nav-icon", label = "导航图标大小", min = 20, max = 40, step = 1, unit = "px", value = Get("nav-icon") },
                new { id = "nav-ring-1", label = "内圈半径", min = 100, max = 200, step = 2, unit = "", value = Get("nav-ring-1") },
                new { id = "nav-ring-2", label = "外圈半径", min = 180, max = 270, step = 2, unit = "", value = Get("nav-ring-2") },
                // 背景层：模糊/亮度/遮罩浓度
                new { id = "bg-blur", label = "背景模糊", min = 0, max = 40, step = 1, unit = "px", value = Get("bg-blur") },
                new { id = "bg-brightness", label = "背景亮度", min = 0.4, max = 1.4, step = 0.05, unit = "×", value = Get("bg-brightness") },
                new { id = "bg-scrim", label = "背景遮罩", min = 0, max = 0.95, step = 0.05, unit = "×", value = Get("bg-scrim") }
            };
        }

        // —— 当前外观（这些是偏好，选了就存）——
        private static object SelectPreset(string id)
        {
            WebAppearance.State state = WebAppearance.Draft();
            if (WebAppearance.FindPreset(id, state) == null) throw new ArgumentException("没有这套预设：" + id);
            state.presetId = id;
            state.overrides.Clear();
            state.accentSource = "theme";
            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        private static object SetMode(string mode)
        {
            if (mode != WebAppearance.Light && mode != WebAppearance.Dark && mode != "system")
                throw new ArgumentException("未知的明暗模式。");
            WebAppearance.State state = WebAppearance.Draft();
            state.mode = mode;
            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        private static object SetAccent(string source, string color)
        {
            if (source != "theme" && source != "system" && source != "custom") throw new ArgumentException("未知的强调色来源。");
            WebAppearance.State state = WebAppearance.Draft();
            state.accentSource = source;
            if (source == "custom")
            {
                string hex = (color ?? "").Trim();
                if (hex.Length != 7 || hex[0] != '#' || !hex.Skip(1).All(Uri.IsHexDigit)) throw new ArgumentException("颜色格式应为 #RRGGBB。");
                state.accentCustom = hex;
            }
            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        /// <summary>底栏布局进入草稿，和颜色一起保存/取消/撤销。</summary>
        private static object SetPlayerButtons(string value)
        {
            WebAppearance.State state = WebAppearance.Draft();
            string normalized = WebAppearance.NormalizePlayerButtons(value);
            if (normalized == state.playerButtons) return Snapshot();
            state.playerButtons = normalized;
            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        /// <summary>字体族进入统一草稿；空字段表示不改。</summary>
        private static object SetTypography(JsonElement msg)
        {
            WebAppearance.State state = WebAppearance.Draft();
            string ui = (ReadString(msg, "fontUi") ?? "").Trim();
            string cjk = (ReadString(msg, "fontCjk") ?? "").Trim();
            string mono = (ReadString(msg, "fontMono") ?? "").Trim();
            if (ui.Length > 0) state.fontUi = ui;
            if (cjk.Length > 0) state.fontCjk = cjk;
            if (mono.Length > 0) state.fontMono = mono;


            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        /// <summary>
        /// 动效偏好（模式/速度/幅度/回弹风格）。只更新内存草稿，跟颜色一起保存/取消/撤销。
        /// speed 与 strength 传 null 表示"沿用预设自带"，传数字就固定成用户值。
        /// </summary>
        private static object SetMotion(JsonElement msg)
        {
            string mode = (ReadString(msg, "mode") ?? "").Trim();
            string bounce = (ReadString(msg, "bounce") ?? "").Trim();
            return ChangeDraft(state =>
            {
                if (mode.Length > 0)
                {
                    if (mode != "system" && mode != "full" && mode != "lite" && mode != "off")
                        throw new ArgumentException("未知的动效模式。");
                    state.motionMode = mode;
                }
                if (bounce.Length > 0)
                {
                    if (bounce != "calm" && bounce != "soft" && bounce != "lively")
                        throw new ArgumentException("未知的回弹风格。");
                    state.motionBounce = bounce;
                }
                // 显式传 null = 撤销用户自己的设定，回到预设自带的性格
                if (msg.TryGetProperty("speed", out JsonElement rawSpeed))
                {
                    double? parsed = ReadNullableNumber(rawSpeed);
                    if (parsed.HasValue) state.motionSpeed = Math.Clamp(parsed.Value, 0.6, 1.6);
                    else state.motionSpeed = null;
                }
                if (msg.TryGetProperty("strength", out JsonElement rawStrength))
                {
                    double? parsed = ReadNullableNumber(rawStrength);
                    if (parsed.HasValue) state.motionStrength = Math.Clamp(parsed.Value, 0, 1.25);
                    else state.motionStrength = null;
                }
                // 被关掉的类别：整份替换，不做增量合并 —— 网页那边是"开关"语义
                if (msg.TryGetProperty("off", out JsonElement rawOff))
                {
                    var ids = new List<string>();
                    if (rawOff.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in rawOff.EnumerateArray())
                        {
                            string id = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
                            if (id.Length > 0) ids.Add(id);
                        }
                    }
                    state.motionOff = WebAppearance.NormalizeMotionOff(string.Join(",", ids));
                }
            });
        }

        /// <summary>读一个数字字段；不是数字就当作"没有"。</summary>
        private static double? ReadNullableNumber(JsonElement raw)
        {
            if (raw.ValueKind == JsonValueKind.Null) return null;
            if (raw.ValueKind != JsonValueKind.Number && raw.ValueKind != JsonValueKind.String) return null;
            string text = raw.ValueKind == JsonValueKind.Number
                ? raw.GetDouble().ToString("0.####", CultureInfo.InvariantCulture)
                : (raw.GetString() ?? "").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
        }

        /// <summary>半圆导航的项目分组/顺序，进入统一草稿。</summary>
        private static object SetNavItems(string value)
        {
            WebAppearance.State state = WebAppearance.Draft();
            string normalized = WebAppearance.NormalizeNavItems(value);
            if (normalized == state.navItems) return Snapshot();

            state.navItems = normalized;
            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        // —— 背景层 ——
        internal const string BackgroundAssetHost = "celeste-asset.internal";

        /// <summary>
        /// 背景图片的落盘目录。网页不能直接读用户选的路径（CSP 不许），
        /// 所以选完先把图拷进这里，再由 WebView2 的虚拟主机映射成 https://celeste-asset.internal/xxx。
        /// </summary>
        internal static string BackgroundFolder
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "webbg");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        /// <summary>改背景模式/适配/位置，进入统一草稿。</summary>
        private static object SetBackground(JsonElement msg)
        {
            string mode = ReadString(msg, "mode");
            string fit = ReadString(msg, "fit");
            string pos = ReadString(msg, "pos");
            return ChangeDraft(state =>
            {
                if (mode.Length > 0) state.backgroundMode = mode;
                if (fit.Length > 0) state.backgroundFit = fit;
                if (pos.Length > 0) state.backgroundPos = pos;
            });
        }

        /// <summary>资源使用唯一文件名，换图不覆盖旧文件，撤销/取消仍可恢复。</summary>
        private async Task<object> PickBackgroundAsync(string media)
        {
            bool video = media == "video";
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            string[] extensions = video ? new[] { ".mp4", ".webm", ".m4v" }
                : new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerHostHandle());
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return Snapshot();
            string path = file.Path;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!extensions.Contains(ext)) throw new ArgumentException("所选文件类型不支持。");
            long limit = video ? 512L * 1024 * 1024 : 40L * 1024 * 1024;
            long length = new FileInfo(path).Length;
            if (length <= 0 || length > limit)
                throw new ArgumentException(video ? "请选择非空且不超过 512 MB 的视频。" : "请选择非空且不超过 40 MB 的图片。");
            string name = Guid.NewGuid().ToString("N") + ext;
            string destination = Path.Combine(BackgroundFolder, name);
            try
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output);
            }
            catch
            {
                if (File.Exists(destination)) File.Delete(destination);
                throw;
            }
            string url = "https://" + BackgroundAssetHost + "/" + name;
            return ChangeDraft(state =>
            {
                if (video) state.backgroundVideo = url;
                else state.backgroundImage = url;
                state.backgroundMode = video ? "video" : "image";
            });
        }

        private static object ClearBackground() => ChangeDraft(state =>
        {
            state.backgroundMode = "solid";
            // 保留资源引用，重新选图片/视频模式、撤销都能恢复。
        });

        /// <summary>所有背景编辑均只更新内存草稿。</summary>
        private static object ChangeDraft(Action<WebAppearance.State> mutate)
        {
            WebAppearance.State state = WebAppearance.Draft();
            mutate(state);

            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        // —— 草稿：改参数先只进内存 ——
        private static object Preview(JsonElement msg)
        {
            WebAppearance.State state = WebAppearance.Draft();
            if (!msg.TryGetProperty("changes", out JsonElement changes) || changes.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("缺少要预览的改动。");

            foreach (JsonProperty property in changes.EnumerateObject())
            {
                int split = property.Name.IndexOf(':');
                if (split <= 0) throw new ArgumentException("变量名要带明暗前缀，例如 light:accent。");
                string mode = property.Name.Substring(0, split);
                if (mode != WebAppearance.Light && mode != WebAppearance.Dark) throw new ArgumentException("未知的明暗前缀：" + mode);
                string name = property.Name.Substring(split + 1);
                if (!WebAppearance.VarNames.Contains(name) && !WebAppearance.NumNames.Contains(name))
                    throw new ArgumentException("不支持的外观参数：" + name);
                string value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
                state.overrides[property.Name] = value;
            }

            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        private static object RestoreDraft(JsonElement msg)
        {
            if (!msg.TryGetProperty("state", out JsonElement raw)) throw new ArgumentException("缺少历史状态。");
            WebAppearance.State restored = raw.Deserialize<WebAppearance.State>() ?? throw new ArgumentException("无效历史状态。");
            WebAppearance.State current = WebAppearance.Draft();
            if (WebAppearance.FindPreset(restored.presetId, current) == null) throw new ArgumentException("历史预设已不存在。");
            foreach (string key in restored.overrides.Keys)
            {
                string[] parts = key.Split(':');
                if (parts.Length != 2 || (parts[0] != WebAppearance.Light && parts[0] != WebAppearance.Dark)
                    || (!WebAppearance.VarNames.Contains(parts[1]) && !WebAppearance.NumNames.Contains(parts[1])))
                    throw new ArgumentException("历史中包含无效参数。");
            }
            restored.customPresets = current.customPresets;
            restored.revision = current.revision;
            WebAppearance.SetDraft(restored);
            return Snapshot();
        }

        private static object Commit()
        {
            if (!WebAppearance.HasDraft()) return Snapshot();
            WebAppearance.State state = WebAppearance.Draft();
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
            return Snapshot();
        }

        private static object Cancel()
        {
            WebAppearance.ClearDraft();
            return Snapshot();
        }

        private static object Reset(JsonElement msg)
        {
            string scope = ReadString(msg, "scope");
            WebAppearance.State state = WebAppearance.Draft();
            if (scope == "all") state.overrides.Clear();
            else if (scope == "keys")
            {
                if (!msg.TryGetProperty("keys", out JsonElement keys) || keys.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("缺少要恢复的参数。");
                foreach (JsonElement key in keys.EnumerateArray())
                {
                    string name = key.ValueKind == JsonValueKind.String ? key.GetString() ?? "" : "";
                    if (name.Length > 0) state.overrides.Remove(name);
                }
            }
            else throw new ArgumentException("未知的恢复范围。");

            WebAppearance.SetDraft(state);
            return Snapshot();
        }

        // —— 我的预设 ——
        private static object SavePreset(string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) throw new ArgumentException("请先给这套外观起个名字。");
            if (trimmed.Length > 40) trimmed = trimmed.Substring(0, 40);

            WebAppearance.State state = WebAppearance.Draft();
            var light = WebAppearance.Resolve(state, WebAppearance.Light);
            var dark = WebAppearance.Resolve(state, WebAppearance.Dark);

            string id = "my-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var preset = new WebAppearance.Preset
            {
                id = id, name = trimmed, group = "我的预设", custom = true,
                // 只存能反推出整套的基色，其余仍由 Derive 计算，避免把 20 多个变量硬编码进去
                lightBg = light["bg"], darkBg = dark["bg"], accent = light["accent"],
                materialStyle = (int)ParseNumber(light, "material-style", 0),
                radius = ParseNumber(light, "radius", 26),
                blur = ParseNumber(light, "blur", 25),
                sat = ParseNumber(light, "sat", 1),
                // 预设里存的是旧的"时长倍率"（大 = 慢），派生时用的是它的倒数（速度）。
                // 保存个人预设时要把速度换回倍率存，否则存进去再取出来方向就反了。
                motionSpeed = 1.0 / Math.Max(0.05, ParseNumber(light, "motion-speed", 1)),
                motionStrength = ParseNumber(light, "motion-strength", 1),
                // 排版也算预设的一部分（录音室更紧、复古更松），存进「我的预设」时要一起带上
                textScale = ParseNumber(light, "text-scale", 1),
                lineHeight = ParseNumber(light, "line-height", 1.5),
                density = ParseNumber(light, "density", 1) < 0.95 ? "compact" : ParseNumber(light, "density", 1) > 1.05 ? "relaxed" : "normal"
            };

            state.customPresets.Add(preset);
            state.presetId = id;
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
            return Snapshot();
        }

        private static object DeletePreset(string id)
        {
            WebAppearance.State state = WebAppearance.Draft();
            WebAppearance.Preset? preset = state.customPresets.FirstOrDefault(p => p.id == id);
            if (preset == null) throw new ArgumentException("只能删除「我的预设」里的外观。");
            state.customPresets.Remove(preset);
            if (state.presetId == id) state.presetId = WebAppearance.BuiltIn[0].id;
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
            return Snapshot();
        }

        private static double ParseNumber(Dictionary<string, string> values, string key, double fallback)
        {
            string text = values.TryGetValue(key, out string? value) ? value : "";
            text = text.Replace("px", "");
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
        }
    }
}
