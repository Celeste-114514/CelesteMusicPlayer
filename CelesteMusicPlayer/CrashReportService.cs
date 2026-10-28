using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 诊断报告的收集与落盘。
    ///
    /// 用户报「出问题了」时，最缺的是**现场**：什么版本、什么环境、上次是不是崩过、
    /// 日志最后几行是什么。让他自己翻 %LocalAppData% 找日志不现实，所以这里一键打包成
    /// 一个纯文本文件，用户在关于页点一下就拿到，直接发给维护者。
    ///
    /// 三条硬规矩：
    /// 1. **默认脱敏** —— 设置里的密码/令牌/密钥一律替换成「已隐藏」，家目录路径替换成 %USERPROFILE%；
    /// 2. **限量** —— 每个日志文件只取最后几百行，总量封顶，避免导出几百 MB 的文件；
    /// 3. **不自动上传** —— 只写本地文件，发不发由用户自己决定。
    /// </summary>
    public static class CrashReportService
    {
        /// <summary>诊断文件保留份数：够对比「这次和上次」就行，不留一堆占地方。</summary>
        private const int KeepReports = 5;

        private const int MaxLogLinesPerFile = 400;
        private const int MaxLogCharsTotal = 400_000;
        private const int MaxSettingsChars = 20_000;
        private const int MaxCrashHistory = 20;

        // UnsafeRelaxedJsonEscaping：默认编码器会把中文转成 \uXXXX，
        // 报告是给人看的，宁可放宽转义也不要满屏反斜杠。
        private static readonly JsonSerializerOptions PrettyJson = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string DataRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer");

        public static string DiagnosticsDirectory => Path.Combine(DataRoot, "diagnostics");

        public static string MarkerPath => Path.Combine(DataRoot, ".running");

        public static string CrashHistoryPath => Path.Combine(DataRoot, "crashes.json");

        /// <summary>上次启动时的运行标记内容（没有标记 = 上次是干净退出）。</summary>
        public static string? ReadMarkerView()
        {
            try
            {
                return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>最近几次「未正常退出」的记录，最新的在最后。</summary>
        public static List<CrashEntry> ReadCrashHistory()
        {
            try
            {
                if (!File.Exists(CrashHistoryPath)) return new List<CrashEntry>();
                return JsonSerializer.Deserialize<List<CrashEntry>>(File.ReadAllText(CrashHistoryPath))
                       ?? new List<CrashEntry>();
            }
            catch
            {
                return new List<CrashEntry>();
            }
        }

        /// <summary>
        /// 启动时发现上次没干净退出：把当时的运行标记记进历史。
        /// 只记录不判断原因 —— 强杀、断电、崩溃都会留下标记，文案不能说成「崩溃」。
        /// </summary>
        public static void RecordUncleanStart(string? markerText)
        {
            try
            {
                List<CrashEntry> history = ReadCrashHistory();
                history.Add(new CrashEntry
                {
                    AtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Version = SafeVersion(),
                    Marker = Trim(markerText, 400)
                });

                if (history.Count > MaxCrashHistory)
                {
                    history.RemoveRange(0, history.Count - MaxCrashHistory);
                }

                WriteAtomic(CrashHistoryPath, JsonSerializer.Serialize(history, PrettyJson));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("CrashReportService.RecordUncleanStart", caught);
            }
        }

        /// <summary>
        /// 导出一份诊断报告，返回落盘路径。失败时抛异常，由调用方转成网页能看懂的提示。
        /// </summary>
        /// <param name="trigger">谁触发的（写进报告，便于分辨是用户点的还是自动导出的）。</param>
        /// <param name="extra">宿主额外补充的现场信息（输出模式、当前曲目之类）。</param>
        public static string Export(string trigger, IReadOnlyList<string>? extra)
        {
            string id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                        + "-" + Random.Shared.Next(0x1000, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
            string text = Build(id, trigger, extra);

            Directory.CreateDirectory(DiagnosticsDirectory);
            string final = Path.Combine(DiagnosticsDirectory, "celeste-diagnostics-" + id + ".txt");
            string temp = final + ".tmp";

            // 先写临时文件再替换：中途断电也不会留下半份报告被当成完整的。
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, final, overwrite: true);

            PruneOldReports();
            StartupLog.Write("[diagnostics] 已导出诊断报告：" + final);
            return final;
        }

        /// <summary>给界面看的一行状态（关于页显示「上次退出：正常 / 上次未正常退出」）。</summary>
        public static Dictionary<string, object?> Snapshot()
        {
            List<CrashEntry> history = ReadCrashHistory();
            string? marker = ReadMarkerView();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unclean"] = marker != null,
                ["marker"] = marker ?? string.Empty,
                ["crashes"] = history.Count,
                ["lastCrashUtc"] = history.Count > 0 ? history[^1].AtUtc : string.Empty,
                ["lastCrashVersion"] = history.Count > 0 ? history[^1].Version : string.Empty,
                ["diagnosticsDirectory"] = DiagnosticsDirectory,
                ["appVersion"] = SafeVersion()
            };
        }

        // —— 组装 ——

        private static string Build(string id, string trigger, IReadOnlyList<string>? extra)
        {
            var builder = new StringBuilder(16 * 1024);

            builder.AppendLine("CelesteMusicPlayer 诊断报告");
            builder.AppendLine("========================================");
            builder.AppendLine("报告编号：" + id);
            builder.AppendLine("生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            builder.AppendLine("触发方式：" + (string.IsNullOrWhiteSpace(trigger) ? "未注明" : trigger));
            builder.AppendLine();

            AppendEnvironment(builder);
            AppendSession(builder);
            AppendExtra(builder, extra);
            AppendSettings(builder);
            AppendLogs(builder);

            builder.AppendLine();
            builder.AppendLine("—— 说明 ——");
            builder.AppendLine("本文件只存在于本机，程序不会自动上传。里面的密码、令牌、密钥已替换为「已隐藏」，");
            builder.AppendLine("家目录已替换为 %USERPROFILE%。分享前可以自己再检查一遍。");
            return builder.ToString();
        }

        private static void AppendEnvironment(StringBuilder builder)
        {
            builder.AppendLine("—— 版本与环境 ——");
            builder.AppendLine("应用版本：" + SafeVersion());
            builder.AppendLine("构建类型：" + (IsSelfContained() ? "自包含版" : "框架依赖版"));
            builder.AppendLine(".NET 运行时：" + Environment.Version.ToString());
            builder.AppendLine("运行时描述：" + RuntimeInformation.FrameworkDescription);
            builder.AppendLine("进程架构：" + RuntimeInformation.ProcessArchitecture);
            builder.AppendLine("系统：" + RuntimeInformation.OSDescription);
            builder.AppendLine("系统架构：" + RuntimeInformation.OSArchitecture);
            builder.AppendLine("WebView2 运行时：" + SafeWebViewVersion());
            builder.AppendLine("处理器数：" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine();
        }

        private static void AppendSession(StringBuilder builder)
        {
            builder.AppendLine("—— 上次退出 ——");
            string? marker = ReadMarkerView();
            builder.AppendLine(marker == null
                ? "上次正常退出（没有残留运行标记）。"
                : "上次未正常退出（残留运行标记：" + Trim(marker, 300) + "）。");
            builder.AppendLine("注意：强杀、断电、崩溃都会留下标记，这一条不能证明发生了崩溃。");

            List<CrashEntry> history = ReadCrashHistory();
            builder.AppendLine("未正常退出累计：" + history.Count.ToString(CultureInfo.InvariantCulture)
                               + " 次（最多保留 " + MaxCrashHistory + " 条）");
            if (history.Count > 0)
            {
                int from = Math.Max(0, history.Count - 5);
                for (int index = from; index < history.Count; index++)
                {
                    CrashEntry entry = history[index];
                    builder.AppendLine("  · " + entry.AtUtc + "  版本 " + entry.Version
                                       + (string.IsNullOrEmpty(entry.Marker) ? string.Empty : "  标记 " + Trim(entry.Marker, 120)));
                }
            }

            builder.AppendLine();
        }

        private static void AppendExtra(StringBuilder builder, IReadOnlyList<string>? extra)
        {
            if (extra == null || extra.Count == 0) return;
            builder.AppendLine("—— 运行现场 ——");
            foreach (string line in extra)
            {
                if (!string.IsNullOrWhiteSpace(line)) builder.AppendLine(line);
            }

            builder.AppendLine();
        }

        private static void AppendSettings(StringBuilder builder)
        {
            builder.AppendLine("—— 设置（已脱敏）——");
            try
            {
                string path = AppSettingsStore.GetSettingsFilePath();
                if (!File.Exists(path))
                {
                    builder.AppendLine("设置文件不存在：" + RedactPath(path));
                }
                else
                {
                    string raw = File.ReadAllText(path);
                    string redacted = RedactSettings(raw);
                    if (redacted.Length > MaxSettingsChars)
                    {
                        redacted = redacted[..MaxSettingsChars] + Environment.NewLine + "…（已截断）";
                    }

                    builder.AppendLine(redacted);
                }
            }
            catch (Exception caught)
            {
                builder.AppendLine("读取设置失败：" + caught.GetType().Name + " " + caught.Message);
            }

            builder.AppendLine();
        }

        private static void AppendLogs(StringBuilder builder)
        {
            builder.AppendLine("—— 日志（每个文件最后 " + MaxLogLinesPerFile + " 行）——");
            string logsRoot = Path.Combine(DataRoot, "logs");
            if (!Directory.Exists(logsRoot))
            {
                builder.AppendLine("日志目录不存在：" + RedactPath(logsRoot));
                return;
            }

            int budget = MaxLogCharsTotal;
            List<string> files;
            try
            {
                files = new List<string>(Directory.EnumerateFiles(logsRoot, "*.log", SearchOption.AllDirectories));
            }
            catch (Exception caught)
            {
                builder.AppendLine("枚举日志失败：" + caught.Message);
                return;
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (budget <= 0)
                {
                    builder.AppendLine("…（日志总量已达上限，其余文件省略）");
                    break;
                }

                try
                {
                    var tail = new List<string>(TailLines(file, MaxLogLinesPerFile));
                    if (tail.Count == 0) continue;

                    builder.AppendLine();
                    builder.AppendLine("=== " + Path.GetRelativePath(logsRoot, file) + " ===");
                    foreach (string line in tail)
                    {
                        if (budget <= 0) break;
                        string cleaned = RedactPath(line);
                        builder.AppendLine(cleaned);
                        budget -= cleaned.Length + 2;
                    }
                }
                catch (Exception caught)
                {
                    builder.AppendLine("读日志失败（" + Path.GetFileName(file) + "）：" + caught.Message);
                }
            }
        }

        /// <summary>只取文件尾部若干行：日志可能有几十万行，整份读进来没有必要。</summary>
        private static IEnumerable<string> TailLines(string path, int count)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var ring = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (ring.Count == count) ring.Dequeue();
                ring.Enqueue(line);
            }

            return ring;
        }

        // —— 脱敏 ——

        private static readonly string[] SensitiveKeys =
        {
            "password", "passwd", "pwd", "token", "secret", "apikey", "api_key",
            "cookie", "credential", "sessionkey", "session_key", "authtoken", "auth_token"
        };

        /// <summary>把 JSON 里敏感字段的值换掉；解析不了就退回纯文本脱敏（不留原始内容）。</summary>
        private static string RedactSettings(string raw)
        {
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject root)
                {
                    return RedactPath(raw);
                }

                RedactNode(root);
                return RedactPath(root.ToJsonString(PrettyJson));
            }
            catch
            {
                return "（设置文件不是合法 JSON，已跳过内容，避免误把敏感信息带出来）";
            }
        }

        private static void RedactNode(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (string key in new List<string>(obj.Select(pair => pair.Key)))
                    {
                        if (IsSensitive(key))
                        {
                            obj[key] = "已隐藏";
                            continue;
                        }

                        if (obj[key] is JsonNode child) RedactNode(child);
                    }

                    break;

                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        if (item != null) RedactNode(item);
                    }

                    break;
            }
        }

        private static bool IsSensitive(string key)
        {
            string lower = key.ToLowerInvariant();
            foreach (string word in SensitiveKeys)
            {
                if (lower.Contains(word, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>把家目录换成 %USERPROFILE%，别把用户名带出去。</summary>
        private static string RedactPath(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string text2 = text;
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    text2 = text2.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                    string alt = home.Replace('\\', '/');
                    text2 = text2.Replace(alt, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 拿不到家目录就原样返回，别为了脱敏把报告搞坏
            }

            return text2;
        }

        private static string Trim(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value[..max] + "…";
        }

        private static string SafeVersion()
        {
            try
            {
                return UpdateChecker.CurrentVersionText();
            }
            catch
            {
                return "未知";
            }
        }

        private static string SafeWebViewVersion()
        {
            try
            {
                var type = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core");
                var method = type?.GetMethod("GetAvailableBrowserVersionString", new[] { typeof(string) });
                string? version = method?.Invoke(null, new object?[] { null }) as string;
                return string.IsNullOrWhiteSpace(version) ? "未安装（或读取失败）" : version;
            }
            catch (Exception caught)
            {
                return "读取失败：" + caught.GetType().Name;
            }
        }

        private static bool IsSelfContained()
        {
            try
            {
                return !string.IsNullOrEmpty(AppContext.BaseDirectory)
                       && File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));
            }
            catch
            {
                return false;
            }
        }

        private static void PruneOldReports()
        {
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(DiagnosticsDirectory).GetFiles("celeste-diagnostics-*.txt"));
                if (files.Count <= KeepReports) return;

                files.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
                for (int index = KeepReports; index < files.Count; index++)
                {
                    try { files[index].Delete(); } catch { /* 删不掉就留着，不影响导出 */ }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("CrashReportService.Prune", caught);
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        public sealed class CrashEntry
        {
            public string AtUtc { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Marker { get; set; } = string.Empty;
        }
    }
}
