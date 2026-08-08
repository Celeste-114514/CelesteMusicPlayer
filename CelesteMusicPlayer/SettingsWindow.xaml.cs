using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System;
using Windows.UI.Core;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>选项设置窗口（对齐 MusicPlayer2 六页结构）。</summary>
    public sealed partial class SettingsWindow : Window
    {
        private const string AutoRunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoRunValueName = "CelesteMusicPlayer";

        private static SettingsWindow? _instance;
        private bool _loadingUi = true;
        private bool _uiReady;
        private List<string> _watchFolders = new();

        private static readonly (string Id, string Label)[] CloseOptions =
        {
            (nameof(CloseWindowAction.Ask), "每次询问"),
            (nameof(CloseWindowAction.MinimizeToTray), "缩小到托盘"),
            (nameof(CloseWindowAction.Exit), "退出程序")
        };

        private static readonly (PlaybackOrder Order, string Label)[] PlaybackOrderOptions =
        {
            (PlaybackOrder.ListLoop, "列表循环"),
            (PlaybackOrder.Sequential, "顺序播放"),
            (PlaybackOrder.Random, "随机播放"),
            (PlaybackOrder.TrackLoop, "单曲循环"),
            (PlaybackOrder.TrackOnce, "单曲播放")
        };

        private static readonly (string Id, string Label)[] LyricSavePolicyOptions =
        {
            ("None", "不保存"),
            ("Auto", "自动保存"),
            ("Ask", "询问")
        };

        private static readonly (string Id, string Label)[] LyricAlignOptions =
        {
            ("Left", "左"),
            ("Right", "右"),
            ("Center", "居中"),
            ("Auto", "自动")
        };

        private static readonly (string Id, string Label)[] LyricServiceOptions =
        {
            ("NetEase", "网易云音乐"),
            ("QQ", "QQ音乐"),
            ("Kugou", "酷狗音乐"),
        };

        private static readonly (string Id, string Label)[] AudioChannelOptions =
        {
            ("Stereo", "立体声"),
            ("Left", "仅左声道"),
            ("Right", "仅右声道")
        };

        private static readonly (string Id, string Label)[] PlaylistFormatOptions =
        {
            ("FileName", "文件名"),
            ("Title", "标题"),
            ("ArtistTitle", "艺术家-标题"),
            ("TitleArtist", "标题-艺术家")
        };

        private static readonly (int Days, string Label)[] RecentRangeOptions =
        {
            (0, "全部"),
            (1, "今天"),
            (3, "三天"),
            (7, "一周"),
            (30, "一月"),
            (180, "半年"),
            (365, "一年")
        };

        private static readonly (HotkeyAction Action, string Shortcut, string Description)[] DefaultHotkeys =
        {
            (HotkeyAction.PlayPause, "Ctrl+Alt+P", "播放/暂停"),
            (HotkeyAction.Stop, "Ctrl+Alt+S", "停止"),
            (HotkeyAction.Next, "Ctrl+Alt+N", "下一首"),
            (HotkeyAction.Previous, "Ctrl+Alt+B", "上一首"),
            (HotkeyAction.VolumeUp, "Ctrl+Alt++", "音量增大"),
            (HotkeyAction.VolumeDown, "Ctrl+Alt+-", "音量减小"),
            (HotkeyAction.SeekForward, "Ctrl+Alt+Right", "快进"),
            (HotkeyAction.SeekBack, "Ctrl+Alt+Left", "快退"),
            (HotkeyAction.ToggleDesktopLyrics, "Ctrl+Alt+L", "桌面歌词"),
            (HotkeyAction.ToggleFavorite, "Ctrl+Alt+F", "收藏"),
            (HotkeyAction.ShowHideMain, "Ctrl+Alt+M", "显示/隐藏主窗口")
        };

        public SettingsWindow()
        {
            _loadingUi = true;
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "选项设置";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(1300, 1000));

            ApplyBackdropFromSettings();

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindowTitleBar titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }

            InitComboBoxes();
            ReloadHotkeyList();
            LoadFromStore();

            if (SettingsNav.MenuItems.Count > 0)
            {
                SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            }

            ThemeColorService.ThemeColorChanged -= OnThemeColorChangedSettings;
            ThemeColorService.ThemeColorChanged += OnThemeColorChangedSettings;
            _uiReady = true;
            _loadingUi = false;
            _lastAppliedAccent = ThemeColorService.CurrentAccent;

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.LoadFromStore();
                _instance.Activate();
                return;
            }

            _instance = new SettingsWindow();
            _instance.Activate();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null)
            {
                return;
            }

            SettingsWindow win = _instance;
            _instance = null;
            win.Close();
        }

        public static void ApplyBackdropIfOpen()
        {
            _instance?.ApplyBackdropFromSettings();
        }

        private void InitComboBoxes()
        {
            FillCombo(CloseActionCombo, CloseOptions);
            PlaybackOrderCombo.Items.Clear();
            foreach ((PlaybackOrder order, string label) in PlaybackOrderOptions)
            {
                PlaybackOrderCombo.Items.Add(new ComboBoxItem { Content = label, Tag = order });
            }

            FillCombo(LyricSavePolicyCombo, LyricSavePolicyOptions);
            FillCombo(LyricAlignCombo, LyricAlignOptions);
            FillCombo(LyricDownloadServiceCombo, LyricServiceOptions);
            FillCombo(OnlineSearchSourceCombo, LyricServiceOptions);
            FillCombo(AudioChannelCombo, AudioChannelOptions);
            FillCombo(PlaylistDisplayFormatCombo, PlaylistFormatOptions);

            WriteId3v23Combo.Items.Clear();
            WriteId3v23Combo.Items.Add(new ComboBoxItem { Content = "ID3v2.3", Tag = true });
            WriteId3v23Combo.Items.Add(new ComboBoxItem { Content = "ID3v2.4", Tag = false });

            RecentPlayedRangeDaysCombo.Items.Clear();
            foreach ((int days, string label) in RecentRangeOptions)
            {
                RecentPlayedRangeDaysCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
            }
        }

        private static void FillCombo(ComboBox combo, IEnumerable<(string Id, string Label)> options)
        {
            combo.Items.Clear();
            foreach ((string id, string label) in options)
            {
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            }
        }

        private void ReloadHotkeyList()
        {
            HotkeyDefaultsListView.Items.Clear();
            AppSettingsState s = AppSettingsStore.Load();
            Dictionary<string, string> custom = s.CustomHotkeys ?? new Dictionary<string, string>();
            foreach ((HotkeyAction action, string shortcut, string description) in DefaultHotkeys)
            {
                string current = custom.TryGetValue(action.ToString(), out string? c) ? c : shortcut;
                HotkeyDefaultsListView.Items.Add(new HotkeyDefaultItem(action, current, description));
            }
        }

        private void LoadFromStore()
        {
            _loadingUi = true;
            try
            {
                AppSettingsState s = AppSettingsStore.Load();

                // 歌词
                SetToggle(PreferInnerLyricSwitch, s.PreferInnerLyric);
                SetToggle(LyricFuzzyMatchSwitch, s.LyricFuzzyMatch);
                SetToggle(ShowLyricTranslateSwitch, s.ShowLyricTranslate);
                SetToggle(ShowSongInfoIfNoLyricSwitch, s.ShowSongInfoIfNoLyric);
                SetText(LyricFolderTextBox, s.LyricFolder);
                SelectComboByTag(LyricSavePolicyCombo, s.LyricSavePolicy);
                SetToggle(LyricKaraokeStyleSwitch, s.LyricKaraokeStyle);
                SetToggle(HideBlankLyricLinesSwitch, s.HideBlankLyricLines);
                SetSlider(LyricLineSpacingSlider, s.LyricLineSpacing);
                SetTextBlock(LyricLineSpacingValueText, s.LyricLineSpacing.ToString());
                SelectComboByTag(LyricAlignCombo, s.LyricAlign);

                SetToggle(OpenDesktopLyricsSwitch, s.OpenDesktopLyricsOnStartup);
                SetToggle(DesktopLyricHideWithoutLyricSwitch, s.DesktopLyricHideWithoutLyric);
                SetToggle(DesktopLyricHideWhenPausedSwitch, s.DesktopLyricHideWhenPaused);
                SetToggle(DesktopLyricLockOnStartSwitch, s.DesktopLyricLockOnStart);
                SetToggle(DesktopLyricShowUnlockWhenLockedSwitch, s.DesktopLyricShowUnlockWhenLocked);
                SetToggle(DesktopLyricDoubleLineSwitch, s.DesktopLyricDoubleLine);
                SetToggle(DesktopLyricClickThroughSwitch, s.DesktopLyricClickThrough);
                SetSlider(DesktopLyricOpacitySlider, s.DesktopLyricOpacity);
                SetTextBlock(DesktopLyricOpacityValueText, s.DesktopLyricOpacity.ToString());
                SetSlider(DesktopLyricFontSizeSlider, s.DesktopLyricFontSize);
                SetTextBlock(DesktopLyricFontSizeValueText, s.DesktopLyricFontSize.ToString("0"));
                SetText(DesktopLyricPlayedColorTextBox, s.DesktopLyricPlayedColor);
                SetText(DesktopLyricUnplayedColorTextBox, s.DesktopLyricUnplayedColor);
                UpdateColorSwatches();

                SetToggle(MiniAlwaysOnTopSwitch, s.MiniPlayerAlwaysOnTop);
                SetToggle(OpenMiniPlayerSwitch, s.OpenMiniPlayerOnStartup);

                // 外观
                SetToggle(FrostedGlassSwitch, s.EnableFrostedGlass);
                SetToggle(ShowSpectrumSwitch, s.ShowSpectrum);
                SetToggle(ShowAlbumCoverSwitch, s.ShowAlbumCover);
                SetToggle(EnableBackgroundSwitch, s.EnableBackground);
                SetToggle(AlbumCoverAsBackgroundSwitch, s.AlbumCoverAsBackground);
                SetToggle(BackgroundGaussBlurSwitch, s.BackgroundGaussBlur);
                SetSlider(GaussBlurRadiusSlider, s.GaussBlurRadius);
                SetTextBlock(GaussBlurRadiusValueText, s.GaussBlurRadius.ToString());
                SetToggle(UseInnerCoverFirstSwitch, s.UseInnerCoverFirst);
                SetText(CoverFolderTextBox, s.CoverFolder);
                SelectComboByTag(AccentSourceCombo,
                    string.IsNullOrWhiteSpace(s.AccentSource)
                        ? (s.FollowSystemAccent ? "System" : "Custom")
                        : s.AccentSource);
                SelectComboByTag(ThemePresetCombo, s.ThemePreset);
                ShowTitleColCheck.IsChecked = s.ShowPlaylistTitle;
                ShowArtistColCheck.IsChecked = s.ShowPlaylistArtist;
                ShowAlbumColCheck.IsChecked = s.ShowPlaylistAlbum;
                ShowYearColCheck.IsChecked = s.ShowPlaylistYear;
                ShowDurationColCheck.IsChecked = s.ShowPlaylistDuration;
                SelectComboByTag(PlaylistDensityCombo, s.PlaylistDensity);
                BackgroundPathTextBox.Text = s.CustomBackgroundPath;
                WaveformProgressSwitch.IsOn = s.ProgressBarStyle == "Waveform";
                _accentHex = string.IsNullOrWhiteSpace(s.CustomAccentColor) ? "#0078D4" : s.CustomAccentColor;
                UpdateAccentColorButton();

                // 常规
                SelectComboByTag(CloseActionCombo, s.CloseAction);
                SetToggle(RestoreLibrarySwitch, s.RestoreLibrary);
                SetToggle(RestorePlaybackSwitch, s.RestorePlayback);
                SetToggle(AutoRunSwitch, s.AutoRun);
                SetToggle(GlobalMouseWheelVolumeSwitch, s.GlobalMouseWheelVolume);
                SetToggle(AutoDownloadLyricsSwitch, s.AutoDownloadLyrics);
                SetToggle(AutoDownloadCoverSwitch, s.AutoDownloadCover);
                SelectComboByTag(LyricDownloadServiceCombo, s.LyricDownloadService);
                SelectComboByTag(OnlineSearchSourceCombo, s.OnlineSearchDefaultSource);
                SelectComboByTag(AudioChannelCombo, s.AudioChannel);
                SetToggle(AlwaysOnTopSwitch, s.AlwaysOnTop);
                SetToggle(SaveLyricToSongFolderSwitch, s.SaveLyricToSongFolder);
                SetToggle(SaveCoverToSongFolderSwitch, s.SaveCoverToSongFolder);
                SetToggle(AutoDownloadOnlyWhenTagFullSwitch, s.AutoDownloadOnlyWhenTagFull);

                // 播放
                SetSlider(VolumeSettingSlider, s.Volume);
                SetTextBlock(VolumeValueText, $"{(int)Math.Round(s.Volume)}%");
                SelectPlaybackOrder(s.PlaybackOrder);
                SetToggle(EnableSmtcSwitch, s.EnableSmtc);
                SetToggle(EnableFadeSwitch, s.EnableFade);
                SetSlider(FadeMsSlider, s.FadeMilliseconds);
                SetTextBlock(FadeMsValueText, $"{s.FadeMilliseconds} ms");
                SetSlider(PlaybackRateSlider, s.PlaybackRate);
                SetTextBlock(PlaybackRateValueText, $"{s.PlaybackRate:0.00}×");
                SetToggle(StopWhenErrorSwitch, s.StopWhenError);
                SetToggle(AutoPlayWhenStartSwitch, s.AutoPlayWhenStart);
                SetToggle(ShowTaskbarProgressSwitch, s.ShowTaskbarProgress);
                SetToggle(ContinueWhenSwitchPlaylistSwitch, s.ContinueWhenSwitchPlaylist);

                // 媒体库
                SetToggle(AutoUpdateLibrarySwitch, s.AutoUpdateLibrary);
                _watchFolders = s.LibraryWatchFolders?.ToList() ?? new List<string>();
                RefreshWatchFoldersList();
                SetToggle(DisableDeleteFromDiskSwitch, s.DisableDeleteFromDisk);
                SetToggle(RemoveMissingOnUpdateSwitch, s.RemoveMissingOnUpdate);
                SetToggle(IgnoreTooShortOnUpdateSwitch, s.IgnoreTooShortOnUpdate);
                if (FileTooShortSecNumberBox != null)
                {
                    FileTooShortSecNumberBox.Value = s.FileTooShortSec;
                }

                SetToggle(InsertPlaylistAtBeginSwitch, s.InsertPlaylistAtBegin);
                SelectComboByTag(PlaylistDisplayFormatCombo, s.PlaylistDisplayFormat);
                SelectComboByTagInt(RecentPlayedRangeDaysCombo, s.RecentPlayedRangeDays);
                SelectWriteId3Version(s.WriteId3v23);
                SetToggle(ShowNavFavoritesSwitch, s.ShowNavFavorites);
                SetToggle(ShowNavRecentSwitch, s.ShowNavRecent);
                SetToggle(ShowNavGenreSwitch, s.ShowNavGenre);
                SetToggle(ShowNavYearSwitch, s.ShowNavYear);
                LoadLastFmCredentialsIntoUi();
                SetToggle(EnableLastFmSwitch, s.EnableLastFm);
                SetToggle(LastFmHttpsSwitch, s.LastFmHttps);
                SetToggle(LastFmNowPlayingSwitch, s.LastFmNowPlaying);
                SetSlider(LastFmLeastPercentSlider, s.LastFmLeastPercent);
                SetTextBlock(LastFmLeastPercentValueText, $"{s.LastFmLeastPercent}%");
                SetSlider(LastFmLeastSecondsSlider, s.LastFmLeastSeconds);
                SetTextBlock(LastFmLeastSecondsValueText, $"{s.LastFmLeastSeconds} 秒");

                // 快捷键
                SetToggle(EnableGlobalHotkeysSwitch, s.EnableGlobalHotkeys);
            }
            finally
            {
                // 构造函数末尾会统一放开；若是二次打开则在此放开
                if (_uiReady)
                {
                    _loadingUi = false;
                }
            }
        }

        private static void SetToggle(ToggleSwitch? control, bool value)
        {
            if (control != null)
            {
                control.IsOn = value;
            }
        }

        private static void SetText(TextBox? control, string? value)
        {
            if (control != null)
            {
                control.Text = value ?? string.Empty;
            }
        }

        private static void SetTextBlock(TextBlock? control, string value)
        {
            if (control != null)
            {
                control.Text = value;
            }
        }

        private static void SetSlider(Slider? control, double value)
        {
            if (control != null)
            {
                control.Value = value;
            }
        }

        private void RefreshWatchFoldersList()
        {
            if (LibraryWatchFoldersListView == null)
            {
                return;
            }

            LibraryWatchFoldersListView.Items.Clear();
            foreach (string folder in _watchFolders)
            {
                LibraryWatchFoldersListView.Items.Add(folder);
            }
        }

        private static void SelectComboByTag(ComboBox? combo, string tag)
        {
            if (combo == null)
            {
                return;
            }

            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item
                    && item.Tag is string id
                    && string.Equals(id, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static void SelectComboByTagInt(ComboBox combo, int tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag is int days && days == tag)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void SelectWriteId3Version(bool writeV23)
        {
            for (int i = 0; i < WriteId3v23Combo.Items.Count; i++)
            {
                if (WriteId3v23Combo.Items[i] is ComboBoxItem item && item.Tag is bool v23 && v23 == writeV23)
                {
                    WriteId3v23Combo.SelectedIndex = i;
                    return;
                }
            }

            WriteId3v23Combo.SelectedIndex = 0;
        }

        private void SelectPlaybackOrder(string name)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out PlaybackOrder order))
            {
                order = PlaybackOrder.ListLoop;
            }

            for (int i = 0; i < PlaybackOrderCombo.Items.Count; i++)
            {
                if (PlaybackOrderCombo.Items[i] is ComboBoxItem item && item.Tag is PlaybackOrder o && o == order)
                {
                    PlaybackOrderCombo.SelectedIndex = i;
                    return;
                }
            }

            PlaybackOrderCombo.SelectedIndex = 0;
        }

        private void PopulateStateFromUi(AppSettingsState s)
        {
            if (s == null)
            {
                return;
            }

            // 任一关键控件尚未生成时，跳过整次写回，避免半初始化 NRE
            if (PreferInnerLyricSwitch == null
                || DesktopLyricFontSizeSlider == null
                || DesktopLyricPlayedColorTextBox == null
                || VolumeSettingSlider == null
                || EnableGlobalHotkeysSwitch == null)
            {
                return;
            }

            s.PreferInnerLyric = PreferInnerLyricSwitch.IsOn;
            s.LyricFuzzyMatch = LyricFuzzyMatchSwitch?.IsOn ?? s.LyricFuzzyMatch;
            s.ShowLyricTranslate = ShowLyricTranslateSwitch?.IsOn ?? s.ShowLyricTranslate;
            s.ShowSongInfoIfNoLyric = ShowSongInfoIfNoLyricSwitch?.IsOn ?? s.ShowSongInfoIfNoLyric;
            s.LyricFolder = LyricFolderTextBox?.Text?.Trim() ?? string.Empty;
            s.LyricSavePolicy = GetComboTagString(LyricSavePolicyCombo, "Ask");
            s.LyricKaraokeStyle = LyricKaraokeStyleSwitch?.IsOn ?? s.LyricKaraokeStyle;
            s.HideBlankLyricLines = HideBlankLyricLinesSwitch?.IsOn ?? s.HideBlankLyricLines;
            if (LyricLineSpacingSlider != null)
            {
                s.LyricLineSpacing = (int)Math.Round(LyricLineSpacingSlider.Value);
            }

            s.LyricAlign = GetComboTagString(LyricAlignCombo, "Center");

            s.OpenDesktopLyricsOnStartup = OpenDesktopLyricsSwitch?.IsOn ?? s.OpenDesktopLyricsOnStartup;
            s.DesktopLyricHideWithoutLyric = DesktopLyricHideWithoutLyricSwitch?.IsOn ?? s.DesktopLyricHideWithoutLyric;
            s.DesktopLyricHideWhenPaused = DesktopLyricHideWhenPausedSwitch?.IsOn ?? s.DesktopLyricHideWhenPaused;
            s.DesktopLyricLockOnStart = DesktopLyricLockOnStartSwitch?.IsOn ?? s.DesktopLyricLockOnStart;
            s.DesktopLyricShowUnlockWhenLocked = DesktopLyricShowUnlockWhenLockedSwitch?.IsOn ?? s.DesktopLyricShowUnlockWhenLocked;
            s.DesktopLyricDoubleLine = DesktopLyricDoubleLineSwitch?.IsOn ?? s.DesktopLyricDoubleLine;
            s.DesktopLyricClickThrough = DesktopLyricClickThroughSwitch?.IsOn ?? s.DesktopLyricClickThrough;
            if (DesktopLyricOpacitySlider != null)
            {
                s.DesktopLyricOpacity = (int)Math.Round(DesktopLyricOpacitySlider.Value);
            }

            s.DesktopLyricFontSize = DesktopLyricFontSizeSlider.Value;
            s.DesktopLyricPlayedColor = string.IsNullOrWhiteSpace(DesktopLyricPlayedColorTextBox.Text)
                ? "#40B4FF"
                : DesktopLyricPlayedColorTextBox.Text.Trim();
            s.DesktopLyricUnplayedColor = string.IsNullOrWhiteSpace(DesktopLyricUnplayedColorTextBox?.Text)
                ? "#F5F5F5"
                : DesktopLyricUnplayedColorTextBox.Text.Trim();

            s.MiniPlayerAlwaysOnTop = MiniAlwaysOnTopSwitch?.IsOn ?? s.MiniPlayerAlwaysOnTop;
            s.OpenMiniPlayerOnStartup = OpenMiniPlayerSwitch?.IsOn ?? s.OpenMiniPlayerOnStartup;

            s.EnableFrostedGlass = FrostedGlassSwitch?.IsOn ?? s.EnableFrostedGlass;
            s.ShowSpectrum = ShowSpectrumSwitch?.IsOn ?? s.ShowSpectrum;
            s.ShowAlbumCover = ShowAlbumCoverSwitch?.IsOn ?? s.ShowAlbumCover;
            s.EnableBackground = EnableBackgroundSwitch?.IsOn ?? s.EnableBackground;
            s.AlbumCoverAsBackground = AlbumCoverAsBackgroundSwitch?.IsOn ?? s.AlbumCoverAsBackground;
            s.BackgroundGaussBlur = BackgroundGaussBlurSwitch?.IsOn ?? s.BackgroundGaussBlur;
            if (GaussBlurRadiusSlider != null)
            {
                s.GaussBlurRadius = (int)Math.Round(GaussBlurRadiusSlider.Value);
            }

            s.UseInnerCoverFirst = UseInnerCoverFirstSwitch?.IsOn ?? s.UseInnerCoverFirst;
            s.CoverFolder = CoverFolderTextBox?.Text?.Trim() ?? string.Empty;
            s.AccentSource = GetComboTagString(AccentSourceCombo, "System");
            s.CustomAccentColor = string.IsNullOrWhiteSpace(_accentHex) ? "#0078D4" : _accentHex;
            s.ProgressBarStyle = WaveformProgressSwitch.IsOn ? "Waveform" : "Gradient";
            s.CustomBackgroundPath = BackgroundPathTextBox?.Text?.Trim() ?? string.Empty;
            s.ThemePreset = GetComboTagString(ThemePresetCombo, "");
            s.ShowPlaylistTitle = ShowTitleColCheck.IsChecked ?? true;
            s.ShowPlaylistArtist = ShowArtistColCheck.IsChecked ?? true;
            s.ShowPlaylistAlbum = ShowAlbumColCheck.IsChecked ?? true;
            s.ShowPlaylistYear = ShowYearColCheck.IsChecked ?? true;
            s.ShowPlaylistDuration = ShowDurationColCheck.IsChecked ?? true;
            s.PlaylistDensity = GetComboTagString(PlaylistDensityCombo, "Comfortable");

            s.CloseAction = GetComboTagString(CloseActionCombo, nameof(CloseWindowAction.Ask));
            s.RestoreLibrary = RestoreLibrarySwitch?.IsOn ?? s.RestoreLibrary;
            s.RestorePlayback = RestorePlaybackSwitch?.IsOn ?? s.RestorePlayback;
            s.AutoRun = AutoRunSwitch?.IsOn ?? s.AutoRun;
            s.GlobalMouseWheelVolume = GlobalMouseWheelVolumeSwitch?.IsOn ?? s.GlobalMouseWheelVolume;
            s.AutoDownloadLyrics = AutoDownloadLyricsSwitch?.IsOn ?? s.AutoDownloadLyrics;
            s.AutoDownloadCover = AutoDownloadCoverSwitch?.IsOn ?? s.AutoDownloadCover;
            s.LyricDownloadService = GetComboTagString(LyricDownloadServiceCombo, "NetEase");
            s.OnlineSearchDefaultSource = GetComboTagString(OnlineSearchSourceCombo, "NetEase");
            s.AudioChannel = GetComboTagString(AudioChannelCombo, "Stereo");
            s.AlwaysOnTop = AlwaysOnTopSwitch?.IsOn ?? s.AlwaysOnTop;
            s.SaveLyricToSongFolder = SaveLyricToSongFolderSwitch?.IsOn ?? s.SaveLyricToSongFolder;
            s.SaveCoverToSongFolder = SaveCoverToSongFolderSwitch?.IsOn ?? s.SaveCoverToSongFolder;
            s.AutoDownloadOnlyWhenTagFull = AutoDownloadOnlyWhenTagFullSwitch?.IsOn ?? s.AutoDownloadOnlyWhenTagFull;

            s.Volume = VolumeSettingSlider.Value;
            if (PlaybackOrderCombo?.SelectedItem is ComboBoxItem playbackItem && playbackItem.Tag is PlaybackOrder order)
            {
                s.PlaybackOrder = order.ToString();
            }

            s.EnableSmtc = EnableSmtcSwitch?.IsOn ?? s.EnableSmtc;
            s.EnableFade = EnableFadeSwitch?.IsOn ?? s.EnableFade;
            if (FadeMsSlider != null)
            {
                s.FadeMilliseconds = (int)Math.Round(FadeMsSlider.Value);
            }

            if (PlaybackRateSlider != null)
            {
                s.PlaybackRate = PlaybackRateSlider.Value;
            }

            s.StopWhenError = StopWhenErrorSwitch?.IsOn ?? s.StopWhenError;
            s.AutoPlayWhenStart = AutoPlayWhenStartSwitch?.IsOn ?? s.AutoPlayWhenStart;
            s.ShowTaskbarProgress = ShowTaskbarProgressSwitch?.IsOn ?? s.ShowTaskbarProgress;
            s.ContinueWhenSwitchPlaylist = ContinueWhenSwitchPlaylistSwitch?.IsOn ?? s.ContinueWhenSwitchPlaylist;

            s.AutoUpdateLibrary = AutoUpdateLibrarySwitch?.IsOn ?? s.AutoUpdateLibrary;
            s.LibraryWatchFolders = (_watchFolders ?? new List<string>()).ToList();
            s.DisableDeleteFromDisk = DisableDeleteFromDiskSwitch?.IsOn ?? s.DisableDeleteFromDisk;
            s.RemoveMissingOnUpdate = RemoveMissingOnUpdateSwitch?.IsOn ?? s.RemoveMissingOnUpdate;
            s.IgnoreTooShortOnUpdate = IgnoreTooShortOnUpdateSwitch?.IsOn ?? s.IgnoreTooShortOnUpdate;
            if (FileTooShortSecNumberBox != null
                && !double.IsNaN(FileTooShortSecNumberBox.Value)
                && !double.IsInfinity(FileTooShortSecNumberBox.Value))
            {
                s.FileTooShortSec = (int)Math.Round(FileTooShortSecNumberBox.Value);
            }

            s.InsertPlaylistAtBegin = InsertPlaylistAtBeginSwitch?.IsOn ?? s.InsertPlaylistAtBegin;
            s.PlaylistDisplayFormat = GetComboTagString(PlaylistDisplayFormatCombo, "ArtistTitle");
            s.RecentPlayedRangeDays = GetComboTagInt(RecentPlayedRangeDaysCombo, 0);
            s.WriteId3v23 = WriteId3v23Combo?.SelectedItem is ComboBoxItem id3Item && id3Item.Tag is bool v23 && v23;
            s.ShowNavFavorites = ShowNavFavoritesSwitch?.IsOn ?? s.ShowNavFavorites;
            s.ShowNavRecent = ShowNavRecentSwitch?.IsOn ?? s.ShowNavRecent;
            s.ShowNavGenre = ShowNavGenreSwitch?.IsOn ?? s.ShowNavGenre;
            s.ShowNavYear = ShowNavYearSwitch?.IsOn ?? s.ShowNavYear;
            s.EnableLastFm = EnableLastFmSwitch?.IsOn ?? s.EnableLastFm;
            s.LastFmHttps = LastFmHttpsSwitch?.IsOn ?? s.LastFmHttps;
            s.LastFmNowPlaying = LastFmNowPlayingSwitch?.IsOn ?? s.LastFmNowPlaying;
            if (LastFmLeastPercentSlider != null)
            {
                s.LastFmLeastPercent = (int)Math.Round(LastFmLeastPercentSlider.Value);
            }

            if (LastFmLeastSecondsSlider != null)
            {
                s.LastFmLeastSeconds = (int)Math.Round(LastFmLeastSecondsSlider.Value);
            }

            s.EnableGlobalHotkeys = EnableGlobalHotkeysSwitch.IsOn;
        }

        private static string GetComboTagString(ComboBox? combo, string fallback)
        {
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                return id;
            }

            return fallback;
        }

        private static int GetComboTagInt(ComboBox? combo, int fallback)
        {
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is int value)
            {
                return value;
            }

            return fallback;
        }

        private void PersistAndApply(Action<AppSettingsState> mutator)
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            AppSettingsStore.Update(mutator);
            AppSettingsState saved2 = AppSettingsStore.Load();
            ThemeColorService.UpdateAccentFromSettings(saved2);
            MainWindow.Instance?.ApplySettingsLive(saved2);
            ApplyBackdropFromSettings();
            RefreshAccentButtonColor();
        }

        /// <summary>主题色保存后:设置窗口自身的强调按钮即时变色(不依赖全局资源刷新)。</summary>
        private void RefreshAccentButtonColor()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                StartupLog.Write("设置窗口主题色刷新: " + s.CustomAccentColor + " source=" + s.AccentSource);
                Windows.UI.Color accent = s.AccentSource == "Custom"
                    ? (ColorPickerWindow.TryParseHex(s.CustomAccentColor, out Windows.UI.Color cc) ? cc : Windows.UI.Color.FromArgb(255, 0, 120, 212))
                    : Windows.UI.Color.FromArgb(255, 0, 120, 212);
                var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
                var lightBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    ColorPickerWindow.TryParseHex(s.CustomAccentColor, out Windows.UI.Color _)
                        ? Windows.UI.Color.FromArgb(255, (byte)(accent.R + (255 - accent.R) * 0.55), (byte)(accent.G + (255 - accent.G) * 0.55), (byte)(accent.B + (255 - accent.B) * 0.55))
                        : accent);
                if (ApplySettingsButton != null)
                {
                    ApplySettingsButton.Background = brush;
                }

                if (SaveSettingsButton != null)
                {
                    SaveSettingsButton.Background = brush;
                }

                // 开关/复选框等强调控件:控件级局部资源 + 重建模板(让资源立即生效)
                if (Content is DependencyObject root)
                {
                    int toggleCount = 0;
                    foreach (Microsoft.UI.Xaml.Controls.ToggleSwitch ts in FindDescendants<Microsoft.UI.Xaml.Controls.ToggleSwitch>(root))
                    {
                        toggleCount++;
                        ts.Resources["ToggleSwitchFillOn"] = brush;
                        ts.Resources["ToggleSwitchFillOnPointerOver"] = lightBrush;
                        ts.Resources["ToggleSwitchFillOnPressed"] = brush;
                        var tsTpl = ts.Template;
                        ts.ClearValue(Microsoft.UI.Xaml.Controls.Control.TemplateProperty);
                        ts.Template = tsTpl;
                    }

                    StartupLog.Write("设置窗口开关数量: " + toggleCount);
                    foreach (Microsoft.UI.Xaml.Controls.Primitives.RangeBase rb in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.RangeBase>(root))
                    {
                        ThemeColorService.ApplySliderAccent(rb, accent);
                    }

                    foreach (Microsoft.UI.Xaml.Controls.CheckBox cb in FindDescendants<Microsoft.UI.Xaml.Controls.CheckBox>(root))
                    {
                        cb.Resources["CheckBoxCheckBackgroundStroke"] = brush;
                        cb.Resources["CheckBoxCheckBackgroundFill"] = brush;
                        cb.Resources["CheckBoxCheckBackgroundStrokePointerOver"] = lightBrush;
                        var cbTpl = cb.Template;
                        cb.ClearValue(Microsoft.UI.Xaml.Controls.Control.TemplateProperty);
                        cb.Template = cbTpl;
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>遍历可视树收集指定类型后代。</summary>
        private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (T sub in FindDescendants<T>(child))
                {
                    yield return sub;
                }
            }
        }

        private void PersistAllFromUi()
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            bool prevAutoRun = AppSettingsStore.Load().AutoRun;
            PersistAndApply(PopulateStateFromUi);

            AppSettingsState saved = AppSettingsStore.Load();
            if (saved.AutoRun != prevAutoRun)
            {
                ApplyAutoRunRegistry(saved.AutoRun);
            }

            MainWindow.Instance?.ApplyOverlayPreferenceFromSettings(saved);
        }

        private static void ApplyAutoRunRegistry(bool enable)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoRunRegistryKey, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(AutoRunRegistryKey);
                if (key == null)
                {
                    return;
                }

                if (enable)
                {
                    string exePath = Environment.ProcessPath
                        ?? Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe");
                    key.SetValue(AutoRunValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AutoRunValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
            }
        }

        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                ShowPanel(tag);
            }
        }

        private void ShowPanel(string tag)
        {
            PanelLyrics.Visibility = tag == "Lyrics" ? Visibility.Visible : Visibility.Collapsed;
            PanelAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PanelGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            PanelPlayback.Visibility = tag == "Playback" ? Visibility.Visible : Visibility.Collapsed;
            PanelMediaLib.Visibility = tag == "MediaLib" ? Visibility.Visible : Visibility.Collapsed;
            PanelHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SettingCheck_Changed(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();
        }

        private void SettingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();
        }

        private void OnThemeColorChangedSettings(Windows.UI.Color accent)
        {
            RefreshAccentButtonColor();
            if (accent != _lastAppliedAccent)
            {
                _lastAppliedAccent = accent;
                PromptThemeRestart();
            }
        }

        /// <summary>主题色已更改:部分元素已即时更新,其余需重启完全生效,弹窗询问是否立即重启。</summary>
        private async void PromptThemeRestart()
        {
            if (_themeRestartPromptShown)
            {
                return;
            }

            _themeRestartPromptShown = true;
            try
            {
                ContentDialog dialog = new()
                {
                    Title = "主题色已更改",
                    Content = "部分界面元素已即时更新,其余(设置窗口开关、迷你播放器等)需要重启播放器才能完全生效。是否立即重启?",
                    PrimaryButtonText = "立即重启",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    MainWindow.Instance?.RestartApp();
                }
            }
            catch
            {
            }
        }

        private void SettingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PersistAllFromUi();
        }

        private async void BrowseBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".webp");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    BackgroundPathTextBox.Text = file.Path;
                    PersistAllFromUi();
                }
            }
            catch
            {
            }
        }

        private void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            BackgroundPathTextBox.Text = string.Empty;
            PersistAllFromUi();
        }

        private void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();
            Close();
        }

        private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettingsState();
            AppSettingsStore.Save(defaults);
            LoadFromStore();
            PersistAllFromUi();
        }

        private void SettingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            if (ReferenceEquals(sender, VolumeSettingSlider))
            {
                VolumeValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
            }
            else if (ReferenceEquals(sender, FadeMsSlider))
            {
                FadeMsValueText.Text = $"{(int)Math.Round(e.NewValue)} ms";
            }
            else if (ReferenceEquals(sender, PlaybackRateSlider))
            {
                PlaybackRateValueText.Text = $"{e.NewValue:0.00}×";
            }
            else if (ReferenceEquals(sender, LyricLineSpacingSlider))
            {
                LyricLineSpacingValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, DesktopLyricOpacitySlider))
            {
                DesktopLyricOpacityValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, DesktopLyricFontSizeSlider))
            {
                DesktopLyricFontSizeValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, GaussBlurRadiusSlider))
            {
                GaussBlurRadiusValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, LastFmLeastPercentSlider))
            {
                LastFmLeastPercentValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
            }
            else if (ReferenceEquals(sender, LastFmLeastSecondsSlider))
            {
                LastFmLeastSecondsValueText.Text = $"{(int)Math.Round(e.NewValue)} 秒";
            }

            PersistAllFromUi();
        }

        private void SettingTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateColorSwatches();
            PersistAllFromUi();
        }

        private string _accentHex = "#0078D4";

        private void AccentSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAccentColorButton();
        }

        private void ThemePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string preset = GetComboTagString(ThemePresetCombo, "");
            if (string.IsNullOrWhiteSpace(preset))
            {
                // 选择"无":回到自由选择,显示主题色选项
                UpdateAccentColorButton();
                return;
            }

            // 应用预设颜色:切到自定义 + 设置颜色
            _accentHex = preset;
            SelectComboByTag(AccentSourceCombo, "Custom");
            UpdateAccentColorButton();
            PersistAllFromUi();
        }

        private void UpdateAccentColorButton()
        {
            if (AccentColorButton == null)
            {
                return;
            }

            bool hasPreset = !string.IsNullOrWhiteSpace(GetComboTagString(ThemePresetCombo, ""));
            if (AccentSourceRow != null)
            {
                AccentSourceRow.Visibility = hasPreset ? Visibility.Collapsed : Visibility.Visible;
            }

            AccentColorButton.Visibility = !hasPreset
                && GetComboTagString(AccentSourceCombo, "System") == "Custom"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (ColorPickerWindow.TryParseHex(_accentHex, out Windows.UI.Color color))
            {
                AccentColorSwatch.Background = new SolidColorBrush(color);
            }
        }

        private void AccentColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = string.IsNullOrWhiteSpace(_accentHex) ? "#0078D4" : _accentHex;
                ColorPickerWindow.Show("自定义主题色", current, hex =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _accentHex = hex;
                        SelectComboByTag(ThemePresetCombo, "");
                        UpdateAccentColorButton();
                        PersistAllFromUi();
                    });
                });
            }
            catch (Exception ex)
            {
                _ = ShowInfoDialogAsync("打开调色板失败", ex.Message);
            }
        }

        private void PickPlayedColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = DesktopLyricPlayedColorTextBox?.Text ?? "#40B4FF";
                ColorPickerWindow.Show("已播放颜色", current, hex =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DesktopLyricPlayedColorTextBox != null)
                        {
                            DesktopLyricPlayedColorTextBox.Text = hex;
                        }

                        UpdateColorSwatches();
                        PersistAllFromUi();
                    });
                });
            }
            catch (Exception ex)
            {
                _ = ShowInfoDialogAsync("打开调色板失败", ex.Message);
            }
        }

        private void PickUnplayedColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = DesktopLyricUnplayedColorTextBox?.Text ?? "#F5F5F5";
                ColorPickerWindow.Show("未播放颜色", current, hex =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DesktopLyricUnplayedColorTextBox != null)
                        {
                            DesktopLyricUnplayedColorTextBox.Text = hex;
                        }

                        UpdateColorSwatches();
                        PersistAllFromUi();
                    });
                });
            }
            catch (Exception ex)
            {
                _ = ShowInfoDialogAsync("打开调色板失败", ex.Message);
            }
        }

        private void UpdateColorSwatches()
        {
            SetSwatch(DesktopLyricPlayedColorSwatch, DesktopLyricPlayedColorTextBox?.Text, "#40B4FF");
            SetSwatch(DesktopLyricUnplayedColorSwatch, DesktopLyricUnplayedColorTextBox?.Text, "#F5F5F5");
        }

        private static void SetSwatch(Border? swatch, string? hex, string fallback)
        {
            if (swatch == null)
            {
                return;
            }

            if (!ColorPickerWindow.TryParseHex(hex, out Windows.UI.Color color)
                && !ColorPickerWindow.TryParseHex(fallback, out color))
            {
                return;
            }

            swatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }

        private void FileTooShortSecNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loadingUi)
            {
                return;
            }

            PersistAllFromUi();
        }

        private async void BrowseLyricFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            string? path = await PickFolderAsync();
            if (path == null)
            {
                return;
            }

            LyricFolderTextBox.Text = path;
            PersistAllFromUi();
        
            }
            catch
            {
            }}

        private async void BrowseCoverFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            string? path = await PickFolderAsync();
            if (path == null)
            {
                return;
            }

            CoverFolderTextBox.Text = path;
            PersistAllFromUi();
        
            }
            catch
            {
            }}

        private async void AddWatchFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = await PickFolderAsync();
            if (path == null)
            {
                return;
            }

            if (!_watchFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _watchFolders.Add(path);
                RefreshWatchFoldersList();
                PersistAllFromUi();
            }
        }

        private void RemoveWatchFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (LibraryWatchFoldersListView.SelectedItem is string selected)
            {
                _watchFolders.RemoveAll(f => string.Equals(f, selected, StringComparison.OrdinalIgnoreCase));
                RefreshWatchFoldersList();
                PersistAllFromUi();
            }
        }

        private async System.Threading.Tasks.Task<string?> PickFolderAsync()
        {
            FolderPicker picker = new();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        private void OpenConfigDirButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = AppSettingsStore.GetConfigDirectory();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private async void ClearRecentPlayedButton_Click(object sender, RoutedEventArgs e)
        {
            TrackStatsStore.ClearRecentlyPlayed();
            await ShowInfoDialogAsync("已清空", "最近播放记录已清除。");
        }

        private async void RegisterAssociationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe");
                FileAssociationHelper.Register(exePath);
                await ShowInfoDialogAsync("已注册", "常见音频格式已关联到 Celeste Music Player。");
            }
            catch (Exception ex)
            {
                await ShowInfoDialogAsync("注册失败", ex.Message);
            }
        }

        private async void UnregisterAssociationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileAssociationHelper.Unregister();
                await ShowInfoDialogAsync("已取消", "已移除 Celeste Music Player 的文件关联。");
            }
            catch (Exception ex)
            {
                await ShowInfoDialogAsync("取消失败", ex.Message);
            }
        }

        private async System.Threading.Tasks.Task ShowInfoDialogAsync(string title, string message)
        {
            ContentDialog dialog = new()
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private Windows.UI.Color _lastAppliedAccent;
        private bool _themeRestartPromptShown;

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        private async void HotkeyDefaultsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {

            if (e.ClickedItem is not HotkeyDefaultItem item)
            {
                return;
            }

            string? combo = await CaptureHotkeyAsync(item.Description);
            if (combo == null)
            {
                return;
            }

            if (combo == "__RESET__")
            {
                AppSettingsStore.Update(s => s.CustomHotkeys?.Remove(item.Action.ToString()));
                MainWindow.Instance?.ApplySettingsLive(AppSettingsStore.Load());
                ReloadHotkeyList();
                return;
            }

            AppSettingsStore.Update(s =>
            {
                s.CustomHotkeys ??= new Dictionary<string, string>();
                s.CustomHotkeys[item.Action.ToString()] = combo;
            });
            MainWindow.Instance?.ApplySettingsLive(AppSettingsStore.Load());
            ReloadHotkeyList();
        
            }
            catch
            {
            }}

        private async Task<string?> CaptureHotkeyAsync(string actionName)
        {
            var hint = new TextBlock
            {
                Text = $"为「{actionName}」按下快捷键（Esc 取消，退格恢复默认）",
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var captureBox = new TextBox
            {
                IsReadOnly = true,
                PlaceholderText = "在此按下快捷键…"
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(hint);
            panel.Children.Add(captureBox);

            ContentDialog dialog = new()
            {
                Title = "录制快捷键",
                Content = panel,
                CloseButtonText = "取消",
                XamlRoot = Content.XamlRoot
            };

            string? result = null;
            bool done = false;
            captureBox.KeyDown += (s, e) =>
            {
                if (done)
                {
                    return;
                }

                VirtualKey key = e.Key;
                if (key == VirtualKey.Escape)
                {
                    done = true;
                    dialog.Hide();
                    return;
                }

                if (key == VirtualKey.Back)
                {
                    done = true;
                    result = "__RESET__";
                    dialog.Hide();
                    return;
                }

                if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                    or VirtualKey.LeftControl or VirtualKey.RightControl
                    or VirtualKey.LeftMenu or VirtualKey.RightMenu
                    or VirtualKey.LeftShift or VirtualKey.RightShift
                    or VirtualKey.LeftWindows or VirtualKey.RightWindows)
                {
                    return;
                }

                string modifiers = string.Empty;
                uint mod = 0;
                if (IsKeyDown(VirtualKey.Control)) { mod |= 0x0002; modifiers += "Ctrl+"; }
                if (IsKeyDown(VirtualKey.Menu)) { mod |= 0x0001; modifiers += "Alt+"; }
                if (IsKeyDown(VirtualKey.Shift)) { mod |= 0x0004; modifiers += "Shift+"; }
                if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
                {
                    mod |= 0x0008;
                    modifiers += "Win+";
                }

                // 必须包含 Ctrl / Alt / Win 之一，避免纯字符键误注册
                if ((mod & (0x0001 | 0x0002 | 0x0008)) == 0)
                {
                    return;
                }

                string? keyName = VirtualKeyToName(key, (mod & 0x0004) != 0);
                if (keyName == null || !GlobalHotkeyService.TryParseHotkey(modifiers + keyName, out _, out _))
                {
                    return;
                }

                done = true;
                result = modifiers + keyName;
                dialog.Hide();
            };

            await dialog.ShowAsync();
            return result;
        }

        private static bool IsKeyDown(VirtualKey key)
            => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

        private static string? VirtualKeyToName(VirtualKey key, bool withShift)
        {
            if (key >= VirtualKey.A && key <= VirtualKey.Z)
            {
                return key.ToString();
            }

            if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            {
                return ((char)('0' + (key - VirtualKey.Number0))).ToString();
            }

            if (key >= VirtualKey.F1 && key <= VirtualKey.F12)
            {
                return key.ToString();
            }

            return key switch
            {
                VirtualKey.Space => "Space",
                VirtualKey.Left => "Left",
                VirtualKey.Right => "Right",
                VirtualKey.Up => "Up",
                VirtualKey.Down => "Down",
                VirtualKey.Enter => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "PageUp",
                VirtualKey.PageDown => "PageDown",
                VirtualKey.Insert => "Insert",
                VirtualKey.Delete => "Delete",
                // 注意：Windows.System.VirtualKey 没有 OemPlus/OemMinus 成员，
                // OEM 键（= / - / +）无法录制，改用字母键或 F 键即可。
                _ => null
            };
        }

        // ---- Last.fm 凭据 ----

        private void LoadLastFmCredentialsIntoUi()
        {
            LastFmCredentials c = LastFmScrobbler.LoadCredentials();
            if (LastFmApiKeyTextBox != null)
            {
                LastFmApiKeyTextBox.Text = c.ApiKey;
            }

            if (LastFmSharedSecretPasswordBox != null)
            {
                LastFmSharedSecretPasswordBox.Password = c.SharedSecret;
            }

            if (LastFmUsernameTextBox != null)
            {
                LastFmUsernameTextBox.Text = c.Username;
            }

            UpdateLastFmStatus(c);
        }

        private void UpdateLastFmStatus(LastFmCredentials c)
        {
            if (LastFmStatusText == null)
            {
                return;
            }

            if (LastFmScrobbler.IsConfigured())
            {
                string display = string.IsNullOrWhiteSpace(c.Username)
                    ? (string.IsNullOrWhiteSpace(c.SessionKey) ? "已保存 Key" : c.SessionKey[..Math.Min(8, c.SessionKey.Length)] + "…")
                    : c.Username;
                LastFmStatusText.Text = "已配置：" + display;
            }
            else
            {
                LastFmStatusText.Text = "未配置：开关无效";
            }
        }

        private void LastFmCredentials_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            SaveLastFmCredentialsFromUi();
        }

        private void LastFmSaveCredentialsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLastFmCredentialsFromUi();
        }

        private void SaveLastFmCredentialsFromUi()
        {
            LastFmCredentials c = LastFmScrobbler.LoadCredentials();
            c.ApiKey = LastFmApiKeyTextBox?.Text?.Trim() ?? string.Empty;
            c.SharedSecret = LastFmSharedSecretPasswordBox?.Password ?? string.Empty;
            LastFmScrobbler.SaveCredentials(c);
            UpdateLastFmStatus(c);
        }

        private async void LastFmLoginButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLastFmCredentialsFromUi();
            string username = LastFmUsernameTextBox?.Text?.Trim() ?? string.Empty;
            string password = LastFmLoginPasswordBox?.Password ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                await ShowInfoDialogAsync("登录 Last.fm", "请输入用户名和密码。");
                return;
            }

            LastFmLoginButton.IsEnabled = false;
            try
            {
                bool ok = await LastFmScrobbler.TryGetMobileSessionAsync(username, password);
                if (ok)
                {
                    LastFmCredentials c = LastFmScrobbler.LoadCredentials();
                    if (LastFmUsernameTextBox != null)
                    {
                        LastFmUsernameTextBox.Text = c.Username;
                    }

                    UpdateLastFmStatus(c);
                    await ShowInfoDialogAsync("登录成功", "Last.fm 会话已保存。");
                }
                else
                {
                    await ShowInfoDialogAsync("登录失败", "请检查 API Key、SharedSecret、用户名和密码。");
                }
            }
            finally
            {
                LastFmLoginButton.IsEnabled = true;
            }
        }

        private sealed class HotkeyDefaultItem
        {
            public HotkeyDefaultItem(HotkeyAction action, string shortcut, string description)
            {
                Action = action;
                Shortcut = shortcut;
                Description = description;
            }

            public HotkeyAction Action { get; }
            public string Shortcut { get; }
            public string Description { get; }
        }
    }
}
