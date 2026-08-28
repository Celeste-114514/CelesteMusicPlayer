using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        /// <summary>当前设置是否处于 HiFi 独占输出模式（WASAPI 独占 / ASIO）。</summary>
        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }


        /// <summary>返回 DSD 容器扩展名大写（"DSF"/"DFF"），非 DSD 返回 null。</summary>
        private static string? DsdExtOf(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext switch { ".dsf" => "DSF", ".dff" => "DFF", _ => null };
        }


        private static bool IsHiFiModeSelected()
        {
            string mode = AppSettingsStore.Load().OutputMode;
            return string.Equals(mode, "WasapiExclusive", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Asio", System.StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>对主窗口进行窗口过程子类化，拦截 WM_GETMINMAXINFO 设置最小尺寸。</summary>
        private void SetupMinSizeHooks()
        {
            try
            {
                if (_minMaxWndProc != null || _mainWindowHwnd == IntPtr.Zero)
                {
                    return;
                }

                uint dpi = GetDpiForWindow(_mainWindowHwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                _minTrackScale = dpi / 96.0;
                _minMaxWndProc = MainMinMaxWndProc;
                _prevWndProc = SetWindowLongPtr64(_mainWindowHwnd, (int)GWL_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_minMaxWndProc));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private static nint MainMinMaxWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                try
                {
                    MINMAXINFO mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = (int)Math.Round(MinWindowWidthDip * _minTrackScale);
                    mmi.ptMinTrackSize.Y = (int)Math.Round(MinWindowHeightDip * _minTrackScale);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            // 系统 resize 边框（hasBorder）已提供四边/四角调大小；这里只需同步最大化/还原状态
            // （自绘最大化按钮图标，含从最大化拖拽还原场景）。
            if (msg == WM_SIZE && MainWindow.Instance is MainWindow mw)
            {
                int sizeType = (int)(wParam.ToInt64() & 0xFFFF);
                mw.OnWindowMaximizeStateChanged(sizeType == SIZE_MAXIMIZED);
            }

            return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
        }


        /// <summary>延后设置 Desktop Acrylic（壁纸色毛玻璃）；失败则回退 Mica / 纯色。</summary>
        private void TryApplySystemBackdrop()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (!settings.EnableFrostedGlass)
                {
                    SystemBackdrop = null;
                    StartupLog.Write("SystemBackdrop disabled by settings");
                    return;
                }

                FrostedGlass.ApplyWindowBackdrop(this);
                StartupLog.Write(
                    SystemBackdrop is DesktopAcrylicBackdrop
                        ? "DesktopAcrylicBackdrop applied"
                        : SystemBackdrop is MicaBackdrop
                            ? "MicaBackdrop fallback applied"
                            : "SystemBackdrop cleared");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyWindowBackdrop", ex);
            }
        }


        // =====================================================================
        // 打开文件 / 选择文件夹
        // =====================================================================

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                picker.FileTypeFilter.Add(".wma");
                picker.FileTypeFilter.Add(".ogg");
                picker.FileTypeFilter.Add(".aac");
                picker.FileTypeFilter.Add(".ape");
                picker.FileTypeFilter.Add(".wv");
                picker.FileTypeFilter.Add(".tta");
                picker.FileTypeFilter.Add(".mpc");
                picker.FileTypeFilter.Add(".tak");
                picker.FileTypeFilter.Add(".opus");
                picker.FileTypeFilter.Add(".dsf");
                picker.FileTypeFilter.Add(".dff");

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                string[] paths = files
                    .Select(f => f.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                LoadAndAddFiles(paths, persistAsFiles: true, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开文件失败", ex.Message);
            }
        }


        /// <summary>把本地路径构造成 file URI,转义 # % ? 避免被解析为 fragment/query。</summary>
        private static Uri CreateFileMediaUri(string path)
        {
            string escaped = path
                .Replace("%", "%25")
                .Replace("#", "%23")
                .Replace("?", "%3F");
            return new Uri(escaped, UriKind.Absolute);
        }


        /// <summary>根据路径读元数据并加入列表，然后按当前排序规则重排。</summary>
        /// <param name="persist">为 true 时写入上次会话（默认）。</param>
        /// <param name="persistAsFiles">为 true 时按「文件列表」模式保存整份播放列表。</param>
        private void LoadAndAddFiles(string[] filePaths, bool persist = true, bool persistAsFiles = false, bool replace = false)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return;
            }

            string? playingPath = null;
            if (replace)
            {
                // 替换模式：停止当前播放并清空媒体库，再载入新内容
                MediaPlayer? player = GetPlayer();
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                }

                StopEngineIfActive();
                _playlist.Clear();
                _currentIndex = -1;
                ClearNowPlayingPanel();
            }
            else
            {
                // 记住当前正在播的文件，排序后要能找回下标
                playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    ? _playlist[_currentIndex].FilePath
                    : null;
            }

            // HashSet 去重，避免大批量导入时对 _playlist 反复线性扫描
            var knownPaths = new HashSet<string>(
                _playlist.Select(i => i.FilePath),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;

            foreach (string path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    continue;
                }

                if (!knownPaths.Add(path))
                {
                    continue;
                }

                try
                {
                    PlaylistItem item = CreatePlaylistItemFromPath(path);
                    _playlist.Add(item);
                    added++;
                }
                catch (Exception ex)
                {
                    knownPaths.Remove(path);
                    System.Diagnostics.Debug.WriteLine($"加载失败: {path} → {ex.Message}");
                }
            }

            if (added == 0)
            {
                if (replace)
                {
                    ApplyCategoryView();
                }

                return;
            }

            // 当前在「歌曲」分类时，按标题默认排序刷新列表
            if (_currentCategory == "Songs")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Albums")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Artists" || _currentCategory == "AlbumArtists")
            {
                ApplyCategoryView();
            }
            else
            {
                ApplySort(preservePlayingPath: playingPath);
            }

            // 不自动播放：用户双击或点播放后再开始
            if (added > 0)
            {
                NowPlayingText.Text = $"已添加 {added} 首，共 {_playlist.Count} 首";
            }

            if (persist && persistAsFiles)
            {
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }


        // =====================================================================
        // 左侧分类导航
        // =====================================================================

        private void CategoryNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                && _openedAlbum == null
                && _openedArtist == null)
            {
                return;
            }

            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = tag;
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }


        // 音效处理按钮：占位入口（后续阶段接入 ECHO 音效处理页面）
        private void NavAudioFxButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AudioFX";
                ApplyCategoryView();
            });
            // 音效处理 DSP 工作台（EQ / 声道平衡 / 限幅）
            NowPlayingText.Text = "音效处理（EQ / 声道平衡 / 安全限幅）";
        }


        private void AddMonoComboItem(string mode, string label)
        {
            AudioFxChannelMonoCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        }


        /// <summary>从持久化状态加载音效面板控件。</summary>
        private void LoadAudioFxUiFromStore()
        {
            _audioFxLoading = true;
            try
            {
                _audioFxEq = EqCurveStore.Load();
                AudioFxEqEnableToggle.IsOn = _audioFxEq.Enabled;
                AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatAudioFxDb(_audioFxEq.PreampDb) + " dB";
                SelectAudioFxEqPreset(_audioFxEq.PresetId);
                AudioFxEqModeRadio.SelectedIndex = 0; // 专业
                SyncAudioFxEqSimpleFromState();
                SelectAudioFxEqBand(_audioFxEqSelected);
                RedrawAudioFxEqCurve();
                RefreshAudioFxEqBandEditor();

                DspExtraState extra = DspExtraStore.Load();
                var ch = extra.ChannelBalance;
                ReplayGainState rglog = ReplayGainStore.Load();
                StartupLog.Write($"[DSP] 加载读盘 eq.Enabled={_audioFxEq.Enabled} limiter={extra.Safety?.EnableLimiter} rg.Mode={rglog.Mode}");
                AudioFxChannelToggle.IsOn = ch.Enabled;
                AudioFxChannelBalanceSlider.Value = ch.Balance;
                AudioFxChannelLeftGainSlider.Value = ch.LeftGainDb;
                AudioFxChannelRightGainSlider.Value = ch.RightGainDb;
                SelectAudioFxMonoMode(ch.MonoMode);
                AudioFxChannelSwapToggle.IsOn = ch.SwapChannels;
                AudioFxChannelInvertLToggle.IsOn = ch.InvertLeft;
                AudioFxChannelInvertRToggle.IsOn = ch.InvertRight;

                var safety = extra.Safety;
                AudioFxSafetyHeadroomSlider.Value = safety.HeadroomDb;
                AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatAudioFxDb(safety.HeadroomDb);
                AudioFxSafetyLimiterToggle.IsOn = safety.EnableLimiter;

                // ReplayGain
                ReplayGainState rg = ReplayGainStore.Load();
                SelectAudioFxRgMode(rg.Mode);
                AudioFxRgPreampSlider.Value = rg.PreampDb;
                AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatAudioFxDb(rg.PreampDb);
                AudioFxRgPreventClippingToggle.IsOn = rg.PreventClipping;
                RefreshAudioFxRgInfo();
            }
            finally
            {
                _audioFxLoading = false;
            }

            // 面板已从盘加载完成，之后才允许 DSP handler 持久化/应用（启动阶段误触发需屏蔽）。
            _audioFxPanelReady = true;
        }


        /// <summary>打开耳机校正（OPRA）独立窗口。</summary>
        private void OpenOpraButton_Click(object sender, RoutedEventArgs e)
        {
            HeadphoneCorrectionWindow.OpenOrActivate();
        }

        /// <summary>打开房间校正（卷积 FIR）独立窗口。</summary>
        private void OpenRoomCorrectionButton_Click(object sender, RoutedEventArgs e)
        {
            RoomCorrectionWindow.OpenOrActivate();
        }


        private static string FormatAudioFxDb(double db)
        {
            double r = Math.Round(db, 1);
            return r > 0 ? "+" + r.ToString("0.#") : r.ToString("0.#");
        }


        private void SelectAudioFxMonoMode(string mode)
        {
            for (int i = 0; i < AudioFxChannelMonoCombo.Items.Count; i++)
            {
                if (AudioFxChannelMonoCombo.Items[i] is ComboBoxItem { Tag: string m } && string.Equals(m, mode, StringComparison.Ordinal))
                {
                    AudioFxChannelMonoCombo.SelectedIndex = i;
                    return;
                }
            }

            AudioFxChannelMonoCombo.SelectedIndex = 0;
        }


        private void ApplySimpleTones()
        {
            var s = new EqCurveState { Enabled = AudioFxEqEnableToggle.IsOn, PreampDb = _audioFxEq.PreampDb, PresetId = "simple", PresetName = "简单模式" };
            SimpleEqStore.Save(new SimpleEqState { Bass = _eqSimpleBass, Vocal = _eqSimpleVocal, Air = _eqSimpleAir, Warm = _eqSimpleWarm });
            if (_eqSimpleBass > 0.01) AppendSimple(s, "bass", _eqSimpleBass);
            if (_eqSimpleVocal > 0.01) AppendSimple(s, "vocal", _eqSimpleVocal);
            if (_eqSimpleAir > 0.01) AppendSimple(s, "air", _eqSimpleAir);
            if (_eqSimpleWarm > 0.01) AppendSimple(s, "warm", _eqSimpleWarm);
            if (s.Bands.Count == 0) s.Bands.Add(new EqBand { Enabled = true, FrequencyHz = 1000, GainDb = 0 });

            _audioFxEq = s;
            _audioFxEqSelected = _audioFxEq.Bands.Count > 0 ? 0 : -1;
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        /// <summary>重建预设下拉里的"我的预设"段（保存/删除后调用）。</summary>
        private void RefreshAudioFxUserPresetItems()
        {
            if (AudioFxEqPresetCombo == null || !_audioFxEqBuilt) return;
            string? selected = (AudioFxEqPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;

            AudioFxEqPresetCombo.Items.Clear();
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "平坦", Tag = "flat" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "古典", Tag = "classical" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "流行", Tag = "pop" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "爵士", Tag = "jazz" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "摇滚", Tag = "rock" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "柔和", Tag = "soft" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "低音增强", Tag = "bass" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "custom" });
            var userPresets = EqUserPresetStore.Load();
            if (userPresets.Count > 0)
            {
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "—— 我的预设 ——", IsEnabled = false });
                foreach (var p in userPresets)
                {
                    AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "★ " + (string.IsNullOrWhiteSpace(p.PresetName) ? "未命名" : p.PresetName), Tag = p.PresetId });
                }
            }

            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "管理（删除）我的预设…", Tag = "manage" });

            _audioFxLoading = true;
            try { SelectAudioFxEqPreset(selected ?? "flat"); }
            finally { _audioFxLoading = false; }
        }


        private void AudioFxChannelMono_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxChannelSwap_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxSafetyHeadroom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading) return;
            if (AudioFxSafetyHeadroomLabel != null)
            {
                AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatAudioFxDb(AudioFxSafetyHeadroomSlider.Value);
            }

            ApplyDspToEngine();
        }


        private void AudioFxRgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioFxLoading) return;
            ApplyReplayGainToEngine();
        }


        private string CurrentAudioFxMonoMode()
        {
            return AudioFxChannelMonoCombo.SelectedItem is ComboBoxItem { Tag: string m } ? m : "off";
        }


        /// <summary>从指定列表进入多选（标签排序面板曲目）。</summary>
        private void EnterMultiSelectModeFrom(ListView list)
        {
            if (list == null) return;
            _multiSelectTargetList = list;
            EnterMultiSelectMode((list.SelectedItems.FirstOrDefault() as PlaylistItem) ?? null);
        }


        /// <summary>预设字符串 → 排序字段链。</summary>
        private static List<(string field, bool asc)> PresetToFields(string preset)
        {
            var asc = new[] {
                ("Album", "专辑"),
                ("AlbumArtist,Album", "专辑艺术家 / 专辑"),
                ("AlbumArtist,Year,Album", "专辑艺术家 / 年份 / 专辑"),
                ("Artist,Album", "艺术家 / 专辑"),
                ("Genre,Album", "流派 / 专辑"),
                ("Year,Album", "年份 / 专辑")
            };
            foreach (var (fields, label) in asc)
            {
                if (preset == label)
                {
                    return fields.Split(',').Select(f => (f, true)).ToList();
                }
            }
            return new List<(string, bool)>();
        }


        /// <summary>根据当前左侧分类刷新中间区域</summary>
        private void ApplyCategoryView()
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            UpdateLibraryNavHighlight();

            // 列表墙/命中单详情默认隐藏：仅其它分类内按需显 Visible，避免切到其它页面残留盖层
            PlaylistWallBorder.Visibility = Visibility.Collapsed;
            PlaylistDetailBorder.Visibility = Visibility.Collapsed;
            TagSortBorder.Visibility = Visibility.Collapsed;
            AudioFxBorder.Visibility = Visibility.Collapsed;
            RatingFilterPanel.Visibility = Visibility.Collapsed;

            switch (_currentCategory)
            {
                case "Songs":
                    LibraryPaneTitle.Text = "歌曲";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _playlist;

                    _sortField = SortField.Title;
                    _sortAscending = true;
                    SortFieldText.Text = "排序：标题";
                    SortOrderText.Text = "升序";
                    ApplySort(playingPath);
                    break;

                case "UserPlaylist":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: true);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _userPlaylist;
                    // 播放列表默认不排序：保持添加顺序（后添加批次在前，批内相对顺序不变）
                    RenumberCollection(_userPlaylist);
                    break;

                case "Albums":
                    LibraryPaneTitle.Text = "专辑";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Visible;
                    UpdateAlbumSortButtonsUi();
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Visible;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshAlbumViewAsync();
                    break;

                case "Artists":
                case "AlbumArtists":
                    LibraryPaneTitle.Text = _currentCategory == "AlbumArtists" ? "专辑艺术家" : "艺术家";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshArtistViewAsync();
                    break;

                case "Folders":
                    LibraryPaneTitle.Text = "文件夹";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshFolderBrowserRoots();
                    break;

                case "PlaylistWall":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    if (string.IsNullOrEmpty(_currentPlaylistDetail))
                    {
                        PlaylistWallBorder.Visibility = Visibility.Visible;
                        PlaylistDetailBorder.Visibility = Visibility.Collapsed;
                        ApplyPlaylistWallCategory();
                    }
                    else
                    {
                        PlaylistWallBorder.Visibility = Visibility.Collapsed;
                        PlaylistDetailBorder.Visibility = Visibility.Visible;
                    }

                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    break;

                case "Favorites":
                case "Recent":
                    ApplyFavoritesOrRecentCategory();
                    break;

                case "Ratings":
                    LibraryPaneTitle.Text = "评分";
                    LibraryPaneTitle.Visibility = Visibility.Visible;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortButton.Visibility = Visibility.Collapsed;
                    RatingFilterPanel.Visibility = Visibility.Visible;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ApplyRatingCategory();
                    break;

                case "MostPlayed":
                    ApplyMostPlayedCategory();
                    break;

                case "TagSort":
                    LibraryPaneTitle.Text = "标签排序";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    BreakoutTagSortView();
                    break;

                case "Genres":
                case "Years":
                    LibraryPaneTitle.Text = _currentCategory == "Genres" ? "流派" : "年份";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ArtistGridView.Visibility = Visibility.Visible;
                    ArtistDetailPanel.Visibility = Visibility.Collapsed;
                    _ = RefreshGenreYearViewAsync();
                    break;

                case "GenreSongs":
                case "YearSongs":
                    LibraryPaneTitle.Text = (_currentCategory == "GenreSongs" ? "流派：" : "年份：") + (_genreYearFilter ?? "");
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    var groupedSongs = _currentCategory == "GenreSongs"
                        ? _playlist
                            .Where(t => string.Equals(t.Genre, _genreYearFilter, StringComparison.OrdinalIgnoreCase)
                                        || (string.IsNullOrWhiteSpace(t.Genre) && _genreYearFilter == "未知流派"))
                            .ToList()
                        : _playlist
                            .Where(t => (t.Year > 0 ? t.Year.ToString() : "未知年份") == _genreYearFilter)
                            .ToList();
                    var groupedCollection = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(groupedSongs);
                    RenumberCollection(groupedCollection);
                    PlaylistView.ItemsSource = groupedCollection;
                    break;

                case "AudioFX":
                    // 音效处理 DSP 工作台（EQ / 声道平衡 / 安全限幅）
                    LibraryPaneTitle.Text = "音效处理";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    TagSortBorder.Visibility = Visibility.Collapsed;
                    AudioFxBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureAudioFxUiBuilt();
                    LoadAudioFxUiFromStore();
                    UpdateDspBitPerfectUi();
                    break;
            }

            // 仅「播放队列」分类支持拖拽重排（其它列表保持排序语义不变）
            PlaylistView.CanReorderItems = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist);
            PlaylistView.CanDragItems = PlaylistView.CanReorderItems;

            // 空状态提示：仅 Favorites / Recent / Ratings 自己管理；其余分类隐藏
            if (_currentCategory is not ("Favorites" or "Recent" or "Ratings"))
            {
                SetPlaylistEmptyHint(false, string.Empty);
            }

            UpdateUserPlaylistActionBarVisibility();
            UpdateLibrarySearchUi();
        }


        private async Task RefreshGenreYearViewAsync()
        {
            List<ArtistEntry> entries = await Task.Run(() =>
            {
                if (_currentCategory == "Genres")
                {
                    return _playlist
                        .GroupBy(t => string.IsNullOrWhiteSpace(t.Genre) || t.Genre == "未知流派" ? "未知流派" : t.Genre)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                        .ToList();
                }

                return _playlist
                    .GroupBy(t => t.Year > 0 ? t.Year.ToString() : "未知年份")
                    .OrderByDescending(g => g.Key == "未知年份"
                        ? -1
                        : int.TryParse(g.Key, out int year) ? year : -1)
                    .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                    .ToList();
            });

            if (_currentCategory is "Genres" or "Years")
            {
                ArtistGridView.ItemsSource = entries;
                RefreshPlaylistSelectionChrome();
            }
        }


        private static readonly (char Trad, char Simp)[] _tradSimpPairs = new[]
        {
            ('樂','乐'),
            ('個','个'),
            ('們','们'),
            ('與','与'),
            ('見','见'),
            ('現','现'),
            ('視','视'),
            ('覺','觉'),
            ('還','还'),
            ('過','过'),
            ('來','来'),
            ('這','这'),
            ('點','点'),
            ('員','员'),
            ('戶','户'),
            ('體','体'),
            ('動','动'),
            ('網','网'),
            ('電','电'),
            ('話','话'),
            ('讓','让'),
            ('該','该'),
            ('開','开'),
            ('關','关'),
            ('說','说'),
            ('請','请'),
            ('講','讲'),
            ('認','认'),
            ('識','识'),
            ('訊','讯'),
            ('華','华'),
            ('結','结'),
            ('構','构'),
            ('組','组'),
            ('約','约'),
            ('紙','纸'),
            ('細','细'),
            ('終','终'),
            ('紀','纪'),
            ('紅','红'),
            ('綠','绿'),
            ('繼','继'),
            ('續','续'),
            ('級','级'),
            ('縣','县'),
            ('綜','综'),
            ('維','维'),
            ('績','绩'),
            ('經','经'),
            ('總','总'),
            ('線','线'),
            ('編','编'),
            ('練','练'),
            ('義','义'),
            ('習','习'),
            ('職','职'),
            ('舊','旧'),
            ('節','节'),
            ('蘭','兰'),
            ('藍','蓝'),
            ('藝','艺'),
            ('藥','药'),
            ('應','应'),
            ('戲','戏'),
            ('趙','赵'),
            ('遠','远'),
            ('遲','迟'),
            ('選','选'),
            ('團','团'),
            ('顧','顾'),
            ('頭','头'),
            ('題','题'),
            ('額','额'),
            ('類','类'),
            ('風','风'),
            ('飛','飞'),
            ('飲','饮'),
            ('飯','饭'),
            ('馬','马'),
            ('駕','驾'),
            ('驚','惊'),
            ('齊','齐'),
            ('齒','齿'),
            ('龍','龙'),
            ('龐','庞'),
            ('麗','丽'),
            ('麥','麦'),
            ('麵','面'),
            ('車','车'),
            ('軌','轨'),
            ('轉','转'),
            ('軟','软'),
            ('輕','轻'),
            ('載','载'),
            ('輪','轮'),
            ('詞','词'),
            ('詩','诗'),
            ('語','语'),
            ('誤','误'),
            ('誠','诚'),
            ('敗','败'),
            ('質','质'),
            ('賬','账'),
            ('貫','贯'),
            ('貼','贴'),
            ('贈','赠'),
            ('則','则'),
            ('側','侧'),
            ('銀','银'),
            ('鍵','键'),
            ('鎖','锁'),
            ('銅','铜'),
            ('門','门'),
            ('問','问'),
            ('間','间'),
            ('聞','闻'),
            ('閣','阁'),
            ('長','长'),
            ('張','张'),
            ('樣','样'),
            ('楊','杨'),
            ('樹','树'),
            ('極','极'),
            ('機','机'),
            ('樓','楼'),
            ('欄','栏'),
            ('權','权'),
            ('檢','检'),
            ('檔','档'),
            ('橋','桥'),
            ('標','标'),
            ('聲','声'),
            ('聽','听'),
            ('聯','联'),
            ('葉','叶'),
            ('蓋','盖'),
            ('簡','简'),
            ('筆','笔'),
            ('範','范'),
            ('簽','签'),
            ('籃','篮'),
            ('絕','绝'),
            ('統','统'),
            ('絲','丝'),
            ('腦','脑'),
            ('臉','脸'),
            ('勝','胜'),
            ('騰','腾'),
            ('膽','胆'),
            ('眾','众'),
            ('書','书'),
            ('會','会'),
            ('陳','陈'),
            ('陣','阵'),
            ('隨','随'),
            ('離','离'),
            ('雖','虽'),
            ('雞','鸡'),
            ('靜','静'),
            ('顯','显'),
            ('飄','飘'),
            ('韻','韵'),
            ('項','项'),
            ('順','顺'),
            ('頑','顽'),
            ('領','领'),
            ('顆','颗'),
            ('頻','频'),
            ('預','预'),
            ('髮','发'),
            ('飼','饲'),
            ('驗','验'),
            ('髒','脏'),
            ('貴','贵'),
            ('買','买'),
            ('賣','卖'),
            ('讓','让'),
            ('彈','弹'),
            ('強','强'),
            ('張','张'),
            ('學','学'),
            ('寶','宝'),
            ('實','实'),
            ('導','导'),
            ('將','将'),
            ('帥','帅'),
            ('廣','广'),
            ('廳','厅'),
            ('後','后'),
            ('復','复'),
            ('從','从'),
            ('應','应'),
            ('當','当'),
            ('寧','宁'),
            ('帶','带'),
            ('幫','帮'),
            ('憶','忆'),
            ('憂','忧'),
            ('惡','恶'),
            ('愛','爱'),
            ('慶','庆'),
            ('應','应'),
            ('趕','赶'),
            ('超','超'),
            ('踴','踊'),
            ('跩','跩'),
            ('單','单'),
            ('麼','么'),
            ('廠','厂'),
            ('廚','厨'),
            ('廟','庙'),
            ('廢','废'),
            ('廣','广'),
            ('廳','厅'),
            ('彈','弹'),
            ('強','强'),
        };


        private static readonly System.Collections.Generic.Dictionary<char, char> _tradToSimp = BuildTradToSimpMap();

        private static System.Collections.Generic.Dictionary<char, char> BuildTradToSimpMap()
        {
            var d = new System.Collections.Generic.Dictionary<char, char>();
            foreach (var p in _tradSimpPairs)
            {
                d[p.Trad] = p.Simp;
            }
            return d;
        }

        private static bool ContainsIgnoreCase(string? source, string query)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query))
            {
                return false;
            }

            string s = SearchNormalize(source);
            string q = SearchNormalize(query);
            if (s.Contains(q, System.StringComparison.Ordinal))
            {
                return true;
            }

            // 拼音首字母：query 为纯字母、源含中文时，命中“周杰伦 ← zjl”
            if (ContainsHan(source) && IsAsciiLetters(q))
            {
                string py = PinyinInitials(source);
                if (!string.IsNullOrEmpty(py) && py.Contains(q, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }


        private static bool IsAsciiLetters(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }


        private static bool ContainsHan(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>取汉字串的拼音首字母（如“周杰伦”→“zjl”），基于 ToolGood.Words 拼音库。</summary>
        private static string PinyinInitials(string text)
        {
            try
            {
                string py = ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin(text ?? string.Empty);
                return string.IsNullOrEmpty(py) ? string.Empty : py.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }


        private static bool IsSupportedAudioFile(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (string supported in AudioExtensions)
            {
                if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>右键详情区歌曲：非多选态弹歌曲菜单；多选态进入多选。</summary>
        private void MediaDetailsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            var song = (fe?.DataContext ?? (sender as ListView)?.SelectedItem) as PlaylistItem;
            if (song == null)
            {
                return;
            }

            if (MediaDetailsList?.SelectionMode == ListViewSelectionMode.Multiple)
            {
                // 已进入多选：右键加入选中
                if (!MediaDetailsList.SelectedItems.Contains(song))
                {
                    MediaDetailsList.SelectedItems.Add(song);
                }

                return;
            }

            // 右键：弹出该歌曲的选项菜单（多选作为其中一项，点多选才进入多选态）
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            PlaylistItem songRef = song;

            var play = new MenuFlyoutItem { Text = "播放" };
            play.Icon = new FontIcon { Glyph = "\uE768" };
            play.Click += (_, _) =>
            {
                PlaylistItem? track = EnsureTrackInLibrary(songRef.FilePath);
                if (track != null)
                {
                    PlayPlaylistItem(track);
                }
            };
            flyout.Items.Add(play);

            var add = new MenuFlyoutItem { Text = "加入播放队列" };
            add.Icon = new FontIcon { Glyph = "\uE710" };
            add.Click += (_, _) => AddToUserPlaylistBack(songRef);
            flyout.Items.Add(add);

            var edit = new MenuFlyoutItem { Text = "编辑标签" };
            edit.Icon = new FontIcon { Glyph = "\uE8D2" };
            edit.Click += (_, _) => TagEditorWindow.ShowBatch(new[] { songRef.FilePath });
            flyout.Items.Add(edit);

            var del = new MenuFlyoutItem { Text = "从媒体库中删除" };
            del.Icon = new FontIcon { Glyph = "\uE74D" };
            del.Click += (_, _) => _ = DeleteMediaSongWithConfirmAsync(songRef);
            flyout.Items.Add(del);

            var multi = new MenuFlyoutItem { Text = "多选" };
            multi.Icon = new FontIcon { Glyph = "\uE700" };
            multi.Click += (_, _) =>
            {
                if (MediaDetailsList != null)
                {
                    MediaDetailsList.SelectionMode = ListViewSelectionMode.Multiple;
                    MediaDetailsList.SelectedItems.Clear();
                    MediaDetailsList.SelectedItems.Add(songRef);
                    RefreshMediaSongSelectionChrome();
                }

                if (MediaOptionsButton != null)
                {
                    MediaOptionsButton.Visibility = Visibility.Visible;
                }

                if (MediaExitMultiSelectButton != null)
                {
                    MediaExitMultiSelectButton.Visibility = Visibility.Visible;
                }
            };
            flyout.Items.Add(multi);

            flyout.ShowAt(MediaDetailsList ?? (FrameworkElement)sender, e.GetPosition(MediaDetailsList ?? (FrameworkElement)sender));
            e.Handled = true;
        }


        /// <summary>详情区歌曲行选中 chrome：主题色圆角背景（去掉多选框）。</summary>
        private void MediaDetailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplyMediaSongSelectionChrome(sender as ListView, container, song);
            }
        }


        private void MediaDetailsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshMediaSongSelectionChrome();
        }


        /// <summary>退出媒体库多选：恢复单选并隐藏选项按钮。</summary>
        private void ExitMediaMultiSelect()
        {
            if (MediaDetailsList != null)
            {
                MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                MediaDetailsList.SelectedItems.Clear();
            }

            if (MediaOptionsButton != null)
            {
                MediaOptionsButton.Visibility = Visibility.Collapsed;
            }

            if (MediaExitMultiSelectButton != null)
            {
                MediaExitMultiSelectButton.Visibility = Visibility.Collapsed;
            }
        }


        /// <summary>显式退出多选按钮。</summary>
        private void MediaExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMediaMultiSelect();

        /// <summary>更新右侧详情列表：中途加载完成/重载时退出多选。</summary>
        private void ResetMediaSelectionUi()
        {
            ExitMediaMultiSelect();
        }


        /// <summary>封面写入磁盘后使缓存失效（下载封面后调用）。</summary>
        internal static void InvalidateCoverCache(string audioPath)
        {
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                CoverBytesCache.TryRemove(audioPath, out _);
            }
        }


        private static byte[]? TryLoadInnerCover(string audioPath)
        {
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(audioPath);
                IPicture[]? pictures = tagFile.Tag.Pictures;
                if (pictures == null || pictures.Length == 0)
                {
                    return null;
                }

                IPicture pic = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover)
                               ?? pictures[0];
                byte[] data = pic.Data.Data;
                return data is { Length: > 0 } ? data : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"提取封面失败 [{audioPath}]: {ex.Message}");
                return null;
            }
        }


        private static byte[]? TryLoadOuterCover(string audioPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(audioPath);
                string[] candidates =
                {
                    Path.Combine(dir, "folder.jpg"),
                    Path.Combine(dir, "folder.png"),
                    Path.Combine(dir, "cover.jpg"),
                    Path.Combine(dir, "cover.png"),
                    Path.Combine(dir, name + ".jpg"),
                    Path.Combine(dir, name + ".png")
                };

                foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        return System.IO.File.ReadAllBytes(candidate);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            return null;
        }


        /// <summary>字节 → BitmapImage（须在 UI 线程）</summary>
        private static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[] bytes)
        {
            try
            {
                var image = new BitmapImage();
                using InMemoryRandomAccessStream stream = new();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("封面解码失败: " + ex.Message);
                return null;
            }
        }


        private void SortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _sortAscending = !_sortAscending;
            SortOrderText.Text = _sortAscending ? "升序" : "降序";

            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;
            ApplySort(playingPath);
        }


        private void UpdateCurrentIndexByPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentIndex = i;
                    return;
                }
            }
        }


        // =====================================================================
        // 播放列表交互 / 右键菜单 / 多选
        // =====================================================================

        /// <summary>主内容区空白点击（非歌曲项）取消当前选中，从主题色背景恢复常态。</summary>
        private void MainContentGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Microsoft.UI.Xaml.DependencyObject? origin = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
            while (origin != null)
            {
                if (origin is ListViewItem or GridViewItem)
                {
                    return;
                }

                origin = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(origin);
            }

            // 点空白：取消各歌曲列表的选中
            if (!_isMultiSelectMode)
            {
                PlaylistView.SelectedItem = null;
                if (MediaDetailsList != null && MediaDetailsList.SelectionMode != ListViewSelectionMode.Multiple)
                {
                    MediaDetailsList.SelectedItem = null;
                }
            }
        }


        private void SelectAllMultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode)
            {
                return;
            }

            if (_multiSelectAlbumGrid != null)
            {
                GridView grid = _multiSelectAlbumGrid;
                ObservableCollection<AlbumEntry> allAlbums = GetAlbumCollectionForGrid(grid);
                bool allSelected = allAlbums.Count > 0
                    && grid.SelectedItems.Count >= allAlbums.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(grid);
                }
                else
                {
                    SelectAllInListViewBase(grid, allAlbums.Count);
                }

                RefreshAlbumWallSelectionChrome(grid, allAlbums);
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            if (_multiSelectFolderList != null)
            {
                bool allSelected = _folderBrowserItems.Count > 0
                    && FolderBrowserView.SelectedItems.Count >= _folderBrowserItems.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(FolderBrowserView);
                }
                else
                {
                    SelectAllInListViewBase(FolderBrowserView, _folderBrowserItems.Count);
                }

                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            ListView? target = _multiSelectTargetList;
            if (target == null)
            {
                return;
            }

            IReadOnlyList<PlaylistItem> allSongs = GetMultiSelectSongSource(target);
            bool songsAllSelected = target.SelectedItems.Count >= allSongs.Count && allSongs.Count > 0;
            if (songsAllSelected)
            {
                ClearListViewBaseSelection(target);
            }
            else
            {
                SelectAllInListViewBase(target, allSongs.Count);
            }

            RefreshSongListSelectionChrome(target);
            UpdateSelectAllMultiSelectButtonState();
        }


        /// <summary>用 SelectRange 一次选中全部，避免逐项 Add 触发数千次 SelectionChanged。</summary>
        private void SelectAllInListViewBase(ListViewBase list, int count)
        {
            if (count <= 0)
            {
                return;
            }

            _suppressSelectionUiUpdates = true;
            try
            {
                list.SelectRange(new ItemIndexRange(0, (uint)count));
            }
            catch
            {
                // 少数控件/状态不支持 SelectRange 时回退逐项添加
                for (int i = 0; i < count; i++)
                {
                    object? item = list.Items[i];
                    if (item == null || list.SelectedItems.Contains(item))
                    {
                        continue;
                    }

                    try
                    {
                        list.SelectedItems.Add(item);
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }


        private void ClearListViewBaseSelection(ListViewBase list)
        {
            _suppressSelectionUiUpdates = true;
            try
            {
                if (list is ListView lv)
                {
                    SetListSelectionMode(lv, ListViewSelectionMode.Multiple);
                }
                else if (list is GridView gv)
                {
                    SetGridSelectionMode(gv, ListViewSelectionMode.Multiple);
                }
                else
                {
                    list.SelectedIndex = -1;
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }


        private void UpdateSelectAllMultiSelectButtonState()
        {
            if (SelectAllMultiSelectIcon == null || !_isMultiSelectMode)
            {
                return;
            }

            bool allSelected;
            if (_multiSelectAlbumGrid != null)
            {
                ObservableCollection<AlbumEntry> all = GetAlbumCollectionForGrid(_multiSelectAlbumGrid);
                allSelected = all.Count > 0
                    && _multiSelectAlbumGrid.SelectedItems.Count >= all.Count;
            }
            else if (_multiSelectFolderList != null)
            {
                allSelected = _folderBrowserItems.Count > 0
                    && FolderBrowserView.SelectedItems.Count >= _folderBrowserItems.Count;
            }
            else if (_multiSelectTargetList != null)
            {
                IReadOnlyList<PlaylistItem> all = GetMultiSelectSongSource(_multiSelectTargetList);
                allSelected = all.Count > 0
                    && _multiSelectTargetList.SelectedItems.Count >= all.Count;
            }
            else
            {
                allSelected = false;
            }

            // E73A CheckboxCompositeChecked / E739 CheckboxComposite
            SelectAllMultiSelectIcon.Glyph = allSelected ? "\uE73A" : "\uE739";
        }


        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMultiSelectMode();

        private void ExitMultiSelectMode()
        {
            if (!_isMultiSelectMode)
            {
                MultiSelectActionBar.Visibility = Visibility.Collapsed;
                MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                if (LibraryPaneTitle != null
                    && (_currentCategory == "Songs"
                        || _currentCategory == "UserPlaylist"
                        || _currentCategory == "Artists"
                        || _currentCategory == "AlbumArtists"
                        || _currentCategory == "Albums"))
                {
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                }

                return;
            }

            ExitSongMultiSelectUiOnly();
            ExitAlbumMultiSelectUiOnly();
            ExitFolderMultiSelectUiOnly();

            _isMultiSelectMode = false;
            ApplyCapsuleSortButtonStyle(accent: true);
            MultiSelectActionBar.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            if (_currentCategory == "Songs" || _currentCategory == "UserPlaylist")
            {
                SongSortPanel.Visibility = Visibility.Visible;
                SetSongSortUiForCategory(isUserPlaylist: string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal));
            }
            else if (_currentCategory == "Albums")
            {
                AlbumSortPanel.Visibility = Visibility.Visible;
            }
            else
            {
                SongSortPanel.Visibility = Visibility.Collapsed;
                AlbumSortPanel.Visibility = Visibility.Collapsed;
            }

            UpdateUserPlaylistActionBarVisibility();
            DispatcherQueue.TryEnqueue(RefreshAllSongListSelectionChrome);
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);
                RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
                RefreshFolderBrowserSelectionChrome();
            });
            UpdateLibrarySearchUi();
        }


        /// <summary>
        /// 通过 None 中转切换选择模式，安全清空选中项，避免 SelectedItems.Clear 崩溃。
        /// </summary>
        private void SetListSelectionMode(ListView list, ListViewSelectionMode mode)
        {
            try
            {
                list.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                list.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 本应用用主题色圆角底表示选中，关闭 Multiple 模式左侧系统复选框（否则会显示小黑块）
            list.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                list.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void SetGridSelectionMode(GridView grid, ListViewSelectionMode mode)
        {
            try
            {
                grid.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                grid.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            grid.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                grid.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void MultiSelectFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (PlaylistItem song in selected)
            {
                TrackStatsStore.SetFavorite(song.FilePath, true);
            }

            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            NowPlayingText.Text = $"已收藏 {selected.Count} 首歌曲";
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            ExitMultiSelectMode();
        }


        private async void MultiSelectDownloadCoverButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                PlaylistItem song = selected[i];
                NowPlayingText.Text = $"正在下载封面 ({i + 1}/{selected.Count})…";
                if (await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath))
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"封面下载完成：{ok}/{selected.Count}";
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}


        private void MultiSelectCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (PlaylistItem song in selected)
            {
                sb.AppendLine(song.FilePath);
            }

            var data = new DataPackage();
            data.SetText(sb.ToString().TrimEnd());
            Clipboard.SetContent(data);
            NowPlayingText.Text = $"已复制 {selected.Count} 个文件位置";
        }


        /// <summary>在资源管理器中选中并显示该文件。</summary>
        private static void OpenFileLocationInExplorer(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("打开文件位置失败: " + ex.Message);
            }
        }


        private static bool MoveToRecycleBin(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x40 | 0x10 | 0x04 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                return SHFileOperation(ref op) == 0;
            }
            catch
            {
                return false;
            }
        }


        private Brush ResolveNavCapsuleBorderBrush()
        {
            if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? card)
                && card is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        }


        private Brush ResolveCapsuleFillBrush()
        {
            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? controlBrush)
                && controlBrush is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }


        private Brush CreateMultiSelectFrostBrush()
        {
            if (_cachedMultiSelectFrostBrush != null)
            {
                return _cachedMultiSelectFrostBrush;
            }

            // 使用半透明纯色，避免 Acrylic 采样悬停高亮后越来越白、移开也不恢复
            Color baseTint = ResolveUiBaseTintColor();
            _cachedMultiSelectFrostBrush = new SolidColorBrush(
                Color.FromArgb(70, baseTint.R, baseTint.G, baseTint.B));
            return _cachedMultiSelectFrostBrush;
        }


        private static HashSet<object>? BuildSelectedItemsLookup(ListViewBase list)
        {
            int count = list.SelectedItems.Count;
            if (count <= 64)
            {
                return null;
            }

            var set = new HashSet<object>();
            foreach (object item in list.SelectedItems)
            {
                set.Add(item);
            }

            return set;
        }


        private static bool IsItemSelected(ListViewBase list, object item, HashSet<object>? selectedSet)
        {
            if (selectedSet != null)
            {
                return selectedSet.Contains(item);
            }

            return list.SelectedItems.Contains(item);
        }


        private static IEnumerable<ListViewItem> EnumerateRealizedListViewItems(ListView list)
        {
            Panel? panel = FindItemsPanel(list);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is ListViewItem item)
                {
                    yield return item;
                }
            }
        }


        private static IEnumerable<GridViewItem> EnumerateRealizedGridViewItems(GridView grid)
        {
            Panel? panel = FindItemsPanel(grid);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is GridViewItem item)
                {
                    yield return item;
                }
            }
        }


        /// <summary>在容器已加载后关闭系统选中勾（避免与词条文字重叠）。</summary>
        private void DisableContainerSelectionCheckMark(Control container)
        {
            SoftenItemPresenterCorners(container);
            if (!container.IsLoaded)
            {
                void OnLoaded(object sender, RoutedEventArgs e)
                {
                    container.Loaded -= OnLoaded;
                    SoftenItemPresenterCorners(container);
                }

                container.Loaded += OnLoaded;
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => SoftenItemPresenterCorners(container));
            }
        }


        /// <summary>保存全部状态后重启播放器(用于主题色等需重启生效的更改)。</summary>
        internal void RestartApp()
        {
            try
            {
                TrackStatsStore.Flush();
                _volumeSaveTimer?.Stop();
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
                PersistPlaybackSession();

                string? exe = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(System.IO.Path.GetDirectoryName(exe))
                            ? string.Empty
                            : System.IO.Path.GetDirectoryName(exe)!
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 直接退出当前进程(由新进程接管,绕过托盘/关闭提示逻辑)
            Environment.Exit(0);
        }


        private void ExitApplication()
        {
            AppSettingsStore.MarkAppCleanExit();
            PersistPlaybackSession();
            _allowClose = true;
            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            Close();
            // 托盘/子窗口等引用可能阻止进程退出,必须显式结束进程
            Application.Current.Exit();
        }


        /// <summary>异步加载当前歌曲波形(供波形进度条)，完成后重绘。</summary>
        private async void LoadWaveformForCurrentAsync(string path)
        {
            try
            {

            _waveformPath = path;
            float[] wave = await WaveformDataProvider.GetWaveformAsync(path, 180);
            if (!string.Equals(_waveformPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // 已切歌
            }

            _waveformData = wave;
            StartupLog.Write("波形回调: " + (wave?.Length > 0 ? "有数据" : "空") + " style=" + _progressBarStyle);
            if (_progressBarStyle == "Waveform")
            {
                RedrawProgressStyle();
            }
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}


        /// <summary>读取并显示当前曲目的音频格式信息（采样率/位深/码率/声道）。</summary>
        private async System.Threading.Tasks.Task UpdateAudioInfoTextAsync(string path)
        {
            try
            {
                string? info = await System.Threading.Tasks.Task.Run(() => AudioInfoFormatter.Format(path));
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (NowPlayingAudioInfoText != null && string.Equals(_nowPlayingPath, path, System.StringComparison.OrdinalIgnoreCase))
                    {
                        NowPlayingAudioInfoText.Text = info ?? string.Empty;
                    }
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawWaveformBars();
        }


        /// <summary>未播放时的静态频谱形状（始终可见，中间高两边低）。</summary>
        private static double IdleLevel(int index)
        {
            double shape = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(index * 1.7 + 1.3));
            return Math.Max(0.18, shape * SpectrumEnvelope(index));
        }


        private void DrawWaveformBars()
        {
            if (WaveformCanvas == null)
            {
                return;
            }

            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            // 兜底：_waveLevels 全为 0 时填充静态频谱
            bool allFlat = true;
            for (int i = 0; i < WaveBarCount; i++)
            {
                if (_waveLevels[i] > 0.05)
                {
                    allFlat = false;
                    break;
                }
            }

            if (allFlat)
            {
                for (int i = 0; i < WaveBarCount; i++)
                {
                    _waveLevels[i] = IdleLevel(i);
                }
            }

            double gap = 2;
            double barWidth = Math.Max(2, (width - gap * (WaveBarCount - 1)) / WaveBarCount);

            while (WaveformCanvas.Children.Count < WaveBarCount)
            {
                int idx = WaveformCanvas.Children.Count;
                WaveformCanvas.Children.Add(new Border
                {
                    Background = new SolidColorBrush(WaveColorFor(idx)),
                    CornerRadius = new CornerRadius(2.5),
                    IsHitTestVisible = false
                });
            }

            while (WaveformCanvas.Children.Count > WaveBarCount)
            {
                WaveformCanvas.Children.RemoveAt(WaveformCanvas.Children.Count - 1);
            }

            for (int i = 0; i < WaveBarCount; i++)
            {
                if (WaveformCanvas.Children[i] is not Border bar)
                {
                    continue;
                }

                // 每次重绘都更新颜色(主题色变化后生效,不能只在新柱子创建时设置)
                bar.Background = new SolidColorBrush(WaveColorFor(i));

                double level = Math.Clamp(_waveLevels[i], 0.12, 1.0);
                double barHeight = Math.Max(10, Math.Min(height, height * level * 1.15));
                double left = i * (barWidth + gap);
                double top = (height - barHeight) / 2;

                // 直接赋值（Border 未设置时 Width/Height 为 NaN，比较判断恒 false 会导致柱子 0×0 不可见）
                bar.Width = barWidth;
                bar.Height = barHeight;
                Canvas.SetLeft(bar, left);
                Canvas.SetTop(bar, top);
            }
        }


        /// <summary>引擎开播淡入（约 320ms 渐入到当前音量）。</summary>
        private async Task FadeInEngineAsync()
        {
            try
            {
                // 独占/ASIO（bit-perfect）：设备主音量由用户拖动音量条设置，切歌不重置（避免拉回 100%）。
                if (IsHiFiModeSelected())
                {
                    return;
                }

                // 共享用「用户持久化的真实音量」（防切模式后滑块被独占锁成 100% 导致巨声）。
                // 共享引擎音量 = NAudio 软件增益 0..1。
                double target = Math.Clamp(AppSettingsStore.Load().Volume, 0, 100) / 100.0;
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * i / steps);
                    await Task.Delay(40);
                }

                _audioEngine?.SetVolume(target);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void HandleMediaEnded()
        {
            if (ConsumeSleepStopIfDue())
            {
                return;
            }

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackLoop:
                    // IsLooping 为 true 时通常不会触发；兜底再播当前曲
                    if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                    {
                        PlayUserPlaylistAt(_userPlaylistIndex);
                    }
                    break;

                case PlaybackOrder.TrackOnce:
                    break;

                case PlaybackOrder.Sequential:
                    PlayNext(autoAdvance: true);
                    break;

                default:
                    PlayNext(autoAdvance: true);
                    break;
            }
        }


        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            if (time.TotalHours >= 1)
            {
                return time.ToString(@"h\:mm\:ss");
            }

            return time.ToString(@"mm\:ss");
        }


        private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null)
        {
            try
            {
                ContentDialog dialog = new()
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = xamlRoot ?? Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }
    }
}
