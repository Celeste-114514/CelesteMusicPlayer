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
        private bool _loadAsyncIgnore;
        private string? _loadedOutputDeviceId;
        /// <summary>设备下拉用 seed 重填时若 seed 在新模式下未能匹配，置 true；
        /// 用于在持久化时保留用户原先保存的 OutputDeviceId，避免回落值覆盖用户记忆。</summary>
        private bool _deviceSeedMatchFail;
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
        };

        /// <summary>在线搜索默认平台（含 iTunes；歌词源不含 iTunes，因 iTunes 不提供歌词）。</summary>
        private static readonly (string Id, string Label)[] OnlineSearchSourceOptions =
        {
            ("NetEase", "网易云音乐"),
            ("QQ", "QQ音乐"),
            ("iTunes", "Apple Music"),
        };

        private static readonly (string Id, string Label)[] AudioChannelOptions =
        {
            ("Stereo", "立体声"),
            ("Left", "仅左声道"),
            ("Right", "仅右声道")
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

        /// <summary>打开设置窗口并定位到「媒体库」板块。</summary>
        public static void ShowMediaLibrary()
        {
            ShowOrActivate();
            SettingsWindow? w = _instance;
            if (w == null)
            {
                return;
            }

            try
            {
                w.DispatcherQueue.TryEnqueue(w.OpenMediaLibrary);
            }
            catch
            {
            }
        }

        /// <summary>选中并显示设置窗口的「媒体库」导航板块。</summary>
        private void OpenMediaLibrary()
        {
            try
            {
                foreach (object o in SettingsNav.MenuItems)
                {
                    if (o is NavigationViewItem item && item.Tag is string t && t == "MediaLib")
                    {
                        SettingsNav.SelectedItem = item;
                        break;
                    }
                }

                ShowPanel("MediaLib");
            }
            catch
            {
            }
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
            FillCombo(OnlineSearchSourceCombo, OnlineSearchSourceOptions);
            FillCombo(AudioChannelCombo, AudioChannelOptions);
            OutputModeCombo.Items.Clear();
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "WASAPI 共享（系统混音）", Tag = "Shared" });
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "WASAPI 独占（HiFi）", Tag = "WasapiExclusive" });
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "ASIO（专有声卡驱动）", Tag = "Asio" });

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

                // 流媒体服务地址
                if (StreamingUrlBox != null)
                {
                    StreamingUrlBox.Text = s.StreamingServiceUrl;
                }

                // 歌词
                SetToggle(PreferInnerLyricSwitch, s.PreferInnerLyric);
                SetToggle(LyricFuzzyMatchSwitch, s.LyricFuzzyMatch);
                SetToggle(ShowLyricTranslateSwitch, s.ShowLyricTranslate);
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

                // 音频输出模式 + 设备
                SelectComboByTag(OutputModeCombo, s.OutputMode);
                UpdateVolumeSettingLockForMode(); // 模式决定设置页音量条是否锁定
                StartupLog.Write("设置加载 输出模式=" + (s.OutputMode ?? "null") + " 下拉选中=" + (OutputModeCombo?.SelectedItem is ComboBoxItem _m && _m.Tag is string _mt ? _mt : "(null)") + " 设备=" + (s.OutputDeviceId ?? "null"));
                _loadAsyncIgnore = true;
                _ = InitOutputDeviceComboAsync(s.OutputDeviceId);

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

        /// <summary>设置页音量滑条：HiFi 独占下调 DAC 驱动音量，共享下调数字音量；两者都可调。</summary>
        private void UpdateVolumeSettingLockForMode()
        {
            if (VolumeSettingSlider == null)
            {
                return;
            }

            // 音量滑条在共享与 HiFi 独占下都可调：共享调 MediaPlayer 数字音量（系统混音），
            // HiFi 独占调 DAC 设备/驱动级主音量（不破坏 bit-perfect，方便无实体音量键的小尾巴）。
            // 因此不再锁定/禁用，仅统一用保存音量回填并显示。
            double saved = AppSettingsStore.Load().Volume;
            SetSlider(VolumeSettingSlider, saved);
            SetTextBlock(VolumeValueText, $"{(int)Math.Round(saved)}%");
        }

        /// <summary>切换输出模式到 HiFi 独占（WASAPI 独占 / ASIO）时弹一次说明，提醒用户：
        /// 播放器数字音量固定 100%（bit-perfect），请用 DAC/驱动音量或程序音量条调响（无实体音量键的小尾巴也能量轻）。</summary>
        private void MaybeWarnHiFiVolume()
        {
            string mode = GetSelectedOutputMode();
            bool hifi = string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase);
            if (!hifi)
            {
                return;
            }

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "HiFi 独占输出提示",
                    Content = "已切换到 WASAPI 独占 / ASIO，播放器将尽力直通（bit-perfect）。\n\n" +
                              "播放器内部数字音量固定为 100%（不参与衰减），音量请在你的 DAC / 耳机 / 驱动端调节，或使用右下角音量条调节 DAC 音量。\n\n" +
                              "如果你的设备没有实体音量键（如无旋钮的小尾巴），用音量条即可调轻，不会破坏直通音质。",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
            catch
            {
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

        /// <summary>异步枚举输出设备并填充下拉框，默认选中 OutputDeviceId（空=系统默认）。</summary>
        private async System.Threading.Tasks.Task InitOutputDeviceComboAsync(string selectedId)
        {
            // 异步填充全程置 _loadingUi=true：填清空/重填/选中任何 SelectionChanged 都不触发保存，
            // 避免把中间态（Shared/空）写回覆盖用户设置；finally 恢复。
            _loadingUi = true;
            try
            {
                _deviceSeedMatchFail = false; // 本次 seed 重填的匹配结果在下方判定
                var prevSelection = new HashSet<string>();
                if (OutputDeviceCombo?.SelectedItem is ComboBoxItem cur && cur.Tag is string cid)
                {
                    prevSelection.Add(cid);
                }

                bool asioMode = string.Equals(GetSelectedOutputMode(), "Asio", StringComparison.OrdinalIgnoreCase);
                _loadedOutputDeviceId = selectedId;
                if (OutputDeviceCombo == null)
                {
                    return;
                }

                OutputDeviceCombo.Items.Clear();

                if (asioMode)
                {
                    // ASIO 模式：枚举 ASIO 驱动（驱动名即设备标识），无默认可选时提供“系统默认”占位
                    var drivers = HiFiOutputBackend.EnumerateAsioDrivers();
                    StartupLog.Write("ASIO 驱动下拉枚举 数量=" + drivers.Count + " 已选=" + selectedId);
                    foreach (string drv in drivers)
                    {
                        StartupLog.Write("  ASIO driver=" + drv);
                    }
                    if (drivers.Count == 0)
                    {
                        OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "（未检测到 ASIO 驱动）", Tag = "" });
                    }
                    else
                    {
                        foreach (string drv in drivers)
                        {
                            OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = drv, Tag = drv });
                        }
                    }

                    // 选中已保存的驱动名；无匹配回落第一个
                    bool asioMatched = SelectRenderDeviceCombo(OutputDeviceCombo, selectedId);
                    if (!asioMatched && OutputDeviceCombo.Items.Count > 0)
                    {
                        OutputDeviceCombo.SelectedIndex = 0;
                        _deviceSeedMatchFail = !string.IsNullOrWhiteSpace(selectedId);
                    }
                    return;
                }

                // WASAPI 模式：用 NAudio 枚举渲染设备（与 HiFi 独占输出同源，ID 稳定）
                var devices = HiFiOutputBackend.EnumerateWasapiDevices();
                string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
                StartupLog.Write("输出设备下拉枚举 数量=" + devices.Count + " 默认=" + defaultId + " 已选=" + selectedId);
                foreach (var dev in devices)
                {
                    StartupLog.Write("  设备 id=" + dev.Id + " name=" + dev.Name);
                }

                // 第一项：系统默认（Tag 为空字符串）
                var defaultItem = new ComboBoxItem { Content = "系统默认", Tag = "" };
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(defaultItem, "跟随 Windows 默认输出设备");
                OutputDeviceCombo.Items.Add(defaultItem);
                foreach ((string id, string name) in devices)
                {
                    string label = string.Equals(id, defaultId, System.StringComparison.OrdinalIgnoreCase)
                        ? name + " (默认)"
                        : name;
                    OutputDeviceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                }

                // 选中保存的设备（或系统默认）；若用户已手动选过则保留
                string target = string.IsNullOrWhiteSpace(selectedId) ? "" : selectedId;
                if (prevSelection.Count > 0 && string.IsNullOrEmpty(target))
                {
                    target = prevSelection.First();
                }

                bool wasapiMatched = SelectRenderDeviceCombo(OutputDeviceCombo, target);
                _deviceSeedMatchFail = !wasapiMatched && !string.IsNullOrWhiteSpace(selectedId);
            }
            finally
            {
                _loadAsyncIgnore = false; // 无论如何都复位，防止永真拦截用户后续保存
                _loadingUi = false; // 异步填充完成，恢复可保存
            }
        }

        /// <summary>输出模式切换：Shared/独占/ASIO 变化时刷新设备下拉列表（WASAPI 设备 ⇄ ASIO 驱动）。</summary>
        private void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            // 先在 _loadingUi 归位前持久化新的输出模式（切换本身必须保存），
            // 再重填设备列表；InitOutputDeviceComboAsync 内部会置 _loadingUi=true 并复位，
            // 若在持久化之后再调用，模式写入不会被 _loadingUi 早退拦截。
            AppSettingsStore.Update(s => s.OutputMode = GetSelectedOutputMode());
            UpdateVolumeSettingLockForMode(); // 切换模式即时刷新设置页音量条锁定态
            MaybeWarnHiFiVolume(); // 切到 HiFi 独占时提醒用户（无实体音量键的小尾巴用滑块调 DAC 音量）

            // 种子设备用「已持久化的 OutputDeviceId」而非当前下拉旧选择：
            // 切换模式时旧列表里的设备 id（如 WASAPI MMDevice id / 旧 ASIO 驱动名）在新模式下不匹配，
            // 若以它作种子会让回落覆盖用户原先保存的设备选择。持久化值才是用户真正想要保留的。
            string seedDevice = AppSettingsStore.Load().OutputDeviceId;
            if (string.IsNullOrWhiteSpace(seedDevice))
            {
                seedDevice = GetSelectedOutputDeviceId();
            }

            // InitOutputDeviceComboAsync 方法体无 await（同步执行），这里同步完成重填；
            // 用 try/catch 兜底，避免枚举/UI 异常留下空下拉并在随后持久化时误写空设备。
            try
            {
                InitOutputDeviceComboAsync(seedDevice).GetAwaiter().GetResult();
            }
            catch
            {
                _deviceSeedMatchFail = true; // 视为未匹配，保留用户已有设备设置
            }

            PersistAllFromUi();
        }

        /// <summary>选中设备下拉：空 = 系统默认；否则按去掉 \?\ 前缀的设备 ID 匹配，防回显成“系统默认”。
        /// 返回是否成功按 <paramref name="selectedId"/> 精确命中（空串视为命中，回落时返回 false）。</summary>
        private bool SelectRenderDeviceCombo(ComboBox? combo, string selectedId)
        {
            if (combo == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedId))
            {
                SelectComboByTag(combo, "");
                return true;
            }

            // NAudio MMDevice.ID / ASIO 驱动名精确匹配（与枚举/保存/输出同源），忽略大小写
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag is string id
                    && string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return true;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0; // 匹配失败回落系统默认/首个驱动
            }

            return false;
        }

        /// <summary>读取当前选中输出设备 ID（空字符串 = 系统默认）。</summary>
        private string GetSelectedOutputMode()
        {
            if (OutputModeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string mode)
            {
                return mode;
            }

            return "Shared";
        }

        private string GetSelectedOutputDeviceId()
        {
            if (OutputDeviceCombo?.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                return id;
            }

            return string.Empty;
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
            s.StreamingServiceUrl = StreamingUrlBox?.Text?.Trim() ?? "";
            s.AudioChannel = GetComboTagString(AudioChannelCombo, "Stereo");
            s.AlwaysOnTop = AlwaysOnTopSwitch?.IsOn ?? s.AlwaysOnTop;
            s.SaveLyricToSongFolder = SaveLyricToSongFolderSwitch?.IsOn ?? s.SaveLyricToSongFolder;
            s.SaveCoverToSongFolder = SaveCoverToSongFolderSwitch?.IsOn ?? s.SaveCoverToSongFolder;
            s.AutoDownloadOnlyWhenTagFull = AutoDownloadOnlyWhenTagFullSwitch?.IsOn ?? s.AutoDownloadOnlyWhenTagFull;

            // 音量滑条在共享与 HiFi 独占下都可调：HiFi 下调节的是 DAC 设备/驱动级主音量（不破坏 bit-perfect），
            // 一并持久化，切换模式/重启后沿用用户设定。
            s.Volume = VolumeSettingSlider.Value;
            if (PlaybackOrderCombo?.SelectedItem is ComboBoxItem playbackItem && playbackItem.Tag is PlaybackOrder order)
            {
                s.PlaybackOrder = order.ToString();
            }

            s.EnableSmtc = EnableSmtcSwitch?.IsOn ?? s.EnableSmtc;
            // 若本次设备下拉是「用 seed 重填但 seed 未匹配」而回落（例如从 WASAPI 切到 ASIO 时，
            // 旧 WASAPI MMDevice id 不在 ASIO 驱动列表），则不覆盖用户先前保存的 OutputDeviceId，
            // 等用户在当前列表里主动选定后再写入，避免回落值静默丢失设备记忆。一次性消费后立即复位，
            // 避免残留阻塞用户后续手动改设备。
            bool deviceSeedFail = _deviceSeedMatchFail;
            _deviceSeedMatchFail = false;
            if (!deviceSeedFail)
            {
                s.OutputDeviceId = GetSelectedOutputDeviceId();
            }

            s.OutputMode = GetSelectedOutputMode();
            StartupLog.Write("设置保存 输出模式=" + (s.OutputMode ?? "null") + " 设备=" + (s.OutputDeviceId ?? "null"));
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
            if (_loadingUi || !_uiReady || _loadAsyncIgnore)
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

            MainWindow.Instance?.ApplySettingsLive(AppSettingsStore.Load());
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
            PanelLibraryHealth.Visibility = tag == "LibraryHealth" ? Visibility.Visible : Visibility.Collapsed;
            PanelStreaming.Visibility = tag == "Streaming" ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            string url = StreamingUrlBox?.Text?.Trim() ?? "";
            if (url.Length == 0)
            {
                StreamingStatusText.Text = "请先填写服务地址（http://<WSL-IP>:21010）";
                return;
            }

            AppSettingsStore.Update(x => x.StreamingServiceUrl = url);
            StreamingServiceClient.ServiceBaseUrl = url;
            StreamingStatusText.Text = "检测中…";
            var ping = await StreamingServiceClient.PingAsync();
            if (ping == null || !ping.Ok)
            {
                StreamingStatusText.Text = "连接失败：请确认 WSL 插件服务已运行、地址正确（含端口 21010）。";
                return;
            }

            var plats = await StreamingServiceClient.GetPlatformsAsync();
            if (plats is { Ok: true } && plats.Platforms.Length > 0)
            {
                StreamingStatusText.Text = "连接成功，可用平台：" + string.Join("、", plats.Platforms);
            }
            else
            {
                StreamingStatusText.Text = "连接成功（未返回平台）：" + (plats?.Error ?? "");
            }
        }

        private async void AmLoginButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowInfoDialogAsync("Apple Music 登录", "登录功能待接入（需 Apple Music 订阅账号）。接入后会在此显示登录状态，并用于加载歌词与在线下载。");
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

        private async void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeChoices.Add("设置备份", new List<string> { ".json" });
                picker.SuggestedFileName = "celeste-settings-backup";
                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                bool ok = AppSettingsStore.ExportTo(file.Path);
                if (ok)
                {
                    await ShowInfoDialogAsync("导出成功", "设置已导出到：\n" + file.Path);
                }
                else
                {
                    await ShowInfoDialogAsync("导出失败", "没有可导出的设置文件。");
                }
            }
            catch (Exception ex)
            {
                await ShowInfoDialogAsync("导出失败", ex.Message);
            }
        }

        private async void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".json");
                picker.FileTypeFilter.Add(".txt"); // 兼容旧的手动备份
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                bool ok = AppSettingsStore.ImportFrom(file.Path);
                if (ok)
                {
                    await ShowInfoDialogAsync("导入成功", "设置已恢复。部分设置在重启后完全生效。\n（导入前已自动备份当前设置到配置目录）");
                }
                else
                {
                    await ShowInfoDialogAsync("导入失败", "无法从该文件恢复设置（文件不存在或格式不符）。");
                }
            }
            catch (Exception ex)
            {
                await ShowInfoDialogAsync("导入失败", ex.Message);
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

        // ---- 曲库健康 ----
        private void HealthScanButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = MainWindow.Instance?.GetCurrentPlaylistSnapshot();
            var rows = new List<HealthIssueRow>();
            int total = 0, missing = 0;
            if (snapshot != null)
            {
                total = snapshot.Count;
                foreach (var item in snapshot)
                {
                    var issues = new List<string>();
                    string path = item.FilePath ?? string.Empty;
                    bool fileMissing = string.IsNullOrWhiteSpace(path) || !File.Exists(path);
                    if (fileMissing)
                    {
                        missing++;
                        issues.Add("文件缺失");
                    }

                    if (string.IsNullOrWhiteSpace(item.Title)) issues.Add("无标题");
                    if (string.IsNullOrWhiteSpace(item.Artist) || item.Artist == "未知艺术家") issues.Add("无艺术家");
                    if (string.IsNullOrWhiteSpace(item.Album) || item.Album == "未知专辑") issues.Add("无专辑");
                    if (!fileMissing && item.Duration <= TimeSpan.Zero) issues.Add("时长异常");
                    if (issues.Count > 0)
                    {
                        rows.Add(new HealthIssueRow { Path = string.IsNullOrWhiteSpace(path) ? "（空路径）" : path, Issues = string.Join("、", issues) });
                    }
                }
            }

            HealthResultsList.ItemsSource = rows;
            HealthSummaryText.Text = $"共 {total} 首，存在 {total - missing} 首，失效 {missing} 首；发现 {rows.Count} 个问题项。";
        }

        private async void HealthRemoveMissingButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = MainWindow.Instance?.GetCurrentPlaylistSnapshot();
            if (snapshot == null)
            {
                return;
            }

            List<string> missing = snapshot
                .Where(i => string.IsNullOrWhiteSpace(i.FilePath) || !System.IO.File.Exists(i.FilePath))
                .Select(i => i.FilePath ?? string.Empty)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (missing.Count == 0)
            {
                await ShowInfoDialogAsync("无需移除", "当前曲库没有失效文件。");
                return;
            }

            MainWindow.Instance?.RemoveFilesFromCurrentPlaylist(missing);
            await ShowInfoDialogAsync("已移除", $"已从曲库索引移除 {missing.Count} 个失效条目（未删除磁盘文件）。");
            HealthScanButton_Click(sender, e);
        }

        private async void HealthEditTagButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = HealthResultsList.ItemsSource as List<HealthIssueRow>;
            if (rows == null || rows.Count == 0)
            {
                await ShowInfoDialogAsync("无可编辑项", "请先点击“立即扫描”。");
                return;
            }

            var selected = HealthResultsList.SelectedItem as HealthIssueRow;
            List<string> paths;
            if (selected != null && !string.IsNullOrWhiteSpace(selected.Path) && selected.Path != "（空路径）" && System.IO.File.Exists(selected.Path))
            {
                paths = new List<string> { selected.Path };
            }
            else
            {
                paths = rows
                    .Select(r => r.Path ?? string.Empty)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && p != "（空路径）" && System.IO.File.Exists(p))
                    .ToList();
            }

            if (paths.Count == 0)
            {
                await ShowInfoDialogAsync("无法编辑", "问题项中没有可编辑的本地文件。");
                return;
            }

            TagEditorWindow.ShowBatch(paths);
        }

        private sealed class HealthIssueRow
        {
            public string Path { get; set; } = string.Empty;
            public string Issues { get; set; } = string.Empty;
        }
    }
}
