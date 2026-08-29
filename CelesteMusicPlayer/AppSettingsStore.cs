using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>应用偏好（对齐 MusicPlayer2：歌词 / 外观 / 常规 / 播放 / 媒体库 / 快捷键）。</summary>
    public sealed class AppSettingsState
    {
        // —— 常规 ——
        /// <summary>Ask / MinimizeToTray / Exit</summary>
        public string CloseAction { get; set; } = nameof(CloseWindowAction.Ask);

        public bool RestoreLibrary { get; set; } = true;

        public bool RestorePlayback { get; set; } = true;

        public bool AutoRun { get; set; }

        public bool EnableFrostedGlass { get; set; } = true;

        /// <summary>0–100</summary>
        public double Volume { get; set; } = 80;

        /// <summary>音频输出设备 ID（HiFi 设备切换）。空 = 系统默认。</summary>
        public string OutputDeviceId { get; set; } = string.Empty;

        /// <summary>HiFi 输出模式：Shared / WasapiExclusive / Asio。独占时所有曲目经 NAudio 输出。</summary>
        public string OutputMode { get; set; } = "Shared";


        /// <summary><see cref="PlaybackOrder"/> 名称</summary>
        public string PlaybackOrder { get; set; } = nameof(CelesteMusicPlayer.PlaybackOrder.ListLoop);

        /// <summary>交叉淡化时长（毫秒）。0 = 关闭（同格式无缝硬切，即加此功能前的原行为）。
        /// 仅对自动连续播放的自然换曲生效，手动切歌不淡化。</summary>
        public int CrossfadeMs { get; set; }

        /// <summary>采样率升频目标（Hz）。0 = 关闭（源采样率原样输出）。
        /// 仅 WASAPI 独占模式生效；设备不支持目标采样率时自动退回不升频。</summary>
        public int SrcTargetHz { get; set; }

        /// <summary>SRC 质量档位：lowlatency / balanced / transparent（默认 balanced）。</summary>
        public string SrcQuality { get; set; } = ResamplingSourceProvider.QualityBalanced;

        /// <summary>SRC 量化前抖动：off / tpdf / highpass / ns5（默认 off）。</summary>
        public string SrcDither { get; set; } = ResamplingSourceProvider.DitherOff;

        public bool MiniPlayerAlwaysOnTop { get; set; } = true;

        public bool OpenDesktopLyricsOnStartup { get; set; }

        public bool OpenMiniPlayerOnStartup { get; set; }

        public bool GlobalMouseWheelVolume { get; set; } = true;

        // —— 在线下载 ——
        public bool AutoDownloadLyrics { get; set; }

        public bool AutoDownloadCover { get; set; }

        /// <summary>NetEase / QQ</summary>
        public string LyricDownloadService { get; set; } = "NetEase";

        public bool SaveLyricToSongFolder { get; set; } = true;

        public bool SaveCoverToSongFolder { get; set; } = true;

        public bool AutoDownloadOnlyWhenTagFull { get; set; } = true;

        public string LyricFolder { get; set; } = string.Empty;

        public string CoverFolder { get; set; } = string.Empty;

        // —— 歌词 ——
        public bool PreferInnerLyric { get; set; } = true;

        /// <summary>A/B 诊断开关：true 时 DSD 直出走 NAudio WasapiOut(独占) 而非本机原生 render，
        /// 用于判断电流/黄灯是否来自我们的原生 WASAPI 渲染层（默认 false=原生态）。</summary>
        public bool DsdUseNaudioOutput { get; set; }

        /// <summary>DSD 直出用 32bit DoP 容器（部分 DAC/KA13 认同 DoP 32bit 而 24bit 不认）。默认 false=24bit。</summary>
        public bool DsDoP32 { get; set; }

        /// <summary>诊断开关：true 时 DSD 不再走 DoP 直出，而是用 ffmpeg 把 DSF/DFF 转成高采样 PCM、
        /// 走成熟的 PCM 独占通路播放。用于判断"电流/黄灯"是来自 DoP 直出链路，还是 KA13 对高采样率 USB 时钟/驱动本身的问题。
        /// 默认 false=走 DoP 直出。</summary>
        public bool DsdUsePcmFallback { get; set; }

        /// <summary>DSD 输出模式（用户可选择）："Pcm"=用 ffmpeg 转成高采样 PCM 输出（默认，保留现有独占/ASIO 的 PCM 方案）；
        /// "Dop"=DoP 直出（独占/ASIO 下把 DSD 1-bit 封进 DoP 容器直通 DAC，bit-perfect）。</summary>
        public string DsdOutputMode { get; set; } = "Pcm";

        public bool LyricFuzzyMatch { get; set; } = true;

        public bool ShowLyricTranslate { get; set; } = true;

        /// <summary>None / Auto / Ask</summary>
        public string LyricSavePolicy { get; set; } = "Ask";

        public bool LyricKaraokeStyle { get; set; } = true;

        public bool HideBlankLyricLines { get; set; }

        public int LyricLineSpacing { get; set; } = 10;

        /// <summary>Left / Right / Center / Auto</summary>
        public string LyricAlign { get; set; } = "Center";

        // —— 桌面歌词 ——
        public bool DesktopLyricHideWithoutLyric { get; set; }

        public bool DesktopLyricHideWhenPaused { get; set; }

        public bool DesktopLyricLockOnStart { get; set; }

        public bool DesktopLyricShowUnlockWhenLocked { get; set; } = true;

        public bool DesktopLyricDoubleLine { get; set; } = true;

        public bool DesktopLyricClickThrough { get; set; }

        /// <summary>20–100</summary>
        public int DesktopLyricOpacity { get; set; } = 100;

        public double DesktopLyricFontSize { get; set; } = 28;

        public string DesktopLyricPlayedColor { get; set; } = "#40B4FF";

        public string DesktopLyricUnplayedColor { get; set; } = "#F5F5F5";

        /// <summary>桌面歌词窗口记住的位置（物理像素；int.MinValue=未定位）。</summary>
        public int DesktopLyricPosX { get; set; } = int.MinValue;
        public int DesktopLyricPosY { get; set; } = int.MinValue;

        // —— 外观 ——
        public bool ShowSpectrum { get; set; } = true;

        public bool ShowAlbumCover { get; set; } = true;

        public bool EnableBackground { get; set; } = true;

        public bool AlbumCoverAsBackground { get; set; } = true;

        public bool BackgroundGaussBlur { get; set; } = true;

        /// <summary>1–8，越大越糊</summary>
        public int GaussBlurRadius { get; set; } = 2;

        public bool UseInnerCoverFirst { get; set; } = true;

        public bool FollowSystemAccent { get; set; } = true;
        public string AccentSource { get; set; } = "System"; // System / Custom
        public string CustomAccentColor { get; set; } = "#0078D4";
        public string ProgressBarStyle { get; set; } = "Gradient"; // Gradient / Waveform / Spotify / AppleLine
        public string CustomBackgroundPath { get; set; } = string.Empty;
        public string ThemePreset { get; set; } = string.Empty;
        public bool ShowPlaylistTitle { get; set; } = true;
        public bool ShowPlaylistArtist { get; set; } = true;
        public bool ShowPlaylistAlbum { get; set; } = true;
        public bool ShowPlaylistYear { get; set; } = true;
        public bool ShowPlaylistDuration { get; set; } = true;
        public string PlaylistDensity { get; set; } = "Comfortable"; // Compact / Comfortable

        // —— 播放 ——
        public bool EnableSmtc { get; set; } = true;

        public bool EnableGlobalHotkeys { get; set; } = true;

        public bool EnableFade { get; set; }

        public int FadeMilliseconds { get; set; } = 500;

        /// <summary>点击进度条定位后的行为：SeekAndPause（跳转并暂停，默认）/ SeekAndPlay（跳转并继续播放）。</summary>
        public string ProgressBarClickBehavior { get; set; } = "SeekAndPause";

        public double PlaybackRate { get; set; } = 1.0;

        public bool StopWhenError { get; set; } = true;

        public bool AutoPlayWhenStart { get; set; }

        public bool ShowTaskbarProgress { get; set; } = true;

        public bool ContinueWhenSwitchPlaylist { get; set; } = true;

        // —— 媒体库 ——
        public bool AutoUpdateLibrary { get; set; }

        public List<string> LibraryWatchFolders { get; set; } = new();

        public bool DisableDeleteFromDisk { get; set; } = true;

        public bool RemoveMissingOnUpdate { get; set; } = true;

        public bool IgnoreTooShortOnUpdate { get; set; }

        public int FileTooShortSec { get; set; } = 10;

        public bool InsertPlaylistAtBegin { get; set; } = true;

        /// <summary>0=全部，其它为天数</summary>
        public int RecentPlayedRangeDays { get; set; }

        public bool WriteId3v23 { get; set; } = true;

        public bool EnableLastFm { get; set; }

        public bool LastFmHttps { get; set; } = true;

        public bool LastFmNowPlaying { get; set; } = true;

        public int LastFmLeastPercent { get; set; } = 50;

        public int LastFmLeastSeconds { get; set; } = 30;

        // —— 导航可见性 ——
        public bool ShowNavFavorites { get; set; } = true;

        public bool ShowNavRecent { get; set; } = true;

        public bool ShowNavGenre { get; set; }

        public bool ShowNavYear { get; set; }

        // —— 全局热键 ——
        /// <summary>自定义全局热键：HotkeyAction 枚举名 → "Ctrl+Alt+P" 形式；为空表示使用默认。</summary>
public Dictionary<string, string> CustomHotkeys { get; set; } = new();

        /// <summary>艺术家头像来源（右键头像 → 从网络获取时使用的平台）："NetEase"（默认，真实歌手头像）或 "iTunes"（精确区分艺人，用其专辑封面作头像）。</summary>
        public string ArtistAvatarSource { get; set; } = "NetEase";

        /// <summary>在线搜索窗口默认平台。</summary>
        public string OnlineSearchDefaultSource { get; set; } = "NetEase";

        /// <summary>流媒体插件服务地址（WSL，如 http://172.20.55.125:21010）；空=未配置。</summary>
        public string StreamingServiceUrl { get; set; } = string.Empty;
        /// <summary>各平台登录 Cookie（播放器内输入，本地明文存储）。空=未登录。</summary>
        public string NetEaseCookie { get; set; } = string.Empty;
        public string QqCookie { get; set; } = string.Empty;
        public string AppleMusicCookie { get; set; } = string.Empty;

        /// <summary>声道：Stereo / Left / Right。</summary>
        public string AudioChannel { get; set; } = "Stereo";

        /// <summary>主窗口置顶。</summary>
        public bool AlwaysOnTop { get; set; }
    }

    public static class AppSettingsStore
    {
        private const string FileName = "app-settings.json";
        private static readonly object Gate = new();
        private static AppSettingsState? _cache;

        public static event Action? Changed;

        public static AppSettingsState Load()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    // 注意：之前这里 new StackTrace() 抓取调用栈用于调试，但它是启动期高频调用
                    // （每首歌提取封面都会触发一次 Load），分配+栈遍历开销不小。已移除，只保留轻量日志。
                    StartupLog.Write("Load 命中缓存 OutputMode=" + (_cache?.OutputMode ?? "null"));
                    return Clone(_cache);
                }

                StartupLog.Write("Load _cache==null, 准备读文件");
                try
                {
                    string path = GetFilePath();
                    if (File.Exists(path))
                    {
                        string raw = File.ReadAllText(path);
                        AppSettingsState? loaded = JsonSerializer.Deserialize<AppSettingsState>(raw);
                        StartupLog.Write("Load 读文件 path=" + path + " 解析成功=" + (loaded != null) + " OutputMode=" + (loaded?.OutputMode ?? "(null)"));
                        _cache = Normalize(loaded ?? new AppSettingsState());
                        // 解密磁盘上的 Cookie（DPAPI）；旧版明文无前缀则原样保留，下次保存自动加密迁移。
                        _cache.NetEaseCookie = SecretProtector.Unprotect(_cache.NetEaseCookie);
                        _cache.QqCookie = SecretProtector.Unprotect(_cache.QqCookie);
                        _cache.AppleMusicCookie = SecretProtector.Unprotect(_cache.AppleMusicCookie);
                    }
                    else
                    {
                        StartupLog.Write("Load 文件不存在 path=" + path + ", 用默认+迁移");
                        _cache = MigrateFromLegacyClosePrefs(new AppSettingsState());
                        SaveCore(_cache);
                    }
                }
                catch (Exception ex)
                {
                    StartupLog.Write("Load 反序列化/读取异常: " + ex.Message);
                    try
                    {
                        string path = GetFilePath();
                        if (File.Exists(path))
                        {
                            string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                            File.Copy(path, backup, overwrite: true);
                            SettingsWereRecovered = true;
                            StartupLog.Write("设置文件损坏，已备份到 " + backup + " 并使用默认设置");
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }

                    _cache = new AppSettingsState();
                }

                StartupLog.Write("Load 返回 OutputMode=" + (_cache?.OutputMode ?? "null"));
                return Clone(_cache);
            }
        }

        public static void Save(AppSettingsState state)
        {
            lock (Gate)
            {
                _cache = Normalize(state ?? new AppSettingsState());
                SaveCore(_cache);
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
        }

        public static void Update(Action<AppSettingsState> mutator)
        {
            AppSettingsState state = Load();
            mutator(state);
            Save(state);
        }

        private static void SaveCore(AppSettingsState state)
        {
            try
            {
                // 落盘前加密 Cookie（DPAPI 当前用户作用域）；内存对象保持明文不变。
                string ne = state.NetEaseCookie, qq = state.QqCookie, ap = state.AppleMusicCookie;
                state.NetEaseCookie = SecretProtector.Protect(ne);
                state.QqCookie = SecretProtector.Protect(qq);
                state.AppleMusicCookie = SecretProtector.Protect(ap);
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetFilePath(), json);
                // 还原明文，保持内存态与缓存一致（下次读取仍是明文）。
                state.NetEaseCookie = ne;
                state.QqCookie = qq;
                state.AppleMusicCookie = ap;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
        }

        private static AppSettingsState MigrateFromLegacyClosePrefs(AppSettingsState state)
        {
            try
            {
                AppClosePreferencesState legacy = AppClosePreferences.LoadLegacyFileOnly();
                if (legacy.DontAskAgain)
                {
                    state.CloseAction = string.Equals(
                        legacy.PreferredAction,
                        nameof(CloseWindowAction.Exit),
                        StringComparison.OrdinalIgnoreCase)
                        ? nameof(CloseWindowAction.Exit)
                        : nameof(CloseWindowAction.MinimizeToTray);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }

            return state;
        }

        private static AppSettingsState Normalize(AppSettingsState s)
        {
            s.Volume = Math.Clamp(s.Volume, 0, 100);
            if (string.IsNullOrWhiteSpace(s.CloseAction))
            {
                s.CloseAction = nameof(CloseWindowAction.Ask);
            }

            if (string.IsNullOrWhiteSpace(s.PlaybackOrder))
            {
                s.PlaybackOrder = nameof(CelesteMusicPlayer.PlaybackOrder.ListLoop);
            }

            s.FadeMilliseconds = Math.Clamp(s.FadeMilliseconds, 0, 60_000);
            s.PlaybackRate = Math.Clamp(s.PlaybackRate, 0.25, 4.0);
            s.DesktopLyricOpacity = Math.Clamp(s.DesktopLyricOpacity, 20, 100);
            s.DesktopLyricFontSize = Math.Clamp(s.DesktopLyricFontSize, 14, 64);
            s.LyricLineSpacing = Math.Clamp(s.LyricLineSpacing, 0, 40);
            s.GaussBlurRadius = Math.Clamp(s.GaussBlurRadius, 1, 8);
            s.FileTooShortSec = Math.Clamp(s.FileTooShortSec, 0, 600);
            s.RecentPlayedRangeDays = Math.Clamp(s.RecentPlayedRangeDays, 0, 3650);
            s.LastFmLeastPercent = Math.Clamp(s.LastFmLeastPercent, 1, 100);
            s.LastFmLeastSeconds = Math.Clamp(s.LastFmLeastSeconds, 1, 600);
            s.LyricFolder ??= string.Empty;
            s.CoverFolder ??= string.Empty;
            s.LyricDownloadService = s.LyricDownloadService switch
            {
                "QQ" => s.LyricDownloadService,
                _ => "NetEase"
            };
            s.OnlineSearchDefaultSource = s.OnlineSearchDefaultSource switch
            {
                "QQ" or "iTunes" => s.OnlineSearchDefaultSource,
                _ => "NetEase"
            };
            s.AudioChannel = s.AudioChannel switch
            {
                "Left" or "Right" => s.AudioChannel,
                _ => "Stereo"
            };
            if (string.IsNullOrWhiteSpace(s.LyricSavePolicy))
            {
                s.LyricSavePolicy = "Ask";
            }

            if (string.IsNullOrWhiteSpace(s.LyricAlign))
            {
                s.LyricAlign = "Center";
            }

            if (string.IsNullOrWhiteSpace(s.DesktopLyricPlayedColor))
            {
                s.DesktopLyricPlayedColor = "#40B4FF";
            }

            if (string.IsNullOrWhiteSpace(s.DesktopLyricUnplayedColor))
            {
                s.DesktopLyricUnplayedColor = "#F5F5F5";
            }

            s.LibraryWatchFolders = s.LibraryWatchFolders?
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            s.CustomHotkeys = s.CustomHotkeys?
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>();

            return s;
        }

        private static AppSettingsState Clone(AppSettingsState s) => new()
        {
            CloseAction = s.CloseAction,
            RestoreLibrary = s.RestoreLibrary,
            RestorePlayback = s.RestorePlayback,
            AutoRun = s.AutoRun,
            EnableFrostedGlass = s.EnableFrostedGlass,
            Volume = s.Volume,
            OutputDeviceId = s.OutputDeviceId,
            OutputMode = s.OutputMode,
            PlaybackOrder = s.PlaybackOrder,
            MiniPlayerAlwaysOnTop = s.MiniPlayerAlwaysOnTop,
            OpenDesktopLyricsOnStartup = s.OpenDesktopLyricsOnStartup,
            OpenMiniPlayerOnStartup = s.OpenMiniPlayerOnStartup,
            GlobalMouseWheelVolume = s.GlobalMouseWheelVolume,
            AutoDownloadLyrics = s.AutoDownloadLyrics,
            AutoDownloadCover = s.AutoDownloadCover,
            LyricDownloadService = s.LyricDownloadService,
            SaveLyricToSongFolder = s.SaveLyricToSongFolder,
            SaveCoverToSongFolder = s.SaveCoverToSongFolder,
            AutoDownloadOnlyWhenTagFull = s.AutoDownloadOnlyWhenTagFull,
            LyricFolder = s.LyricFolder,
            CoverFolder = s.CoverFolder,
            PreferInnerLyric = s.PreferInnerLyric,
            LyricFuzzyMatch = s.LyricFuzzyMatch,
            ShowLyricTranslate = s.ShowLyricTranslate,
            LyricSavePolicy = s.LyricSavePolicy,
            LyricKaraokeStyle = s.LyricKaraokeStyle,
            HideBlankLyricLines = s.HideBlankLyricLines,
            LyricLineSpacing = s.LyricLineSpacing,
            LyricAlign = s.LyricAlign,
            DesktopLyricHideWithoutLyric = s.DesktopLyricHideWithoutLyric,
            DesktopLyricHideWhenPaused = s.DesktopLyricHideWhenPaused,
            DesktopLyricLockOnStart = s.DesktopLyricLockOnStart,
            DesktopLyricShowUnlockWhenLocked = s.DesktopLyricShowUnlockWhenLocked,
            DesktopLyricDoubleLine = s.DesktopLyricDoubleLine,
            DesktopLyricClickThrough = s.DesktopLyricClickThrough,
            DesktopLyricOpacity = s.DesktopLyricOpacity,
            DesktopLyricFontSize = s.DesktopLyricFontSize,
            DesktopLyricPlayedColor = s.DesktopLyricPlayedColor,
            DesktopLyricUnplayedColor = s.DesktopLyricUnplayedColor,
            DesktopLyricPosX = s.DesktopLyricPosX,
            DesktopLyricPosY = s.DesktopLyricPosY,
            ShowSpectrum = s.ShowSpectrum,
            ShowAlbumCover = s.ShowAlbumCover,
            EnableBackground = s.EnableBackground,
            AlbumCoverAsBackground = s.AlbumCoverAsBackground,
            BackgroundGaussBlur = s.BackgroundGaussBlur,
            GaussBlurRadius = s.GaussBlurRadius,
            UseInnerCoverFirst = s.UseInnerCoverFirst,
            FollowSystemAccent = s.FollowSystemAccent,
            AccentSource = string.IsNullOrWhiteSpace(s.AccentSource)
                ? (s.FollowSystemAccent ? "System" : "Custom")
                : s.AccentSource,
            CustomAccentColor = string.IsNullOrWhiteSpace(s.CustomAccentColor) ? "#0078D4" : s.CustomAccentColor,
        ProgressBarStyle = s.ProgressBarStyle switch
        {
            "Waveform" or "Spotify" or "AppleLine" => s.ProgressBarStyle,
            _ => "Gradient"
        },
        CustomBackgroundPath = s.CustomBackgroundPath?.Trim() ?? string.Empty,
        ThemePreset = s.ThemePreset?.Trim() ?? string.Empty,
        PlaylistDensity = s.PlaylistDensity is "Compact" or "Comfortable" ? s.PlaylistDensity : "Comfortable",
            EnableSmtc = s.EnableSmtc,
            EnableGlobalHotkeys = s.EnableGlobalHotkeys,
            EnableFade = s.EnableFade,
            FadeMilliseconds = s.FadeMilliseconds,
            ProgressBarClickBehavior = s.ProgressBarClickBehavior,
            PlaybackRate = s.PlaybackRate,
            StopWhenError = s.StopWhenError,
            AutoPlayWhenStart = s.AutoPlayWhenStart,
            ShowTaskbarProgress = s.ShowTaskbarProgress,
            ContinueWhenSwitchPlaylist = s.ContinueWhenSwitchPlaylist,
            AutoUpdateLibrary = s.AutoUpdateLibrary,
            LibraryWatchFolders = s.LibraryWatchFolders.ToList(),
            DisableDeleteFromDisk = s.DisableDeleteFromDisk,
            RemoveMissingOnUpdate = s.RemoveMissingOnUpdate,
            IgnoreTooShortOnUpdate = s.IgnoreTooShortOnUpdate,
            FileTooShortSec = s.FileTooShortSec,
            InsertPlaylistAtBegin = s.InsertPlaylistAtBegin,
            RecentPlayedRangeDays = s.RecentPlayedRangeDays,
            WriteId3v23 = s.WriteId3v23,
            EnableLastFm = s.EnableLastFm,
            LastFmHttps = s.LastFmHttps,
            LastFmNowPlaying = s.LastFmNowPlaying,
            LastFmLeastPercent = s.LastFmLeastPercent,
            LastFmLeastSeconds = s.LastFmLeastSeconds,
            ShowNavFavorites = s.ShowNavFavorites,
            ShowNavRecent = s.ShowNavRecent,
            ShowNavGenre = s.ShowNavGenre,
            ShowNavYear = s.ShowNavYear,
            ShowPlaylistTitle = s.ShowPlaylistTitle,
            ShowPlaylistArtist = s.ShowPlaylistArtist,
            ShowPlaylistAlbum = s.ShowPlaylistAlbum,
            ShowPlaylistYear = s.ShowPlaylistYear,
            ShowPlaylistDuration = s.ShowPlaylistDuration,
            AudioChannel = s.AudioChannel,
            // 诊断开关必须随 Clone 拷贝，否则 Load() 返回的克隆里恒为默认 false（此前导致 DSD 诊断 A/B 开关从未生效、配置被重写）。
            DsDoP32 = s.DsDoP32,
            DsdUseNaudioOutput = s.DsdUseNaudioOutput,
            DsdUsePcmFallback = s.DsdUsePcmFallback,
            DsdOutputMode = s.DsdOutputMode,
            OnlineSearchDefaultSource = s.OnlineSearchDefaultSource,
            ArtistAvatarSource = s.ArtistAvatarSource,
            StreamingServiceUrl = s.StreamingServiceUrl,
            NetEaseCookie = s.NetEaseCookie,
            QqCookie = s.QqCookie,
            AppleMusicCookie = s.AppleMusicCookie,
            CustomHotkeys = s.CustomHotkeys.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
        };

        internal static string GetFilePath()
        {
            // 固定使用普通用户目录，规避 MSIX ApplicationData 虚拟化导致的写入不稳定/多路径混乱。
            // 打包与 unpackaged 读写完全同源，杜绝“保存正确却读回默认”的另一文件路径问题。
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CelesteMusicPlayer");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, FileName);
            startupLogOnce(path);
            return path;
        }

        /// <summary>上次运行是否异常退出（运行标记残留）。供启动提示。</summary>
        public static bool WasUncleanExitLastTime { get; private set; }

        /// <summary>启动时调用：写入运行标记；若标记已存在则说明上次异常退出。</summary>
        public static void MarkAppStart()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer");
                Directory.CreateDirectory(dir);
                string marker = Path.Combine(dir, ".running");
                WasUncleanExitLastTime = File.Exists(marker);
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
        }

        /// <summary>正常退出时调用：清除运行标记。</summary>
        public static void MarkAppCleanExit()
        {
            try
            {
                string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer", ".running");
                File.Delete(marker);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
        }

        /// <summary>本次进程加载时设置文件损坏并已用默认恢复（供 UI 提示一次）。</summary>
        public static bool SettingsWereRecovered { get; private set; }

        private static bool _pathLogged;
        private static void startupLogOnce(string path)
        {
            if (_pathLogged)
            {
                return;
            }

            _pathLogged = true;
            try
            {
                System.IO.File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "CelesteSettingsPath.log"),
                    DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " GetFilePath=" + path + Environment.NewLine);
                System.IO.File.AppendAllText(path + ".pathlog", DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " GetFilePath called" + Environment.NewLine);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
        }

        public static string GetConfigDirectory()
        {
            string path = GetFilePath();
            return Path.GetDirectoryName(path) ?? path;
        }

        /// <summary>设置主文件完整路径（app-settings.json）。</summary>
        public static string GetSettingsFilePath() => GetFilePath();

        /// <summary>把当前设置导出（备份）到目标路径。返回是否成功。</summary>
        public static bool ExportTo(string destPath)
        {
            try
            {
                string path = GetFilePath();
                if (!string.IsNullOrWhiteSpace(destPath) && File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    File.Copy(path, destPath, overwrite: true);
                    return true;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
            return false;
        }

        /// <summary>从备份文件恢复设置到主设置文件，并重新加载缓存。返回是否成功。</summary>
        public static bool ImportFrom(string srcPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                {
                    return false;
                }

                string path = GetFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 导入前先备份当前设置，便于回滚
                string rollback = path + ".pre-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try
                {
                    if (File.Exists(path))
                    {
                        File.Copy(path, rollback, overwrite: true);
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }

                File.Copy(srcPath, path, overwrite: true);
                // 清除缓存，下次 Load() 从磁盘重新读
                lock (Gate)
                {
                    _cache = null;
                }
                try { Changed?.Invoke(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
                return true;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppSettingsStore.cs", caught); }
            return false;
        }
    }
}
