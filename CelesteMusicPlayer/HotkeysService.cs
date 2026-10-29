using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CelesteMusicPlayer;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 设置页「全局热键」那一组：读取默认/自定义绑定、写回、恢复默认。
    ///
    /// W13：从 MainWindow.WebSettings.cs 搬出来。它只需要一个动作 ——
    /// 改完之后让宿主重启热键注册（必须回到 UI 线程，因为热键绑定挂在线程消息循环上）。
    /// </summary>
    internal sealed class HotkeysService(Action restartHotkeys)
    {
        internal object Handle(JsonElement msg)
        {
            // 快照里取热键时不带消息体（default JsonElement），按 "get" 处理。
            if (msg.ValueKind != JsonValueKind.Object) return BuildHotkeys();

            string action = ReadString(msg, "action");
            if (action == "get") return BuildHotkeys();

            if (action == "reset")
            {
                AppSettingsStore.Update(state => state.CustomHotkeys.Clear());
                restartHotkeys();
                return BuildHotkeys();
            }

            if (action != "set") throw new ArgumentException("未知的热键操作。");

            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (msg.TryGetProperty("bindings", out JsonElement element) && element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string value = (property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : "").Trim();
                    if (string.Equals(value, "__clear__", StringComparison.Ordinal)) continue; // 留空=回到默认
                    if (value.Length == 0) continue;
                    if (!Enum.TryParse(property.Name, ignoreCase: true, out HotkeyAction _))
                        throw new ArgumentException("未知的热键动作：" + property.Name);
                    if (!GlobalHotkeyService.TryParseHotkey(value, out _, out _))
                        throw new ArgumentException("热键格式不认识：" + value + "（例如 Ctrl+Alt+P）");
                    bindings[property.Name] = value;
                }
            }

            AppSettingsStore.Update(state =>
            {
                state.CustomHotkeys.Clear();
                foreach (KeyValuePair<string, string> pair in bindings) state.CustomHotkeys[pair.Key] = pair.Value;
            });
            restartHotkeys();
            return BuildHotkeys();
        }

        private object BuildHotkeys()
        {
            AppSettingsState state = AppSettingsStore.Load();
            var defaults = GlobalHotkeyService.DefaultBindings.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var custom = state.CustomHotkeys.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return new
            {
                defaults,
                custom,
                enabled = state.EnableGlobalHotkeys,
                hint = "留空表示使用默认组合。改完会重启热键注册；有没有真正抢到，取决于当时有没有别的软件占用同一个组合。"
            };
        }

        private static string ReadString(JsonElement msg, string property)
        {
            if (msg.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            return "";
        }
    }
}
