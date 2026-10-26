using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

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
        internal object WebAppearanceAsync(JsonElement msg)
        {
            string operation = ReadString(msg, "operation");
            switch (operation)
            {
                case "get": return Snapshot();
                case "select": return SelectPreset(ReadString(msg, "presetId"));
                case "mode": return SetMode(ReadString(msg, "mode"));
                case "accent": return SetAccent(ReadString(msg, "source"), ReadString(msg, "color"));
                case "preview": return Preview(msg);
                case "commit": return Commit();
                case "cancel": return Cancel();
                case "reset": return Reset(msg);
                case "savePreset": return SavePreset(ReadString(msg, "name"));
                case "deletePreset": return DeletePreset(ReadString(msg, "presetId"));
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
                presets = PresetCards(state),
                light = Bundle(WebAppearance.Resolve(state, WebAppearance.Light)),
                dark = Bundle(WebAppearance.Resolve(state, WebAppearance.Dark)),
                // 可继续编辑的参数清单：类型/边界/单位都在这里，网页不另存一份默认值
                numbers = NumberSchema(state)
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
                if (state.overrides.TryGetValue("light:" + key, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
                var numbers = WebAppearance.Numbers(preset);
                string text = numbers.TryGetValue(key, out string? value) ? value : "";
                return double.TryParse(text.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double fallback) ? fallback : 0;
            }

            return new object[]
            {
                new { id = "radius", label = "圆角", min = 0, max = 34, step = 1, unit = "px", value = Get("radius") },
                new { id = "blur", label = "玻璃模糊", min = 0, max = 60, step = 1, unit = "px", value = Get("blur") },
                new { id = "sat", label = "饱和度", min = 0.4, max = 1.6, step = 0.05, unit = "×", value = Get("sat") },
                new { id = "motion-speed", label = "动效速度", min = 0.4, max = 1.6, step = 0.05, unit = "×", value = Get("motion-speed") },
                new { id = "motion-strength", label = "动效幅度", min = 0, max = 1.6, step = 0.05, unit = "×", value = Get("motion-strength") },
                new { id = "density", label = "间距密度", min = 0.8, max = 1.25, step = 0.01, unit = "×", value = Get("density") }
            };
        }

        // —— 当前外观（这些是偏好，选了就存）——
        private static object SelectPreset(string id)
        {
            WebAppearance.State state = WebAppearance.Draft();
            if (WebAppearance.FindPreset(id, state) == null) throw new ArgumentException("没有这套预设：" + id);
            state.presetId = id;
            WebAppearance.SetDraft(state);
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
            return Snapshot();
        }

        private static object SetMode(string mode)
        {
            if (mode != WebAppearance.Light && mode != WebAppearance.Dark && mode != "system")
                throw new ArgumentException("未知的明暗模式。");
            WebAppearance.State state = WebAppearance.Draft();
            state.mode = mode;
            WebAppearance.SetDraft(state);
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
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
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
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
            return new { draft = true, light = Bundle(WebAppearance.Resolve(state, WebAppearance.Light)), dark = Bundle(WebAppearance.Resolve(state, WebAppearance.Dark)) };
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
            WebAppearance.Save(state);
            WebAppearance.ClearDraft();
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
                radius = ParseNumber(light, "radius", 26),
                blur = ParseNumber(light, "blur", 25),
                sat = ParseNumber(light, "sat", 1),
                motionSpeed = ParseNumber(light, "motion-speed", 1),
                motionStrength = ParseNumber(light, "motion-strength", 1),
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
