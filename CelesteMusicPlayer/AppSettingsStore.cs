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

        public bool LyricFuzzyMatch { get; set; } = true;

        public bool ShowLyricTranslate { get; set; } = true;

        public bool ShowSongInfoIfNoLyric { get; set; } = true;

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

        /// <summary>FileName / Title / ArtistTitle / TitleArtist</summary>
        public string PlaylistDisplayFormat { get; set; } = "ArtistTitle";

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

        /// <summary>艺术家头像来源（右键头像 → 从网络获取时使用的平台）。</summary>

        /// <summary>在线搜索窗口默认平台。</summary>
        public string OnlineSearchDefaultSource { get; set; } = "NetEase";

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
                    StartupLog.Write("Load 命中缓存 _cache 已设, OutputMode=" + (_cache?.OutputMode ?? "null"));
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
                    catch
                    {
                    }

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
            catch
            {
            }
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
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
            }
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
            catch
            {
            }

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
                "QQ" or "Kugou" => s.LyricDownloadService,
                _ => "NetEase"
            };
            s.OnlineSearchDefaultSource = s.OnlineSearchDefaultSource switch
            {
                "QQ" or "Kugou" => s.OnlineSearchDefaultSource,
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

            if (string.IsNullOrWhiteSpace(s.PlaylistDisplayFormat))
            {
                s.PlaylistDisplayFormat = "ArtistTitle";
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
            ShowSongInfoIfNoLyric = s.ShowSongInfoIfNoLyric,
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
            PlaylistDisplayFormat = s.PlaylistDisplayFormat,
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
            OnlineSearchDefaultSource = s.OnlineSearchDefaultSource,
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
            catch
            {
            }
        }

        /// <summary>正常退出时调用：清除运行标记。</summary>
        public static void MarkAppCleanExit()
        {
            try
            {
                string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer", ".running");
                File.Delete(marker);
            }
            catch
            {
            }
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
            catch
            {
            }
        }

        public static string GetConfigDirectory()
        {
            string path = GetFilePath();
            return Path.GetDirectoryName(path) ?? path;
        }
    }
}
