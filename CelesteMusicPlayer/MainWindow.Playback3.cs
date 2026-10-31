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

        private void RefreshRealizedSongListSelectionChrome(ListView list)
        {
            HashSet<object>? selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(list);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(list))
            {
                if (list.ItemFromContainer(container) is PlaylistItem song)
                {
                    ApplySongListItemSelectionChrome(list, container, song, selectedSet);
                }
            }
        }


        private void ApplySongListItemSelectionChrome(
            ListView list,
            ListViewItem container,
            PlaylistItem song,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ColorHelper.ResolveContrastingForeground(accent);
            bool multiOnThisList = _isMultiSelectMode && ReferenceEquals(_multiSelectTargetList, list);
            Brush unselectedBg = multiOnThisList
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            // 容器本身保持透明，避免 Presenter 方角选中层
            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisList
                ? VisualTreeWalker.IsItemSelected(list, song, selectedSet)
                : ReferenceEquals(list.SelectedItem, song);

            Border? chrome = VisualTreeWalker.FindTaggedBorder(container, "SongRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 40;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                // 让行内容横向铺满列表宽度（选中矩形也因此铺满整行、字段对齐表头）。
                // 用显式宽度而非仅靠 HorizontalContentAlignment，确保在 ScrollViewer 布局下也生效。
                if (list.ActualWidth > 0)
                {
                    chrome.Width = list.ActualWidth;
                }

                if (IsOpaqueUiStyleActive())
                {
                    // 经典 / 极客界面选中样式：左侧常驻 3px 边框（未选中时透明，避免选中瞬间内容横移），
                    // 选中时左边框变主题色竖条 + 底色为半透明灰（随明暗主题切换）；文字保持原色。
                    // 整行填强调色的款在极客近黑底上过于突兀（用户实测反馈），与左侧导航保持一致。
                    // 专辑封面等方形元素走 AlbumRowChrome，不经此分支，不受影响。
                    chrome.BorderThickness = new Thickness(3, 0, 0, 0);
                    bool darkRow = container.ActualTheme == ElementTheme.Dark;
                    // 条样式底色是「半透明灰」不是强调色，文字必须回落到主题前景色。
                    // 注意：ListViewItem 默认模板的 Selected 视觉状态是用 ObjectAnimation 把
                    // ContentPresenter.Foreground 动画成白色（动画优先级高于任何本地值），
                    // 所以光设置 container.Foreground 压不住 —— 必须给行内每个 TextBlock
                    // 显式设值（ApplyForegroundToDescendants），显式值优先于继承。
                    // 令牌测试皮肤：行文字=令牌 Text.Primary——选中底换成令牌色后不一定是深底，
                    // 不能沿用经典"深色主题白字/浅色主题黑字"的写法。取色失败回落经典灰阶。
                    var rowForeground = new SolidColorBrush(darkRow
                        ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                        : Windows.UI.Color.FromArgb(255, 0, 0, 0));
                    container.Foreground = rowForeground;
                    if (selected)
                    {
                        // 选中底色走统一入口（令牌皮肤=Selection.Background，经典/极客=原半透明灰）
                        chrome.Background = new SolidColorBrush(ResolveClassicRowSelectionColor(container));
                        chrome.BorderBrush = accent;
                        ApplyForegroundToDescendants(chrome, rowForeground);
                    }
                    else
                    {
                        chrome.Background = unselectedBg;
                        chrome.BorderBrush = new SolidColorBrush(Colors.Transparent);
                        ClearForegroundOnDescendants(chrome);
                    }
                }
                else if (selected)
                {
                    chrome.BorderThickness = new Thickness(0);
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.BorderThickness = new Thickness(0);
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                // 兜底：无模板 Border 时仍尽量圆角
                if (IsOpaqueUiStyleActive())
                {
                    bool darkRow = container.ActualTheme == ElementTheme.Dark;
                    // 选中底色走统一入口（令牌皮肤=Selection.Background，经典/极客=原半透明灰）
                    container.Background = new SolidColorBrush(ResolveClassicRowSelectionColor(container));
                    container.BorderThickness = new Thickness(3, 0, 0, 0);
                    container.BorderBrush = accent;
                }
                else
                {
                    container.Background = accent;
                }

                container.Foreground = selectedFg;
                ApplyForegroundToDescendants(container, selectedFg);
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
                ClearForegroundOnDescendants(container);
            }
        }



        // =====================================================================
        // 底部控制按钮
        // =====================================================================

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            QuickPlayerCoordinator.PauseForMain();
            if (_audioEngine?.IsPlaying == true)
            {
                _audioEngine.Pause();
                _isEnginePaused = true;
                UpdateWaveformTimerForPlaybackState(false);
                MediaSession.SetStatus(MediaPlaybackStatus.Paused);

                // 任务栏缩略图按钮：暂停 → 显示"播放"图标（提示用户点播放）
                _taskbarButtons?.UpdatePlayPause(false);

                return;
            }

            if (_isEnginePaused && _audioEngine != null)
            {
                _audioEngine.Resume();
                // 暂停-恢复本质是一次 seek 重建会话，无缝源 _next 已被清空；
                // 重新预加载下一首，避免恢复后播到尾因无续接而误切歌。
                _ = Session.PreloadNextAsync();

                _isEnginePaused = false;
                UpdateWaveformTimerForPlaybackState(true);
                MediaSession.SetStatus(MediaPlaybackStatus.Playing);

                // 任务栏缩略图按钮：播放中 → 显示"暂停"图标（提示用户点暂停）
                _taskbarButtons?.UpdatePlayPause(true);

                // 独占下音量完全由 Windows 托盘/DAC 物理键控制，程序不写设备主音量(避免多次 select/暂停音量跳变到 0/100)；
                // 仅共享模式在恢复时做软件增益淡入。
                if (!IsHiFiModeSelected())
                {
                    double resumeTarget = VolumeSlider.Value / 100.0;
                    _ = FadeInEngineAfterResumeAsync(resumeTarget);
                }

                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            if (player.Source == null)
            {
                // 启动续播后就绪但未播（Source 为空）时，恢复的是 _queue.Index 指向的那首，
                // 不是队列第 0 首 —— 以前靠"预加载进 MediaPlayer"兜出 wasPlaying 判断，
                // 预加载已删（会弹 SourceNotSupported），这里必须显式按恢复索引起播。
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    PlayUserPlaylistAt(_queue.Index);
                }
                else if (_queue.Items.Count > 0)
                {
                    PlayUserPlaylistAt(0);
                }
                else if (_playlist.Count > 0)
                {
                    PlayAtIndex(0);
                }

                return;
            }

            bool wasPlaying = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            if (wasPlaying)
            {
                player.Pause();
                MediaSession.SetStatus(MediaPlaybackStatus.Paused);
                _taskbarButtons?.UpdatePlayPause(false);   // 暂停后 → 显示"播放"图标
            }
            else
            {
                // 启动续播预加载的就绪态（MediaPlayer 有 Source 但未走引擎）：点播放时应转走
                // 引擎路径，与双击播放一致 —— 否则走 player.Play() 是 MediaPlayer 路径，
                // 没有 DSP 链 → 实时电平表不显示（LevelMeterChannels 恒 0），SMTC 进度也不对。
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    PlayUserPlaylistAt(_queue.Index);
                    return;
                }

                player.Play();
                MediaSession.SetStatus(MediaPlaybackStatus.Playing);
                _taskbarButtons?.UpdatePlayPause(true);    // 播放后 → 显示"暂停"图标
            }
        }


        private void PreviousButton_Click(object sender, RoutedEventArgs e)
            => PlayPrevious();

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => PlayNext();

        // =====================================================================
        // 进度条 / 音量
        // =====================================================================

        private void ProgressSlider_PointerPressed(dynamic sender, dynamic e)
        {
            _isUserSeeking = true;
            _seekGestureConsumed = false;
        }


        private void ProgressSlider_PointerReleased(dynamic sender, dynamic e)
        {
            EndSeekGesture();
        }


        private void ProgressSlider_PointerCaptureLost(dynamic sender, dynamic e)
        {
            EndSeekGesture();
        }


        /// <summary>
        /// 一次拖动只跳转一次：松手时 PointerReleased / PointerCaptureLost 会先后触发，
        /// 两次都 seek 会卡顿并重复预加载下一首，这里用 _seekGestureConsumed 去重。
        /// </summary>
        private void EndSeekGesture()
        {
            if (!_isUserSeeking || _seekGestureConsumed)
            {
                _isUserSeeking = false;
                return;
            }

            _seekGestureConsumed = true;
            try
            {
            }
            finally
            {
                _isUserSeeking = false;
            }
        }


        private void VolumeSlider_ValueChanged(dynamic sender, dynamic e)
        {

            // 共享模式：MediaPlayer 数字音量（跟随用户）。
            MediaPlayer? player = GetPlayer();

            // 设备/引擎音量：共享=数字增益随滑块；独占/ASIO 默认软件音量条固定 100%（实际音量由 DAC 硬件旋钮/
            // 系统端点音量控制，bit-perfect 保真）；设置页开启「HiFi 软件音量」后，独占/ASIO 下滑条改走
            // DSP 采样级衰减（音量≠100% 即破坏 bit-perfect，界面徽标会提示"音量"）。
            bool softVol = IsHiFiSoftVolumeUiUnlocked();
            if (!IsHiFiModeSelected() || softVol)
            {
                _audioEngine?.SetVolume(e.NewValue / 100.0);
            }

            if (!_applyingSettingsVolume && (!IsHiFiModeSelected() || softVol) && _volumeStartupApplied)
            {
                // 记录用户最后一次主动设定的音量（跨入口唯一真值源）
                LastUserVolume = Math.Clamp(e.NewValue, 0, 100);
                ScheduleVolumeSave(e.NewValue);
            }

        }


        /// <summary>音量写盘去抖:停止拖动 300ms 后才保存一次,避免每 tick 全量写盘。</summary>
        private void ScheduleVolumeSave(double value)
        {
            _volumeToSave = value;
            _volumeSaveTimer ??= DispatcherQueue.CreateTimer();
            _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _volumeSaveTimer.IsRepeating = false;
            _volumeSaveTimer.Tick -= OnVolumeSaveTick;
            _volumeSaveTimer.Tick += OnVolumeSaveTick;
            _volumeSaveTimer.Start();
        }


        private void OnVolumeSaveTick(dynamic sender, dynamic e)
        {
            try
            {
                double v = Math.Clamp(LastUserVolume >= 0 ? LastUserVolume : _volumeToSave, 0, 100);
                AppSettingsStore.Update(s => s.Volume = v);
                global::CelesteMusicPlayer.StartupLog.Write($"[音量] 去抖写盘 = {v:0.##}");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void DesktopLyricsButton_Click(object sender, RoutedEventArgs e)
            => SetDesktopLyricsEnabled(!_desktopLyricsEnabled);

        // 迷你播放器相关方法（MiniPlayerButton_Click / SetMiniPlayerEnabled /
        // EnsureMiniPlayerWindow / OnMiniPlayerClosedByUser / UpdateMiniPlayerBadge）
        // 2026-10-19 全部移除：用户对现在的迷你播放器很不满意，删掉重做，
        // 重建方案见 outputs/迷你播放器重建方案-2026-10-19.md。
        // 主窗口这侧的入口按钮（MiniPlayerButton）也一并从 MainWindow.xaml 里去掉了。


        // ---- Mini player / desktop lyrics helpers ----

        internal PlaylistItem? GetCurrentPlayingItem()
        {
            PlaylistItem? indexed = null;
            if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
            {
                indexed = _queue.Items[_queue.Index];
            }
            else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                indexed = _playlist[_currentIndex];
            }

            // 「当前在播哪首」只有一个权威答案：StartPlayback 那一刻记下来的这首。
            // 队列下标和曲库下标是两套独立事实（自动续播走队列、手动点歌可能走曲库），
            // 谁"看起来有效"谁就赢会让网页停在上一首 —— 手动切歌正常、自动切歌卡住就是这个原因。
            PlaylistItem? playing = _currentPlayingItem;
            if (playing != null)
            {
                if (indexed != null && !string.Equals(indexed.FilePath, playing.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    string signature = playing.FilePath + "|" + indexed.FilePath;
                    if (!string.Equals(signature, _stateMismatchLogged, StringComparison.Ordinal))
                    {
                        _stateMismatchLogged = signature;
                        StartupLog.Write("[状态] 下标与正在播放不一致，按正在播放为准：下标曲目=" + indexed.Title
                            + " 实际在播=" + playing.Title + " 队列下标=" + _queue.Index + " 曲库下标=" + _currentIndex);
                    }
                }
                return playing;
            }

            return indexed;
        }

        // ⚠ _currentPlayingItem 已是属性（MainWindow.PlaybackHost.cs）：存储在 PlaybackSession.CurrentItem，
        //   StartPlayback 钉下、无缝换轨由会话推进。别在这里再声明同名字段。

        /// <summary>下标与实际播放不一致的日志签名，避免每秒刷屏。</summary>
        private string _stateMismatchLogged = "";


        internal ImageSource? GetCurrentCoverImage() => _lastCoverImage;

        /// <summary>
        /// 当前曲目的封面**缩略图**（Thumb 160 档，播放栏同款同一张）。
        /// 迷你播放器 72×72 小封面用这个 —— 按显示尺寸分档取图，不拿大图硬缩
        /// （见 <see cref="_lastCoverThumbImage"/> 注释：768→72 十倍缩小必糊）。
        /// 取图跟随播放栏那次异步取图，刚切歌瞬间可能还是上一首的（与播放栏
        /// 「保持旧图不眨眼」同策略），迷你窗下一个 tick（≤200ms）就收到新的。
        /// </summary>
        internal ImageSource? GetCurrentCoverThumbImage() => _lastCoverThumbImage;

        /// <summary>
        /// 当前曲目的封面**原始字节**（迷你播放器取色用）。
        /// 取色必须拿字节 —— BitmapImage 解码完不给像素，从 ImageSource 反推不出主色。
        /// 空则回落到按路径现抽一次（例如还没解析完就被问到）。
        /// </summary>
        internal byte[]? GetCurrentCoverBytes()
        {
            if (_lastCoverBytes is { Length: > 0 })
            {
                return _lastCoverBytes;
            }

            return string.IsNullOrEmpty(_nowPlayingPath) ? null : ExtractCoverBytes(_nowPlayingPath);
        }

        internal MediaPlayer? GetMediaPlayerPublic() => GetPlayer();

        internal string GetPlaybackOrderGlyphPublic()
            => _orderResolver.Order switch
            {
                PlaybackOrder.Sequential => "\uE8FD",
                PlaybackOrder.Random => "\uE8B1",
                PlaybackOrder.ListLoop => "\uE8EE",
                PlaybackOrder.TrackLoop => "\uE8ED",
                PlaybackOrder.TrackOnce => "\uE72A",
                _ => "\uE8EE"
            };


        internal void CyclePlaybackOrderPublic() => PlaybackOrderButton_Click(this, new RoutedEventArgs());

        internal void PreviousPublic() => PlayPrevious();

        internal void NextPublic() => PlayNext();

        internal void TogglePlayPausePublic() => PlayPauseButton_Click(this, new RoutedEventArgs());

        /// <summary>把当前播放曲目加入/移出"我喜欢的音乐"（托盘/菜单/任务栏缩略图复用）。
        /// 点击行为：未收藏 → 加入；已收藏 → 移除。托盘和任务栏共用同一份逻辑。</summary>
        internal void FavoriteCurrentPublic()
        {
            string? path = GetCurrentPlayingItem()?.PersistenceKey ?? _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    path = _queue.Items[_queue.Index].PersistenceKey;
                }
                else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    path = _playlist[_currentIndex].PersistenceKey;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                StartupLog.Write("[托盘] 收藏当前：无正在播放曲目");
                return;
            }

            bool fav = TrackStatsStore.ToggleFavorite(path);
            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryViewData();
            }


            // 任务栏缩略图按钮：fav=true 实心红心 / fav=false 空心轮廓心
            _taskbarButtons?.UpdateFavorite(fav);

            NotifyWebHomeChanged();
            StartupLog.Write("[托盘] " + (fav ? "已收藏 " : "已取消收藏 ") + path);
        }


        /// <summary>迷你播放器等外部入口：按"原歌曲时间"跳转（自动处理变速倍率换算）。</summary>
        internal void SeekSourceSeconds(double seconds)
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                try
                {
                    // 进度条上是源时间，引擎吃 WAV 时间
                    _audioEngine.Seek(TimeSpan.FromSeconds(seconds / TempoScale()));

                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                return;
            }

            SeekPublic(TimeSpan.FromSeconds(seconds));
        }

        internal void SeekPublic(TimeSpan position)
        {
            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                player.PlaybackSession.Position = position;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        // =====================================================================
        // 迷你播放器专用公开接口
        // 迷你播放器是独立窗口（不同类），拿不到 MainWindow 的私有成员，
        // 所以按项目既有的 "...Public" 约定在这里开几个最小包装。
        // =====================================================================

        /// <summary>当前音量（0–100）。迷你播放器音量条读它。</summary>
        internal double GetVolumePublic() => VolumeSlider?.Value ?? 100;

        /// <summary>
        /// 设置音量（0–100）。刻意走主窗口同一个 VolumeSlider，而不是直接改 MediaPlayer.Volume，
        /// 这样音量图标切换、写盘持久化、音频引擎侧同步全部复用主界面已有那套逻辑，不会分叉。
        /// </summary>
        internal void SetVolumePublic(double value0To100)
        {

            VolumeSlider.Value = Math.Clamp(value0To100, 0, VolumeSlider.Maximum);
        }

        /// <summary>当前播放队列快照。迷你播放器队列面板用。</summary>
        internal IReadOnlyList<PlaylistItem> GetUserPlaylistPublic() => _queue.Items;

        /// <summary>队列中正在播放那首的下标（-1 = 没有）。用于给队列面板标出当前项。</summary>
        internal int GetUserPlaylistIndexPublic() => _queue.Index;

        /// <summary>当前曲目是否已收藏。迷你播放器心形按钮用它显示空心/实心。</summary>
        internal bool IsCurrentFavoritePublic()
        {
            string? path = GetCurrentPlayingItem()?.PersistenceKey ?? _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    path = _queue.Items[_queue.Index].PersistenceKey;
                }
                else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    path = _playlist[_currentIndex].PersistenceKey;
                }
            }

            // TrackStatsStore 没有 IsFavorite 查询方法，只有 Get() 取整条记录
            return !string.IsNullOrWhiteSpace(path)
                && (TrackStatsStore.Get(path)?.IsFavorite ?? false);
        }


        private void PersistDesktopLyricPosition()
        {
            try
            {
                var w = _desktopLyricsWindow;
                if (w == null || !w.IsVisible)
                {
                    return;
                }

                var (px, py) = w.CurrentPosition;
                AppSettingsStore.Update(s =>
                {
                    s.DesktopLyricPosX = px;
                    s.DesktopLyricPosY = py;
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        // =====================================================================
        // MediaPlayer 事件
        // =====================================================================

        private void Player_MediaOpened(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TimeSpan duration = sender.PlaybackSession.NaturalDuration;

                if ((duration.TotalSeconds <= 0 || double.IsNaN(duration.TotalSeconds))
                    && _currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    duration = _playlist[_currentIndex].Duration;
                }

                _isUpdatingProgressUi = true;
                try
                {
                    double totalSeconds = duration.TotalSeconds;
                    if (totalSeconds <= 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
                    {
                    }
                    else
                    {
                    }

                    double start = 0;
                }
                finally
                {
                    _isUpdatingProgressUi = false;
                }
            });
        }


        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // E768=播放，E769=暂停
                bool playing = sender.PlaybackState == MediaPlaybackState.Playing;
                UpdateWaveformTimerForPlaybackState(playing);
                _desktopLyricsWindow?.SetPlaybackPaused(!playing && sender.PlaybackState != MediaPlaybackState.Opening);
            });
        }


        private void UpdateWaveformTimerForPlaybackState(bool playing)
        {
            if (_waveformTimer == null)
            {
                return;
            }

            if (playing)
            {
                _waveformIdleSettleTicks = 0;
                if (!_waveformTimer.IsRunning)
                {
                    _waveformTimer.Start();
                }
            }
            // 暂停/停止：若定时器仍在跑，由 Tick 做回落动画后自行 Stop
        }


        private void Player_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(HandleMediaEnded);
        }


        private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (AppSettingsStore.Load().StopWhenError)
                {
                    try
                    {
                        sender.Pause();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                // 本次运行里用户（或自动续播）从未真正开始过播放 → 这是"陈旧的源"留下的失败
                // （典型场景：启动恢复上次曲目时，某个早就该丢弃的 MediaPlayer 源自己失败）。
                // 这时候弹「无法播放」纯粹是误报：既没在播，也没人点播放。只记日志、不弹窗。
                if (!_anyPlaybackStarted)
                {
                    global::CelesteMusicPlayer.StartupLog.Write(
                        "Player_MediaFailed(未开播，忽略弹窗): 错误类型=" + args.Error + " 说明="
                        + (string.IsNullOrWhiteSpace(args.ErrorMessage) ? "(空)" : args.ErrorMessage)
                        + " 当前源=" + (_nowPlayingPath ?? "(无)"));
                    global::CelesteMusicPlayer.StartupLog.Flush();
                    return;
                }


                // args.ErrorMessage 在某些场景（如设备独占失败、续播恢复时源已释放）下为空，
                // 仅靠它无法定位根因。补上 Error / HResult / ExtendedErrorCode / 当前源，
                // 并同时写日志，方便用户把详细信息发回来定位。
                string msg = args.ErrorMessage;
                if (string.IsNullOrWhiteSpace(msg))
                {
                    msg = "(播放器未给出错误说明)";
                }
                string detail = $"错误类型：{args.Error}\r\n" +
                                $"说明：{msg}\r\n" +
                                $"当前源：{_nowPlayingPath ?? "(无)"}";
                global::CelesteMusicPlayer.StartupLog.Write("Player_MediaFailed: " + detail);
                global::CelesteMusicPlayer.StartupLog.Flush();
                await ShowErrorAsync("无法播放", detail);
            });
        }


        private void UpdateTaskbarProgress(TimeSpan position)
        {
            if (!AppSettingsStore.Load().ShowTaskbarProgress)
            {
                _taskbarProgress?.Clear();
                return;
            }

            if (_mainWindowHwnd == IntPtr.Zero)
            {
                return;
            }

            _taskbarProgress ??= new TaskbarProgressHelper(_mainWindowHwnd);
            MediaPlayer? player = GetPlayer();
            bool paused = player?.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
            double totalSeconds = GetCurrentPlayingItem()?.Duration.TotalSeconds ?? 0;
            if (totalSeconds <= 0)
            {
                _taskbarProgress.Clear();
                return;
            }

            _taskbarProgress.SetProgress(position.TotalSeconds, totalSeconds, paused);
        }


        private void PositionTimer_Tick(dynamic sender, dynamic e)
        {
            // 播放/暂停按钮的图标同步必须放在所有早退之前：
            // 引擎播放、无源、用户拖动进度条这几种情况下定时器都会提前 return，
            // 但按钮状态恰恰在这些时候最容易跟真实状态脱节（"歌在响、按钮还是 ▶"）。
            SyncTransportPlayState();

            // 迷你播放器状态推送（有窗口在听才推；也要放在早退之前 —— 引擎播放时
            // 这个 tick 会提前 return，而迷你条照样要显示进度）
            MiniPlayerBridge.PushTick();

            if (_usingEnginePlayback)
            {
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null || _isUserSeeking)
            {
                return;
            }

            TimeSpan position = player.PlaybackSession.Position;
            UpdateTaskbarProgress(position);

            SyncLyricsToPosition(position);
            _desktopLyricsWindow?.Sync(position);
            Journal.SampleListenSeconds();

            if ((DateTime.UtcNow - _lastPlaybackPersistUtc).TotalSeconds >= 4)
            {
                _lastPlaybackPersistUtc = DateTime.UtcNow;
                PersistPlaybackSession();
            }
        }

        /// <summary>
        /// Margin 变化是否超过"布局还在动"的阈值（2px）。
        /// 2026-10-16 探针实测：启动补校存在 ±1px 自激极限环（补量帧与 0.5px 写入阈值
        /// 错位，P 在三个相邻整像素间翻、B 在相差 0.5px 的两个值间翻），
        /// 精确比较会让"稳定"永远达不到、定时器永远不停。2px 以下视为已停。
        /// </summary>
        private static bool MarginDrift(Microsoft.UI.Xaml.Thickness a, Microsoft.UI.Xaml.Thickness b)
            => Math.Abs(a.Left - b.Left) >= 2
            || Math.Abs(a.Top - b.Top) >= 2
            || Math.Abs(a.Right - b.Right) >= 2
            || Math.Abs(a.Bottom - b.Bottom) >= 2;

        /// <summary>按钮组相对播放栏中轴的偏移量（px）：0 = 正好落在窗口左右中轴上。</summary>
        private const double TransportButtonsShiftX = 0;

        /// <summary>进度条/按钮组相对垂直居中的下移量（px）：离播放栏上沿留出呼吸。</summary>
        private const double TransportProgressShiftY = 8;

        /// <summary>
        /// 启动期补校几次进度条位置（2026-10-16：修"刚启动没对齐、拖一下窗口才对齐"）。
        ///
        /// ⚠️ 历史三版（都别再改回去）：
        /// ① 挂 LayoutUpdated 每次布局都校 → 进度条和按钮**持续快速抖动**
        ///    （改 Margin → 触发新布局 → 再校，来回颤），同步改还会直接触发
        ///    LayoutCycleException（冒烟抓到过）。
        /// ② 启动后 200/400/600/800ms 各校一次 → **用户 2026-10-16 傍晚复现仍然错位**：
        ///    实测启动时序里主题字典在 FirstActivated 后约 2.5s 才挂载、媒体库约 4s 才
        ///    加载完，布局真正落定远比 800ms 晚，四次补校量到的全是过渡值，且之后没有
        ///    事件再触发重算，于是一直错到用户拖窗口。
        ///
        /// 现在这版：DispatcherQueueTimer 每 150ms 校一次，**连续 10 次漂移不足 2px
        /// （≈1.5s 稳定）才停**，最长 67 tick（≈10s）兜底。定时器间隔远大于布局帧、
        /// 稳定即停，不会抖也不会 LayoutCycle；晚到的主题/曲库/元数据造成的布局变化会被
        /// 后续 Tick 继续盖住，直到真的稳了才收手。运行期仍然只靠 SizeChanged。
        ///
        /// ⚠️ 2026-10-16 探针实测的两个坑（改这函数前必读）：
        /// ① "有没有动"必须用 2px 阈值，不能用精确比较：补校自身有 ±1px 自激极限环
        ///    （CenterOnElement 的 0.5px 写入阈值与 UpdateTransportProgressCentering 里
        ///    "排一帧补量"错位，P 在 -325/-326/-327 间翻、B 在 -5/-5.5 间翻），
        ///    精确比较永远判"动"→ 稳定永远达不到（第一版定时器跑了 195+ tick 不停）。
        /// ② 兜底上限必须无条件检查：曾把 Stop 写在 if(changed) 的 else 里，
        ///    changed 恒真时永远走不到，定时器跑一辈子。
        /// </summary>
        private DispatcherQueueTimer? _startupCenteringTimer;
        private int _startupCenteringStable;
        private int _startupCenteringTicks;

        private void TransportInfoPanel_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
        }


        private static Color Lighten(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

        private static Color Darken(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

        /// <summary>渐变光晕：渐变填充 + 圆角轨道 + 光晕滑块。</summary>
        private void DrawGradientStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            Color light = Lighten(accent, 0.55);
            Color dark = Darken(accent, 0.35);

            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 4) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle { Width = Math.Max(2, w * ratio), Height = 4, RadiusX = 2, RadiusY = 2 };
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                grad.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = light, Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 8, Math.Max(8, w - 8));
                var glow = new Shapes.Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(glow, cx - 8);
                Canvas.SetTop(glow, (h - 16) / 2);
                canvas.Children.Add(glow);

                var dot = new Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Stroke = new SolidColorBrush(dark),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 5);
                Canvas.SetTop(dot, (h - 10) / 2);
                canvas.Children.Add(dot);
            }
        }


        /// <summary>Spotify 圆环：细轨道 + 白色圆环滑块。</summary>
        private void DrawSpotifyStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 3) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 3,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 3) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 6, Math.Max(6, w - 6));
                var ring = new Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(ring, cx - 6);
                Canvas.SetTop(ring, (h - 12) / 2);
                canvas.Children.Add(ring);
            }
        }


        /// <summary>Apple 细线：2px 细线 + 圆点滑块。</summary>
        private void DrawAppleLineStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 2) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 2) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 4, Math.Max(4, w - 4));
                var dot = new Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 4);
                Canvas.SetTop(dot, (h - 8) / 2);
                canvas.Children.Add(dot);
            }
        }


        /// <summary>播放列表内容变化时,若未播放则尝试预览波形(列表恢复完成后自动触发)。</summary>
        private void OnPlaylistForWaveformPreview(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            TryLoadWaveformPreview();
        }


        /// <summary>未播放时加载列表选中(或第一首)歌曲的波形预览。</summary>
        private void TryLoadWaveformPreview()
        {
            if (!_waveformProgress || !string.IsNullOrEmpty(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = _playlist.Count > 0 ? _playlist[0] : null;

            if (item != null
                && !string.Equals(_waveformPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadWaveformForCurrentAsync(item.FilePath);
            }
        }


        /// <summary>延迟重试:等媒体库异步恢复完成后再尝试加载预览波形。</summary>
        private async System.Threading.Tasks.Task RetryWaveformPreviewLaterAsync()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1200);
                TryLoadWaveformPreview();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>将当前播放队列保存为命名播放列表（Poweramp 式持久化，不影响当前队列）。</summary>
        internal void SaveCurrentQueueAsPlaylist(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                List<string> paths = _queue.Items
                    .Select(p => p.PersistenceKey)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                NamedPlaylistStore.SaveSongs(name.Trim(), paths);
                StartupLog.Write("队列已保存为播放列表: " + name.Trim() + " (" + paths.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SaveCurrentQueueAsPlaylist", ex);
            }
        }


        /// <summary>把命名单歌曲追加到当前播放队列（不播放、不替换）。</summary>
        internal void AddNamedPlaylistToQueue(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path) && SourceFromReference(path) == null) continue;
                    try { items.Add(CreatePlaylistItemFromPath(path)); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }
                if (items.Count == 0) return;
                AddSongsToUserPlaylist(items);
                StartupLog.Write("已把播放列表加入队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("AddNamedPlaylistToQueue", ex);
            }
        }


        /// <summary>把命名播放列表载入当前队列并开始播放（队列替换为列表内容）。</summary>
        internal async System.Threading.Tasks.Task LoadNamedPlaylistToQueueAndPlayAsync(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                if (paths.Count == 0)
                {
                    return;
                }

                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path) && SourceFromReference(path) == null)
                    {
                        continue;
                    }

                    try
                    {
                        items.Add(await System.Threading.Tasks.Task.Run(() => CreatePlaylistItemFromPath(path)));
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                if (items.Count == 0)
                {
                    return;
                }

                _queue.Items.Clear();
                AddSongsToUserPlaylist(items);
                PlayUserPlaylistAt(0);
                StartupLog.Write("已载入播放列表到队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LoadNamedPlaylistToQueueAndPlay", ex);
            }
        }


        /// <summary>已保存的命名播放列表名（供 UI 列示，含“我喜欢的音乐”）。</summary>
        internal IReadOnlyList<string> ListNamedPlaylists() => NamedPlaylistStore.List();

        /// <summary>当前播放列表拖拽重排后的回调(UserPlaylist 与主窗口共享,集合顺序已自动更新)。</summary>
        internal void RefreshFromPlaylistReorder()
        {
            // 用户播放列表顺序变化由共享 ObservableCollection 自动反映到主窗口;
            // 这里只需按当前播放曲目重新定位 _queue.Index,保证下一首播放方向正确。
            if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                for (int i = 0; i < _queue.Items.Count; i++)
                {
                    if (string.Equals(_queue.Items[i].FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _queue.Index = i;
                        return;
                    }
                }
            }

            if (_queue.Index >= _queue.Items.Count)
            {
                _queue.Index = _queue.Items.Count - 1;
            }
        }


        private void VolumeStyleCanvas_SizeChanged(dynamic sender, dynamic e)
        {
        }


        private void ClearNowPlayingPanel()
        {
            // 保留旧波形:停止/切歌时进度条静态显示上次波形,不闪占位
            _waveformPath = null;
            _nowPlayingPath = null;
            _currentPlayingItem = null;   // 停止/清空：权威记录一起清，别再按旧下标猜
            _lastCoverImage = null;   // 迷你播放器整窗背景同步清（它读的是大图，不是播放栏缩略图）
            _lastCoverThumbImage = null;   // 迷你播放器 72×72 小封面同步清（Thumb 160 档，见 UpdateTransportCoverAsync）
            ApplyNowPlayingPaneTransparent();
            UpdateTransportNowPlaying(null, null);
            ClearLyricsUi("开始播放后显示歌词");
            ClearAlbumArtBackground();
        }


        /// <param name="coverKnown">
        /// false = 封面还在后台解析中（切歌瞬间先刷标题的那次调用）：
        /// 此时**保持**播放条上原有的封面与占位图标状态不动，避免「先闪一下光盘占位图、再被真封面盖住」的眨眼感。
    /// true  = 封面已确定（可能是 null，表示这首歌确实没有内嵌封面），照实刷新并同步占位图标。
    /// </param>
    /// <param name="applyCover">
    /// false = 只刷文字，不动封面（封面由调用方随后单独填）。
    /// 播放栏那颗 58px 小封面要按自己的显示尺寸取 Thumb 档，不能拿播放页大图缩，
    /// 所以这里先不塞图，等 UpdateTransportCoverAsync 取到小图再填，
    /// 免得中间闪一下"大图硬缩到 58px"的糊图。
    /// </param>
    private void UpdateTransportNowPlaying(PlaylistItem? item, ImageSource? cover, bool coverKnown = true, bool applyCover = true)
        {
            if (item == null)
            {
                return;
            }

            if (coverKnown && applyCover)
            {
            }
        }


        /// <summary>
        /// 播放栏（底部条）左侧那颗小封面的加载：按自己的显示尺寸取 **Thumb 档**（分档磁盘缓存）。
        ///
        /// 为什么要单独一份：以前这里是直接把播放页那张大图（按 320px 框解码，高 DPI 下
        /// 实际 480px）塞进 58×58 的 Image —— 缩放比 5~8 倍，WinUI 缩这么狠会明显发糊，
        /// 用户 10-19 报「播放栏封面糊，歌曲面板里的却清楚」就是这个（歌曲面板走的是
        /// Thumb 档，尺寸和显示尺寸接近，所以清楚）。
        /// 现在改成分档取图：Thumb 档 160，显示 58（150% 缩放 = 87 物理像素、200% = 116），
        /// 任何缩放下都是"缩小显示"，解码器又用 Fant 高质量重采样，锐度对得上。
        /// </summary>
        private async Task UpdateTransportCoverAsync(string path, byte[]? coverBytes)
        {
            if (coverBytes == null || coverBytes.Length == 0)
            {
                // 这首歌确实没有内嵌封面：清掉（含占位图标复位），不能留着上一首的图。
                // ⚠️ 迷你播放器的小封面也读这里清出来的 _lastCoverThumbImage —— 不清的话
                //    换到无封面的歌时迷你条会挂着上一首的封面（回归问题）。
                _lastCoverThumbImage = null;
                return;
            }

            BitmapImage? thumb = await CoverArtService.GetAsync(coverBytes, CoverVariant.Thumb);
            bool fromCache = thumb != null;
            thumb ??= await CreateBitmapFromBytesAsync(coverBytes, CoverDecodeTargetPx(TransportCoverBorder, 160));

            // 切歌竞态：取图期间已经换了歌就丢掉这次结果
            if (thumb == null || !string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 一行自检：缓存到底有没有命中、最终图多大像素（"糊"类问题第一取证点）。
            // 实测这行落在 logs/app/ 当日日志里（搜「播放栏封面」即可）。
            global::CelesteMusicPlayer.StartupLog.Write(
                $"[播放栏封面] 缓存={(fromCache ? "命中" : "现解码")} 像素={thumb.PixelWidth}x{thumb.PixelHeight}");


            // 迷你播放器 72×72 小封面共用这一张（Thumb 160 档）：它以前拿播放页大图
            // （Large 768）硬缩到 72，十倍缩小必糊 —— 用户 10-19 报「缩略图也糊」。
            // 与播放栏共用同一张图、零额外解码；切歌竞态上面已挡（旧结果不会覆盖新状态）。
            _lastCoverThumbImage = thumb;
        }


        /// <summary>独占/HiFi 输出时显示实际输出格式（WASAPI 设备端采样率/位深）；无则清空该行。</summary>
        private void UpdateNowPlayingOutputFormat()
        {
            try
            {
                string? outFmt = _audioEngine?.ActualOutputFormat;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>信号链调试面板：实时显示 源格式→输出格式→是否独占→是否经过 DSP（对标 foobar 排障"假 bit-perfect"）。</summary>
        internal void UpdateSignalChainDisplay()
        {

            try
            {
                bool hifi = IsHiFiModeSelected();
                var chain = _audioEngine?.ChainFormat;

                // 与链路面板同一口径（ChainFormat 结构化值）：源文件 → 转码 WAV 一段讲清
                string src = chain is { HasSession: true }
                    ? (chain.SourceFile?.Describe() ?? chain.SourceFileDescription ?? "DSD/未探测")
                      + " → WAV " + (chain.TranscodeWav?.Describe() ?? "?") + chain.OutcomeNote()
                    : (hifi ? "（未知/解析中）" : "MediaPlayer（系统解码）");

                // 输出格式 / 设备（含结论：源直通·数值有损·重采样·待确认）
                string outp = chain?.DeviceOutput is AudioFormat dev
                    ? dev.Describe() + " · " + chain.VerdictNote()
                    : (hifi ? "（未知/解析中）" : "系统混音器（Shared）");

                string exclusivo = hifi ? "独占" : "共享";

                // DSP 摘要：EQ 在 HiFi DSP 链里实时生效；DSP 全关时端到端 bit-perfect。
                string dsp;
                if (hifi)
                {
                    dsp = "无（bit-perfect 直通）";
                }
                else
                {
                    bool eqOn = EqCurveStore.Load().HasEffect();
                    dsp = eqOn ? "EQ=on" : "EQ=off";
                }


            }
            catch
            {
            }
        }


        private async Task UpdateNowPlayingPanelAsync(PlaylistItem item)
        {
            _nowPlayingPath = item.FilePath;

            // 切歌：临时播放标记失效。新歌若在音乐库里，「添加到音乐库」按钮自动收起；
            // 不在（比如双击打开还没入库的散装文件）就继续显示。
            if (!string.Equals(_externalPlayPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _externalPlayPath = null;
            }

            // 「已加入音乐库」的确认态只对刚加的那首歌有效，切走了就复位
            if (!string.Equals(_justAddedToLibraryPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _justAddedToLibraryPath = null;
            }

            UpdateAddToLibraryButtonVisibility();

            UpdateNowPlayingArtistAlbumText(item);
            UpdateTransportNowPlaying(item, null, coverKnown: false);
            _ = UpdateAudioInfoTextAsync(item.FilePath);

            byte[]? coverBytes = await Task.Run(() => ExtractCoverBytes(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            BitmapImage? coverImage = null;
            if (coverBytes != null && coverBytes.Length > 0)
            {
                // 2026-10-19「播放栏封面糊」：播放页大图改走封面分档缓存（Large 768 档），
                // 命中时零 TagLib / 零解码；没命中（首播、缓存被清）才回落"按显示尺寸现解码"。
                coverImage = await CoverArtService.GetAsync(coverBytes, CoverVariant.Large);
                coverImage ??= await CreateBitmapFromBytesAsync(coverBytes, CoverDecodeTargetPx(NowPlayingCoverBorder, 320));
            }

            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            _lastCoverImage = coverImage;   // 迷你播放器整窗背景同步（它读的是大图，不是播放栏缩略图）
            UpdateTransportNowPlaying(item, coverImage, applyCover: false);
            _ = UpdateTransportCoverAsync(item.FilePath, coverBytes);
            _lastCoverBytes = coverBytes;   // 供自定义背景移除后恢复封面背景
            _ = ApplyAlbumArtBackgroundAsync(coverBytes, item.FilePath);
            ApplyNowPlayingPaneTransparent();

            List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            try
            {
                // 建行整段兜底：以前这里的异常会静默吞掉（Task 未 await），
                // 症状只有「播放页没有歌词」而日志一片干净，排查全靠猜。
                BuildLyricsUi(lyrics);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("BuildLyricsUi", caught);
            }
            _ = MaybeAutoDownloadExtrasAsync(item, lyrics, coverBytes);
        }


        // 「点艺术家超链接进详情」的处理器已于 2026-10-31 删除：它从未接到任何按钮的 Click
        // （LegacyControls 造的那个 Button 只被设置 IsEnabled，没人订阅），属于死代码；
        // 新外壳的艺术家详情在 artists.js 里，走网页导航。



        private void UpdateNowPlayingArtistAlbumText(PlaylistItem item)
        {
            bool hasArtist = !string.IsNullOrWhiteSpace(item.Artist) && item.Artist != "未知艺术家";
            bool hasAlbum = !string.IsNullOrWhiteSpace(item.Album) && item.Album != "未知专辑";


        }


        /// <summary>播放面板背景保持透明，与专辑详情页一致（露出主程序背景，非浮层）。
        /// ⚠️ 必须用"透明画刷"而不是 null：WinUI 里 Background=null 的元素不参与命中测试，
        /// 点击会漏到下面的音乐库界面。透明画刷视觉上一模一样，但能正常拦住鼠标。</summary>
        private void ApplyNowPlayingPaneTransparent()
        {
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void SyncLyricsToPosition(TimeSpan position, bool force = false, bool applyOffset = true)
        {
            TimeSpan adj = applyOffset ? OffsetLyricPosition(position) : position;

            if (_lyricLines.Count == 0 || _lyricTextBlocks.Count == 0)
            {
                return;
            }

            int index = 0;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].Time <= adj)
                {
                    // 跳过翻译行：高亮停在原文行（翻译行紧跟原文、同一时刻，若不清跳会让主题色落到译文上）
                    if (!_lyricLines[i].IsTranslation)
                    {
                        index = i;
                    }
                }
                else
                {
                    break;
                }
            }

            if (!force && index == _currentLyricIndex)
            {
                // 当前行未变：保持整行主题色，不把 Run 染成灰白
                if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                {
                    Color? curGeek = GeekLyricAccentColor();
                    Brush curAccent = curGeek is Color curGeekColor
                        ? new SolidColorBrush(curGeekColor)
                        : ResolveAccentBrush();
                    TextBlock row = _lyricTextBlocks[_currentLyricIndex];
                    row.Foreground = curAccent;
                    row.Opacity = 1.0;
                    if (curAccent is SolidColorBrush scbCur2)
                    {
                        ResetRowRunColors(row, scbCur2.Color);
                    }
                }

                return;
            }

            _currentLyricIndex = index;
            if (force)
            {
                StartupLog.Write("歌词强制重渲染 index=" + index + " 行数=" + _lyricTextBlocks.Count);
            }

            // 方案A：当前句主题色强调 + 相邻句微亮（纯属性调整，不改行结构、不用 Inlines）
            // 极客模式三层全换磷光色（画刷 alpha 分层，Opacity 不动）；经典模式保持原灰阶
            // 字号/字体跟随「主歌词」设置（四档：当前=设置值，邻-4、远-5；翻译行建时-7）
            (double sizeCur, double sizeNear, double sizeFar, _) = MainLyricFontSizes();
            FontFamily? lyricFontSync = MainLyricFontFamily();
            Color? geekLyric = GeekLyricAccentColor();
            Brush accent = geekLyric is Color geekAccent
                ? new SolidColorBrush(geekAccent)
                : ResolveAccentBrush();
            for (int i = 0; i < _lyricTextBlocks.Count; i++)
            {
                TextBlock row = _lyricTextBlocks[i];
                // 同 BuildLyricsUi：未选字体（跟随系统）时不碰 FontFamily，赋 null 会抛异常
                if (lyricFontSync != null)
                {
                    row.FontFamily = lyricFontSync;
                }
                int dist = Math.Abs(i - index);
                if (dist == 0)
                {
                    row.FontSize = sizeCur;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    row.Foreground = accent;
                    row.Opacity = 1.0;
                    // 当前行整行保持主题色（不把 Run 染成灰白，避免播放中被掩盖成灰色）
                    if (accent is SolidColorBrush scbCur)
                    {
                        ResetRowRunColors(row, scbCur.Color);
                    }
                    else
                    {
                        ResetRowRunColors(row, Color.FromArgb(255, 255, 255, 255));
                    }
                }
                else if (dist == 1)
                {
                    row.FontSize = sizeNear;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = geekLyric is Color geekNear
                        ? PhosphorShade(geekNear, 0xB3)
                        : new SolidColorBrush(Color.FromArgb(255, 205, 205, 205));
                    row.Opacity = 0.85;
                    ResetRowRunColors(row, geekLyric is Color geekNearRun
                        ? Color.FromArgb(0xB3, geekNearRun.R, geekNearRun.G, geekNearRun.B)
                        : Color.FromArgb(255, 205, 205, 205));
                }
                else
                {
                    row.FontSize = sizeFar;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = geekLyric is Color geekFar
                        ? PhosphorShade(geekFar, 0x8C)
                        : new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                    row.Opacity = 0.55;
                    ResetRowRunColors(row, geekLyric is Color geekFarRun
                        ? Color.FromArgb(0x8C, geekFarRun.R, geekFarRun.G, geekFarRun.B)
                        : Color.FromArgb(255, 154, 154, 154));
                }

            }

            // 用户手动滚动中：仅更新颜色高亮，不强制视口吸附（避免抢走用户正在看的滚动位置）
            if (!_userScrollingLyrics)
            {
                ScrollLyricToCenter(_lyricTextBlocks[index]);
            }
        }


        /// <summary>把播放位置按用户手动偏移换算成歌词匹配位置（正偏移=歌词延后显示）。</summary>
        private static TimeSpan OffsetLyricPosition(TimeSpan position)
        {
            double ms = AppSettingsStore.Load()?.LyricOffsetMs ?? 0.0;
            return position - TimeSpan.FromMilliseconds(ms);
        }


        /// <summary>当前行的逐字高亮刷新（无逐字数据时无操作）。</summary>
        private void UpdateCurrentLineCharHighlight(TimeSpan position)
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
            {
                UpdateCharHighlight(_lyricTextBlocks[_currentLyricIndex], OffsetLyricPosition(position));
            }
        }


        /// <summary>逐字着色：已唱字符白色，未唱灰色。仅对带 CharTimes 的行生效。</summary>
        private void UpdateCharHighlight(TextBlock row, TimeSpan position)
        {
            if (row.Tag is not LyricLine line
                || line.CharTimes == null
                || line.CharTimes.Count != line.Text.Length
                || row.Inlines.Count == 0)
            {
                return;
            }

            int n = 0;
            for (int i = 0; i < line.CharTimes.Count; i++)
            {
                if (line.CharTimes[i] <= position)
                {
                    n = i + 1;
                }
                else
                {
                    break;
                }
            }

            // 极客模式：已唱=磷光满色、未唱=暗磷光（35%）；经典模式保持白 / 灰154
            Color? geekChar = GeekLyricAccentColor();
            var played = geekChar is Color geekPlayed
                ? new SolidColorBrush(geekPlayed)
                : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            var unplayed = geekChar is Color geekUnplayed
                ? PhosphorShade(geekUnplayed, 0x59)
                : new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
            for (int i = 0; i < row.Inlines.Count; i++)
            {
                if (row.Inlines[i] is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = i < n ? played : unplayed;
                }
            }
        }


        /// <summary>
        /// 把播放跳到某一句歌词的时刻。
        /// playAfter=false（点进度条的语义）：跳过去后暂停，方便反复定位；
        /// playAfter=true（歌词行里的播放按钮）：跳过去后继续播，即"从这句开始听"。
        /// </summary>
        private void SeekToLyricLine(TimeSpan target, bool playAfter = false)
        {
            // 跳到目标时间（引擎 seek；若 MediaPlayer 播放也用其 seek）
            bool handled = false;
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                // 歌词时刻是"原歌曲时间"，换算回 WAV 时间再 seek
                _audioEngine.Seek(TimeSpan.FromSeconds(target.TotalSeconds / TempoScale()));
                handled = true;
            }

            MediaPlayer? player = GetPlayer();
            if (player != null && player.Source != null && !handled)
            {
                try { player.PlaybackSession.Position = target; handled = true; }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            if (!handled)
            {
                // 启动就绪态：还没开播（引擎没建、MediaPlayer 没源），没有可 seek 的会话。
                // 这时点歌词不能没反应 —— 记下目标位置直接起播，等于"从这句开始听"。
                StartPlaybackFromPendingPosition(target.TotalSeconds, pauseAfter: false);
                return;
            }

            if (_audioEngine != null)
            {
                if (playAfter)
                {
                    // 从这句开始听：暂停中的话先恢复，本来就播着的保持不动
                    if (_isEnginePaused)
                    {
                        _audioEngine.Resume();
                        _isEnginePaused = false;
                    }

                    UpdateWaveformTimerForPlaybackState(true);
                    MediaSession.SetStatus(MediaPlaybackStatus.Playing);


                    _taskbarButtons?.UpdatePlayPause(true);
                }
                else if (_audioEngine.IsPlaying)
                {
                    // 与点进度条一致：seek 后暂停，方便定位（用户再按播放继续）
                    _audioEngine.Pause();
                    _isEnginePaused = true;
                    UpdateWaveformTimerForPlaybackState(false);
                    MediaSession.SetStatus(MediaPlaybackStatus.Paused);


                    _taskbarButtons?.UpdatePlayPause(false);
                }

            }
            else if (player != null && player.Source != null)
            {
                // 非引擎（普通 MediaPlayer）路径
                if (playAfter)
                {
                    if (player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                    {
                        player.Play();
                    }

                    MediaSession.SetStatus(MediaPlaybackStatus.Playing);
                    _taskbarButtons?.UpdatePlayPause(true);
                }
                else if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    player.Pause();
                    MediaSession.SetStatus(MediaPlaybackStatus.Paused);
                    _taskbarButtons?.UpdatePlayPause(false);
                }
            }

            // 重挂下一首（seek 丢弃无缝源里的 next）
            _ = Session.PreloadNextAsync();

            // 立即把高亮切到目标行（点击跳转按原位置，不套用偏移）
            global::CelesteMusicPlayer.StartupLog.Write(
                $"[歌词] 双击跳转 target={target.TotalSeconds:F2}s → 强制同步(force=true, applyOffset=false)");
            SyncLyricsToPosition(target, force: true, applyOffset: false);
            // 单击选中：无视用户滚动状态，把该行滚到中间
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
            {
            }
        }


        private void WaveformTimer_Tick(dynamic sender, dynamic e)
        {
            MediaPlayer? player = GetPlayer();
            bool enginePlaying = _audioEngine?.IsPlaying == true;
            bool playing = (player?.Source != null
                    && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                || enginePlaying;

            double t = Environment.TickCount64 / 1000.0;
            double volume = enginePlaying ? GetVolumePublic() / 100.0 : (player?.Volume ?? 0.8);

            // 真频谱优先：拿得到实际播放信号的 FFT 结果就用真的（低频在左、高频在右，跟着音乐动）；
            // 拿不到（DSD 直出、刚开始播放样本不足、非引擎播放）才回退到装饰性呼吸动画。
            bool realSpectrum = playing && _audioEngine != null && _audioEngine.TryGetSpectrum(_spectrumBands);

            bool changed = false;

            for (int i = 0; i < WaveBarCount; i++)
            {
                double target;
                if (playing)
                {
                    if (realSpectrum)
                    {
                        // 真频谱：不再乘音量系数（信号本身已含软件音量；硬件音量下不该让画面塌下去）
                        target = Math.Clamp(_spectrumBands[i], 0.06, 1.0);
                    }
                    else
                    {
                        // 对称呼吸式频谱：中间高两边低、每柱各自节奏，无横向滚动
                        double rhythm = 0.5 + 0.5 * Math.Sin(t * 1.8 + _wavePhases[i]);
                        double halfSpan = Math.Max(1.0, (WaveBarCount - 1) / 2.0);
                        double pos = (i - (WaveBarCount - 1) / 2.0) / halfSpan;
                        double symmetry = 0.5 + 0.5 * (1.0 - Math.Min(1.0, Math.Abs(pos)));
                        double n = rhythm * symmetry;
                        target = Math.Clamp(n * (0.55 + 0.45 * volume), 0.1, 1.0);
                    }
                }
                else
                {
                    target = IdleLevel(i);
                }

                // 真频谱已在分析器内做过"快起慢落"，这里只补一点插值避免逐帧跳变
                double ease = realSpectrum ? 0.55 : (playing ? 0.35 : 0.18);
                double next = _waveLevels[i] + (target - _waveLevels[i]) * ease;
                if (Math.Abs(next - _waveLevels[i]) > 0.002)
                {
                    changed = true;
                }

                _waveLevels[i] = next;
            }

            if (!playing)
            {
                _waveformIdleSettleTicks++;
                if (_waveformIdleSettleTicks >= 12 || !changed)
                {
                    _waveformTimer?.Stop();
                    _waveformIdleSettleTicks = 0;
                }
            }
            else
            {
                _waveformIdleSettleTicks = 0;
            }
        }


        // =====================================================================
        // 播放核心
        // =====================================================================

        private void PlayAtIndex(int index)
            => PlayLibraryItemAt(index, syncUserPlaylistIndex: true);

        private void PlayLibraryItemAt(int index, bool syncUserPlaylistIndex)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                return;
            }

            PlaylistItem item = _playlist[index];
            _currentIndex = index;
            if (syncUserPlaylistIndex)
            {
                _queue.Index = FindUserPlaylistIndex(item.FilePath);
            }

            if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && index >= 0
                && _playlistViewSource is System.Collections.IList songsList
                && index < songsList.Count)
            {
            }

            StartPlayback(item);
        }


        private void PlayUserPlaylistAt(int index)
        {
            if (index < 0 || index >= _queue.Items.Count)
            {
                return;
            }

            PlaylistItem item = _queue.Items[index];

            // SACD 镜像(.iso)：播放时懒抽取为逐轨 DSF 并就地展开进队列，复用既有 DSD 全链路。
            // 引擎层不感知 .iso，展开后的 DSF 走与本地 DSD 文件完全相同的播放路径（DoP/PCM/续播/持久化）。
            if (SacdIsoExtractor.IsSacdIso(item.FilePath))
            {
                _ = ExpandAndPlaySacdAsync(index);
                return;
            }

            _queue.Index = index;
            _currentIndex = FindLibraryIndex(item.FilePath);

            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                && !_isMultiSelectMode)
            {
            }
            else if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && _currentIndex >= 0
                && _playlistViewSource is System.Collections.IList songsList2
                && _currentIndex < songsList2.Count)
            {
            }

            StartPlayback(item);
            PersistPlaybackSession();
            NotifyCurrentPlaylistWindow();
        }


        /// <summary>
        /// 把队列里 index 处的 .iso 抽取为逐轨 DSF，并就地展开进队列（首轨留在原 index），
        /// 随后像普通 DSD 文件一样开始播放；后续切歌由既有的 HandleMediaEnded 自然推进各轨。
        /// </summary>
        private async Task ExpandAndPlaySacdAsync(int isoIndex)
        {
            try
            {
                if (isoIndex < 0 || isoIndex >= _queue.Items.Count)
                {
                    return;
                }

                string isoPath = _queue.Items[isoIndex].FilePath;

                var tracks = await SacdIsoExtractor.ExtractTracksAsync(isoPath, s =>
                    DispatcherQueue.TryEnqueue(() => { })).ConfigureAwait(false);

                if (tracks.Count == 0)
                {
                    return;
                }

                // 用既有的标签读取构建 DSF 播放项（标题/艺术家/专辑/时长等）
                var dsItems = new List<PlaylistItem>();
                foreach (var t in tracks)
                {
                    try
                    {
                        dsItems.Add(CreatePlaylistItemFromPath(t));
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught);
                    }
                }

                if (dsItems.Count == 0)
                {
                    return;
                }

                // 用逐轨 DSF 替换队列里的 .iso 那一项（就地展开），首轨仍在 isoIndex
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        _queue.Items.RemoveAt(isoIndex);
                        for (int k = 0; k < dsItems.Count; k++)
                        {
                            _queue.Items.Insert(isoIndex + k, dsItems[k]);
                        }

                        _queue.Index = isoIndex;                       // 指向首轨
                        _currentIndex = FindLibraryIndex(dsItems[0].FilePath);
                        StartPlayback(dsItems[0]);
                        PersistPlaybackSession();
                        NotifyCurrentPlaylistWindow();
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught);
                    }
                });
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught);
            }
        }


        private void StartPlayback(PlaylistItem item)
        {
            // 所有起播（手点、自动续播、无缝重建、命令行）都经过这里：
            // 在这里把"当前在播哪首"钉死，网页状态就不必再去猜队列下标还是曲库下标。
            _currentPlayingItem = item;
            if (item.SourceId.Length > 0 && !System.IO.File.Exists(item.FilePath)) { _ = PlaySourceAsync(item); return; }
            PrepareSourcePlayback(item);
            // 标记"用户主动开播过"：之后 MediaPlayer 报错才值得弹「无法播放」对话框。
            _anyPlaybackStarted = true;
            // 起播途中：引擎可能还播着上一首，这期间不写进度（避免记串曲目）
            _startPlaybackInFlight = true;
            Journal.ScrobblePrevious();

            // 进度条样式(读设置缓存) + 异步加载波形(波形样式用)
            _waveformProgress = AppSettingsStore.Load().WaveformProgress;
            // 保留旧波形直到新波形解码完成(避免加载过程闪占位)
            _waveformPath = null;
            StartupLog.Write("波形加载开始: " + item.FilePath + " style=" + (_waveformProgress ? "Waveform" : "Gradient"));
            LoadWaveformForCurrentAsync(item.FilePath);

            // DSD(DSF/DFF) 在非 WASAPI 独占模式下自动转码为 PCM 输出（保留可听性，非 bit-perfect），
            // 独占模式下走 DoP 原生直出。提示在 PlayExtendedWithEngineAsync 成功后给出。
            StartupLog.Write("StartPlayback: " + item.FilePath + " mode=" + (AppSettingsStore.Load().OutputMode));

            // （2026-10-16 删除）DSD×ECHO 内核提醒：ECHO 内核选项已删（程序只用 native2），
            // 该分支永远不成立，整套提醒逻辑已移除。

            // 三模式统一走 FFmpeg 引擎 + NAudio/HiFi 输出（共享 / WASAPI 独占 / ASIO），
            // 使曲线 EQ / 声道平衡 / 限幅在所有输出模式下都能实时生效、暂停后继续保留。
            // 直接按设置判断（而非 _audioEngine.IsHiFiMode），避免 engine 尚未创建/未设 mode 时的首次播放漏走。
            {
                // 用源实际时长作为进度/播完上限（规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界）。
                // DSD 源用 ffmpeg 探测真实音轨时长（TagLib 读 DSD 源时长可能不准）；其它用元数据时长，异步不阻塞开播。
                _ = ApplyEngineSourceDurationAsync(item);
                // 停掉 MediaPlayer，避免与引擎同时出声
                MediaPlayer? curPlayer = GetPlayer();
                if (curPlayer != null && curPlayer.Source != null)
                {
                    try
                    {
                        curPlayer.Pause();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                _ = PlayExtendedWithEngineAsync(item);
                return;
            }

        }


        /// <summary>
        /// 就绪态（还没开播）下用户点了歌词或拖了进度条：记下目标位置，直接把当前这首起播。
        /// 引擎起来后由 ApplyPendingStartPosition 定位过去。
        /// </summary>
        private void StartPlaybackFromPendingPosition(double seconds, bool pauseAfter)
        {
            if (_queue.Index < 0 || _queue.Index >= _queue.Items.Count)
            {
                return;
            }

            PlaylistItem item = _queue.Items[_queue.Index];
            _pendingStartSeconds = Math.Max(0, seconds);
            _pendingStartPauseAfter = pauseAfter;
            _pendingStartPath = item.FilePath;
            StartPlayback(item);
        }


        /// <summary>
        /// 起播成功后，把"待应用的起始位置"真正落下去。
        /// 三种来源共用一个通道：①启动续播（上次听到哪儿）②就绪态双击歌词 ③就绪态拖进度条。
        /// 这三种情况下还没有播放会话，没法提前 seek，只能等引擎起来再定位。
        /// </summary>
        private void ApplyPendingStartPosition(string? playingFilePath)
        {
            double startAt = _pendingStartSeconds;
            bool pauseAfter = _pendingStartPauseAfter;
            string? ownerPath = _pendingStartPath;
            _pendingStartSeconds = 0;
            _pendingStartPauseAfter = false;
            _pendingStartPath = null;

            if (_audioEngine == null || startAt <= 0.5)
            {
                return;
            }

            // 位置是给"那一首"记的：如果期间用户点了别的歌，就不能拿过来用。
            if (!string.IsNullOrEmpty(ownerPath)
                && !string.IsNullOrEmpty(playingFilePath)
                && !string.Equals(ownerPath, playingFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                double scale = TempoScale();
                double duration = _audioEngine.Duration.TotalSeconds;
                if (duration > 1 && startAt > duration * scale - 0.5)
                {
                    startAt = Math.Max(0, duration * scale - 0.5);
                }

                // target 是"原歌曲时间"（进度条/歌词/SMTC 口径），引擎 seek 要换算回 WAV 时间
                var target = TimeSpan.FromSeconds(startAt);
                _audioEngine.Seek(TimeSpan.FromSeconds(startAt / scale));

                MediaSession.UpdateTimeline(target, TimeSpan.FromSeconds(duration * scale));

                // 歌词直接跳到该位置对应的那句（别从第一句慢慢滚过去）
                SyncLyricsToPosition(target, force: true, applyOffset: false);
                if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                {
                }

                StartupLog.Write("续播/定位: " + startAt.ToString("F2") + "s");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            if (pauseAfter)
            {
                try
                {
                    _audioEngine.Pause();
                    _isEnginePaused = true;
                    UpdateWaveformTimerForPlaybackState(false);
                    MediaSession.SetStatus(MediaPlaybackStatus.Paused);


                    _taskbarButtons?.UpdatePlayPause(false);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

        }


        /// <summary>引擎播放前异步设置源实际时长（DSD 用 ffmpeg 探测真实音轨时长，其它用元数据时长），
        /// 作为进度/播完上限规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界。</summary>
        private async System.Threading.Tasks.Task ApplyEngineSourceDurationAsync(PlaylistItem item)
        {
            try
            {
                if (_audioEngine == null || item == null) return;
                string srcExt = System.IO.Path.GetExtension(item.FilePath).ToLowerInvariant();
                TimeSpan src = item.Duration;
                if (srcExt is ".dsf" or ".dff")
                {
                    var probed = await _audioEngine.ProbeSourceDurationAsync(item.FilePath);
                    if (probed > TimeSpan.Zero) src = probed;
                }

                _audioEngine.SetSourceDuration(src);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }



        /// <summary>用 FFmpeg 引擎播放扩展格式（APE/WavPack 等）。</summary>
        private async Task PlayExtendedWithEngineAsync(PlaylistItem item)
        {
            QuickPlayerCoordinator.PauseForMain();
            // 播放历史：切到新歌前，把上一首（若有）记入历史（未播完）
            if (!string.IsNullOrWhiteSpace(_nowPlayingPath)
                && !string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                Journal.RecordHistory(completed: false);
            }


            // 引擎接线（建引擎 / 输出模式 / 设备 / EQ / DSP / 房间校正 / ReplayGain）全部在会话里。
            // 这里只要两样：给面板用的 ReplayGain 数据，以及"能不能刷读数"。
            PlaybackPreparation preparation = await Session.PrepareAsync(item).ConfigureAwait(true);
            _currentRgData = preparation.ReplayGain;
            if (preparation.ReplayGainApplied) DispatcherQueue.TryEnqueue(RefreshAudioFxRgInfo);

            // Engine events are wired once by PlaybackSession; do not handle each transition twice.
            // CUE 分轨与起播本身也在会话里：会话只回"成没成、是不是 DoP 直出、转了 PCM 没有、多长"。
            PlaybackStartResult result = await Session
                .StartAsync(item, s => DispatcherQueue.TryEnqueue(() => { }))
                .ConfigureAwait(true);
            if (result.Ok)
            {
                _isEnginePaused = false;
                _usingEnginePlayback = true;
                // DSD：以输出后端的结构化标志位为准——守卫全过且 DoP 直出已起播才提示直出；
                // 守卫拒绝（DFF/DST/DSD512/多声道/共享/DSP 未关）或设备协商失败 → 已回退 PCM，如实提示转码。
                if (result.Kind == PlaybackKind.DsdDirect)
                {
                    // 变速诚实性（2026-10-16 起口径变了）：DoP 直出 = 1-bit 位流直通设备，
                    // 不经 DSP 链，托管实时变速够不着 → 倍率≠1 时如实说明按 1x 播。
                    if (result.TempoActive)
                    {
                    }
                }
                else if (result.Kind == PlaybackKind.DsdTranscoded)
                {
                    StartupLog.Write("DSD 已转码 PCM 输出: " + item.FilePath + " → " + result.PcmDescription);
                    // 转成 PCM 的 DSD 走的就是普通 PCM 链路，实时变速对它有效（2026-10-16 起），
                    // 不再需要"不支持变速"的提示。
                }
                // 设备主音量（DAC 驱动级）只由用户拖动音量条时设置，切歌不重置，保持用户设定的响度。
                // （播放器数字音量恒 100% 直通，bit-perfect；无实体音量键的小尾巴可拖动音量条调轻）
                _ = UpdateNowPlayingPanelAsync(item);
                UpdateNowPlayingOutputFormat();
                UpdateSignalChainDisplay();
                Journal.RecordStart(item);

                // 引擎桥接：时长 / 进度条 / 波形（变速时进度条按"原歌曲时间"：WAV 时长 × 倍率）
                double tempoScale = TempoScale();
                double totalWav = result.Duration.TotalSeconds;
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.PositionChanged += EnginePositionChanged;
                UpdateWaveformTimerForPlaybackState(true);

                // 启动续播 / 就绪态点歌词 / 拖进度条：起播成功后立刻定位到目标位置
                // （放在淡入之前，避免先出声再跳造成爆音）
                ApplyPendingStartPosition(item.FilePath);

                _ = FadeInEngineAsync();
                MediaSession.ConfigureTrack(item, playing: true);
                _ = Session.PreloadNextAsync();
                _startPlaybackInFlight = false;
            }
            else
            {
                _startPlaybackInFlight = false;
                _pendingStartSeconds = 0;
                _pendingStartPauseAfter = false;
                _pendingStartPath = null;

                string? reason = result.FailureReason;

                if (result.NeedFfmpegHint)
                {
                    _ = ShowErrorAsync(
                        "无法播放该格式",
                        "内置 FFmpeg 解码器未找到。\n\n这通常是被杀毒软件（如火绒/360）拦截删除。\n请将程序目录下 Assets\\ffmpeg 文件夹加入杀毒软件信任区，然后重新打开程序。");
                }
            }
        }



        private async Task FadeInEngineAfterResumeAsync(double target)
        {
            // 防御：若读到的暂停音量异常(<=1%≈静音误判)，回退全音量，避免恢复后设备音量停在约 0 造成"系统托盘音量归0"。
            if (target <= 0.01 || double.IsNaN(target))
            {
                target = 1.0;
            }

            target = Math.Clamp(target, 0.0, 1.0);
            try
            {
                // 暂停恢复后独占会话重建，瞬时全音量可达造成爆音/音量暴增：
                // 从较低音量极短渐变到目标，缓解瞬态。（仅暂停恢复路径调用，不影响常规切歌的 bit-perfect 直通。）
                _audioEngine?.SetVolume(target * 0.18);
                const int steps = 5;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * (0.18 + 0.82 * i / (double)steps));
                    await Task.Delay(30);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            finally
            {
                // 无论 Fade 是否中断/异常，最终都恢复目标设备音量，避免停在 0.02 静音值
                try { _audioEngine?.SetVolume(target); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void OnEnginePlaybackEnded()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                StartupLog.Write("[播放] 引擎播完事件到达，准备推进（在播=" + (_currentPlayingItem?.Title ?? "<无>")
                    + " 队列下标=" + _queue.Index + "）");
                // 播放历史：自然播完 → 记为已完成
                Journal.RecordHistory(completed: true);
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                MediaSession.SetStatus(MediaPlaybackStatus.Stopped);
                HandleMediaEnded();
            });
        }


        /// <summary>顺序播放时确定"当前曲目之后"的一首（播放队列优先，否则媒体库列表）。</summary>
        private PlaylistItem? ResolveSequentialNextItem(PlaylistItem current)
        {
            // 无缝预加载预测"即将播放的下一首"：必须遵循当前播放顺序（含随机），
            // 否则随机播放时自动切歌会错误地切到列表顺序的下一首而非随机下一首。
            if (_queue.Items.Count > 0)
            {
                int baseIdx = FindUserPlaylistIndex(current.FilePath);
                if (baseIdx < 0)
                {
                    baseIdx = _queue.Index >= 0 ? _queue.Index : 0;
                }

                int next = _orderResolver.NextIndexByOrder(_queue.Items.Count, baseIdx, autoAdvance: true);
                if (next >= 0 && next < _queue.Items.Count)
                {
                    return _queue.Items[next];
                }

                return null;
            }

            if (_playlist.Count > 0)
            {
                int baseIdx = _playlist.ToList().FindIndex(p => string.Equals(p.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase));
                if (baseIdx < 0)
                {
                    baseIdx = _currentIndex >= 0 ? _currentIndex : 0;
                }

                int next = _orderResolver.NextIndexByOrder(_playlist.Count, baseIdx, autoAdvance: true);
                if (next >= 0 && next < _playlist.Count)
                {
                    return _playlist[next];
                }
            }

            return null;
        }


        /// <summary>
        /// 无缝切到预加载的下一首：更新正在播放信息与索引，并继续预加载下下首。
        /// 预载记录与"当前在播哪首"的推进都在会话里；这里只做界面的事。
        /// </summary>
        private void OnSeamlessAdvanced(PlaylistItem? next)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (next == null)
                {
                    // 换轨事件到了但会话没有预载记录可对账：引擎已经在放下一首了，界面不能装作没发生。
                    // 这里没有可靠的"当前是哪首"来源（引擎不暴露正在播的文件），只把状态标对并留下现场。
                    _isEnginePaused = false;
                    _usingEnginePlayback = true;
                    StartupLog.Write("[无缝] 换轨事件到达但预载记录为空：界面未切换（队列下标=" + _queue.Index
                        + " 记录在播=" + (_nowPlayingPath ?? "<空>") + "）");
                    return;
                }

                // 权威记录会话已推进（SeamlessAdvanced 之前）；这里再钉一次让来源显式。
                _currentPlayingItem = next;
                _isEnginePaused = false;
                _usingEnginePlayback = true;
                _ = UpdateNowPlayingPanelAsync(next);
                // 无缝续接后重新加载下一首的波形/进度条样式（否则会残留上一首的波形与时长）
                _waveformProgress = AppSettingsStore.Load().WaveformProgress;
                _waveformPath = null;
                StartupLog.Write("无缝切歌 波形加载开始: " + next.FilePath + " style=" + (_waveformProgress ? "Waveform" : "Gradient"));
                LoadWaveformForCurrentAsync(next.FilePath);
                UpdateNowPlayingOutputFormat();
                Journal.RecordStart(next);
                if (_audioEngine != null)
                {
                    // 无缝切歌同样按"原歌曲时间"显示总时长（变速时 WAV 被拉伸过）
                    double scale = TempoScale();
                    double wavSeconds = _audioEngine.Duration.TotalSeconds;
                }
                MediaSession.ConfigureTrack(next, playing: true);
                AdvanceUserPlaylistIndexTo(next);
                _ = Session.PreloadNextAsync();
            });
        }


        /// <summary>无缝切歌后同步用户播放队列当前索引（命中则设为该项）。</summary>
        private void AdvanceUserPlaylistIndexTo(PlaylistItem item)
        {
            if (_queue.Items.Count == 0) return;
            int idx = FindUserPlaylistIndex(item.FilePath);
            if (idx >= 0)
            {
                _queue.Index = idx;
            }
        }


        /// <summary>
        /// 拿到 SMTC 专用宿主 MediaPlayer（懒创建），并确保它挂着"静音循环占位源"真正开始一个音频播放会话。
        /// 系统 SMTC 只有在应用开始音频播放会话后，才会在系统浮窗/音量条旁显示歌曲元数据；
        /// 否则手动塞进 DisplayUpdater 的标题/歌手/封面会被系统忽略（只剩 exe 图标 + 路径）。
        /// 本播放器真正出声走 NAudio/WASAPI 引擎，主 MediaPlayer 从不真正播放，故用独立宿主 + 占位源。
        /// 独立宿主不 SetMediaPlayer、不注册主播放器事件，避免静音源触发 Player_MediaOpened 等污染主播放 UI。
        /// Volume=0 保证绝不发声，不干扰引擎（含 WASAPI 独占 / bit-perfect 直通）。
        /// </summary>
        // 波形加载主流程（2026-10-31 从 MainWindow.Misc.cs 搬来）：StartPlayback / OnSeamlessAdvanced 等活路径在调，字段组本来就住在 Playback.cs。

        /// <summary>异步加载当前歌曲波形(供波形进度条)，完成后重绘。</summary>
        private async void LoadWaveformForCurrentAsync(string path, double startSeconds = 0, double lengthSeconds = 0)
        {
            try
            {

            _waveformPath = path;

            // CUE 分轨：没显式带区间时按当前播放条目补（同一文件的多轨要各自的波形）
            if (startSeconds <= 0 && _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                && string.Equals(_queue.Items[_queue.Index].FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                startSeconds = _queue.Items[_queue.Index].StartTimeSeconds;
                lengthSeconds = startSeconds > 0 ? _queue.Items[_queue.Index].Duration.TotalSeconds : 0;
            }

            // 守卫键带区间：同文件切轨时路径相同，靠起点区分
            string rangeKey = startSeconds > 0
                ? path + "#t=" + startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : path;
            _waveformRangeKey = rangeKey;

            // 错峰（2026-09-21）：起播那一瞬间会同时跑两个满负荷的 ffmpeg —— 一个整轨转码、
            // 一个整轨解波形。两个一起抢 CPU，独占渲染线程就饿着 → 每首歌开头最容易顿。
            // 波形不是立刻要看的东西，让转码先跑 900ms 再开工。
            await System.Threading.Tasks.Task.Delay(900);
            if (!string.Equals(_waveformRangeKey, rangeKey, StringComparison.OrdinalIgnoreCase))
            {
                return; // 已切歌/切轨
            }

            // 桶数 = 波形柱子根数。2026-10-16 用户反馈「每根竖线太细」：
            // 180 根摊在 720px 上每根只有约 2.6px（还要再扣柱间缝），看着就是一撮细丝。
            // 降到 120 根后每根约 4.8px，是 Poweramp 那种能看清起伏的柱状波形。
            // ⚠️ 改这个值会让已缓存的波形全部重算一次（缓存按桶数校验），属预期。
            float[] wave = await WaveformDataProvider.GetWaveformAsync(path, 120, startSeconds, lengthSeconds);
            if (!string.Equals(_waveformRangeKey, rangeKey, StringComparison.OrdinalIgnoreCase))
            {
                return; // 已切歌/切轨
            }

            _waveformData = wave;
            StartupLog.Write("波形回调: " + (wave?.Length > 0 ? "有数据" : "空") + " style=" + (_waveformProgress ? "Waveform" : "Gradient"));
            if (_waveformProgress)
            {
            }
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}

        // 封面缩略图缓存（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：TransportCover / NowPlayingPanel 刷新在读写。
        /// <summary>
        /// 最近一次当前播放曲目的封面**缩略图**（Thumb 160 档，与播放栏同一张）。
        /// 迷你播放器 72×72 小封面用这张 —— 不能拿 <see cref="_lastCoverImage"/>（Large 768）硬缩：
        /// 768→72 十倍缩小，WinUI bilinear 出来必糊（用户 10-19 报「迷你播放器的专辑封面
        /// 缩略图也糊」，和之前「播放栏糊」同一个病根：没用对应显示尺寸的分档）。
        /// 由 UpdateTransportCoverAsync 取到 Thumb 档后一并写这里，两边共用一张图、零额外解码。
        /// </summary>
        private ImageSource? _lastCoverThumbImage;
        // 选中行前景色下探/清除（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：静态纯视觉树遍历，只操作传入对象，被 Playback/Playback3 的选中刷新调用。

        private static void ApplyForegroundToDescendants(DependencyObject root, Brush foreground)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.Foreground = foreground;
                }
                else if (child is Control control)
                {
                    control.Foreground = foreground;
                }

                ApplyForegroundToDescendants(child, foreground);
            }
        }

        private static void ClearForegroundOnDescendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.ClearValue(TextBlock.ForegroundProperty);
                }
                else if (child is Control control)
                {
                    control.ClearValue(Control.ForegroundProperty);
                }

                ClearForegroundOnDescendants(child);
            }
        }
        // 歌词行渐隐色重置（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：操作传入的 TextBlock，唯一用户是 Playback3 的 SyncLyricsToPosition。

        /// <summary>将行的逐字 Run 统一重置为指定颜色（用于非当前行）。</summary>
        private void ResetRowRunColors(TextBlock row, byte r, byte g, byte b)
        {
            if (row.Inlines.Count == 0)
            {
                return;
            }

            var brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
            foreach (Microsoft.UI.Xaml.Documents.Inline inline in row.Inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = brush;
                }
            }
        }

        /// <summary>重载：按整色（可带 alpha）重置逐字 Run 颜色。极客磷光分层（暗磷光行）用。</summary>
        private void ResetRowRunColors(TextBlock row, Color color)
        {
            if (row.Inlines.Count == 0)
            {
                return;
            }

            var brush = new SolidColorBrush(color);
            foreach (Microsoft.UI.Xaml.Documents.Inline inline in row.Inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = brush;
                }
            }
        }
        // 封面缓存失效（2026-10-31 搬来）：封面域状态随 Playback3。

        /// <summary>封面写入磁盘后使缓存失效（下载封面后调用）。</summary>
        internal static void InvalidateCoverCache(string audioPath)
        {
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                CoverBytesCache.TryRemove(audioPath, out _);
            }
        }
        // 封面域缓存（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：TransportCover / NowPlayingPanel 刷新在读写。

                /// <summary>最近一次当前播放曲目的封面字节：清掉自定义背景后恢复封面背景用。</summary>
        private byte[]? _lastCoverBytes;

        /// <summary>最近一次当前播放曲目的封面大图（迷你播放器整窗背景同步用，读大图不是播放栏缩略图）。</summary>
        private ImageSource? _lastCoverImage;
    }
}
