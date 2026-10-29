using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 设置页里几组**不依赖窗口、不依赖播放**的操作：缓存统计与清理、文件关联、
    /// 清「最近播放」时间标记、关于信息。
    ///
    /// W13：从 MainWindow.WebSettings.cs 搬出来。它们本来就是静态方法，
    /// 搬出来之后设置页的分发器只负责转发，不再把这几坨逻辑挂在主窗口上。
    /// </summary>
    internal static class WebSettingsMisc
    {
        internal static Task<object> CacheOperation(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            string kind = ReadString(msg, "kind");
            if (action == "stats") return Task.FromResult<object>(BuildCacheStats());

            if (action == "clear")
            {
                string result = kind switch
                {
                    "transcode" => ClearTranscode(),
                    "webdav" => ClearWebDavDownloadCache(),
                    "dsd" => ClearDsdPreload(),
                    _ => throw new ArgumentException("未知的缓存类型：" + kind)
                };
                return Task.FromResult<object>(new { cleared = result, stats = BuildCacheStats() });
            }

            throw new ArgumentException("未知的缓存操作。");
        }

        private static object BuildCacheStats()
        {
            (long transcodeBytes, int transcodeCount) = FfmpegDecoderBackend.GetTranscodeCacheUsage();
            (long dsdBytes, int dsdFiles) = DsdPreloadService.Stat();
            long webDavBytes = 0; int webDavFiles = 0;
            try
            {
                WebDavLocation location = WebDavLocation.FromSettings(AppSettingsStore.Load());
                if (location.IsConfigured)
                {
                    (long bytes, int count) = WebDavCache.GetUsage(location);
                    webDavBytes = bytes; webDavFiles = count;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.WebSettings.cs", caught); }

            AppSettingsState state = AppSettingsStore.Load();
            return new
            {
                transcode = new { bytes = transcodeBytes, count = transcodeCount, limitMb = state.TranscodeCacheLimitMb, dir = FfmpegDecoderBackend.TranscodeCacheDir, size = WebDavCache.FormatSize(transcodeBytes) },
                webdav = new { bytes = webDavBytes, count = webDavFiles, limitMb = state.WebDavCacheLimitMb, dir = ResolveWebDavCacheDir(state), size = WebDavCache.FormatSize(webDavBytes) },
                dsd = new { bytes = dsdBytes, count = dsdFiles, dir = DsdPreloadService.CacheRoot, size = WebDavCache.FormatSize(dsdBytes), defaultDir = DsdPreloadService.DefaultCacheRoot }
            };
        }

        private static string ResolveWebDavCacheDir(AppSettingsState state)
        {
            WebDavLocation location = WebDavLocation.FromSettings(state);
            return location.IsConfigured ? WebDavCache.GetRoot(location) : WebDavCache.DefaultRoot;
        }

        private static string ClearTranscode()
        {
            (int count, long bytes) = FfmpegDecoderBackend.ClearTranscodeCache();
            return $"已清理 {count} 个文件，释放 {WebDavCache.FormatSize(bytes)}";
        }

        private static string ClearWebDavDownloadCache()
        {
            WebDavLocation location = WebDavLocation.FromSettings(AppSettingsStore.Load());
            if (!location.IsConfigured) return "还没有配置网络音乐库，没有可清理的下载缓存。";
            int count = WebDavCache.Clear(location);
            return $"已清理 {count} 个缓存文件（连接本身保留）";
        }

        private static string ClearDsdPreload()
        {
            int count = DsdPreloadService.ClearAll();
            return $"已清理 {count} 个 DSD 预载缓存文件";
        }

        internal static object FileAssoc(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (action == "get")
            {
                List<string> associated = FileAssociationHelper.GetAssociatedExtensions();
                return new
                {
                    registered = associated.ToArray(),
                    recommended = FileAssociationHelper.RecommendedExtensions,
                    executable = FileAssociationHelper.GetRegisteredExecutable() ?? "",
                    currentExe = Environment.ProcessPath ?? ""
                };
            }

            string executePath = Environment.ProcessPath ?? "";
            if (executePath.Length == 0) throw new InvalidOperationException("取不到当前程序路径，无法修改文件关联。");

            if (action == "register")
            {
                var picked = ReadExtensionList(msg);
                FileAssociationHelper.Register(executePath, picked.Count == 0 ? null : picked);
            }
            else if (action == "unregister")
            {
                FileAssociationHelper.Unregister();
            }
            else throw new ArgumentException("未知的文件关联操作。");

            return new { registered = FileAssociationHelper.GetAssociatedExtensions().ToArray() };
        }

        private static List<string> ReadExtensionList(JsonElement msg)
        {
            var list = new List<string>();
            if (msg.TryGetProperty("extensions", out JsonElement element) && element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray().Take(200))
                {
                    string? value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(value)) list.Add(value!.Trim().TrimStart('.').ToLowerInvariant());
                }
            }
            return list;
        }

        internal static object ClearRecentlyPlayed()
        {
            TrackStatsStore.ClearRecentlyPlayed();
            return new
            {
                message = "已清掉「最近播放」的时间标记。"
                          + "这首歌的播放次数、评分、收藏都没动；播放历史流水也还在。"
            };
        }

        internal static object BuildAbout() => new
        {
            version = UpdateChecker.CurrentVersionText(),
            variant = UpdateChecker.InstalledVariant(),
            configDirectory = AppSettingsStore.GetConfigDirectory(),
            settingsFile = AppSettingsStore.GetSettingsFilePath(),
            recovered = AppSettingsStore.SettingsWereRecovered
        };

        private static string ReadString(JsonElement msg, string property)
        {
            if (msg.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            return "";
        }
    }
}
