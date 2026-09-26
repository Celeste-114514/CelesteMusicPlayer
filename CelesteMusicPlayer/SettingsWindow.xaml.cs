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

        /// <summary>AppSettingsStore.Changed 的订阅句柄；窗口关闭时必须解绑（静态事件，不解绑会留住本窗口实例）。</summary>
        private Action? _storeChangedHandler;
        private bool _uiReady;
        /// <summary>窗口显示后的"延迟加载"是否已跑过（快捷键列表 / 设置回填 / 文件关联状态）。</summary>
        private bool _deferredInitDone;
        /// <summary>设备下拉的"代次"：后台枚举返回时若已不是最新一次，就放弃填充，
        /// 免得用户在枚举期间切换输出模式导致两次填充交错、列表串味。</summary>
        private int _deviceComboGeneration;
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

        /// <summary>「启动后进入」下拉选项：Id = 分类 Tag（与左侧导航一致），Label = 显示名。</summary>
        private static readonly (string Id, string Label)[] StartupCategoryOptions =
        {
            ("UserPlaylist", "播放队列"),
            ("Songs", "歌曲"),
            ("Albums", "专辑"),
            ("Artists", "艺术家"),
            ("AlbumArtists", "专辑艺术家"),
            ("Favorites", "我喜欢的音乐"),
            ("Ratings", "评分"),
            ("Recent", "最近播放"),
            ("PlaylistWall", "播放列表"),
        };

        private static readonly (PlaybackOrder Order, string Label)[] PlaybackOrderOptions =
        {
            (PlaybackOrder.ListLoop, "列表循环"),
            (PlaybackOrder.Sequential, "顺序播放"),
            (PlaybackOrder.Random, "随机播放"),
            (PlaybackOrder.TrackLoop, "单曲循环"),
            (PlaybackOrder.TrackOnce, "单曲播放")
        };

        /// <summary>进度条点击后行为。</summary>
        private static readonly (string Id, string Label)[] ProgressBarClickBehaviorOptions =
        {
            ("SeekAndPause", "跳转后暂停"),
            ("SeekAndPlay", "跳转并继续播放")
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

        /// <summary>在线搜索默认平台（含 iTunes/MusicBrainz；歌词源不含它们，因均不提供歌词/下载）。</summary>
        private static readonly (string Id, string Label)[] OnlineSearchSourceOptions =
        {
            ("NetEase", "网易云音乐"),
            ("QQ", "QQ音乐"),
            ("iTunes", "Apple Music"),
            ("MusicBrainz", "MusicBrainz（封面）"),
        };

        /// <summary>艺术家头像来源平台（NetEase 默认真实歌手头像；iTunes 精确区分艺人，用其专辑封面作头像）。</summary>
        private static readonly (string Id, string Label)[] ArtistAvatarSourceOptions =
        {
            ("NetEase", "网易云（真实歌手头像）"),
            ("iTunes", "iTunes（精确艺人，专辑封面）"),
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
            var initWatch = Stopwatch.StartNew();
            _loadingUi = true;
            InitializeComponent();
            long initMs = initWatch.ElapsedMilliseconds;
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

            // ⚠️ 重活（快捷键列表 / 设置回填 / 音频设备枚举 / 文件关联查询）已挪到窗口显示之后，
            // 见 RunDeferredInit()。以前这些是同步跑在构造里的，用户点「选项设置」得等它们全做完
            // 窗口才出现，观感就是"点了没反应、卡一下才弹出来"。
            StartupLog.Write($"[设置窗口] 构造完成 InitializeComponent={initMs}ms 合计={initWatch.ElapsedMilliseconds}ms");

            if (SettingsNav.MenuItems.Count > 0)
            {
                SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            }

            ThemeColorService.ThemeColorChanged -= OnThemeColorChangedSettings;
            ThemeColorService.ThemeColorChanged += OnThemeColorChangedSettings;

            // 外部改设置（典型：播放页右上角「布局」按钮）时，把值同步回本窗口的下拉框。
            // 不做这一步的话：设置窗口开着时用按钮切了布局，本窗口仍是旧值，
            // 之后再随便改个别设置就会把布局"改回去"。
            _storeChangedHandler = () =>
            {
                try
                {
                    DispatcherQueue.TryEnqueue(SyncNowPlayingLayoutComboFromStore);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.storeChanged", caught); }
            };
            AppSettingsStore.Changed -= _storeChangedHandler;
            AppSettingsStore.Changed += _storeChangedHandler;

            _uiReady = true;
            // 仍保持拦截状态：设置还没回填完，这期间的用户操作不能被写回（RunDeferredInit 末尾放开）
            _loadingUi = true;
            _lastAppliedAccent = ThemeColorService.CurrentAccent;

            // 等窗口真正被激活（第一帧画出来）后再补内容
            Activated += SettingsWindow_FirstActivated;

            Closed += (_, _) =>
            {
                if (_storeChangedHandler != null)
                {
                    AppSettingsStore.Changed -= _storeChangedHandler;
                    _storeChangedHandler = null;
                }

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
                // 先把窗口顶到前台，内容刷新（含音频设备枚举）排到队尾
                _instance.Activate();
                _instance.QueueReloadFromStore();
                return;
            }

            _instance = new SettingsWindow();
            _instance.Activate();
        }

        /// <summary>窗口首次激活后触发一次：把重活排到 UI 队列尾部，让窗口先把第一帧画出来。</summary>
        private void SettingsWindow_FirstActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
        {
            Activated -= SettingsWindow_FirstActivated;
            if (!DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => RunDeferredInit()))
            {
                RunDeferredInit();
            }
        }

        /// <summary>
        /// 窗口显示之后才做的重活：快捷键列表、设置回填（内部会同步枚举 WASAPI 设备 / ASIO 驱动）、
        /// 文件关联状态。同步跑在构造函数里会让窗口"卡一下才弹出来"。
        /// </summary>
        private void RunDeferredInit()
        {
            if (_deferredInitDone)
            {
                return;
            }

            _deferredInitDone = true;
            var watch = Stopwatch.StartNew();
            try
            {
                _loadingUi = true;
                ReloadHotkeyList();
                long t1 = watch.ElapsedMilliseconds;
                LoadFromStore();
                long t2 = watch.ElapsedMilliseconds;
                RefreshAssociationStatus();
                StartupLog.Write($"[设置窗口] 延迟加载 快捷键={t1}ms 设置回填={t2 - t1}ms 关联={watch.ElapsedMilliseconds - t2}ms 合计={watch.ElapsedMilliseconds}ms");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.RunDeferredInit", caught);
            }
            finally
            {
                _loadingUi = false;
            }
        }

        /// <summary>窗口已存在时重新打开：内容刷新（含设备枚举）排到队尾，先把窗口激活到前台。</summary>
        private void QueueReloadFromStore()
        {
            void Reload()
            {
                if (!_deferredInitDone)
                {
                    return; // 首次加载还没跑，它会把设置一起回填，别重复枚举设备
                }

                var watch = Stopwatch.StartNew();
                try
                {
                    _loadingUi = true;
                    LoadFromStore();
                    RefreshAssociationStatus();
                    StartupLog.Write($"[设置窗口] 重开刷新 {watch.ElapsedMilliseconds}ms");
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.QueueReloadFromStore", caught);
                }
                finally
                {
                    _loadingUi = false;
                }
            }

            // 用默认（Normal）优先级：Low 会被主窗口的后台活（缩略图泵、波形解码等）压住，
            // 表现为"重开设置窗口后内容半天不刷新"。Normal 排在这些活前面，仍不会挡住窗口激活。
            if (!DispatcherQueue.TryEnqueue(() => Reload()))
            {
                Reload();
            }
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
        }

        /// <summary>打开设置窗口并定位到「关于」面板（自动检查到新版本时从托盘气泡点击进入）。</summary>
        public static void ShowAbout()
        {
            ShowOrActivate();
            SettingsWindow? w = _instance;
            if (w == null)
            {
                return;
            }

            try
            {
                w.DispatcherQueue.TryEnqueue(w.OpenAboutPanel);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
        }

        /// <summary>选中并显示设置窗口的「关于」导航板块，并刷新自动检查缓存的更新状态。</summary>
        private void OpenAboutPanel()
        {
            try
            {
                foreach (object o in SettingsNav.MenuItems)
                {
                    if (o is NavigationViewItem item && item.Tag is string t && t == "About")
                    {
                        SettingsNav.SelectedItem = item;
                        break;
                    }
                }

                ShowPanel("About");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }

            // 关于面板：显示当前版本号（静态读取，构造时一次即可）
            try
            {
                if (AboutVersionText != null)
                {
                    AboutVersionText.Text = $"版本 {CurrentVersionText()}";
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
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
            FillCombo(StartupCategoryCombo, StartupCategoryOptions);
            PlaybackOrderCombo.Items.Clear();
            foreach ((PlaybackOrder order, string label) in PlaybackOrderOptions)
            {
                PlaybackOrderCombo.Items.Add(new ComboBoxItem { Content = label, Tag = order });
            }

            FillCombo(LyricSavePolicyCombo, LyricSavePolicyOptions);
            FillCombo(LyricAlignCombo, LyricAlignOptions);
            FillCombo(LyricDownloadServiceCombo, LyricServiceOptions);
            FillCombo(OnlineSearchSourceCombo, OnlineSearchSourceOptions);
            FillCombo(ArtistAvatarSourceCombo, ArtistAvatarSourceOptions);
            FillCombo(AudioChannelCombo, AudioChannelOptions);
            FillCombo(ProgressBarClickBehaviorCombo, ProgressBarClickBehaviorOptions);
            OutputModeCombo.Items.Clear();
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "WASAPI 共享（系统混音）", Tag = "Shared" });
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "WASAPI 独占（HiFi）", Tag = "WasapiExclusive" });
            OutputModeCombo.Items.Add(new ComboBoxItem { Content = "ASIO（专有声卡驱动）", Tag = "Asio" });

            DsdOutputModeCombo.Items.Clear();
            DsdOutputModeCombo.Items.Add(new ComboBoxItem { Content = "DoP 直出（HiFi，独占/ASIO）", Tag = "Dop" });
            DsdOutputModeCombo.Items.Add(new ComboBoxItem { Content = "转 PCM（兼容，先可听）", Tag = "Pcm" });

            DopContainerCombo.Items.Clear();
            DopContainerCombo.Items.Add(new ComboBoxItem { Content = "24bit 紧凑（默认）", Tag = "Packed24" });
            DopContainerCombo.Items.Add(new ComboBoxItem { Content = "32bit 标准（标记最高字节）", Tag = "Container32" });

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

                // 平台 Cookie
                if (NeCookieBox != null) NeCookieBox.Text = s.NetEaseCookie;
                if (QqCookieBox != null) QqCookieBox.Text = s.QqCookie;

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
                SetSlider(LyricOffsetSlider, s.LyricOffsetMs);
                SetTextBlock(LyricOffsetValueText, FormatLyricOffset(s.LyricOffsetMs));

                SetToggle(OpenDesktopLyricsSwitch, s.OpenDesktopLyricsOnStartup);
                SetToggle(DesktopLyricHideWithoutLyricSwitch, s.DesktopLyricHideWithoutLyric);
                SetToggle(DesktopLyricHideWhenPausedSwitch, s.DesktopLyricHideWhenPaused);
                SetToggle(DesktopLyricLockOnStartSwitch, s.DesktopLyricLockOnStart);
                SetToggle(DesktopLyricShowUnlockWhenLockedSwitch, s.DesktopLyricShowUnlockWhenLocked);
                SelectComboByTag(DesktopLyricVisibleLinesCombo, s.DesktopLyricVisibleLines.ToString());
                SetToggle(DesktopLyricClickThroughSwitch, s.DesktopLyricClickThrough);
                SetSlider(DesktopLyricOpacitySlider, s.DesktopLyricOpacity);
                SetTextBlock(DesktopLyricOpacityValueText, s.DesktopLyricOpacity.ToString());
                SetSlider(DesktopLyricFontSizeSlider, s.DesktopLyricFontSize);
                SetTextBlock(DesktopLyricFontSizeValueText, s.DesktopLyricFontSize.ToString("0"));
                SetText(DesktopLyricPlayedColorTextBox, s.DesktopLyricPlayedColor);
                SetText(DesktopLyricUnplayedColorTextBox, s.DesktopLyricUnplayedColor);

                // —— 外观增强 ——
                SelectComboByTag(DesktopLyricFontFamilyCombo, s.DesktopLyricFontFamily);
                SelectComboByTag(DesktopLyricColorPresetCombo, s.DesktopLyricColorPreset);
                SetToggle(DesktopLyricHideTranslationSwitch, s.DesktopLyricHideTranslation);
                SelectComboByTag(DesktopLyricAlignCombo, s.DesktopLyricAlign);
                SelectComboByTag(DesktopLyricShadowCombo, s.DesktopLyricShadowStrength.ToString());
                SetSlider(DesktopLyricOutlineWidthSlider, s.DesktopLyricOutlineWidth);
                SetTextBlock(DesktopLyricOutlineWidthValueText, s.DesktopLyricOutlineWidth.ToString("0.0"));
                SetText(DesktopLyricOutlineColorTextBox, s.DesktopLyricOutlineColor);
                SetSlider(DesktopLyricLineSpacingSlider, s.DesktopLyricLineSpacing);
                SetTextBlock(DesktopLyricLineSpacingValueText, s.DesktopLyricLineSpacing.ToString("0"));
                SetToggle(DesktopLyricShowBackgroundBarSwitch, s.DesktopLyricShowBackgroundBar);
                SetText(DesktopLyricBackgroundColorTextBox, s.DesktopLyricBackgroundColor);

                UpdateColorSwatches();

                SetToggle(MiniAlwaysOnTopSwitch, s.MiniPlayerAlwaysOnTop);
                SetToggle(OpenMiniPlayerSwitch, s.OpenMiniPlayerOnStartup);

                // 外观
                SelectComboByTag(UiStyleModeCombo, s.UiStyleMode);
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
                SelectComboByTag(NowPlayingLayoutCombo,
                    string.IsNullOrWhiteSpace(s.NowPlayingLayout) ? "Classic" : s.NowPlayingLayout);
                ShowTitleColCheck.IsChecked = s.ShowPlaylistTitle;
                ShowArtistColCheck.IsChecked = s.ShowPlaylistArtist;
                ShowAlbumColCheck.IsChecked = s.ShowPlaylistAlbum;
                ShowYearColCheck.IsChecked = s.ShowPlaylistYear;
                ShowDurationColCheck.IsChecked = s.ShowPlaylistDuration;
                SelectComboByTag(PlaylistDensityCombo, s.PlaylistDensity);
                BackgroundPathTextBox.Text = s.CustomBackgroundPath;
                SelectBackgroundPresetRadio(s.BackgroundPreset);
                SetToggle(BackgroundPresetMotionSwitch, s.BackgroundPresetMotion);
                WaveformProgressSwitch.IsOn = s.ProgressBarStyle == "Waveform";
                // 波形已播配色：渐变 / 纯色；白度只在纯色下有意义（渐变时置灰）
                SelectComboByTag(WaveColorModeCombo, s.WaveColorMode == "Solid" ? "Solid" : "Gradient");
                if (WaveSolidWhitenessSlider != null)
                {
                    WaveSolidWhitenessSlider.Value = Math.Clamp(s.WaveSolidWhiteness, 0.0, 1.0) * 100.0;
                    WaveSolidWhitenessSlider.IsEnabled = s.WaveColorMode == "Solid";
                }

                if (WaveSolidWhitenessValueText != null)
                {
                    WaveSolidWhitenessValueText.Text = (int)Math.Round(Math.Clamp(s.WaveSolidWhiteness, 0.0, 1.0) * 100.0) + "%";
                }

                _accentHex = string.IsNullOrWhiteSpace(s.CustomAccentColor) ? "#0078D4" : s.CustomAccentColor;
                UpdateAccentColorButton();

                // 常规
                SelectComboByTag(CloseActionCombo, s.CloseAction);
                SelectComboByTag(StartupCategoryCombo, s.StartupCategory);
                SetToggle(RestoreLibrarySwitch, s.RestoreLibrary);
                SetToggle(RestorePlaybackSwitch, s.RestorePlayback);
                SetToggle(AutoRunSwitch, s.AutoRun);
                SetToggle(GlobalMouseWheelVolumeSwitch, s.GlobalMouseWheelVolume);
                SetToggle(AutoDownloadLyricsSwitch, s.AutoDownloadLyrics);
                SetToggle(AutoDownloadCoverSwitch, s.AutoDownloadCover);
                SelectComboByTag(LyricDownloadServiceCombo, s.LyricDownloadService);
                SelectComboByTag(OnlineSearchSourceCombo, s.OnlineSearchDefaultSource);
                SelectComboByTag(ArtistAvatarSourceCombo, string.IsNullOrWhiteSpace(s.ArtistAvatarSource) ? "NetEase" : s.ArtistAvatarSource);
                SelectComboByTag(AudioChannelCombo, s.AudioChannel);
                SelectComboByTag(ProgressBarClickBehaviorCombo, s.ProgressBarClickBehavior);
                SetToggle(AlwaysOnTopSwitch, s.AlwaysOnTop);
                SetToggle(SaveLyricToSongFolderSwitch, s.SaveLyricToSongFolder);
                SetToggle(SaveCoverToSongFolderSwitch, s.SaveCoverToSongFolder);
                SetToggle(AutoDownloadOnlyWhenTagFullSwitch, s.AutoDownloadOnlyWhenTagFull);

                // 播放
                SetSlider(VolumeSettingSlider, s.Volume);
                SetTextBlock(VolumeValueText, $"{(int)Math.Round(s.Volume)}%");
                SetToggle(HiFiSoftwareVolumeSwitch, s.HiFiSoftwareVolume);
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

                // 转码缓存（FFmpeg 转出的 PCM WAV）
                if (TranscodeCacheLimitNumberBox != null)
                {
                    double tLimit = s.TranscodeCacheLimitMb > 0 ? s.TranscodeCacheLimitMb : 2048;
                    TranscodeCacheLimitNumberBox.Value = Math.Clamp(tLimit, 256, 102_400);
                }

                UpdateTranscodeCacheUsage();

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
                SelectComboByTag(DsdOutputModeCombo, string.IsNullOrWhiteSpace(s.DsdOutputMode) ? "Pcm" : s.DsdOutputMode);
                SelectComboByTag(DopContainerCombo, string.IsNullOrWhiteSpace(s.DopContainerMode) ? "Packed24" : s.DopContainerMode);
                SetToggle(DsdPreloadSwitch, s.DsdPreloadEnabled);
                SetToggle(AsioFeederSwitch, s.AsioFeederEnabled);
                if (DsdCachePathBox != null)
                {
                    DsdCachePathBox.Text = DsdPreloadService.CacheRoot;
                }

                RefreshDsdCacheStat();
                UpdateVolumeSettingLockForMode(); // 模式决定设置页音量条是否锁定
                StartupLog.Write("设置加载 输出模式=" + (s.OutputMode ?? "null") + " 下拉选中=" + (OutputModeCombo?.SelectedItem is ComboBoxItem _m && _m.Tag is string _mt ? _mt : "(null)") + " 设备=" + (s.OutputDeviceId ?? "null"));
                // _loadAsyncIgnore 的置位已挪进 InitOutputDeviceComboAsync（只在填下拉框时短暂拦事件）；
                _ = InitOutputDeviceComboAsync(s.OutputDeviceId);

                // 网络音乐库（WebDAV）
                LoadWebDavIntoUi(s);

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

        /// <summary>设置页音量滑条：始终显示保存的音量值（跨模式统一偏好）。
        /// 实际路由由模式决定——共享=程序内数字衰减；独占/ASIO 默认=DAC/系统端点音量（不破坏直通），
        /// 开启「HiFi 软件音量」后=DSP 衰减（失去 bit-perfect，徽标提示）。滑条本身不锁定。</summary>
        private void UpdateVolumeSettingLockForMode()
        {
            if (VolumeSettingSlider == null)
            {
                return;
            }

            double saved = AppSettingsStore.Load().Volume;
            SetSlider(VolumeSettingSlider, saved);
            SetTextBlock(VolumeValueText, $"{(int)Math.Round(saved)}%");
        }

        /// <summary>切换输出模式到 HiFi（WASAPI 独占 / ASIO）时弹一次说明：播放器默认不调音量（bit-perfect），
        /// 音量走 DAC 硬件旋钮 / 系统端点音量；设备无实体音量键时引导开启本页「HiFi 软件音量」（会失去直通）。</summary>
        private void MaybeWarnHiFiVolume()
        {
            string mode = GetSelectedOutputMode();
            bool exclusive = string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase);
            bool asio = string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase);
            if (!exclusive && !asio)
            {
                return;
            }

            string title = exclusive ? "WASAPI 独占输出提示" : "ASIO 输出提示";
            string body = exclusive
                ? "已切换到 WASAPI 独占，播放器将尽力直通（bit-perfect）。\n\n" +
                  "播放器内部数字音量固定为 100%（不参与衰减），音量有三种调法：\n" +
                  "1. DAC / 耳放上的硬件旋钮（最推荐，完全不碰数字信号）；\n" +
                  "2. Windows 任务栏音量条（调节 DAC 端点音量，部分设备支持，同样不破坏直通）；\n" +
                  "3. 在下方「HiFi 软件音量」开关开启后，用主界面音量条调节——此为程序内数字衰减，会失去 bit-perfect（界面徽标会有提示）。\n\n" +
                  "如果以上都无法调节音量（如无旋钮小尾巴），建议选第 3 种。DSD 直出播放时软件音量不可用，请用硬件旋钮。"
                : "已切换到 ASIO 输出。ASIO 没有统一的系统音量接口，播放器默认不调音量。\n\n" +
                  "请用声卡硬件旋钮或声卡驱动面板调节音量；\n" +
                  "如果设备没有实体音量键（如无旋钮小尾巴），可在下方开启「HiFi 软件音量」开关——此为程序内数字衰减，会失去 bit-perfect（界面徽标会有提示）。\n\n" +
                  "DSD 直出播放时软件音量不可用，请用硬件旋钮。";

            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = body,
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
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
            // ⚠️ 慢活（WASAPI 设备枚举）必须在后台线程跑：本机实测枚举一次要 1.2~1.4 秒
            //（MMDevice 属性存储逐个读友好名 + 音频服务往返）。以前它是同步跑在 UI 线程上的，
            // 正是"点选项设置卡一下、窗口才弹出来"的主因。
            // 而且这里只在真正动下拉框（清空/填项/选中）的那几毫秒里置 _loadingUi=true 拦事件，
            // 免得刚打开设置窗口的一两秒内用户改设置被吞掉（以前整个枚举期间都在拦）。
            int generation = ++_deviceComboGeneration;
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

            if (!asioMode)
            {
                // WASAPI：先把设备列表取回来（慢活在后台线程）
                System.Collections.Generic.IReadOnlyList<(string Id, string Name)> devices;
                string defaultId;
                try
                {
                    (devices, defaultId) = await System.Threading.Tasks.Task.Run(() =>
                        (HiFiOutputBackend.EnumerateWasapiDevices(), HiFiOutputBackend.GetDefaultWasapiDeviceId()));
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.InitOutputDeviceComboAsync", caught);
                    devices = Array.Empty<(string, string)>();
                    defaultId = string.Empty;
                }

                if (generation != _deviceComboGeneration)
                {
                    return; // 期间又有新的填充请求（用户切了输出模式），本次结果作废
                }

                // 回到 UI 线程填下拉框（几毫秒的活）
                _loadingUi = true;
                try
                {
                    StartupLog.Write("输出设备下拉枚举 数量=" + devices.Count + " 默认=" + defaultId + " 已选=" + selectedId);
                    foreach ((string id, string name) in devices)
                    {
                        StartupLog.Write("  设备 id=" + id + " name=" + name);
                    }

                    OutputDeviceCombo.Items.Clear();

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
                    _loadingUi = false;       // 填充完成，恢复可保存
                }

                return;
            }

            // ASIO 模式：枚举 ASIO 驱动（驱动名即设备标识），无默认可选时提供“系统默认”占位。
            // 这条路径保持同步：ASIO 驱动 DLL 交给后台线程加载容易出问题，且只有切到 ASIO 才会走。
            _loadingUi = true;
            try
            {
                OutputDeviceCombo.Items.Clear();
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
            }
            finally
            {
                _loadAsyncIgnore = false;
                _loadingUi = false;
            }
        }

        /// <summary>输出模式切换：Shared/独占/ASIO 变化时刷新设备下拉列表（WASAPI 设备 ⇄ ASIO 驱动）。</summary>
        private async void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

            // 设备枚举在后台线程跑（WASAPI 那条约 1 秒），这里 await 不会卡住界面
            try
            {
                await InitOutputDeviceComboAsync(seedDevice);
            }
            catch
            {
                _deviceSeedMatchFail = true; // 视为未匹配，保留用户已有设备设置
            }

            PersistAllFromUi();
        }

        /// <summary>DSD 输出模式切换（DoP 直出 / 转 PCM）：仅持久化，播放时按文件即时生效。</summary>
        private void DsdOutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            AppSettingsStore.Update(s => s.DsdOutputMode = DsdOutputModeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string mode
                ? mode : "Dop");
        }

        /// <summary>DoP 容器摆位切换（24bit 紧凑 / 32bit 标准）：仅持久化，下次开播生效。</summary>
        private void DopContainerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            AppSettingsStore.Update(s => s.DopContainerMode = DopContainerCombo?.SelectedItem is ComboBoxItem item && item.Tag is string mode
                ? mode : "Packed24");
        }

        // ---- DSD 预加载缓存目录 ----
        private void RefreshDsdCacheStat()
        {
            if (DsdCacheStatText == null)
            {
                return;
            }

            (long bytes, int files) = DsdPreloadService.Stat();
            DsdCacheStatText.Text = files == 0
                ? $"当前目录：{DsdPreloadService.CacheRoot}（暂无缓存）"
                : $"当前目录：{DsdPreloadService.CacheRoot} — 已用 {bytes / 1024.0 / 1024.0:F0}MB / {files} 个文件";
        }

        private void DsdCacheApplyButton_Click(object sender, RoutedEventArgs e)
        {
            string p = DsdCachePathBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(p))
            {
                p = string.Empty; // 空=回落默认目录
            }

            AppSettingsStore.Update(s => s.DsdCachePath = p);
            RefreshDsdCacheStat();
        }

        private void DsdCacheOpenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = DsdPreloadService.CacheRoot;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.xaml.cs", caught);
            }
        }

        // 两段式确认（点一次变"确认删除？"，再点才真删）——不依赖 ContentDialog/XamlRoot
        private bool _dsdCacheClearArmed;

        private void DsdCacheClearButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            (long bytes, int files) = DsdPreloadService.Stat();

            if (!_dsdCacheClearArmed)
            {
                if (files == 0)
                {
                    RefreshDsdCacheStat();
                    return;
                }

                _dsdCacheClearArmed = true;
                if (btn != null)
                {
                    btn.Content = $"确认删除 {files} 个（约 {bytes / 1024.0 / 1024.0:F0}MB）？";
                }

                return;
            }

            _dsdCacheClearArmed = false;
            if (btn != null)
            {
                btn.Content = "清空缓存";
            }

            int n = DsdPreloadService.ClearAll();
            RefreshDsdCacheStat();
            StartupLog.Write($"[DSD预载] 用户清空缓存，删除 {n} 个文件");
        }

        /// <summary>转码缓存上限改动：即时持久化（清理阈值下次转码后生效）。</summary>
        private void TranscodeCacheLimitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loadingUi || !_uiReady)
            {
                return;
            }

            if (double.IsNaN(args.NewValue) || double.IsInfinity(args.NewValue))
            {
                return;
            }

            int mb = (int)Math.Clamp(Math.Round(args.NewValue), 256, 102_400);
            AppSettingsStore.Update(s => s.TranscodeCacheLimitMb = mb);
            UpdateTranscodeCacheUsage();
        }

        /// <summary>手动清理转码缓存：把 FFmpeg 转出来的 PCM WAV 全部删掉（正在播放 / 已预载的那首保留）。</summary>
        private void TranscodeClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            if (TranscodeClearCacheButton != null)
            {
                TranscodeClearCacheButton.IsEnabled = false;
            }

            try
            {
                // 删几百 MB 到几 GB 的文件不能在 UI 线程上干，会整窗卡住
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    (int count, long bytes) = FfmpegDecoderBackend.ClearTranscodeCache();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (TranscodeCacheUsageText != null)
                        {
                            double mb = bytes / (1024.0 * 1024.0);
                            TranscodeCacheUsageText.Text = count > 0
                                ? $"已清理 {count} 个文件，释放 {mb:0.0} MB（正在播放的那首已保留）"
                                : "没有可清理的缓存（正在播放的那首会等播完再清）。";
                        }

                        if (TranscodeClearCacheButton != null)
                        {
                            TranscodeClearCacheButton.IsEnabled = true;
                        }
                    });
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.xaml.cs", caught);
                if (TranscodeClearCacheButton != null)
                {
                    TranscodeClearCacheButton.IsEnabled = true;
                }
            }
        }

        /// <summary>刷新转码缓存占用显示（后台统计，避免扫描大目录卡住 UI）。</summary>
        private void UpdateTranscodeCacheUsage()
        {
            if (TranscodeCacheUsageText == null)
            {
                return;
            }

            TranscodeCacheUsageText.Text = "正在统计…";
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                (long bytes, int count) = FfmpegDecoderBackend.GetTranscodeCacheUsage();
                double mb = bytes / (1024.0 * 1024.0);
                int limitMb = AppSettingsStore.Load().TranscodeCacheLimitMb;
                string text = count > 0
                    ? $"当前占用：{mb:0.0} MB / 上限 {limitMb} MB（{count} 个文件）"
                    : "暂无缓存";
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (TranscodeCacheUsageText != null)
                    {
                        TranscodeCacheUsageText.Text = text;
                    }
                });
            });
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
            if (LyricOffsetSlider != null) s.LyricOffsetMs = (int)Math.Round(LyricOffsetSlider.Value);

            s.OpenDesktopLyricsOnStartup = OpenDesktopLyricsSwitch?.IsOn ?? s.OpenDesktopLyricsOnStartup;
            s.DesktopLyricHideWithoutLyric = DesktopLyricHideWithoutLyricSwitch?.IsOn ?? s.DesktopLyricHideWithoutLyric;
            s.DesktopLyricHideWhenPaused = DesktopLyricHideWhenPausedSwitch?.IsOn ?? s.DesktopLyricHideWhenPaused;
            s.DesktopLyricLockOnStart = DesktopLyricLockOnStartSwitch?.IsOn ?? s.DesktopLyricLockOnStart;
            s.DesktopLyricShowUnlockWhenLocked = DesktopLyricShowUnlockWhenLockedSwitch?.IsOn ?? s.DesktopLyricShowUnlockWhenLocked;
            s.DesktopLyricVisibleLines = int.Parse(GetComboTagString(DesktopLyricVisibleLinesCombo, "3"));
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

            // —— 外观增强 ——
            s.DesktopLyricFontFamily = GetComboTagString(DesktopLyricFontFamilyCombo, "Microsoft YaHei UI");
            s.DesktopLyricColorPreset = GetComboTagString(DesktopLyricColorPresetCombo, "Custom");
            s.DesktopLyricHideTranslation = DesktopLyricHideTranslationSwitch?.IsOn ?? s.DesktopLyricHideTranslation;
            s.DesktopLyricAlign = GetComboTagString(DesktopLyricAlignCombo, "Center");
            s.DesktopLyricShadowStrength = int.Parse(GetComboTagString(DesktopLyricShadowCombo, "2"));
            if (DesktopLyricOutlineWidthSlider != null)
            {
                s.DesktopLyricOutlineWidth = DesktopLyricOutlineWidthSlider.Value;
            }

            s.DesktopLyricOutlineColor = string.IsNullOrWhiteSpace(DesktopLyricOutlineColorTextBox?.Text)
                ? "#000000"
                : DesktopLyricOutlineColorTextBox.Text.Trim();
            if (DesktopLyricLineSpacingSlider != null)
            {
                s.DesktopLyricLineSpacing = DesktopLyricLineSpacingSlider.Value;
            }

            s.DesktopLyricShowBackgroundBar = DesktopLyricShowBackgroundBarSwitch?.IsOn ?? s.DesktopLyricShowBackgroundBar;
            s.DesktopLyricBackgroundColor = string.IsNullOrWhiteSpace(DesktopLyricBackgroundColorTextBox?.Text)
                ? "#66000000"
                : DesktopLyricBackgroundColorTextBox.Text.Trim();

            s.MiniPlayerAlwaysOnTop = MiniAlwaysOnTopSwitch?.IsOn ?? s.MiniPlayerAlwaysOnTop;
            s.OpenMiniPlayerOnStartup = OpenMiniPlayerSwitch?.IsOn ?? s.OpenMiniPlayerOnStartup;

            s.UiStyleMode = GetComboTagString(UiStyleModeCombo, "");
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
            s.WaveColorMode = GetComboTagString(WaveColorModeCombo, "Gradient") == "Solid" ? "Solid" : "Gradient";
            if (WaveSolidWhitenessSlider != null)
            {
                s.WaveSolidWhiteness = Math.Clamp(WaveSolidWhitenessSlider.Value / 100.0, 0.0, 1.0);
            }

            s.CustomBackgroundPath = BackgroundPathTextBox?.Text?.Trim() ?? string.Empty;
            s.BackgroundPreset = GetBackgroundPresetTag();
            s.BackgroundPresetMotion = BackgroundPresetMotionSwitch?.IsOn ?? true;
            s.ThemePreset = GetComboTagString(ThemePresetCombo, "");
            s.NowPlayingLayout = GetComboTagString(NowPlayingLayoutCombo, "Classic");
            s.ShowPlaylistTitle = ShowTitleColCheck.IsChecked ?? true;
            s.ShowPlaylistArtist = ShowArtistColCheck.IsChecked ?? true;
            s.ShowPlaylistAlbum = ShowAlbumColCheck.IsChecked ?? true;
            s.ShowPlaylistYear = ShowYearColCheck.IsChecked ?? true;
            s.ShowPlaylistDuration = ShowDurationColCheck.IsChecked ?? true;
            s.PlaylistDensity = GetComboTagString(PlaylistDensityCombo, "Comfortable");

            s.CloseAction = GetComboTagString(CloseActionCombo, nameof(CloseWindowAction.Ask));
            // 「启动后进入」写回：下拉 Tag 即分类名（与左侧导航一致）。只接受白名单内的分类，
            // 未选中 / 非法值一律回退「播放队列」，与 AppSettingsStore.Normalize 的兜底保持一致。
            string startupCategory = GetComboTagString(StartupCategoryCombo, "UserPlaylist");
            s.StartupCategory = AppSettingsStore.ValidStartupCategories.Contains(startupCategory)
                ? startupCategory
                : "UserPlaylist";
            s.RestoreLibrary = RestoreLibrarySwitch?.IsOn ?? s.RestoreLibrary;
            s.RestorePlayback = RestorePlaybackSwitch?.IsOn ?? s.RestorePlayback;
            s.AutoRun = AutoRunSwitch?.IsOn ?? s.AutoRun;
            s.GlobalMouseWheelVolume = GlobalMouseWheelVolumeSwitch?.IsOn ?? s.GlobalMouseWheelVolume;
            s.AutoDownloadLyrics = AutoDownloadLyricsSwitch?.IsOn ?? s.AutoDownloadLyrics;
            s.AutoDownloadCover = AutoDownloadCoverSwitch?.IsOn ?? s.AutoDownloadCover;
            s.LyricDownloadService = GetComboTagString(LyricDownloadServiceCombo, "NetEase");
            s.OnlineSearchDefaultSource = GetComboTagString(OnlineSearchSourceCombo, "NetEase");
            s.ArtistAvatarSource = GetComboTagString(ArtistAvatarSourceCombo, "NetEase");
            s.AudioChannel = GetComboTagString(AudioChannelCombo, "Stereo");
            s.ProgressBarClickBehavior = GetComboTagString(ProgressBarClickBehaviorCombo, "SeekAndPause");
            s.AlwaysOnTop = AlwaysOnTopSwitch?.IsOn ?? s.AlwaysOnTop;
            s.SaveLyricToSongFolder = SaveLyricToSongFolderSwitch?.IsOn ?? s.SaveLyricToSongFolder;
            s.SaveCoverToSongFolder = SaveCoverToSongFolderSwitch?.IsOn ?? s.SaveCoverToSongFolder;
            s.AutoDownloadOnlyWhenTagFull = AutoDownloadOnlyWhenTagFullSwitch?.IsOn ?? s.AutoDownloadOnlyWhenTagFull;

            // 音量滑条在共享与 HiFi 独占下都可调：HiFi 下调节的是 DAC 设备/驱动级主音量（不破坏 bit-perfect），
            // 设置页「HiFi 软件音量」开关开启后改走 DSP 衰减（失去 bit-perfect，徽标提示），一并持久化。
            s.Volume = VolumeSettingSlider.Value;
            s.HiFiSoftwareVolume = HiFiSoftwareVolumeSwitch?.IsOn ?? s.HiFiSoftwareVolume;
            // 同步到跨入口唯一真值源：设置页与主界面是两个独立滑条，
            // 若主界面滑条因故未同步，退出时会用旧值把这里保存的音量覆盖回默认 80。
            MainWindow.LastUserVolume = Math.Clamp(VolumeSettingSlider.Value, 0, 100);
            global::CelesteMusicPlayer.StartupLog.Write($"[音量] 设置页保存 = {VolumeSettingSlider.Value:0.##}");
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
            s.DsdOutputMode = DsdOutputModeCombo?.SelectedItem is ComboBoxItem dsi && dsi.Tag is string dst
                ? dst : "Dop";
            s.DopContainerMode = DopContainerCombo?.SelectedItem is ComboBoxItem dci && dci.Tag is string dct
                ? dct : "Packed24";
            s.DsdPreloadEnabled = DsdPreloadSwitch?.IsOn ?? s.DsdPreloadEnabled;
            s.AsioFeederEnabled = AsioFeederSwitch?.IsOn ?? s.AsioFeederEnabled;
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

            if (TranscodeCacheLimitNumberBox != null
                && !double.IsNaN(TranscodeCacheLimitNumberBox.Value)
                && !double.IsInfinity(TranscodeCacheLimitNumberBox.Value))
            {
                s.TranscodeCacheLimitMb = (int)Math.Clamp(Math.Round(TranscodeCacheLimitNumberBox.Value), 256, 102_400);
            }

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

            // 网络音乐库（WebDAV）：让「应用 / 保存并关闭」也能一起存，
            // 省得用户改完地址去点底部的保存却发现没生效。
            PopulateWebDavIntoState(s);

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
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

        private static string FormatLyricOffset(int ms)
            => ms == 0 ? "0.00s" : (ms > 0 ? "+" : "") + (ms / 1000.0).ToString("0.00") + "s";

        private void PersistAllFromUi()
        {
            if (_loadingUi || !_uiReady || _loadAsyncIgnore)
            {
                return;
            }

            PersistAndApply(PopulateStateFromUi);

            AppSettingsState saved = AppSettingsStore.Load();
            // 无条件按当前设置校正注册表（不再只在「值变了」时才写）：
            // 安装包（NSIS SEC_AUTORUN 组件）会在安装时直接写 Run 项，若只在值变化时同步，
            // 该项会永久残留 → 「设置里关着但开机仍自启」。这里每次都同步，开关为关即清掉残留。
            ApplyAutoRunRegistry(saved.AutoRun);

            MainWindow.Instance?.ApplySettingsLive(AppSettingsStore.Load());
            MainWindow.Instance?.ApplyOverlayPreferenceFromSettings(saved);
        }

        private static void ApplyAutoRunRegistry(bool enable)
        {
            // 统一走 AutoRunHelper（注册表逻辑集中一处，且被程序启动时的校正复用）
            AutoRunHelper.Apply(enable);
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
            PanelWebDav.Visibility = tag == "WebDAV" ? Visibility.Visible : Visibility.Collapsed;
            PanelHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            PanelLibraryHealth.Visibility = tag == "LibraryHealth" ? Visibility.Visible : Visibility.Collapsed;
            PanelStreaming.Visibility = tag == "Streaming" ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
            // 进入关于面板时，把启动自动检查到的新版本状态直接显示出来（不用等用户手动点「检查更新」）
            if (tag == "About")
            {
                RefreshUpdateStatusFromCache();
            }
        }

        private void SaveNeCookieButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.Update(x => x.NetEaseCookie = NeCookieBox?.Text?.Trim() ?? "");
            StreamingStatusText.Text = "网易云 Cookie 已保存（本地）。";
        }

        private void SaveQqCookieButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsStore.Update(x => x.QqCookie = QqCookieBox?.Text?.Trim() ?? "");
            StreamingStatusText.Text = "QQ Cookie 已保存（本地）。";
        }

        private void SettingCheck_Changed(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();
        }

        private void SettingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            PersistAllFromUi();

            // 「预设背景缓慢移动」需要当场起停动画，其它开关不用打扰背景
            if (ReferenceEquals(sender, BackgroundPresetMotionSwitch))
            {
                MainWindow.Instance?.RefreshBackgroundMotion();
            }
            else if (ReferenceEquals(sender, HiFiSoftwareVolumeSwitch))
            {
                // HiFi 软件音量切换即时刷新主窗口音量条冻结态（解冻 / 钉 100%）与引擎音量路由
                MainWindow.Instance?.ApplySettingsLive(AppSettingsStore.Load());
            }
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
        }

        private void SettingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 波形配色选「渐变」时白度不起作用 → 滑块置灰，避免误以为调了没反应
            if (ReferenceEquals(sender, WaveColorModeCombo) && WaveSolidWhitenessSlider != null)
            {
                WaveSolidWhitenessSlider.IsEnabled = GetComboTagString(WaveColorModeCombo, "Gradient") == "Solid";
            }

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
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".m4v");
                picker.FileTypeFilter.Add(".mkv");
                picker.FileTypeFilter.Add(".webm");
                picker.FileTypeFilter.Add(".mov");
                picker.FileTypeFilter.Add(".avi");
                picker.FileTypeFilter.Add(".wmv");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    BackgroundPathTextBox.Text = file.Path;
                    PersistAllFromUi();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
        }

        private void ClearBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            BackgroundPathTextBox.Text = string.Empty;
            SelectBackgroundPresetRadio(string.Empty);
            PersistAllFromUi();
        }

        private void BackgroundPresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_loadingUi || sender is not RadioButton rb || !rb.IsChecked.GetValueOrDefault())
            {
                return;
            }

            string tag = rb.Tag as string ?? string.Empty;
            AppSettingsStore.Update(s => s.BackgroundPreset = tag);
            // 通知主窗口立即应用新背景（设置窗口开着时也能看到效果）。
            // 选「无」时要把自定义路径交回去，否则会把用户已设的图片/视频也一起清掉。
            MainWindow.Instance?.ApplyCustomBackground(AppSettingsStore.Load().CustomBackgroundPath);
        }

        private void SelectBackgroundPresetRadio(string preset)
        {
            BackgroundPresetNoneRadio.IsChecked = string.IsNullOrWhiteSpace(preset);
            BackgroundPresetAuroraRadio.IsChecked = preset == BackgroundPresetGenerator.PresetAurora;
            BackgroundPresetSunsetRadio.IsChecked = preset == BackgroundPresetGenerator.PresetSunset;
            BackgroundPresetMidnightRadio.IsChecked = preset == BackgroundPresetGenerator.PresetMidnight;
        }

        private string GetBackgroundPresetTag()
        {
            if (BackgroundPresetAuroraRadio?.IsChecked == true) return BackgroundPresetGenerator.PresetAurora;
            if (BackgroundPresetSunsetRadio?.IsChecked == true) return BackgroundPresetGenerator.PresetSunset;
            if (BackgroundPresetMidnightRadio?.IsChecked == true) return BackgroundPresetGenerator.PresetMidnight;
            return string.Empty;
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
                // 记录最后一次用户设定的音量，供主窗口退出写盘时优先采用（统一两个音量入口的真值源）
                MainWindow.LastUserVolume = Math.Clamp(e.NewValue, 0, 100);
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
                else if (ReferenceEquals(sender, LyricOffsetSlider))
                {
                    LyricOffsetValueText.Text = FormatLyricOffset((int)Math.Round(e.NewValue));
                }
            else if (ReferenceEquals(sender, DesktopLyricOpacitySlider))
            {
                DesktopLyricOpacityValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, DesktopLyricFontSizeSlider))
            {
                DesktopLyricFontSizeValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            else if (ReferenceEquals(sender, DesktopLyricOutlineWidthSlider))
            {
                DesktopLyricOutlineWidthValueText.Text = $"{e.NewValue:0.0}";
            }
            else if (ReferenceEquals(sender, DesktopLyricLineSpacingSlider))
            {
                DesktopLyricLineSpacingValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
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
            else if (ReferenceEquals(sender, WaveSolidWhitenessSlider))
            {
                WaveSolidWhitenessValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
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

        /// <summary>界面风格（现有/经典浅色/深色/跟随系统）：立即持久化，主窗口订阅后会实时套用。</summary>
        private void UiStyleModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi) return;
            PersistAllFromUi();
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

        /// <summary>播放页布局切换：立即持久化；主窗口订阅 AppSettingsStore.Changed 后会实时套用。</summary>
        private void NowPlayingLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PersistAllFromUi();
        }

        /// <summary>
        /// 把设置里的播放页布局同步回下拉框（用于「播放页角落布局按钮改完 → 本窗口跟着变」）。
        /// 临时置 _loadingUi 拦掉自身 SelectionChanged，避免触发回写形成回环；
        /// 用保存/恢复而非直接置 false，防止打断正在进行的异步填充。
        /// </summary>
        private void SyncNowPlayingLayoutComboFromStore()
        {
            if (_loadingUi || !_uiReady || NowPlayingLayoutCombo == null)
            {
                return;
            }

            try
            {
                string layout = AppSettingsStore.Load().NowPlayingLayout;
                if (!MainWindow.IsKnownLayout(layout))
                {
                    layout = "Classic";
                }

                if (GetComboTagString(NowPlayingLayoutCombo, "") == layout)
                {
                    return; // 已经是这个值，不做无谓改动
                }

                bool previousLoading = _loadingUi;
                _loadingUi = true;
                try
                {
                    SelectComboByTag(NowPlayingLayoutCombo, layout);
                }
                finally
                {
                    _loadingUi = previousLoading;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.SyncNowPlayingLayoutComboFromStore", caught);
            }
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

        private static readonly IReadOnlyDictionary<string, (string Played, string Unplayed)> DesktopLyricPresetColors = new Dictionary<string, (string Played, string Unplayed)>
        {
            ["IceBlue"] = ("#40B4FF", "#EAF6FF"),
            ["WarmOrange"] = ("#FF9D3C", "#FFE9D0"),
            ["NeonPurple"] = ("#C77DFF", "#ECD9FF"),
            ["PureWhite"] = ("#FFFFFF", "#DCDCDC"),
            ["Lime"] = ("#B6FF3C", "#E6FFCC"),
        };

        private void DesktopLyricColorPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string preset = GetComboTagString(DesktopLyricColorPresetCombo, "Custom");
            if (DesktopLyricPresetColors.TryGetValue(preset, out var colors))
            {
                if (DesktopLyricPlayedColorTextBox != null)
                {
                    DesktopLyricPlayedColorTextBox.Text = colors.Played;
                }

                if (DesktopLyricUnplayedColorTextBox != null)
                {
                    DesktopLyricUnplayedColorTextBox.Text = colors.Unplayed;
                }

                UpdateColorSwatches();
            }

            PersistAllFromUi();
        }

        private void PickOutlineColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = DesktopLyricOutlineColorTextBox?.Text ?? "#000000";
                ColorPickerWindow.Show("描边颜色", current, hex =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DesktopLyricOutlineColorTextBox != null)
                        {
                            DesktopLyricOutlineColorTextBox.Text = hex;
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

        private void PickBackgroundColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = DesktopLyricBackgroundColorTextBox?.Text ?? "#66000000";
                ColorPickerWindow.Show("背景条颜色", current, hex =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DesktopLyricBackgroundColorTextBox != null)
                        {
                            DesktopLyricBackgroundColorTextBox.Text = hex;
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
            SetSwatch(DesktopLyricOutlineColorSwatch, DesktopLyricOutlineColorTextBox?.Text, "#000000");
            SetSwatch(DesktopLyricBackgroundColorSwatch, DesktopLyricBackgroundColorTextBox?.Text, "#66000000");
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }}

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }}

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
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

        /// <summary>
        /// 刷新「文件关联」区域的状态提示。
        ///
        /// 为什么要有这块常驻提示：Debug（F5）跑的是 MSIX 打包版，Windows 不允许它
        /// 作为文件的默认打开程序 —— 手动把它设成"打开方式"后双击音频会静默退出，
        /// 表现就是"双击没反应"，而且没有任何报错，靠用户自己完全排查不出来。
        /// 这里把结论直接写在设置界面上，并把「关联指到了一个用不了的程序」也点明。
        /// </summary>
        private void RefreshAssociationStatus()
        {
            try
            {
                if (AssociationStatusText == null) return;

                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe");

                bool selfPackaged = FileAssociationHelper.IsPackagedLayout(exePath);

                string? registered = FileAssociationHelper.GetRegisteredExecutable();
                bool registeredPackaged = registered != null
                    && FileAssociationHelper.IsPackagedLayout(registered);

                if (registeredPackaged)
                {
                    // 关联指到了一个不能用的程序：这正是「双击音频没反应」的根因
                    AssociationStatusText.Text =
                        "⚠ 当前关联指向的程序不能用来打开文件（调试/打包版）：\n" + registered
                        + "\n请先点「取消文件关联」，再用免安装版或安装后的正式版本重新注册。";
                    AssociationStatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                }
                else if (selfPackaged)
                {
                    AssociationStatusText.Text =
                        "⚠ 你现在运行的是调试（打包）版本，Windows 不允许它作为文件的默认打开程序，"
                        + "双击音频不会有任何反应。\n要使用文件关联，请运行 Release（免安装）版或安装后的正式版本。";
                    AssociationStatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                }
                else if (!string.IsNullOrEmpty(registered))
                {
                    AssociationStatusText.Text = "✓ 当前版本可用于文件关联，已注册到：\n" + registered;
                    AssociationStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 76, 175, 80));
                }
                else
                {
                    AssociationStatusText.Text = "✓ 当前版本可用于文件关联（尚未注册）。";
                    AssociationStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 76, 175, 80));
                }

                AssociationStatusText.Visibility = Visibility.Visible;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }
        }

        /// <summary>
        /// 打开「文件关联」窗口，让用户逐项勾选要关联哪些音频格式。
        ///
        /// 原来这里是一个"一键注册全部格式"的按钮，但一键全注册会把 .iso/.cue 这类
        /// 本来属于别的软件的格式也抢过来，而且用户无法选择。改成开窗勾选后，
        /// 注册 / 取消 / 挑选程序文件 都在那个窗口里完成，这里只负责把门打开。
        /// </summary>
        private void OpenAssociationWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileAssociationWindow.ShowOrActivate();
            }
            catch (Exception ex)
            {
                _ = ShowInfoDialogAsync("打开失败", ex.Message);
            }
        }

        private async void UnregisterAssociationButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileAssociationHelper.Unregister();
                RefreshAssociationStatus();
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
                // 关毛玻璃时窗口不经过 ApplyWindowBackdrop，这里补一次：经典界面下弹窗也要分深浅色
                FrostedGlass.ApplyWindowTheme(this);
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SettingsWindow.xaml.cs", caught); }}

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

        private void HealthDuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = MainWindow.Instance?.GetCurrentPlaylistSnapshot();
            var rows = new List<HealthIssueRow>();
            int groupCount = 0, dupCount = 0;
            if (snapshot != null)
            {
                // 只读：按（归一化文件名, 大小, 时长近似秒）分组，仅列出真正重复项（组内首条为保留）
                var groups = snapshot
                    .Where(i => !string.IsNullOrWhiteSpace(i.FilePath) && File.Exists(i.FilePath))
                    .GroupBy(i => (
                        NormalizeKey(Path.GetFileName(i.FilePath)),
                        SafeLen(i.FilePath),
                        (int)Math.Round(Math.Max(0.0, i.Duration.TotalSeconds))),
                        System.Collections.Generic.EqualityComparer<(
                        string, long, int)>.Default)
                    .Where(g => g.Count() > 1).ToList();
                foreach (var g in groups)
                {
                    var sorted = g.OrderBy(x => x.FilePath).ToList();
                    groupCount++;
                    for (int k = 1; k < sorted.Count; k++)
                    {
                        dupCount++;
                        rows.Add(new HealthIssueRow
                        {
                            Path = sorted[k].FilePath,
                            Issues = "重复项：" + (sorted.Count - 1) + " 个，保留：" + Path.GetFileName(sorted[0].FilePath)
                        });
                    }
                }
            }
            HealthResultsList.ItemsSource = rows;
            HealthSummaryText.Text = groupCount == 0
                ? "未发现重复歌曲"
                : $"发现 {groupCount} 组重复，共 {dupCount} 个可去除（去索引不删文件）。";
        }

        private void HealthRelocateButton_Click(object sender, RoutedEventArgs e)
        {
            // 只读：为失效文件查找“唯一同名现存候选”路径（供用户自行重定位，不做自动改写）
            var snapshot = MainWindow.Instance?.GetCurrentPlaylistSnapshot()?.ToList();
            var rows = new List<HealthIssueRow>();
            int missingCount = 0, found = 0;
            if (snapshot != null)
            {
                var existing = snapshot
                    .Where(i => !string.IsNullOrWhiteSpace(i.FilePath) && File.Exists(i.FilePath))
                    .ToList();
                var existingByName = existing
                    .GroupBy(i => NormalizeKey(Path.GetFileName(i.FilePath)))
                    .ToDictionary(g => g.Key, g => g.ToList());
                foreach (var lost in snapshot.Where(i => !string.IsNullOrWhiteSpace(i.FilePath) && !File.Exists(i.FilePath)))
                {
                    missingCount++;
                    string name = Path.GetFileName(lost.FilePath);
                    string key = NormalizeKey(name);
                    if (existingByName.TryGetValue(key, out var cands) && cands.Count == 1)
                    {
                        found++;
                        rows.Add(new HealthIssueRow
                        {
                            Path = lost.FilePath,
                            Issues = "可重定位 → 候选：" + cands[0].FilePath
                        });
                    }
                }
            }
            if (found == 0 && missingCount > 0)
            {
                rows.Add(new HealthIssueRow { Path = "（无唯一候选）", Issues = $"共 {missingCount} 个失效文件，未找到唯一同名候选，无法安全自动重定位。" });
            }
            HealthResultsList.ItemsSource = rows;
            HealthSummaryText.Text = found > 0
                ? "重定位候选 " + found + " 个（需你确认后手动更新路径，未自动改写）"
                : (missingCount == 0 ? "无失效文件" : "未找到唯一重定位候选");
        }

        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c) || c=='_' || c=='-' || c=='[' || c==']' || c=='(' || c==')')
                {
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static long SafeLen(string path)
        {
            try { return new FileInfo(path).Length; } catch { return -1; }
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
