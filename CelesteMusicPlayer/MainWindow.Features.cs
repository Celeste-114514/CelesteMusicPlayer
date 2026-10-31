using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives; // RangeBaseValueChangedEventArgs（播放速度滑块）
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media;
using Windows.Media.Playback;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace CelesteMusicPlayer
{
    /// <summary>MusicPlayer2 对齐功能：SMTC、热键、收藏、下一首播放、在线资源等。</summary>
    public sealed partial class MainWindow
    {


        // 引擎由 PlaybackSession 持有，这里只留转发属性（见 MainWindow.PlaybackHost.cs）。
        private readonly LibraryWatchService _libraryWatch = new();
        private bool _featuresInitialized;

        /// <summary>上次应用到「最近播放」列表的过滤天数（-1=未应用过）。设置变更时对比用。</summary>
        private int _recentRangeDaysApplied = -1;

        /// <summary>上次应用到歌词行的主歌词字号/字体。变更时强制重排字号。</summary>
        private double _appliedMainLyricFontSize = -1;
        private string? _appliedMainLyricFontFamily;

        private void InitializeMusicPlayer2Features()
        {
            if (_featuresInitialized)
            {
                return;
            }

            _featuresInitialized = true;

            // EQ is applied by the production web DSP commands.
            // TagEditorWindow.TagsSaved 订阅已随旧窗口删除移除：标签板块的 TagsSaved
            // 在 EnsureTagEditUiBuilt 里边创建面板边订阅（面板是延迟创建的）。

            MediaSession.ConfigureFromSettings();
            ApplyPlaybackRateFromSettings();
            RestartGlobalHotkeysFromSettings();
            RestartLibraryWatchFromSettings();
            AttachRootKeyboardAccelerators();
            UpdateFavoriteButtonUi();
            AppSettingsState boot = AppSettingsStore.Load();
            ApplyNavVisibilityFromSettings(boot);
            ApplySpectrumVisibilityFromSettings(boot);
            ApplyCoverVisibilityFromSettings(boot);
            AttachGlobalMouseWheelVolume();
            AttachLyricSaveConfirm();
        }

        /// <summary>
        /// 「询问」歌词保存策略的确认弹窗。OnlineMusicApi 内部用了 ConfigureAwait(false)，
        /// 回调可能在后台线程被调用——必须先切回 UI 线程再弹 ContentDialog。
        /// </summary>
        private void AttachLyricSaveConfirm()
        {
            OnlineMusicApi.LyricSaveConfirm = async candidatePath =>
            {
                TaskCompletionSource<bool> tcs = new();
                bool enqueued = DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "保存歌词",
                            Content = "已找到歌词，是否保存到：\n" + Path.GetFileName(candidatePath),
                            PrimaryButtonText = "保存",
                            CloseButtonText = "不保存",
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = Content.XamlRoot
                        };
                        tcs.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught);
                        tcs.TrySetResult(false);
                    }
                });

                if (!enqueued)
                {
                    return false; // 主窗口已销毁：不落盘
                }

                return await tcs.Task;
            };
        }

        private void AttachGlobalMouseWheelVolume()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.PointerWheelChanged -= Root_PointerWheelChanged;
            root.PointerWheelChanged += Root_PointerWheelChanged;
        }

        private void Root_PointerWheelChanged(dynamic sender, dynamic e)
        {
            if (!AppSettingsStore.Load().GlobalMouseWheelVolume)
            {
                return;
            }

            if (FocusManager.GetFocusedElement(Content.XamlRoot) is Slider)
            {
                return;
            }

            int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            AdjustVolumeBy(delta > 0 ? 2 : -2);
            e.Handled = true;
        }

        private void DisposeMusicPlayer2Features()
        {
            try
            {

                // 标签面板的 TagsSaved 退订：面板可能还没创建（_tagPanel 为 null 时无从退起，
                // 随窗口一起销毁，无需处理）。

            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _libraryWatch.Stop();
                _libraryWatch.Dispose();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>
        /// 把设置里的变速推给引擎（2026-10-15 改：旧实现设 MediaPlayer 的
        /// PlaybackSession.PlaybackRate——那条路只在 MediaPlayer 直出时有效、音调还会一起变；
        /// 现在统一走托管实时变速，起播/设置变更时都要推一次）。
        /// 引擎还没建时只是个 no-op：起播建会话时 HiFiOutputBackend 会从设置里读同样值灌进去。
        /// </summary>
        private void ApplyPlaybackRateFromSettings()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                _audioEngine?.SetPlaybackSpeed(s.PlaybackRate, s.PlaybackRatePreservePitch);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.ApplyPlaybackRateFromSettings", caught); }
        }

        private void AttachRootKeyboardAccelerators()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.KeyDown -= Root_KeyDown;
            root.KeyDown += Root_KeyDown;
        }

        private void Root_KeyDown(dynamic sender, dynamic e)
        {
            if (e.Handled)
            {
                return;
            }

            // 输入框内不抢快捷键
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox)
            {
                return;
            }

            VirtualKey key = e.Key;
            if (key == VirtualKey.Space)
            {
                TogglePlayPausePublic();
                e.Handled = true;
            }
            else if (key == VirtualKey.Left)
            {
                SeekBySeconds(-5);
                e.Handled = true;
            }
            else if (key == VirtualKey.Right)
            {
                SeekBySeconds(5);
                e.Handled = true;
            }
            // 「查找歌曲」已移除（老版本遗留），F / F3 不再绑定任何动作。
        }

        internal void ApplyExtendedSettingsLive(AppSettingsState settings)
        {
            // 歌词与桌面歌词设置最先应用，避免被其它子系统的异常阻断
            try
            {
                ApplyLyricPanelFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 自定义背景图片：保存后即时应用
            try
            {
                ApplyCustomBackground(settings.CustomBackgroundPath);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 播放列表列显隐/密度：保存后即时应用
            try
            {
                ApplyPlaylistColumnSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 进度条样式：保存后即时切换
            try
            {
                bool wasWaveform = _waveformProgress;
                // 恒用波形进度条（2026-10-15 定稿）：设置里的开关已下架，这里不再读旧配置里的 false
                _waveformProgress = true;
                RedrawProgressStyle();
                if (_waveformProgress && !wasWaveform)
                {
                    // 刚打开波形开关:立即加载当前播放(或选中)歌曲的波形
                    string? cur = _nowPlayingPath;
                    if (string.IsNullOrEmpty(cur) && PlaylistView.SelectedItem is PlaylistItem selItem)
                    {
                        cur = selItem.FilePath;
                    }

                    if (!string.IsNullOrEmpty(cur))
                    {
                        LoadWaveformForCurrentAsync(cur);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 最近播放范围：保存后即时过滤（0=全部；N=只留最近 N 天，正在看该列表才需要重刷）
            try
            {
                if (_recentRangeDaysApplied != settings.RecentPlayedRangeDays)
                {
                    _recentRangeDaysApplied = settings.RecentPlayedRangeDays;
                    if (string.Equals(_currentCategory, "Recent", StringComparison.Ordinal))
                    {
                        ApplyFavoritesOrRecentCategory();
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 主题色：全局资源键在启动时应用(重启完全生效)；
            // 保存后即时刷新自绘强调元素（选中高亮/导航/正在播放卡/排序按钮）
            try
            {
                _waveAccentColor = ResolveAccentColor();
                StartupLog.Write("主题色应用-波形: " + _waveAccentColor.ToString());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 歌词:用新主题色重新渲染当前行
            try
            {
                TimeSpan lyricPos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(lyricPos, force: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 迷你播放器:刷新强调元素
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 音量条(自绘)重绘 + 进度条主题色
            try
            {
                Windows.UI.Color accent2 = ResolveAccentColor();
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent2);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyCapsuleSortButtonStyle(accent: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                UpdateLibraryNavHighlight();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyNowPlayingCardChrome();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAccentSelectionResources(PlaylistView);
                ApplyAccentSelectionResources(AlbumGridView);
                ApplyAccentSelectionResources(AlbumTrackListView);
                ApplyAccentSelectionResources(ArtistTrackListView);
                ApplyAccentSelectionResources(ArtistAlbumGridView);
                ApplyAccentSelectionResources(FolderBrowserView);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                MediaSession.ConfigureFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyPlaybackRateFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAlwaysOnTopFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                UpdatePlaybackRateButtonText();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.ApplyBindings(settings.CustomHotkeys);
                RestartGlobalHotkeysFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                RestartLibraryWatchFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyNavVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplySpectrumVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyCoverVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            if (!string.IsNullOrWhiteSpace(_nowPlayingPath)
                && settings.EnableBackground
                && settings.AlbumCoverAsBackground)
            {
                try
                {
                    // 模糊半径变更时重绘背景
                    byte[]? bytes = ExtractCoverBytes(_nowPlayingPath);
                    _ = ApplyAlbumArtBackgroundAsync(bytes, _nowPlayingPath);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }
            else if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                try
                {
                    ClearAlbumArtBackground();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }

            // 主题色变更：刷新列表，让选中高亮等立即使用新颜色
            try
            {
                ApplyCategoryView();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void ApplyNavVisibilityFromSettings(AppSettingsState settings)
        {
            SetNavEntryVisibility(NavFavoritesButton, settings.ShowNavFavorites);
            SetNavEntryVisibility(NavRecentButton, settings.ShowNavRecent);
            SetNavEntryVisibility(NavGenreButton, settings.ShowNavGenre);
            SetNavEntryVisibility(NavYearButton, settings.ShowNavYear);

            // 网络音乐库：配了地址才显示（名字取显示名，没填就用主机名）
            ApplyWebDavNavEntry(settings);
        }

        private void ApplyLyricPanelFromSettings(AppSettingsState settings)
        {
            if (LyricsPanel == null)
            {
                return;
            }

            LyricsPanel.Spacing = settings.LyricLineSpacing;
            TextAlignment align = settings.LyricAlign switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                "Auto" => TextAlignment.Center,
                _ => TextAlignment.Center
            };

            // 主歌词字体：随设置即时换（字号走末尾的强制重排，按距离分档）
            FontFamily? mainLyricFont = string.IsNullOrWhiteSpace(settings.MainLyricFontFamily)
                ? null
                : new FontFamily(settings.MainLyricFontFamily);
            foreach (var child in LyricsPanel.Children)
            {
                if (child is TextBlock tb)
                {
                    tb.TextAlignment = align;
                }
                else if (child is Grid lyricRow)
                {
                    // 每行是 Grid → Border（圆角框）→ 歌词 TextBlock：对齐要下钻两层
                    foreach (var inner in lyricRow.Children)
                    {
                        if (inner is Border frame && frame.Child is TextBlock rowText)
                        {
                            rowText.TextAlignment = align;
                            // 同上：未选字体时不要赋 null（会抛 "Unknown" is not a valid value）
                            if (mainLyricFont != null)
                            {
                                rowText.FontFamily = mainLyricFont;
                            }
                        }
                    }
                }
            }

            // 主歌词字号变更：强制重排（SyncLyricsToPosition 按距离套用四档字号）
            if (_lyricTextBlocks.Count > 0
                && (_appliedMainLyricFontSize != settings.MainLyricFontSize
                    || _appliedMainLyricFontFamily != (settings.MainLyricFontFamily ?? string.Empty)))
            {
                _appliedMainLyricFontSize = settings.MainLyricFontSize;
                _appliedMainLyricFontFamily = settings.MainLyricFontFamily ?? string.Empty;
                SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero, force: true);
            }
        }

        private void ApplySpectrumVisibilityFromSettings(AppSettingsState settings)
        {
            // 「显示频谱」关掉时把可视化整体藏起来；打开时交给 ApplyNowPlayingVisual
            // 按当前模式（波形/频谱柱/径向/示波器）决定到底显示哪一块画布，
            // 避免这里只认 WaveformCanvas、把径向模式也给盖掉。
            if (!settings.ShowSpectrum)
            {
                if (WaveformCanvas != null)
                {
                    WaveformCanvas.Visibility = Visibility.Collapsed;
                }

                if (RadialVisualCanvas != null)
                {
                    RadialVisualCanvas.Visibility = Visibility.Collapsed;
                }

                return;
            }

        }

        private void ApplyCoverVisibilityFromSettings(AppSettingsState settings)
        {
            if (NowPlayingCoverBorder != null)
            {
                NowPlayingCoverBorder.Visibility = settings.ShowAlbumCover ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TransportCoverBorder != null)
            {
                TransportCoverBorder.Visibility = settings.ShowAlbumCover ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnEqualizerApplied()
        {
            EqualizerState state = EqualizerStore.Load();
            // 把增益实际应用到引擎：共享 / ASIO / 原生 WASAPI 独占 均可走统一 DSP 链；启用 EQ 后输出非 bit-perfect。
            _audioEngine?.SetEqualizer(state.BandGains);
            bool any = state.BandGains != null && state.BandGains.Any(g => Math.Abs(g) > 0.01);
            NowPlayingText.Text = any
                ? "均衡器已应用（输出非 bit-perfect）"
                : "均衡器已应用（bit-perfect 直通）";
        }

        /// <summary>将歌曲插入当前播放项之后（下一首播放）。</summary>
        internal void PlaySongsNext(IEnumerable<PlaylistItem> songs)
        {
            List<PlaylistItem> incoming = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
                .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (incoming.Count == 0)
            {
                return;
            }

            string? playingPath = _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                ? _queue.Items[_queue.Index].FilePath
                : null;

            var incomingPaths = new HashSet<string>(
                incoming.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            var withoutIncoming = _queue.Items
                .Where(s => !incomingPaths.Contains(s.FilePath))
                .Select(ClonePlaylistItem)
                .ToList();

            int insertAt = 0;
            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                int playingIndex = withoutIncoming.FindIndex(s =>
                    string.Equals(s.FilePath, playingPath, StringComparison.OrdinalIgnoreCase));
                insertAt = playingIndex >= 0 ? playingIndex + 1 : 0;
            }

            var rebuilt = new List<PlaylistItem>(withoutIncoming.Count + incoming.Count);
            rebuilt.AddRange(withoutIncoming.Take(insertAt));
            rebuilt.AddRange(incoming.Select(ClonePlaylistItem));
            rebuilt.AddRange(withoutIncoming.Skip(insertAt));

            for (int i = 0; i < rebuilt.Count; i++)
            {
                rebuilt[i].Index = i + 1;
            }

            bool rebind = ReferenceEquals(PlaylistView.ItemsSource, _queue.Items);
            if (rebind)
            {
                PlaylistView.ItemsSource = null;
            }

            _queue.Replace(rebuilt);
            if (rebind || string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _queue.Items;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _queue.Index = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            NowPlayingText.Text = $"已加入下一首播放：{incoming.Count} 首";
        }

        internal void ToggleFavoriteForCurrent()
        {
            string? path = _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    path = _queue.Items[_queue.Index].FilePath;
                }
                else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    path = _playlist[_currentIndex].FilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            bool fav = TrackStatsStore.ToggleFavorite(path);
            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            // 任务栏缩略图按钮：fav=true 实心红心 / fav=false 空心轮廓心
            _taskbarButtons?.UpdateFavorite(fav);

            NowPlayingText.Text = fav ? "已添加到我喜欢的音乐" : "已取消喜欢";
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
            => ToggleFavoriteForCurrent();

        // ---- 播放歌曲信息页（状态条封面进入；左上角倒三角箭头返回） ----
        private void TransportCover_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 点击状态条封面：在主程序（状态条上方）切换播放歌曲信息页（整合式视图切换）
            if (NowPlayingPane == null || string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            SetNowPlayingPaneVisible(!_nowPlayingPaneOpen);
        }

        /// <summary>
        /// 还原左分类栏的「浮动卡片」外观（2026-10-16）：Margin 贴左缘（-16）+
        /// 卡片底色 + CardStroke 右边线 + ThemeShadow 投影。
        /// 播放页展开时会把这些全部抹掉（沉浸视觉，只留条目），收起时调本方法恢复。
        /// 资源读取失败（主题资源被换掉）时留透明，绝不让面板起不来。
        /// </summary>
        /// <summary>左栏卡片（展开态）的外边距：单一真相源，XAML 初值与还原逻辑都用它。
        /// ⚠️ 左 = -16 是**故意的**：MainContentGrid 有 16 的左外边距，-16 正好把卡片左缘
        /// 顶到窗口左边界（用户 2026-10-16 明确要求"分类栏这一块要和主程序左边界相接"）。
        /// 图标离边界的距离不靠这个 Margin 调，靠卡片内边距 LeftNavPaddingExpanded 调。</summary>
        internal static Microsoft.UI.Xaml.Thickness LeftNavCardMarginExpanded => new(-16, 4, 14, 4);

        /// <summary>左栏卡片（收起态窄条）的外边距：同样贴窗口左缘（与展开态一致），
        /// 只把右侧留白收窄。卡片宽 = 56(列宽) + 16(-16 左溢出) - 6(右留白) = 66px；
        /// 图标在卡片内边距 15/15 下自动居中（内容盒中心 32.5），收起态不再带投影
        /// （ThemeShadow 渐带约 25px，在窄条右缘显脏——用户 2026-10-16 反馈
        /// "右边界似乎有宽度、遮住了一点图标"的根因之一）。</summary>
        internal static Microsoft.UI.Xaml.Thickness LeftNavCardMarginCollapsed => new(-16, 4, 6, 4);

        /// <summary>播放页展开态：左栏卡片去掉背景后用的外边距（上下 10 是给标题栏压 0 留的呼吸）。</summary>
        internal static Microsoft.UI.Xaml.Thickness LeftNavCardMarginNowPlaying => new(-16, 10, 14, 10);

        private void RestoreLeftCategoryNavCard()
        {
            if (LeftCategoryNavBorder == null)
            {
                return;
            }

            // ⚠️ 与 MainWindow.xaml 里 LeftCategoryNavBorder 的 Margin 保持一致。
            // 2026-10-16：左栏当时是收起态的话，必须还原成**窄条外壳**（右留白 6 + 无投影），
            // 不能一律按展开态还原——否则"收起着左栏关播放页"会把窄条撑宽、投影回来。
            // 外壳三态的单一真相源在 Misc.ApplyLeftNavCardChrome。
            LeftCategoryNavBorder.Margin = _leftNavCollapsed ? LeftNavCardMarginCollapsed : LeftNavCardMarginExpanded;
            try
            {
                if (Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] is Brush bg)
                {
                    LeftCategoryNavBorder.Background = bg;
                }

                if (Application.Current.Resources["CardStrokeColorDefaultBrush"] is Brush stroke)
                {
                    LeftCategoryNavBorder.BorderBrush = stroke;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.RestoreLeftCategoryNavCard", caught);
            }

            // 2026-10-16：收起态窄条**不带投影**（投影渐带约 25px，在 66px 窄条上右缘会显脏）。
            // 单一真相源在 Misc.ApplyLeftNavCardChrome——收起态就不该在这里把投影加回来。
            if (!_leftNavCollapsed && LeftCategoryNavBorder.Shadow is not Microsoft.UI.Xaml.Media.ThemeShadow)
            {
                LeftCategoryNavBorder.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                LeftCategoryNavBorder.Translation = new System.Numerics.Vector3(0, 0, 40f);
            }
        }

        /// <summary>
        /// 关播放页时还原左栏三件套：可见性 + 列宽 + 卡片外壳（margin/背景/边线/投影）。
        ///
        /// 2026-10-16 晚间用户要求「打开播放页时左侧分类栏要隐藏」：展开播放页时
        /// LeftCategoryNavBorder 整块 Collapsed、左列宽压 0（播放页四边沉浸）；
        /// 关闭时必须原样还回来。列宽不另存字段——按左栏自己的状态还原
        ///（收起态 LeftNavCollapsedWidth / 展开态 _leftNavExpandedWidth，后者记住
        /// 用户拖宽后的宽度），与 Misc.SetLeftNavCollapsed 共用同一套状态。
        /// </summary>
        private void RestoreLeftNavForNowPlaying()
        {
            if (LeftCategoryNavBorder != null)
            {
                LeftCategoryNavBorder.Visibility = Visibility.Visible;
                RestoreLeftCategoryNavCard();
            }

            if (CategoryColumn != null)
            {
                if (_leftNavCollapsed)
                {
                    CategoryColumn.Width = new GridLength(LeftNavCollapsedWidth);
                    CategoryColumn.MinWidth = LeftNavCollapsedWidth;
                }
                else
                {
                    CategoryColumn.Width = new GridLength(_leftNavExpandedWidth);
                    CategoryColumn.MinWidth = LeftNavExpandedMinWidth;
                }
            }
        }

        /// <summary>景深切换：切出播放信息页时面板前推、主内容区(含左侧分类)退后变暗；收起则反向恢复。</summary>
        private void SetNowPlayingPaneVisible(bool visible)        {
            _nowPlayingPaneOpen = visible;
            if (NowPlayingPane == null)
            {
                return;
            }

            var mcBack = MainContentGrid?.Resources["MainContentDepthBackStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var mcRestore = MainContentGrid?.Resources["MainContentDepthRestoreStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var depthIn = NowPlayingPane.Resources["NowPlayingDepthInStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var depthOut = NowPlayingPane.Resources["NowPlayingDepthOutStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;

            if (visible)
            {
                try
                {
                    mcRestore?.Stop();
                    depthOut?.Stop();
                    nowPlayingDepthOutSubscribed = false;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

                NowPlayingPane.Opacity = 0;
                NowPlayingPane.Visibility = Visibility.Visible;
                // E1：播放页盖到窗口最顶——标题栏整条压 0（Row 0 塌陷）、主内容区边距清零，
                // 页面顶边直接顶到窗口上沿、右缘抵到窗口边缘。
                AppTitleBar.Height = 0;
                MainContentGrid.Margin = new Thickness(0);
                // 2026-10-16 晚间用户改要求：「打开播放页的时候左侧分类栏要隐藏」。
                // （同天早先的要求是"仍显示、点分类条目照样能切库"——那条作废，以最新为准；
                //   再往前的旧方案 SetLibraryNavHiddenForNowPlaying 也早已删除。）
                // 整块 Collapsed + 左列宽压 0：播放页左缘也抵到窗口左缘（E1 沉浸四边）。
                // 关闭时按左栏自己的状态还原列宽（收起态 56 / 展开态 _leftNavExpandedWidth，
                // 复用 Misc 那套状态不另存字段），卡片外壳走 RestoreLeftNavForNowPlaying。
                if (LeftCategoryNavBorder != null && CategoryColumn != null)
                {
                    LeftCategoryNavBorder.Visibility = Visibility.Collapsed;
                    CategoryColumn.Width = new GridLength(0);
                    CategoryColumn.MinWidth = 0;
                }
                // 右上角标题栏按钮列整列隐藏（沉浸看歌）。含最小化/最大化/关闭——
                // 展开态要用系统按钮需先退出播放页（左上角返回或 Esc）。用户 2026-10-16 明确要求。
                if (TitleBarFloatingButtons != null)
                {
                    TitleBarFloatingButtons.Visibility = Visibility.Collapsed;
                }
                // 浮动列让位后，播放页自己的「可视化/布局」两个文字按钮挪到右缘（原右距 280 是避让浮动列）
                if (NowPlayingTopRightButtons != null)
                {
                    NowPlayingTopRightButtons.Margin = new Thickness(0, 0, 8, 0);
                }
                // 拖动区右缘同步收紧：只避让那两个文字按钮（约 240px），不再给隐藏的浮动列留 280
                if (NowPlayingTitleBarDragArea != null)
                {
                    NowPlayingTitleBarDragArea.Margin = new Thickness(44, 0, 240, 0);
                }
                TrySwitchTitleBar(NowPlayingTitleBarDragArea);
                StartupLog.Write($"[深度] 展开播放页：mcBack={(mcBack != null)} depthIn={(depthIn != null)} " +
                                 $"左栏={LeftCategoryGrid?.Visibility}");
                // 沉浸式：隐藏底部常驻状态条，改由页内悬浮控制条承担播放控制
                ShowFloatingTransport();
                // Begin 前把 TargetName 全部换成代码 SetTarget（否则 mcBack.Begin() 必抛
                // COMException，主内容区不退后 = 播放页和原页面重叠）
                RetargetStoryboard(mcBack, "mcBack");
                RetargetStoryboard(depthIn, "depthIn");
                try
                {
                    mcBack?.Begin();
                    depthIn?.Begin();
                }
                catch (Exception caught)
                {
                    // 以前这里是空 catch：景深动画一旦起不来（比如 TargetName 解析不到），
                    // 表现就是"播放页底下的左侧分类/分隔线全透出来"，而日志里一个字都没有。
                    global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.SetNowPlayingPaneVisible(展开)", caught);
                    NowPlayingPane.Opacity = 1;
                }
            }
            else
            {
                // E1：恢复标题栏高度与主内容区边距，拖动职责交还标题栏
                // （在主界面淡入之前恢复，退场动画里看到的就是正常形态）
                AppTitleBar.Height = 48;
                MainContentGrid.Margin = new Thickness(16, 8, 16, 0);
                TrySwitchTitleBar(AppTitleBar);
                // 2026-10-16 晚间：左栏在播放页展开期间是**整块隐藏**的（用户最新要求），
                // 这里连可见性 + 列宽 + 卡片外壳一起还原。列宽按左栏自己的状态走
                //（收起态 56 / 展开态 _leftNavExpandedWidth），单一真相源在 Misc。
                RestoreLeftNavForNowPlaying();
                if (TitleBarFloatingButtons != null)
                {
                    TitleBarFloatingButtons.Visibility = Visibility.Visible;
                }
                if (NowPlayingTopRightButtons != null)
                {
                    NowPlayingTopRightButtons.Margin = new Thickness(0, 0, 280, 0);
                }
                if (NowPlayingTitleBarDragArea != null)
                {
                    NowPlayingTitleBarDragArea.Margin = new Thickness(44, 0, 280, 0);
                }
                try
                {
                    mcBack?.Stop();
                    depthIn?.Stop();
                    if (!nowPlayingDepthOutSubscribed && depthOut != null)
                    {
                        depthOut.Completed += NowPlayingDepthOut_Completed;
                        nowPlayingDepthOutSubscribed = true;
                    }
                    // 收起路径同样先重定向再 Begin（mcRestore/depthOut 也是 TargetName 写法）
                    RetargetStoryboard(mcRestore, "mcRestore");
                    RetargetStoryboard(depthOut, "depthOut");
                    mcRestore?.Begin();
                    depthOut?.Begin();
                    // 退出播放页：恢复底部常驻状态条，收起页内悬浮控制条
                    HideFloatingTransport();
                }
                catch
                {
                    // 动画起不来时手动把状态摆正：播放页收起 + 主内容区不透明（否则会剩下一片"退后"的暗界面）
                    try
                    {
                        mcRestore?.Stop();
                        depthOut?.Stop();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

                    // E1：动画起不来的降级路径同样要恢复标题栏，否则收起后标题栏仍是压 0 状态
                    AppTitleBar.Height = 48;
                    MainContentGrid.Margin = new Thickness(16, 8, 16, 0);
                    TrySwitchTitleBar(AppTitleBar);
                    NowPlayingPane.Visibility = Visibility.Collapsed;
                    NowPlayingPane.Opacity = 1;

                    // 2026-10-16 晚间：降级路径同样要还原左栏三件套（可见性 + 列宽 + 卡片外壳）
                    RestoreLeftNavForNowPlaying();
                    if (TitleBarFloatingButtons != null)
                    {
                        TitleBarFloatingButtons.Visibility = Visibility.Visible;
                    }
                    if (NowPlayingTopRightButtons != null)
                    {
                        NowPlayingTopRightButtons.Margin = new Thickness(0, 0, 280, 0);
                    }
                    if (NowPlayingTitleBarDragArea != null)
                    {
                        NowPlayingTitleBarDragArea.Margin = new Thickness(44, 0, 280, 0);
                    }

                    if (LibraryPaneRoot != null)
                    {
                        LibraryPaneRoot.Opacity = 1;
                    }
                }
            }
        }

        private bool nowPlayingDepthOutSubscribed;

        /// <summary>
        /// 把 XAML 故事板里按 Storyboard.TargetName 定位的动画逐个改成代码 SetTarget。
        ///
        /// 2026-10-16 实录：MainContentGrid.Resources 里的 MainContentDepthBackStoryboard
        /// 运行时 Begin() 抛 COMException「Cannot resolve TargetName LeftCategoryTransform」
        /// （日志 15:05 / 16:03 / 16:50 三次逐字一致）。已排除的猜想：
        /// ① "有人 new CompositeTransform 换掉了实例"——全仓库无第二处替换，且改成只改
        ///    TranslateX 后照样抛；② "名字没注册"——生成代码里 LeftCategoryTransform 与
        ///    能正常工作的 AudioSettingsPanelTransform 注册方式逐字相同。
        /// 结论：元素 Resources 故事板的 TargetName 解析在本仓有案不明的失败，不赌它。
        ///
        /// 后果链：mcBack 一抛 → 同 try 里的 depthIn 也不跑 → 主内容区不退后、
        /// 播放页停在 0.92 缩放 + 下移 16px → 用户看到「播放页和原来页面重叠」。
        ///
        /// 修法：按名字先从页面作用域 FindName，再按已知 x:Name 字段兜底，拿到对象直接
        /// SetTarget——SetTarget 优先于 TargetName，Begin() 不再走名字解析。
        /// 幂等，每次开合播放页都调，开销 = 几条动画。</summary>
        private void RetargetStoryboard(Microsoft.UI.Xaml.Media.Animation.Storyboard? storyboard, string tag)
        {
            if (storyboard == null)
            {
                return;
            }

            int ok = 0;
            int fail = 0;
            foreach (var child in storyboard.Children)
            {
                string? name = Microsoft.UI.Xaml.Media.Animation.Storyboard.GetTargetName(child);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                DependencyObject? target = RootShell?.FindName(name) as DependencyObject;
                if (target == null)
                {
                    target = name switch
                    {
                        "LeftCategoryGrid" => LeftCategoryGrid,
                        "LeftCategoryTransform" => LeftCategoryGrid?.RenderTransform,
                        "LibraryPaneRoot" => LibraryPaneRoot,
                        "LibraryPaneTransform" => LibraryPaneRoot?.RenderTransform,
                        "NowPlayingPane" => NowPlayingPane,
                        "NowPlayingPaneTransform" => NowPlayingPane?.RenderTransform,
                        // 音频链路抽屉（B 组）：与上面同族，名字解析在本仓有失败案，一律代码锁死
                        "AudioChainPanelHost" => AudioChainPanelHost,
                        "AudioChainPanelTransform" => AudioChainPanelHost?.RenderTransform,
                        "AudioSettingsPanel" => AudioSettingsPanel,
                        "AudioSettingsPanelTransform" => AudioSettingsPanel?.RenderTransform,
                        _ => null
                    };
                }

                if (target != null)
                {
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(child, target);
                    ok++;
                }
                else
                {
                    fail++;
                    StartupLog.Write($"[深度] 重定向失败 tag={tag} name={name}");
                }
            }

            StartupLog.Write($"[深度] 重定向 tag={tag} 动画数={storyboard.Children.Count} 已定向={ok} 失败={fail}");
        }

        /// <summary>E1：切换窗口拖动区（标题栏 ↔ 播放页内透明拖动条）。
        /// SetTitleBar 重复调用即替换；目标元素不可见时切换无效，先校验再切，异常只记日志不炸。</summary>
        private void TrySwitchTitleBar(FrameworkElement element)
        {
            try
            {
                if (element != null && element.Visibility == Visibility.Visible)
                {
                    SetTitleBar(element);
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.TrySwitchTitleBar", caught);
            }
        }

        /// <summary>收起动画完成后折叠面板。</summary>
        private void NowPlayingDepthOut_Completed(object? sender, object e)
        {
            nowPlayingDepthOutSubscribed = false;
            if (NowPlayingPane != null)
            {
                NowPlayingPane.Visibility = Visibility.Collapsed;
                NowPlayingPane.Opacity = 1;
            }
        }

        // ---- 页内悬浮控制条（进入播放页显示，鼠标静止自动淡出） ----
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatingIdleTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatingFadeTimer;
        private double _floatingFadeFrom;
        private double _floatingFadeTo;
        private int _floatingFadeStep;
        private bool _floatingShownTarget;
        private bool _floatingMirrorSubscribed;
        private bool _floatingPaneWired;
        private bool _floatingMirror;
        private Visibility _bottomBarVisibilityBefore = Visibility.Visible;

        /// <summary>进入播放页：隐藏底部常驻状态条，显示页内悬浮控制条并同步一次当前播放状态，随后开始闲置淡出计时。</summary>
        private void ShowFloatingTransport()
        {
            try
            {
                // 隐藏底部常驻状态条（记录原状态以便退出时恢复）
                if (BottomTransportBar != null)
                {
                    _bottomBarVisibilityBefore = BottomTransportBar.Visibility;
                    BottomTransportBar.Visibility = Visibility.Collapsed;
                }

                if (NowPlayingFloatingBar == null)
                {
                    return;
                }

                // 订阅真实进度条的变化，把位置/时间镜像到悬浮条（仅一次）
                if (!_floatingMirrorSubscribed && ProgressSlider != null)
                {
                    ProgressSlider.ValueChanged += ProgressSliderMirror_ValueChanged;
                    _floatingMirrorSubscribed = true;
                }
                // 鼠标在播放页内移动时唤醒悬浮条（仅挂一次）
                if (!_floatingPaneWired && NowPlayingPaneContent != null)
                {
                    NowPlayingPaneContent.PointerMoved += NowPlayingPaneContent_PointerMoved;
                    _floatingPaneWired = true;
                }

                SyncFloatingTransport();
                ApplyFloatingBarStyle();
                SetFloatingBarVisible(true, false);

                // 进度条拖动/悬停时的浮动提示默认显示"原始秒数"（如 123.45），换成 分:秒。
                // 用代码赋值而不是 XAML 资源：避免动 XAML 触发本项目偶发的 XamlCompiler 崩溃。
                // ⚠️ 转换器类名是 SecondsToTimeSpanConverter（定义在 MainWindow.xaml.cs），别写错。
                var timeTip = new SecondsToTimeSpanConverter();
                if (NowPlayingProgressSlider != null && NowPlayingProgressSlider.ThumbToolTipValueConverter == null)
                {
                    NowPlayingProgressSlider.ThumbToolTipValueConverter = timeTip;
                }
                // 底部常驻进度条同样换成 分:秒（否则两处提示口径不一致）
                if (ProgressSlider != null && ProgressSlider.ThumbToolTipValueConverter == null)
                {
                    ProgressSlider.ThumbToolTipValueConverter = timeTip;
                }

                // 两种布局都改成"内嵌常显"：不再启动闲置淡出计时（用户要求经典也内嵌）。
                _floatingIdleTimer?.Stop();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ShowFloatingTransport", caught);
            }
        }

        /// <summary>按布局调整播放条样式：水面=内嵌透明（贴在波形下方）；经典=亚克力药丸，内嵌在面板底部。
        /// 两种布局都是"内嵌常显"，不再做鼠标闲置淡出（用户要求经典也内嵌）。</summary>
        private void ApplyFloatingBarStyle()
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }

            // 两种布局统一「内嵌」：不要亚克力卡片底、不要描边、不要圆角 ——
            // 带背景+描边的药丸看起来就是「浮在页面上的悬浮条」，用户要的是融进播放页底部。
            // 宽度/边距由 UpdateNowPlayingCardLayout 设置（横跨面板、随宽度变、不被 680 封顶）。
            NowPlayingFloatingBar.Background = new SolidColorBrush(Colors.Transparent);
            NowPlayingFloatingBar.BorderBrush = new SolidColorBrush(Colors.Transparent);
            NowPlayingFloatingBar.BorderThickness = new Thickness(0);
            NowPlayingFloatingBar.CornerRadius = new CornerRadius(0);
            NowPlayingFloatingBar.Padding = new Thickness(0, 0, 0, 10);
        }

        /// <summary>退出播放页：恢复底部常驻状态条，收起悬浮控制条并停止闲置计时。</summary>
        private void HideFloatingTransport()
        {
            try
            {
                _floatingIdleTimer?.Stop();
                if (BottomTransportBar != null)
                {
                    BottomTransportBar.Visibility = _bottomBarVisibilityBefore;
                }
                if (NowPlayingFloatingBar != null)
                {
                    NowPlayingFloatingBar.Visibility = Visibility.Collapsed;
                    NowPlayingFloatingBar.Opacity = 1;
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.HideFloatingTransport", caught);
            }
        }

        /// <summary>把真实进度条的位置 / 时间 / 播放暂停图标镜像到悬浮控制条。</summary>
        private void SyncFloatingTransport()
        {
            if (NowPlayingProgressSlider == null)
            {
                return;
            }
            _floatingMirror = true;
            try
            {
                NowPlayingProgressSlider.Maximum = ProgressSlider?.Maximum ?? 100;
                NowPlayingProgressSlider.Value = ProgressSlider?.Value ?? 0;
                if (FloatingCurrentTime != null) FloatingCurrentTime.Text = CurrentTimeText?.Text ?? "00:00";
                if (FloatingTotalTime != null) FloatingTotalTime.Text = TotalTimeText?.Text ?? "00:00";
                if (FloatingPlayPauseIcon != null) FloatingPlayPauseIcon.Glyph = PlayPauseIcon?.Glyph ?? "\uE768";
            }
            finally
            {
                _floatingMirror = false;
            }

        }

        /// <summary>真实进度条变化时镜像到悬浮条（镜像期间忽略悬浮条自身的 ValueChanged，避免回环）。</summary>
        private void ProgressSliderMirror_ValueChanged(dynamic sender, dynamic e)
        {
            if (NowPlayingProgressSlider == null)
            {
                return;
            }

            // 用户正在拖页内进度条：此时不要用"播放位置"回写滑块，
            // 否则拖到一半会被播放进度拽回去（不跟手 / 松手跳回原处）。
            if (_isUserSeeking)
            {
                return;
            }

            _floatingMirror = true;
            try
            {
                NowPlayingProgressSlider.Maximum = ProgressSlider?.Maximum ?? 100;
                NowPlayingProgressSlider.Value = ProgressSlider?.Value ?? 0;
                if (FloatingCurrentTime != null) FloatingCurrentTime.Text = CurrentTimeText?.Text ?? "00:00";
                if (FloatingTotalTime != null) FloatingTotalTime.Text = TotalTimeText?.Text ?? "00:00";
            }
            finally
            {
                _floatingMirror = false;
            }
        }

        private void NowPlayingProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_floatingMirror)
            {
                return;
            }

            if (!_isUserSeeking)
            {
                return;
            }

            double seconds = Math.Clamp(e.NewValue, 0, NowPlayingProgressSlider?.Maximum ?? e.NewValue);

            // 拖动中：时间文字跟着走（手感），同时把值同步回真实进度条，
            // 让松手时的 SeekToSliderValue() 用的是用户拖到的位置。
            if (FloatingCurrentTime != null)
            {
                FloatingCurrentTime.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            }

            if (ProgressSlider != null && Math.Abs(ProgressSlider.Value - seconds) >= 0.01)
            {
                double keep = ProgressSlider.Value;
                ProgressSlider.Value = seconds;
                // 若真实进度条拒绝该值（Maximum 还没同步等极端情况），回退，避免两处长期不一致
                if (Math.Abs(ProgressSlider.Value - seconds) >= 0.01)
                {
                    ProgressSlider.Value = keep;
                }
            }
        }

        private void NowPlayingPaneContent_PointerMoved(dynamic sender, dynamic e)
        {
            if (_nowPlayingPaneOpen)
            {
                ResetFloatingIdle();
            }
        }

        private void NowPlayingFloatingBar_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_nowPlayingPaneOpen)
            {
                ResetFloatingIdle();
            }
        }

        private void EnsureFloatingIdleTimer()
        {
            if (_floatingIdleTimer == null)
            {
                _floatingIdleTimer = DispatcherQueue.CreateTimer();
                _floatingIdleTimer.Interval = TimeSpan.FromMilliseconds(2600);
                _floatingIdleTimer.Tick += (_, _) => SetFloatingBarVisible(false, true);
            }
        }

        private void ResetFloatingIdle()
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }
            // 播放条已改成"内嵌常显"，两种布局都不再参与闲置淡出；
            // 这里只需保证它处于可见状态（不做淡入动画、不重启计时）。
            if (NowPlayingFloatingBar.Visibility != Visibility.Visible)
            {
                SetFloatingBarVisible(true, false);
            }
        }

        private void SetFloatingBarVisible(bool visible, bool animate)
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }
            _floatingShownTarget = visible;
            if (visible)
            {
                if (NowPlayingFloatingBar.Visibility != Visibility.Visible)
                {
                    NowPlayingFloatingBar.Visibility = Visibility.Visible;
                }
                NowPlayingFloatingBar.IsHitTestVisible = true;
            }
            if (!animate)
            {
                _floatingFadeTimer?.Stop();
                NowPlayingFloatingBar.Opacity = visible ? 1 : 0;
                if (!visible)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            double from = NowPlayingFloatingBar.Opacity;
            double to = visible ? 1 : 0;
            if (Math.Abs(from - to) < 0.01)
            {
                NowPlayingFloatingBar.Opacity = to;
                if (!visible)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            if (_floatingFadeTimer == null)
            {
                _floatingFadeTimer = DispatcherQueue.CreateTimer();
                _floatingFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _floatingFadeTimer.Tick += FloatingFadeTick;
            }
            _floatingFadeTimer.Stop();
            _floatingFadeFrom = from;
            _floatingFadeTo = to;
            _floatingFadeStep = 0;
            _floatingFadeTimer.Start();
        }

        private void FloatingFadeTick(object? sender, object e)
        {
            if (NowPlayingFloatingBar == null || _floatingFadeTimer == null)
            {
                return;
            }
            _floatingFadeStep++;
            const int total = 14;
            if (_floatingFadeStep >= total)
            {
                NowPlayingFloatingBar.Opacity = _floatingFadeTo;
                _floatingFadeTimer.Stop();
                if (!_floatingShownTarget)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            NowPlayingFloatingBar.Opacity = _floatingFadeFrom
                + (_floatingFadeTo - _floatingFadeFrom) * (_floatingFadeStep / (double)total);
        }

        /// <summary>状态条封面 hover：封面变暗 + 朝上三角箭头淡入动画，提示可展开。</summary>
        private void TransportCover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (TransportCoverBorder == null)
            {
                return;
            }

            try
            {
                var arrowIn = TransportCoverBorder.Resources["TransportArrowInStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                var arrowOut = TransportCoverBorder.Resources["TransportArrowOutStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                arrowOut?.Stop();
                // 描边轻微高亮，提示可点击
                TransportCoverBorder.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(ResolveAccentColor());
                TransportCoverBorder.BorderThickness = new Thickness(2);
                arrowIn?.Begin();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void TransportCover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (TransportCoverBorder == null)
            {
                return;
            }

            try
            {
                var arrowIn = TransportCoverBorder.Resources["TransportArrowInStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                var arrowOut = TransportCoverBorder.Resources["TransportArrowOutStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                arrowIn?.Stop();
                // 移出 hover 后，按"有无封面"恢复边框：有封面→无框，无封面→显示框
                ApplyCoverFrame(TransportCoverBorder, TransportCoverImage);
                arrowOut?.Begin();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void NowPlayingCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            // 左上角返回按钮：收起播放歌曲信息页，恢复主内容区视图
            SetNowPlayingPaneVisible(false);
        }

        private void SeekBackButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(-5);

        private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(5);

        private void FeaturesMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

            // 「查找歌曲…」已按用户要求移除（老版本遗留功能，窗口一并删除）。

            // 「在线搜索…」已按用户要求整体下架（窗口文件删除；
            // 在线标签能力已由标签板块的「在线标签」子页承接（MusicBrainz+iTunes+CAA+LRClib）。

            // 「下载当前歌词 / 批量下载歌词 / 编辑歌词」已按用户要求移除：
            // 歌词相关功能统一收敛到标签板块（在线歌词 / 批量下载歌词两个子页）。

            // 「重复文件检测…」已按用户要求移除：曲库健康里已有重复文件检测。

            // 用户定稿的五项（2026-10-15）：只留与「正在播放这首歌」直接相关的动作
            var addToPlaylist = new MenuFlyoutItem { Text = "加入歌单…" };
            addToPlaylist.Click += async (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                {
                    await AddPathsToNamedPlaylistAsync(new[] { _nowPlayingPath });
                }
            };
            flyout.Items.Add(addToPlaylist);

            var editTag = new MenuFlyoutItem { Text = "编辑标签" };
            editTag.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                {
                    OpenTagEditForFiles(new[] { _nowPlayingPath });
                }
            };
            flyout.Items.Add(editTag);

            var locateAlbum = new MenuFlyoutItem { Text = "定位到专辑" };
            locateAlbum.Click += NowPlayingAlbumLinkButton_Click;
            flyout.Items.Add(locateAlbum);

            var showInFolder = new MenuFlyoutItem { Text = "在文件夹中显示" };
            showInFolder.Click += (_, _) => OpenFileLocationInExplorer(_nowPlayingPath);
            flyout.Items.Add(showInFolder);

            var copyTitleArtist = new MenuFlyoutItem { Text = "复制歌名与艺术家" };
            copyTitleArtist.Click += (_, _) => CopyNowPlayingTitleArtist();
            flyout.Items.Add(copyTitleArtist);

            flyout.Items.Add(new MenuFlyoutSeparator());

            // 2026-10-15（用户定案）：原「更多工具」子菜单里的「导入 M3U… / 导出当前列表为 M3U… /
            // 打开 CUE…」已挪到播放列表墙页眉（导入选歌单、不入媒体库；导出选歌单在卡片右键）。
            // WebUI 的同名入口不受影响（仍走 ImportM3u_Click / ExportM3u_Click）。

            if (sender is FrameworkElement fe)
            {
                flyout.ShowAt(fe);
            }
        }


        /// <summary>复制「歌名 - 艺术家」到剪贴板（播放状态栏更多菜单）。</summary>
        private void CopyNowPlayingTitleArtist()
        {
            string title = NowPlayingTitleText?.Text?.Trim() ?? "";
            string artist = NowPlayingArtistText?.Text?.Trim() ?? "";
            if (title.Length == 0 && artist.Length == 0)
            {
                return;
            }

            string text = artist.Length == 0 ? title : title + " - " + artist;
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            if (NowPlayingText != null)
            {
                NowPlayingText.Text = "已复制歌名与艺术家";
            }
        }

        /// <summary>
        /// 播放条「音频设置」面板弹出时刷新一次（输出模式高亮、设备列表、ASIO 提示等）。
        /// 这些控件原先在右上角面板里，现已搬进本面板，刷新的还是同一批控件。
        /// 2026-10-16：面板改 4 页签结构，这里额外负责建页签（首次）与刷新速度/定时两页的状态。
        /// </summary>
        private void AudioSettingsFlyout_Opened(object sender, object e)
        {
            try
            {
                EnsureAudioSettingsNavBuilt();
                RefreshAudioSettingsPanel();
                RefreshSpeedPage();
                RefreshSleepPage();
                RefreshAbLoopPage();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.AudioSettingsFlyout_Opened", caught);
            }
        }

        // ===== 音频设置面板：4 页签（2026-10-16 用户要求按分类拆开）=====
        // 复用 ShellPageHost（标签页左侧区域那条横向子导航的同一套实现），保证视觉一致。
        private ShellPageHost? _audioSettingsNav;
        private bool _audioSettingsNavBuilt;

        /// <summary>
        /// 面板「就绪」守卫：XAML 初值 + 事件绑定会在 InitializeComponent 期间就触发
        /// ValueChanged/Checked（WinUI3 秒崩铁律，见 MEMORY.md）——那时后续 x:Name 字段还没解析，
        /// handler 里碰任何控件都可能 NRE 秒崩。就绪前的刷新一律由本文件的 Refresh* 显式做。
        /// </summary>
        private bool _audioSettingsReady;

        private void EnsureAudioSettingsNavBuilt()
        {
            if (_audioSettingsNavBuilt)
            {
                return;
            }

            _audioSettingsNavBuilt = true;
            try
            {
                _audioSettingsNav = new ShellPageHost(AudioSettingsNavPanel, () => DspAccentBrush());
                _audioSettingsNav.Register("音频输出", string.Empty, AudioSettingsPageOutput);
                _audioSettingsNav.Register("AB 循环", string.Empty, AudioSettingsPageAbLoop);
                _audioSettingsNav.Register("播放速度", string.Empty, AudioSettingsPageSpeed);
                _audioSettingsNav.Register("睡眠定时", string.Empty, AudioSettingsPageSleep);
                _audioSettingsNav.Build();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.EnsureAudioSettingsNavBuilt", caught);
            }
        }

        // ---------- 页3：播放速度 ----------

        /// <summary>滑块拖动 → 实时变速（下一个音频块生效，不重载不断音）。</summary>
        private void PlaybackRateSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_audioSettingsReady)
            {
                return;
            }

            _ = SetPlaybackRateAsync(e.NewValue);
        }

        /// <summary>音调模式切换：保调（WSOLA）/ 变调（重采样）。立即生效。</summary>
        private void PlaybackRatePitchMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_audioSettingsReady)
            {
                return;
            }

            bool preserve = ReferenceEquals(sender, PlaybackRatePitchPreserveRadio);
            AppSettingsStore.Update(s => s.PlaybackRatePreservePitch = preserve);
            double rate = AppSettingsStore.Load().PlaybackRate;
            _audioEngine?.SetPlaybackSpeed(rate, preserve);
            if (Math.Abs(rate - 1.0) > 0.001 && IsEngineActiveNow)
            {
                NowPlayingText.Text = "播放速度 " + rate.ToString("0.##") + "x · "
                    + (preserve ? "音调不变" : "音调随速度变");
            }
        }

        private void PlaybackRateResetButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SetPlaybackRateAsync(1.0);
            if (PlaybackRateSlider != null)
            {
                PlaybackRateSlider.Value = 1.0; // 触发 ValueChanged → 已是 1.0 时内部 no-op
            }
        }

        /// <summary>弹出面板时把设置里的倍率/模式同步到滑块与单选（批量赋值期间挂就绪守卫，避免触发应用）。</summary>
        private void RefreshSpeedPage()
        {
            if (PlaybackRateSlider == null || PlaybackRatePitchPreserveRadio == null || PlaybackRatePitchFollowRadio == null)
            {
                return;
            }

            _audioSettingsReady = false;
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                PlaybackRateSlider.Value = Math.Clamp(s.PlaybackRate, 0.5, 2.0);
                PlaybackRatePitchPreserveRadio.IsChecked = s.PlaybackRatePreservePitch;
                PlaybackRatePitchFollowRadio.IsChecked = !s.PlaybackRatePreservePitch;
                UpdatePlaybackRateButtonText();
            }
            finally
            {
                _audioSettingsReady = true;
            }
        }

        // ---------- 页4：睡眠定时 ----------

        private static int SleepRadioIndex(string? name) => name switch
        {
            "SleepOffRadio" => 0,
            "Sleep15Radio" => 1,
            "Sleep30Radio" => 2,
            "Sleep60Radio" => 3,
            "Sleep90Radio" => 4,
            "Sleep120Radio" => 5,
            "SleepAfterTrackRadio" => 6,
            "SleepAfterTracksRadio" => 7,
            _ => -1
        };

        /// <summary>睡眠定时单选：选完即生效（不再弹对话框；WebUI 遥控仍走原来的对话框）。</summary>
        private void SleepModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_audioSettingsReady || sender is not RadioButton rb)
            {
                return;
            }

            int sel = SleepRadioIndex(rb.Name);
            if (sel < 0)
            {
                return;
            }

            if (SleepTracksBox != null)
            {
                SleepTracksBox.Visibility = sel == 7 ? Visibility.Visible : Visibility.Collapsed;
            }

            ApplySleepSelection(sel, SleepTracksBox?.Value ?? 1);
        }

        /// <summary>弹出面板时把当前定时状态回显到单选（批量赋值挂就绪守卫）。</summary>
        private void RefreshSleepPage()
        {
            if (SleepOffRadio == null || SleepAfterTracksRadio == null || SleepTracksBox == null)
            {
                return;
            }

            _audioSettingsReady = false;
            try
            {
                int sel = _sleepMode switch
                {
                    SleepStopMode.AfterTrack => 6,
                    SleepStopMode.AfterTracks => 7,
                    SleepStopMode.AfterMinutes => _sleepMinutes switch
                    {
                        15 => 1,
                        30 => 2,
                        60 => 3,
                        90 => 4,
                        120 => 5,
                        _ => 2
                    },
                    _ => 0
                };

                SleepOffRadio.IsChecked = sel == 0;
                Sleep15Radio.IsChecked = sel == 1;
                Sleep30Radio.IsChecked = sel == 2;
                Sleep60Radio.IsChecked = sel == 3;
                Sleep90Radio.IsChecked = sel == 4;
                Sleep120Radio.IsChecked = sel == 5;
                SleepAfterTrackRadio.IsChecked = sel == 6;
                SleepAfterTracksRadio.IsChecked = sel == 7;
                SleepTracksBox.Value = Math.Clamp(_sleepTracksRemaining <= 0 ? 1 : _sleepTracksRemaining, 1, 99);
                SleepTracksBox.Visibility = sel == 7 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _audioSettingsReady = true;
            }
        }

        /// <summary>
        /// 睡眠定时设置对话框。2026-10-16 起主入口改成音频设置面板里的「睡眠定时」页签
        /// （选完即生效），本对话框只留给 WebUI 遥控（"sleeptimer" 命令）。
        /// 应用逻辑与页签共用 <see cref="ApplySleepSelection"/>。
        /// </summary>
        private async Task ShowSleepTimerDialogAsync()
        {
            var radio = new RadioButtons();
            radio.Items.Add("关闭定时器");
            radio.Items.Add("15 分钟");
            radio.Items.Add("30 分钟");
            radio.Items.Add("60 分钟");
            radio.Items.Add("90 分钟");
            radio.Items.Add("120 分钟");
            radio.Items.Add("当前曲目播完后停止");
            radio.Items.Add("再播放指定曲目数后停止");
            radio.SelectedIndex = 2;

            var numberBox = new NumberBox
            {
                Minimum = 1,
                Maximum = 99,
                Value = 1,
                Header = "曲目数",
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Visibility = Visibility.Collapsed
            };
            radio.SelectionChanged += (_, _) =>
            {
                numberBox.Visibility = radio.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 260 };
            panel.Children.Add(new TextBlock { Text = "睡眠定时器：到时自动暂停播放", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(radio);
            panel.Children.Add(numberBox);

            var dialog = new ContentDialog
            {
                Title = "睡眠定时器",
                Content = panel,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // 与音频设置面板「睡眠定时」页签共用同一套应用逻辑（sel 口径一致）
            ApplySleepSelection(radio.SelectedIndex, numberBox.Value);
        }

        /// <summary>
        /// 打开 ReplayGain 扫描：2026-10-12 阶段 2 起不再弹独立窗口 ——
        /// 扫描功能已内嵌到音效处理的「响度 · ReplayGain」页，这里直接跳过去。
        /// 待扫描曲目由范围决定（整库 / 当前播放列表 / 选中曲目），见 BuildRgScanInputs。
        /// </summary>
        private void OpenReplayGainScan()
        {
            GoToDspPage(DspPageRgIndex);
        }


        private PlaylistItem? FindLibraryItemByPath(string path)
        {
            int index = FindLibraryIndex(path);
            return index >= 0 ? _playlist[index] : null;
        }

        private async void ExportM3u_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedFileName = "playlist";
                picker.FileTypeChoices.Add("M3U8 播放列表", new List<string> { ".m3u8" });
                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                var paths = _queue.Items.Select(s => s.FilePath).ToList();
                var entries = _queue.Items
                    .Select(s => (s.FilePath, s.Title, s.Artist, s.Duration.TotalSeconds))
                    .ToList();
                M3uPlaylistIO.WriteM3u8(file.Path, paths, entries);
                NowPlayingText.Text = "已导出 M3U：" + file.Path;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导出 M3U 失败", ex.Message);
            }
        }

        private async void ConvertCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            try
            {
                var picker = new FileSavePicker();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_nowPlayingPath);
                picker.FileTypeChoices.Add("WAV", new List<string> { ".wav" });
                picker.FileTypeChoices.Add("MP3", new List<string> { ".mp3" });
                picker.FileTypeChoices.Add("FLAC", new List<string> { ".flac" });
                picker.FileTypeChoices.Add("OGG", new List<string> { ".ogg" });
                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                NowPlayingText.Text = "正在转换…";
                string format = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();
                (bool ok, string message) = await FormatConvertService.ConvertAsync(_nowPlayingPath, file.Path, format);
                NowPlayingText.Text = ok ? "转换完成：" + file.Path : "转换失败";
                if (!ok)
                {
                    await ShowErrorAsync("格式转换", message);
                }
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("格式转换失败", ex.Message);
            }
        }

        private async Task DownloadCoverForCurrentAsync()
        {
            if (string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = FindLibraryItemByPath(_nowPlayingPath);
            if (item == null)
            {
                return;
            }

            NowPlayingText.Text = "正在下载封面…";
            bool ok = await OnlineMusicApi.DownloadAndEmbedCoverAsync(item.Title, item.Artist, item.FilePath);
            if (ok)
            {
                InvalidateCoverCache(item.FilePath);
            }

            if (!ok)
            {
                NowPlayingText.Text = "未找到可下载的封面";
                return;
            }

            await UpdateNowPlayingPanelAsync(item);
            NowPlayingText.Text = "封面已更新";
        }

        private void ApplyFavoritesOrRecentCategory()
        {
            LibraryPaneTitle.Visibility = Visibility.Visible;
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

            var items = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                foreach (string path in TrackStatsStore.GetAllFavorites())
                {
                    PlaylistItem? fromLib = FindLibraryItemByPath(path);
                    if (fromLib != null)
                    {
                        items.Add(ClonePlaylistItem(fromLib));
                    }
                    else if (System.IO.File.Exists(path))
                    {
                        try
                        {
                            items.Add(CreatePlaylistItemFromPath(path));
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                    }
                }
            }
            else
            {
                // 最近播放 = 播放历史事件流水（每次播放一条记录，含播放时间/时长/是否播完）。
                // 与 LibraryDb 记录点（切歌/播完）对应；双击等交互复用歌曲行通用逻辑。
                // 「最近播放范围」设置：0=全部；N=只显示最近 N 天（本地时区换算成 UTC 比较）。
                int recentRangeDays = AppSettingsStore.Load().RecentPlayedRangeDays;
                DateTime rangeFromUtc = recentRangeDays > 0
                    ? DateTime.UtcNow.AddDays(-recentRangeDays)
                    : DateTime.MinValue;
                foreach (LibraryDb.PlaybackHistoryEntry e in LibraryDb.LoadPlaybackHistory(200))
                {
                    if (e.PlayedAtUtc < rangeFromUtc)
                    {
                        continue; // 早于范围起点，跳过
                    }

                    PlaylistItem? item = null;
                    if (System.IO.File.Exists(e.FilePath))
                    {
                        PlaylistItem? fromLib = FindLibraryItemByPath(e.FilePath);
                        if (fromLib != null)
                        {
                            item = ClonePlaylistItem(fromLib);
                        }
                        else
                        {
                            try { item = CreatePlaylistItemFromPath(e.FilePath); }
                            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                        }
                    }

                    item ??= new PlaylistItem { FilePath = e.FilePath, Title = e.Title };
                    if (!string.IsNullOrWhiteSpace(e.Title)
                        && string.Equals(item.Title, System.IO.Path.GetFileNameWithoutExtension(e.FilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        item.Title = e.Title; // 无内嵌标题时用历史记录的标题
                    }

                    // 第二行显示播放信息："播放于 MM-dd HH:mm - 播放 m:ss · 播完/未播完"
                    DateTime local = e.PlayedAtUtc == DateTime.MinValue
                        ? DateTime.MinValue
                        : e.PlayedAtUtc.ToLocalTime();
                    string timeText = local == DateTime.MinValue ? "—" : local.ToString("MM-dd HH:mm");
                    string durText = e.PlayedSeconds < 1
                        ? "—"
                        : e.PlayedSeconds < 60
                            ? (int)e.PlayedSeconds + " 秒"
                            : TimeSpan.FromSeconds(e.PlayedSeconds).ToString(@"m\:ss");
                    item.Artist = "播放于 " + timeText;
                    item.Album = "播放 " + durText + " · " + (e.Completed ? "播完" : "未播完");
                    items.Add(item);
                }
            }

            RenumberCollection(items);
            PlaylistView.ItemsSource = items;
            LibraryPaneTitle.Text = string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                ? "我喜欢的音乐"
                : "最近播放";
            SetPlaylistEmptyHint(items.Count == 0,
                string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal) ? "暂时没有添加我喜欢的音乐" : "最近没有播放记录");
        }

        /// <summary>歌曲列表空状态提示：空时显示 hint，否则收起。</summary>
        private void SetPlaylistEmptyHint(bool empty, string hint)
        {
            if (PlaylistEmptyHint == null) return;
            PlaylistEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            PlaylistEmptyHint.Text = hint;
        }
        /// <summary>播放最多：按播放次数降序显示媒体库歌曲。</summary>
        private void ApplyMostPlayedCategory()
        {
            LibraryPaneTitle.Visibility = Visibility.Visible;
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

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                int count = TrackStatsStore.Get(track.FilePath)?.PlayCount ?? 0;
                if (count > 0)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            var sorted = col
                .Select(x => new { Item = x, Count = TrackStatsStore.Get(x.FilePath)?.PlayCount ?? 0 })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Item)
                .ToList();

            var result = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem item in sorted)
            {
                result.Add(item);
            }

            RenumberCollection(result);
            PlaylistView.ItemsSource = result;
            LibraryPaneTitle.Text = "播放最多";
        }

        // ---------------- 评分分类（未评分 + 1..5 星） ----------------

        /// <summary>右上角刷新当前页面：按当前分类重载对应数据。</summary>

        /// <summary>设置窗口四角风格（无边框自绘按钮窗口用；ROUND=圆角、DONOTROUND=全屏填满直角）。</summary>
        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                if (_mainWindowHwnd == IntPtr.Zero) return;
                int corner = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttributeInt(_mainWindowHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>无边框 + 自绘按钮的窗口 chrome：保留系统 resize 边框（四边/四角可调大小、四角圆角、最大化到工作区留任务栏），隐藏系统标题栏按钮（caption）。</summary>
        private void MakeWindowBorderless()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p)
                {
                    // hasBorder 保留 resize 边框（原生四边调大小 + DWM 圆角 + 最大化到工作区）；
                    // hasTitleBar=false 去掉系统标题栏/最小化/最大化/关闭按钮，由自绘按钮接管。
                    p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void WindowMinButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void WindowMaxRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p)
                {
                    // 最大化状态与图标由 WM_SIZE（OnWindowMaximizeStateChanged）统一同步，
                    // 这样从最大化拖拽还原后按钮也能正确变回“最大化”。
                    if (_windowMaximized) p.Restore();
                    else p.Maximize();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private async void WindowCloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 自绘关闭按钮不能直接 Close()：WinUI 3 的 Window.Close() 不触发 AppWindow.Closing，
            // 会绕过关闭策略（最小化到托盘/每次询问）导致直接退出。统一走 HandleCloseRequestAsync。
            try { await HandleCloseRequestAsync(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        // ---------------- 音频设置右侧面板（输出模式 / 链路状态 / 专业播放状态） ----------------
        // （2026-10-31 W13 第十六刀：面板域成员已整体转活 MainWindow.AudioOutputHost.cs）

        /// <summary>
        /// 打开「音频设置」右侧面板。
        /// 2026-10-17 起不再由标题栏齿轮按钮直接触发（那个按钮改成音频链路入口），
        /// 改由音频链路面板底部的「打开音频设置」调用（见 MainWindow.AudioChain.cs）。
        /// </summary>
        internal async void OpenAudioSettingsPanel()
        {
            // 半透明亚克力 + 高斯模糊背景（AcrylicBrush），覆盖 XAML 的纯色底
            AudioSettingsPanel.Background = FrostedGlass.CreatePanelBrush();
            AudioSettingsOverlayHost.Visibility = Visibility.Visible;
            AudioSettingsOverlayHost.IsHitTestVisible = true;
            AudioSettingsPanelTransform.TranslateX = 360;
            AudioSettingsOpenStoryboard?.Begin();
            await FillAudioSettingsCombosAsync();
            RefreshAudioSettingsPanel();
        }

        private void AudioSettingsCloseButton_Click(object sender, RoutedEventArgs e) => HideAudioSettingsPanel();

        private void AudioSettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => HideAudioSettingsPanel();

        private void HideAudioSettingsPanel()
        {
            if (AudioSettingsCloseStoryboard != null && AudioSettingsOverlayHost.Visibility == Visibility.Visible)
            {
                AudioSettingsCloseStoryboard.Completed += (_, _) =>
                {
                    AudioSettingsOverlayHost.Visibility = Visibility.Collapsed;
                    AudioSettingsOverlayHost.IsHitTestVisible = false;
                };
                AudioSettingsCloseStoryboard.Begin();
            }
            else
            {
                AudioSettingsOverlayHost.Visibility = Visibility.Collapsed;
                AudioSettingsOverlayHost.IsHitTestVisible = false;
            }
        }

        private async void AudioModeCard_Click(object sender, RoutedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            if (sender is not Button b || b.Tag is not string mode)
            {
                return;
            }

            await ChangeOutputModeAsync(mode);
        }

        /// <summary>内核自检：内存生成 3 秒 1kHz 正弦测试音（48k/16bit/立体声），走完整播放链路试听，
        /// 不进音乐库、不碰曲库索引。用来快速验证当前选中的内核「起播即出声、无爆音、无卡顿」。</summary>
        private void EngineSelfTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string engine = AppSettingsStore.Load().ExclusiveEngine ?? "native2";
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "celeste_selftest_1khz_3s.wav");
                new SineWaveSource().WriteWav(path);
                // scattered:false = 主窗口内临时播放：试听场合不弹零散播放窗口
                PlayExternalFileAsync(path, scattered: false);
                if (EngineSelfTestStatus != null)
                {
                    EngineSelfTestStatus.Text = $"已用「{EngineDisplayName(engine)}」播放 3 秒 1kHz 测试音：注意起播是否立即出声、有没有爆音或卡顿（临时文件 {path}）。";
                }
                StartupLog.Write($"[设置] 内核自检播放测试音 engine={engine} wav={path}");
            }
            catch (Exception ex)
            {
                if (EngineSelfTestStatus != null)
                {
                    EngineSelfTestStatus.Text = "测试音播放失败：" + ex.Message;
                }
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs(EngineSelfTest_Click)", ex);
            }
        }

        // ---------- （2026-10-16 删除）DSD×ECHO 内核提醒卡 ----------
        // 背景：echo 内核不负责 DSD/DoP，用户用 echo 播 DSF 时左上角弹提醒建议切 native2。
        // ECHO 内核选项已按用户要求删除（程序只用 native2），该提醒永不触发，整套逻辑移除：
        // ShowDsdEngineTip / HideDsdEngineTip / DsdEngineTipClose_Click / DsdEngineTipSwitch_Click
        // / _dsdEngineTipTimer / DsdEngineTipCard 及其 XAML。

        // ---------- 页2：AB 循环点（2026-10-16 实现，2026-10-31 整体转活 AudioOutputHost.cs） ----------

        private void AbLoopSetAButton_Click(object sender, RoutedEventArgs e)
            => SetAbLoopStart();

        private void AbLoopSetBButton_Click(object sender, RoutedEventArgs e)
            => SetAbLoopEnd();

        private void AbLoopToggleButton_Click(object sender, RoutedEventArgs e)
            => ToggleAbLoop();

        private void AbLoopClearButton_Click(object sender, RoutedEventArgs e)
            => ClearAbLoop();

        private async void AudioOutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            string? appliedDevice = null;
            if (AudioOutputDeviceCombo.SelectedItem is ComboBoxItem it && it.Tag is string did)
            {
                // 用户手动改选 → 放弃「等设备插回自动切回」的暂存偏好
                appliedDevice = did;
                await ChangeOutputDeviceAsync(did);
            }

            RefreshAudioSettingsPanel();
            // 按设备记忆：切到具体设备时自动套用该设备的 DSP 配置档
            ApplyDeviceDspProfileIfEnabled(appliedDevice);
        }

        private void DeviceDspMemoryToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                bool on = DeviceDspMemoryToggle?.IsOn == true;
                DeviceDspProfileStore.SetEnabled(on);
                // 开启时，若当前设备尚无存档则把当前配置存为它的配置档，立即生效
                string cur = _audioEngine?.OutputDeviceId ?? string.Empty;
                if (on && !string.IsNullOrWhiteSpace(cur) && !DeviceDspProfileStore.HasProfile(cur))
                {
                    DeviceDspProfileStore.SaveProfile(cur, DeviceDspProfileStore.CaptureCurrent());
                }

                RefreshAudioSettingsPanel();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private void SaveDeviceDspButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cur = _audioEngine?.OutputDeviceId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cur))
                {
                    DeviceDspHint.Text = "当前为系统默认设备，无法按设备记忆；请先在「输出设备」里选具体设备。";
                    return;
                }

                DeviceDspProfileStore.SaveProfile(cur, DeviceDspProfileStore.CaptureCurrent());
                DeviceDspHint.Text = "已保存当前配置为设备：" + cur;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private void RefreshCurrentPageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (_currentCategory)
                {
                    case "Albums": _ = RefreshAlbumViewAsync(); break;
                    case "Artists":
                    case "AlbumArtists": _ = RefreshArtistViewAsync(); break;
                    case "Folders": RefreshFolderBrowserRoots(); break;
                    case "Favorites":
                    case "Recent": ApplyFavoritesOrRecentCategory(); break;
                    case "Ratings": ApplyRatingCategory(); break;
                    case "MostPlayed": ApplyMostPlayedCategory(); break;
                    case "Genres":
                    case "Years": _ = RefreshGenreYearViewAsync(); break;
                    case "PlaylistWall": ApplyPlaylistWallCategory(); break;
                    default: ApplyCategoryView(); break;
                }

                NowPlayingText.Text = "已刷新当前页面";
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>刷新评分分类：按 _ratingFilter 过滤媒体库歌曲并填充列表。</summary>
        private void ApplyRatingCategory()
        {
            UpdateRatingFilterHighlight();

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                if (_ratingFilter < 0)
                {
                    // 未点选：显示所有已评分歌曲
                    if (track.Rating > 0) col.Add(ClonePlaylistItem(track));
                }
                else if (track.Rating == _ratingFilter)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            RenumberCollection(col);
            PlaylistView.ItemsSource = col;
            LibraryPaneTitle.Text = "评分";
            SetPlaylistEmptyHint(col.Count == 0, _ratingFilter >= 0
                ? (_ratingFilter == 0 ? "没有未评分的歌曲" : "没有 " + _ratingFilter + " 星评分的歌曲")
                : "还没有给任何歌曲评分");
        }

        private void RatingFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string s && int.TryParse(s, out int rating))
            {
                _ratingFilter = rating;
                ApplyRatingCategory();
            }
        }

        private void UpdateRatingFilterHighlight()
        {
            var accent = ResolveAccentBrush();
            var fg = ColorHelper.ResolveContrastingForeground(accent);
            var idleBg = ResolveCapsuleFillBrush();
            var border = ResolveNavCapsuleBorderBrush();
            (Button Btn, int Val)[] maps =
            {
                (RatingFilter0Button, 0), (RatingFilter1Button, 1), (RatingFilter2Button, 2),
                (RatingFilter3Button, 3), (RatingFilter4Button, 4), (RatingFilter5Button, 5)
            };
            foreach (var (btn, val) in maps)
            {
                bool active = val == _ratingFilter;
                if (active) { btn.Background = accent; btn.Foreground = fg; btn.BorderThickness = new Thickness(0); }
                else { btn.Background = idleBg; btn.ClearValue(Control.ForegroundProperty); btn.BorderThickness = new Thickness(1); btn.BorderBrush = border; }
            }
        }


        private void AppendPlaylistContextFeatureItems(MenuFlyout flyout, PlaylistItem song, bool inUserPlaylist)
        {
            var playNext = new MenuFlyoutItem { Text = "下一首播放" };
            playNext.Icon = new FontIcon { Glyph = "\uE893" };
            playNext.Click += (_, _) => PlaySongsNext(new[] { song });
            flyout.Items.Insert(1, playNext);

            bool isFav = TrackStatsStore.Get(song.FilePath)?.IsFavorite ?? false;
            var fav = new MenuFlyoutItem { Text = isFav ? "取消喜欢" : "添加到我喜欢的音乐" };
            fav.Icon = new FontIcon { Glyph = isFav ? "\uEB52" : "\uEB51" };
            fav.Click += (_, _) =>
            {
                TrackStatsStore.ToggleFavorite(song.FilePath);
                NamedPlaylistStore.SyncFavoritesPlaylist();
                UpdateFavoriteButtonUi();
                if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
                {
                    ApplyCategoryView();
                }
            };
            flyout.Items.Add(fav);

            var ratingFlyout = new MenuFlyoutSubItem { Text = "评分" };
            for (int r = 0; r <= 5; r++)
            {
                int rating = r;
                var item = new MenuFlyoutItem { Text = rating == 0 ? "未评分" : new string('★', rating) };
                item.Click += (_, _) => TrackStatsStore.SetRating(song.FilePath, rating);
                ratingFlyout.Items.Add(item);
            }

            flyout.Items.Add(ratingFlyout);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var tag = new MenuFlyoutItem { Text = "标签编辑" };
            tag.Click += (_, _) => OpenTagEditForFiles(new[] { song.FilePath });
            flyout.Items.Add(tag);

            // 「编辑歌词… / 下载歌词」已按用户要求移除：歌词功能统一收敛到标签板块。

            // 「下载封面」已按用户要求移除（2026-10-19）：单曲右键菜单里不再给这个入口。
            // （批量菜单里的「批量下载封面」是另一处，用户没提，保留。）

            // 「在线搜索…」入口已下架（与 FeaturesMoreButton_Click 同步）。
        }


        private async Task AddFilesToLibraryAsync(IEnumerable<string> paths)
        {
            // 复用现有导入逻辑：走批量添加入口
            List<string> list = paths.Where(System.IO.File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
            {
                return;
            }

            int before = _playlist.Count;
            foreach (string path in list)
            {
                if (FindLibraryIndex(path) >= 0)
                {
                    continue;
                }

                try
                {
                    // CUE 分轨展开：有同名 .cue 的文件按轨加入（设置关闭时原样返回单条）
                    foreach (PlaylistItem track in CueTrackExpansion.Expand(new List<PlaylistItem> { CreatePlaylistItemFromPath(path) }))
                    {
                        _playlist.Add(track);
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }

            if (_playlist.Count != before)
            {
                RenumberCollection(_playlist);
                // Distinct：CUE 分轨展开后同一文件对应多条，会话里按文件去重（恢复时再展开）
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        // ---- 媒体库自动重扫监听（第十七刀暂退回：RescanLocalLibraryButton_Click 连着 Library.cs 深水区，待媒体库域刀一起转活）----

        private void RestartLibraryWatchFromSettings()
        {
            _libraryWatch.Changed -= LibraryWatch_Changed;
            _libraryWatch.Stop();

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.AutoUpdateLibrary)
            {
                return;
            }

            var folders = new List<string>();
            if (settings.LibraryWatchFolders != null)
            {
                folders.AddRange(settings.LibraryWatchFolders.Where(Directory.Exists));
            }

            try
            {
                string? sessionFolder = LibrarySessionStore.TryLoad()?.FolderPath;
                if (!string.IsNullOrWhiteSpace(sessionFolder)
                    && Directory.Exists(sessionFolder)
                    && !LibraryPathGuard.IsInternalCacheDir(sessionFolder)
                    && !folders.Any(f => string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    folders.Add(sessionFolder);
                    AppSettingsStore.Update(s =>
                    {
                        s.LibraryWatchFolders ??= new List<string>();
                        if (!s.LibraryWatchFolders.Any(f =>
                                string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                        {
                            s.LibraryWatchFolders.Add(sessionFolder);
                        }
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            if (folders.Count == 0)
            {
                return;
            }

            _libraryWatch.Changed += LibraryWatch_Changed;
            _libraryWatch.Start(folders);
        }

        private void LibraryWatch_Changed(IReadOnlyList<string> paths)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (paths.Count == 0)
                {
                    return;
                }

                if (!AppSettingsStore.Load().AutoUpdateLibrary)
                {
                    return;
                }

                // 去抖合并:1 秒内的连续变更只触发一次重扫
                _libraryWatchDebounce ??= DispatcherQueue.CreateTimer();
                _libraryWatchDebounce.Interval = TimeSpan.FromMilliseconds(1000);
                _libraryWatchDebounce.IsRepeating = false;
                _libraryWatchDebounce.Tick -= OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Tick += OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Start();
            });
        }

        private void OnLibraryWatchDebounceTick(dynamic sender, dynamic e)
        {
            if (_libraryRescanInProgress)
            {
                // 上一次重扫还没完成:稍后重试
                _libraryWatchDebounce?.Start();
                return;
            }

            NowPlayingText.Text = "媒体库有更新，正在自动刷新…";
            RescanLocalLibraryButton_Click(null!, new RoutedEventArgs());
        }

        // ---- 标签保存后从磁盘刷新（第二十刀暂退回：连着 ApplyCategoryView 分类大域，待分类域刀一起转活）----

        private void OnTagsSaved(string path)
        {
            DispatcherQueue.TryEnqueue(() => RefreshTrackMetadataFromDisk(path));
        }

        private void RefreshTrackMetadataFromDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                PlaylistItem fresh = CreatePlaylistItemFromPath(path);
                ReplaceMetadataInCollection(_playlist, fresh);
                ReplaceMetadataInCollection(_queue.Items, fresh);
                if (string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateNowPlayingPanelAsync(fresh);
                }

                if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Recent", StringComparison.Ordinal))
                {
                    ApplyCategoryView();
                }

                NotifyCurrentPlaylistWindow();
            }
            catch (Exception ex)
            {
                _ = ShowErrorAsync("刷新标签失败", ex.Message);
            }
        }

        private static void ReplaceMetadataInCollection(
            System.Collections.ObjectModel.ObservableCollection<PlaylistItem> list,
            PlaylistItem fresh)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].FilePath, fresh.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PlaylistItem old = list[i];
                old.Title = fresh.Title;
                old.Artist = fresh.Artist;
                old.AlbumArtist = fresh.AlbumArtist;
                old.Album = fresh.Album;
                old.Track = fresh.Track;
                old.Year = fresh.Year;
                old.Duration = fresh.Duration;
                old.Genre = fresh.Genre;
            }
        }
    }
}
