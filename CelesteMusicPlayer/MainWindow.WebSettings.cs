using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页设置页的桥接层。
    ///
    /// 只做三件事：按白名单读写设置、把改动接到真实服务上、把命令类操作（选目录、清缓存、
    /// 测试连接、导入导出）调用现有服务对象完成。业务逻辑一律复用原生设置已经验证过的那条路，
    /// 不在这里重写一个新分支 —— 否则网页和旧界面会各自漂移。
    ///
    /// 每个字段 Huewei能不能改、改了什么时候生效，写在 <see cref="WebSettingsSchema"/> 的 note / scope 里，
    /// 网页原样展示；这里不替修改安排"看起来生效"的假动作。
    /// </summary>
    public sealed partial class MainWindow
    {
        private static long _settingsRevision;
        private static bool _settingsRevisionHooked;

        /// <summary>设置修订号：任何入口改了设置都会 +1，网页用它判断自己手上的快照是不是过期了。</summary>
        private static void EnsureSettingsRevisionHook()
        {
            if (_settingsRevisionHooked) return;
            _settingsRevisionHooked = true;
            AppSettingsStore.Changed += () => Interlocked.Increment(ref _settingsRevision);
        }

        internal async Task<object> WebSettingsAsync(JsonElement msg, Microsoft.UI.Xaml.Window host)
        {
            EnsureSettingsRevisionHook();
            string operation = ReadString(msg, "operation");
            switch (operation)
            {
                case "get": return BuildSettingsSnapshot();
                case "patch": return await PatchSettingsAsync(msg).ConfigureAwait(true);
                case "pickFolder": return new { canceled = false, path = await PickFolderAsync(host).ConfigureAwait(true) };
                case "pickFile": return new { canceled = false, path = await PickFileAsync(host).ConfigureAwait(true) };
                case "cache": return await CacheOperationAsync(msg).ConfigureAwait(true);
                case "webdav": return await WebDavSettingsAsync(msg).ConfigureAwait(true);
                case "credentials": return await CredentialsAsync(msg).ConfigureAwait(true);
                case "fileAssoc": return FileAssocOperation(msg);
                case "hotkeys": return HotkeysOperation(msg);
                case "folders": return await WatchFoldersAsync(msg, host).ConfigureAwait(true);
                case "clearRecent": return ClearRecentlyPlayed();
                case "export": return await ExportPreferencesAsync(host).ConfigureAwait(true);
                case "import": return await ImportPreferencesAsync(host).ConfigureAwait(true);
                case "reset": return await ResetGroupAsync(msg).ConfigureAwait(true);
                case "about": return BuildAbout();
                case "update": return await WebUpdateAsync(msg).ConfigureAwait(true);
                default: throw new ArgumentException("未知的设置操作：" + operation);
            }
        }

        // —— get ——

        private object BuildSettingsSnapshot()
        {
            AppSettingsState state = AppSettingsStore.Load();
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Func<AppSettingsState, object?>> reader in WebSettingsSchema.Readers)
                values[reader.Key] = reader.Value(state);

            LastFmCredentials credentials = LastFmScrobbler.LoadCredentials();

            return new
            {
                revision = Interlocked.Read(ref _settingsRevision),
                groups = WebSettingsSchema.Groups,
                defs = BuildDefs(),
                values,
                // 凭据只回「有没有」，不回原文：读不到明文，也就没法被快照意外带走。
                accounts = new
                {
                    lastfm = new
                    {
                        configured = LastFmScrobbler.IsConfigured(),
                        username = credentials.Username,
                        hasSession = !string.IsNullOrWhiteSpace(credentials.SessionKey)
                    },
                    lastfmApiKey = !string.IsNullOrWhiteSpace(state.LastFmApiKey),
                    discogsToken = !string.IsNullOrWhiteSpace(state.DiscogsToken),
                    discogsConsumerKey = !string.IsNullOrWhiteSpace(state.DiscogsConsumerKey),
                    discogsConsumerSecret = !string.IsNullOrWhiteSpace(state.DiscogsConsumerSecret),
                    fanartTvApiKey = !string.IsNullOrWhiteSpace(state.FanartTvApiKey),
                    webdavPassword = !string.IsNullOrWhiteSpace(state.WebDavPassword)
                },
                hotkeys = BuildHotkeys(),
                folderCount = new { watch = state.LibraryWatchFolders.Count, manual = state.ManualLibraryFiles.Count },
                about = BuildAbout()
            };
        }

        /// <summary>主题文件列表要运行时读，塞不进静态表，这里补做成候选值。</summary>
        private static IEnumerable<object> BuildDefs()
        {
                string[] themes = new[] { "" };
                try
                {
                    themes = themes.Concat(CelesteThemeStore.ListThemes()).ToArray();
                }
                catch (Exception caught) { StartupLog.WriteException("MainWindow.WebSettings.cs", caught); }

            foreach (WebSettingsSchema.Def def in WebSettingsSchema.Defs)
            {
                object? options = def.options;
                if (def.id == "CelesteThemeName")
                    options = themes.Select(name => name.Length == 0 ? "|内置默认配色" : name + "|" + name).ToArray();
                yield return new
                {
                    id = def.id, group = def.group, label = def.label, kind = def.kind, note = def.note,
                    scope = def.scope, options, min = def.min, max = def.max, step = def.step,
                    unit = def.unit, advanced = def.advanced, readOnly = IsReadOnly(def.id)
                };
            }
        }

        /// <summary>这些项的后端入口在别处（底栏音频面板/设备切换），这里只给读，避免两处能写同一件事。</summary>
        private static bool IsReadOnly(string id) => id == "OutputMode";

        // —— patch ——

        private async Task<object> PatchSettingsAsync(JsonElement msg)
        {
            if (!msg.TryGetProperty("changes", out JsonElement changes) || changes.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("缺少要修改的字段。");

            long clientRevision = 0;
            if (msg.TryGetProperty("revision", out JsonElement revisionElement) && revisionElement.ValueKind == JsonValueKind.Number)
                clientRevision = (long)revisionElement.GetDouble();

            // 先把要改的东西摊平，别把 JsonElement 带进 Task / UI 线程。
            var requested = new List<KeyValuePair<string, JsonElement>>();
            foreach (JsonProperty property in changes.EnumerateObject())
            {
                if (!WebSettingsSchema.Writers.ContainsKey(property.Name))
                    throw new ArgumentException("这个字段不支持从这里修改：" + property.Name);
                if (IsReadOnly(property.Name))
                    throw new ArgumentException("此项需要在底栏「音频设置」面板里切换，以免两处同时写同一件事。");
                requested.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value));
            }

            if (requested.Count == 0) throw new ArgumentException("没有需要保存的改动。");
            if (requested.Count > 200) throw new ArgumentException("一次提交的字段过多。");

            AppSettingsState state = AppSettingsStore.Load();
            lock (_settingsWriteGate)
            {
                long server = Interlocked.Read(ref _settingsRevision);
                if (clientRevision != 0 && clientRevision != server)
                {
                    // 别人改过设置（旧界面 / 另一个窗口）：把最新值整包发回去，不能让过期表单覆盖新数据。
                    return new
                    {
                        conflict = true,
                        message = "设置已经在别处被改过，页面显示的是最新值，请确认后重新提交。",
                        revision = server,
                        values = SnapshotOf(WebSettingsSchema.Readers.Keys.ToArray())
                    };
                }

                foreach (KeyValuePair<string, JsonElement> pair in requested)
                    WebSettingsSchema.Writers[pair.Key](state, pair.Value);

                AppSettingsStore.Save(state);
            }

            string[] ids = requested.Select(pair => pair.Key).ToArray();
            await ApplyWebSettingsSideEffectsAsync(ids).ConfigureAwait(true);

            return new
            {
                conflict = false,
                revision = Interlocked.Read(ref _settingsRevision),
                values = SnapshotOf(ids),
                note = BuildEffectNote(ids)
            };
        }

        private static readonly object _settingsWriteGate = new();

        private Dictionary<string, object?> SnapshotOf(IEnumerable<string> ids)
        {
            AppSettingsState state = AppSettingsStore.Load();
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (WebSettingsSchema.Readers.TryGetValue(id, out Func<AppSettingsState, object?>? reader))
                    result[id] = reader(state);
            }
            return result;
        }

        /// <summary>把改动接到真实服务上。没有对应服务的字段就只在 note 里说明，不做假动作。</summary>
        private async Task ApplyWebSettingsSideEffectsAsync(string[] ids)
        {
            AppSettingsState state = AppSettingsStore.Load();
            if (ids.Contains("AutoRun")) AutoRunHelper.Apply(state.AutoRun);

            await RunOnUiAsync(() =>
            {
                try
                {
                    if (ids.Contains("AlwaysOnTop")) ApplyAlwaysOnTopFromSettings();
                    if (ids.Contains("EnableGlobalHotkeys") || ids.Contains("CustomHotkeys")) RestartGlobalHotkeysFromSettings();
                    if (ids.Contains("EnableSmtc")) ConfigureSmtcFromSettings();
                    if (ids.Contains("AutoUpdateLibrary")) RestartLibraryWatchFromSettings();
                    if (ids.Contains("OpenDesktopLyricsOnStartup")) ApplyOverlayPreferenceFromSettings(state);

                    if (ids.Any(id => id.StartsWith("DesktopLyric", StringComparison.Ordinal)))
                        _desktopLyricsWindow?.ApplySettings(state);

                    if (ids.Contains("HiFiSoftwareVolume") && _audioEngine != null)
                        _audioEngine.SoftwareVolumeEnabled = state.HiFiSoftwareVolume;

                    if (ids.Contains("PlaybackRate") || ids.Contains("PlaybackRatePreservePitch"))
                        _audioEngine?.SetPlaybackSpeed(state.PlaybackRate, state.PlaybackRatePreservePitch);

                    if (ids.Contains("WebDavUrl") || ids.Contains("WebDavDisplayName")) RefreshWebDavNavEntry();

                    // 外观/列表相关：走原生那套「设置页变更后即时生效」，不另写一套应用逻辑
                    if (ids.Any(id => id is "UiStyleMode" or "CelesteThemeName" or "AccentSource" or "CustomAccentColor"
                                          or "EnableFrostedGlass" or "EnableBackground" or "AlbumCoverAsBackground"
                                          or "BackgroundGaussBlur" or "GaussBlurRadius" or "BackgroundPreset"
                                          or "BackgroundPresetMotion" or "CustomBackgroundPath" or "NavEmojiIcons"
                                          or "NowPlayingLayout" or "ShowNavFavorites" or "ShowNavRecent"
                                          or "ShowNavGenre" or "ShowNavYear"))
                        ApplySettingsLive(state);

                    if (ids.Any(id => id.StartsWith("ShowPlaylist", StringComparison.Ordinal) || id == "PlaylistDensity"))
                        SyncHeaderColumnsFromState();
                }
                catch (Exception caught) { StartupLog.WriteException("MainWindow.WebSettings.cs", caught); }
            }).ConfigureAwait(true);
        }

        /// <summary>把一段 UI 线程动作包装成可观await的任务。WinAppSDK 的扩展方法靠不住时这里是可回滚点。</summary>
        private Task RunOnUiAsync(Action action)
        {
            var completion = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(() =>
            {
                try { action(); completion.TrySetResult(); }
                catch (Exception caught) { completion.TrySetException(caught); }
            });
            return completion.Task;
        }

        /// <summary>保存完告诉用户「什么时候生效」，而不是笼统说「已保存」。</summary>
        private static string BuildEffectNote(string[] ids)
        {
            if (ids.Contains("OutputBufferMs") || ids.Contains("AsioFeederEnabled") || ids.Contains("DsdPreloadEnabled"))
                return "已保存。缓冲/喂料/预载这一类要下一首歌或重新起播才生效。";
            if (ids.Contains("AutoRun"))
                return "已保存，并且已经同步了系统的启动项。";
            if (ids.Contains("AutoUpdateLibrary"))
                return "已保存，目录监视服务已重启。";
            if (ids.Contains("EnableGlobalHotkeys"))
                return "已保存，热键服务已重启（能不能真正抢到组合键还取决于有没有别的软件占用）。";
            return "已保存。";
        }

        // —— 目录 / 文件选择 ——

        private static async Task<string> PickFolderAsync(Microsoft.UI.Xaml.Window host)
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(host);
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path ?? "";
        }

        private static async Task<string> PickFileAsync(Microsoft.UI.Xaml.Window host)
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(host);
            var picker = new FileOpenPicker();
            foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSingleFileAsync();
            return file?.Path ?? "";
        }

        // —— 缓存 ——

        private static Task<object> CacheOperationAsync(JsonElement msg)
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

        // —— WebDAV ——

        private static async Task<object> WebDavSettingsAsync(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (action != "test") throw new ArgumentException("未知的网络音乐库操作。");

            AppSettingsState state = AppSettingsStore.Load();
            WebDavLocation location = WebDavLocation.FromSettings(state);
            if (!location.IsConfigured)
                return new { ok = false, headline = "还没填服务器地址", detail = "先填地址并保存，再回来测试。" };

            try
            {
                using WebDavClient client = location.CreateClient();
                WebDavTestResult result = await client.TestConnectionAsync().ConfigureAwait(true);
                return new
                {
                    ok = result.Ok,
                    headline = result.Headline,
                    detail = result.Detail,
                    items = result.Items.Take(20).Select(entry => new { entry.Name, entry.IsFolder }).ToArray()
                };
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebSettings.cs", caught);
                return new { ok = false, headline = "连接失败", detail = caught.Message };
            }
        }

        // —— 凭据（明确写入；空表示保留，__clear__ 表示清除）——

        private static Task<object> CredentialsAsync(JsonElement msg)
        {
            string service = ReadString(msg, "service");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (msg.TryGetProperty("fields", out JsonElement container) && container.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in container.EnumerateObject())
                    fields[property.Name] = property.Value.ValueKind == JsonValueKind.String ? (property.Value.GetString() ?? "").Trim() : "";
            }

            switch (service)
            {
                case "lastfm": return Task.FromResult(SaveLastFmCredentials(fields));
                case "discogs": return Task.FromResult(SaveDiscogsCredentials(fields));
                case "fanart": return Task.FromResult(SaveFanartCredential(fields));
                case "webdav": return Task.FromResult(SaveWebDavPassword(fields));
                default: throw new ArgumentException("未知的凭据服务：" + service);
            }
        }

        /// <summary>空=保留原来的值（避免一次误提交把账号抹掉）；__clear__=明确清除。</summary>
        private static string Secret(Dictionary<string, string> fields, string key, string current, int maxLength = 512)
        {
            if (!fields.TryGetValue(key, out string value)) return current;
            if (value.Length == 0) return current;
            if (string.Equals(value, "__clear__", StringComparison.Ordinal)) return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static object SaveLastFmCredentials(Dictionary<string, string> fields)
        {
            LastFmCredentials credentials = LastFmScrobbler.LoadCredentials();
            AppSettingsState state = AppSettingsStore.Load();

            string apiKey = Secret(fields, "apiKey", credentials.ApiKey.Length > 0 ? credentials.ApiKey : state.LastFmApiKey);
            string sharedSecret = Secret(fields, "sharedSecret", credentials.SharedSecret);

            LastFmScrobbler.SaveCredentials(new LastFmCredentials
            {
                ApiKey = apiKey,
                SharedSecret = sharedSecret,
                SessionKey = credentials.SessionKey,
                Username = credentials.Username
            });
            // 在线标签/元数据查询读的是 AppSettings 里那份，两边必须一致，否则"收听能提交、标签查不了"。
            AppSettingsStore.Update(s => s.LastFmApiKey = apiKey);

            return new
            {
                configured = LastFmScrobbler.IsConfigured(),
                hasApiKey = apiKey.Length > 0,
                username = credentials.Username,
                hasSession = credentials.SessionKey.Length > 0,
                message = credentials.SessionKey.Length > 0
                    ? "已保存。Last.fm 网页授权会话还在，可以直接提交收听记录。"
                    : "已保存密钥。还没有授权会话，需要在旧界面的 Last.fm 面板里完成一次网页授权。"
            };
        }

        private static object SaveDiscogsCredentials(Dictionary<string, string> fields)
        {
            AppSettingsState state = AppSettingsStore.Load();
            AppSettingsStore.Update(s =>
            {
                s.DiscogsToken = Secret(fields, "token", state.DiscogsToken, 256);
                s.DiscogsConsumerKey = Secret(fields, "consumerKey", state.DiscogsConsumerKey, 256);
                s.DiscogsConsumerSecret = Secret(fields, "consumerSecret", state.DiscogsConsumerSecret, 256);
            });

            AppSettingsState after = AppSettingsStore.Load();
            return new
            {
                hasToken = after.DiscogsToken.Length > 0,
                hasConsumer = after.DiscogsConsumerKey.Length > 0 && after.DiscogsConsumerSecret.Length > 0,
                message = after.DiscogsConsumerKey.Length > 0 && after.DiscogsConsumerSecret.Length > 0
                    ? "已保存。Consumer Key/Secret 配好了，标签补全可以走 Discogs。"
                    : "已保存 Token（在线搜索/封面用）。标签补全读的是 Consumer Key/Secret，还没配。"
            };
        }

        private static object SaveFanartCredential(Dictionary<string, string> fields)
        {
            AppSettingsState state = AppSettingsStore.Load();
            AppSettingsStore.Update(s => s.FanartTvApiKey = Secret(fields, "apiKey", state.FanartTvApiKey, 256));
            return new
            {
                configured = AppSettingsStore.Load().FanartTvApiKey.Length > 0,
                message = AppSettingsStore.Load().FanartTvApiKey.Length > 0 ? "已保存 fanart.tv API key。" : "已清除 fanart.tv API key。"
            };
        }

        private static object SaveWebDavPassword(Dictionary<string, string> fields)
        {
            AppSettingsState state = AppSettingsStore.Load();
            AppSettingsStore.Update(s => s.WebDavPassword = Secret(fields, "password", state.WebDavPassword, 512));
            return new
            {
                configured = AppSettingsStore.Load().WebDavPassword.Length > 0,
                message = "已保存密码（落盘时走系统加密，快照里不会回传明文）。"
            };
        }

        // —— 文件关联 ——

        private static object FileAssocOperation(JsonElement msg)
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

        // —— 全局热键 ——

        private object HotkeysOperation(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (action == "get") return BuildHotkeys();

            if (action == "reset")
            {
                AppSettingsStore.Update(state => state.CustomHotkeys.Clear());
                DispatcherQueue.TryEnqueue(RestartGlobalHotkeysFromSettings);
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
            DispatcherQueue.TryEnqueue(RestartGlobalHotkeysFromSettings);
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

        // —— 监视目录 ——

        private async Task<object> WatchFoldersAsync(JsonElement msg, Microsoft.UI.Xaml.Window host)
        {
            string action = ReadString(msg, "action");
            AppSettingsState state = AppSettingsStore.Load();

            if (action == "list")
                return BuildFolderList(state);

            if (action == "add")
            {
                string                 path = await PickFolderAsync(host).ConfigureAwait(true);
                if (path.Length == 0) return BuildFolderList(state);
                AppSettingsStore.Update(s =>
                {
                    s.LibraryWatchFolders.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
                    s.LibraryWatchFolders.Add(path);
                });
                await RunOnUiAsync(RestartLibraryWatchFromSettings).ConfigureAwait(true);
                return BuildFolderList(AppSettingsStore.Load());
            }

            if (action == "remove")
            {
                string path = ReadString(msg, "path");
                AppSettingsStore.Update(s => s.LibraryWatchFolders.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)));
                await RunOnUiAsync(RestartLibraryWatchFromSettings).ConfigureAwait(true);
                return BuildFolderList(AppSettingsStore.Load());
            }

            throw new ArgumentException("未知的目录操作。");
        }

        private object BuildFolderList(AppSettingsState state) => new
        {
            folders = state.LibraryWatchFolders.Select(path => new
            {
                path,
                exists = Directory.Exists(path),
                tracks = CountLibraryFilesIn(path)
            }).ToArray(),
            manualCount = state.ManualLibraryFiles.Count
        };

        /// <summary>给「这个目录当前贡献了多少首歌」一个粗略数字；取不到就返回 -1，不猜。</summary>
        private int CountLibraryFilesIn(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return -1;
                string prefix = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return LibraryTracks.Count(item => item.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.WebSettings.cs", caught); return -1; }
        }

        // —— 最近播放 / 备份 / 重置 ——

        private static object ClearRecentlyPlayed()
        {
            TrackStatsStore.ClearRecentlyPlayed();
            return new
            {
                message = "已清掉「最近播放」的时间标记。"
                          + "这首歌的播放次数、评分、收藏都没动；播放历史流水也还在。"
            };
        }

        private async Task<object> ExportPreferencesAsync(Microsoft.UI.Xaml.Window host)
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(host);
            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("设置备份", new List<string> { ".json" });
            picker.SuggestedFileName = "celeste-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return new { canceled = true };

            bool ok = AppSettingsStore.ExportTo(file.Path);
            return new
            {
                canceled = false, ok, path = file.Path,
                message = ok
                    ? "已导出应用偏好。里面只有主设置文件，不含主题、DSP 方案、Last.fm 账号和曲库。"
                    : "导出失败，可能是目标位置没有写权限。"
            };
        }

        private async Task<object> ImportPreferencesAsync(Microsoft.UI.Xaml.Window host)
        {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(host);
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return new { canceled = true };

            // 导入前先确认这是个能解析的设置文件，别把坏文件直接盖上去
            string text = await File.ReadAllTextAsync(file.Path).ConfigureAwait(true);
            AppSettingsState? parsed;
            try { parsed = JsonSerializer.Deserialize<AppSettingsState>(text); }
            catch (JsonException caught) { return new { canceled = false, ok = false, message = "这个文件不是有效的设置备份：" + caught.Message }; }
            if (parsed == null) return new { canceled = false, ok = false, message = "这个文件不是有效的设置备份。" };

            bool ok = AppSettingsStore.ImportFrom(file.Path);
            string[] reload = WebSettingsSchema.Readers.Keys.ToArray();
            await ApplyWebSettingsSideEffectsAsync(reload).ConfigureAwait(true);
            return new
            {
                canceled = false, ok,
                message = ok
                    ? "已导入并应用。导入前自动备份了一份原设置。"
                    : "导入失败，当前设置没有被改动。",
                revision = Interlocked.Read(ref _settingsRevision),
                values = SnapshotOf(reload)
            };
        }

        private async Task<object> ResetGroupAsync(JsonElement msg)
        {
            string group = ReadString(msg, "group");
            if (!WebSettingsSchema.Groups.Any(candidate => candidate.id == group))
                throw new ArgumentException("未知的设置分组：" + group);

            var ids = WebSettingsSchema.Defs.Where(def => def.group == group && !IsReadOnly(def.id)).Select(def => def.id).ToArray();
            if (ids.Length == 0) throw new ArgumentException("这个分组没有可以恢复的项。");

            AppSettingsState defaults = new();
            AppSettingsStore.Update(state =>
            {
                foreach (string id in ids)
                {
                    if (!WebSettingsSchema.Readers.TryGetValue(id, out Func<AppSettingsState, object?>? reader)) continue;
                    object? value = reader(defaults);
                    using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
                    WebSettingsSchema.Writers[id](state, document.RootElement);
                }
            });

            await ApplyWebSettingsSideEffectsAsync(ids).ConfigureAwait(true);
            return new
            {
                message = $"已把「{WebSettingsSchema.Groups.First(candidate => candidate.id == group).label}」恢复默认，其它分组没有动。",
                revision = Interlocked.Read(ref _settingsRevision),
                values = SnapshotOf(ids)
            };
        }

        // —— 关于 ——

        private static object BuildAbout() => new
        {
            version = UpdateChecker.CurrentVersionText(),
            variant = UpdateChecker.InstalledVariant(),
            configDirectory = AppSettingsStore.GetConfigDirectory(),
            settingsFile = AppSettingsStore.GetSettingsFilePath(),
            recovered = AppSettingsStore.SettingsWereRecovered
        };

        private static async Task<object> CheckUpdateAsync()
        {
            // 逻辑已全部搬进 UpdateService（状态机 + 事务目录 + 校验）；这里保留一个薄壳，
            // 免得启动期自动检查和其它调用点各写一份。
            return BuildUpdateWire(await UpdateService.Shared.CheckAsync().ConfigureAwait(true));
        }
    }
}
