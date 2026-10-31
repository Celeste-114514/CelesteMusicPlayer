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

        /// <summary>引擎播放位置 → 进度条 / 时间 / 任务栏进度。</summary>
        /// <summary>更新 SMTC timeline 属性（进度/时长），让系统媒体小组件(deskbox)显示当前曲目进度并可 seek。</summary>
        private void EnginePositionChanged(TimeSpan position)
        {
            try
            {
                // 诊断：每 3s 打点一次，确认引擎位置回调是否持续触发
                if (Environment.TickCount64 - _lastSmtcTimelineLogMs > 3000)
                {
                    _lastSmtcTimelineLogMs = Environment.TickCount64;
                    global::CelesteMusicPlayer.StartupLog.Write("[SMTC] EnginePositionChanged: pos=" + position.TotalSeconds.ToString("F2")
                        + " usingEngine=" + _usingEnginePlayback + " enginePlaying=" + (_audioEngine?.IsPlaying ?? false));
                }

                if (!_usingEnginePlayback)
                {
                    return;
                }

                // 收听时长采样挂引擎心跳：正式播放不跑界面进度定时器，
                // 只在 PositionTimer_Tick 里采样等于永远采不到（旧实现的「累计收听秒数恒为 0」就是这么来的）。
                Journal.SampleListenSeconds();

                // 用户正在拖动/点击进度条时不覆盖，避免跳动
                if (_isUserSeeking)
                {
                    return;
                }

                // SMTC timeline（系统媒体小组件/deskbox 进度与可 seek）：约 500ms 一次
                if (Environment.TickCount64 - _lastSmtcTimelineMs > 400)
                {
                    _lastSmtcTimelineMs = Environment.TickCount64;
                    MediaSession.UpdateTimeline(position);
                }

                // 变速播放按"原歌曲时间"显示：WAV 时长 × 倍率
                double scale = TempoScale();
                double duration = (_audioEngine?.Duration.TotalSeconds ?? 0) * scale;

                // 界面/歌词/SMTC/迷你播放器全部消费"源时间"：引擎位置 × 倍率
                double seconds = position.TotalSeconds * scale;

                TimeSpan sourcePos = TimeSpan.FromSeconds(seconds);
                _desktopLyricsWindow?.Sync(sourcePos);
                if (duration > 1)
                {
                    _taskbarProgress?.SetProgress(seconds, duration, paused: false);
                }

                // 引擎（HiFi 独占/ASIO）路径也要推进当前歌词行与滚动，
                // 否则歌词不随播放滚动（普通 MediaPlayer 路径由 PositionTimer_Tick 调用）。
                SyncLyricsToPosition(sourcePos);

                // ★ 定期把进度写盘：PositionTimer_Tick 在 _usingEnginePlayback 时会直接早退，
                // 而所有播放都走引擎 → 进度从来没被保存过，下次启动只能从头开始。
                if ((DateTime.UtcNow - _lastPlaybackPersistUtc).TotalSeconds >= 5)
                {
                    _lastPlaybackPersistUtc = DateTime.UtcNow;
                    PersistPlaybackSession();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>停止引擎播放并复位相关 UI 状态（切到普通格式时调用）。</summary>
        private void StopEngineIfActive()
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.Stop();
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                MediaSession.SetStatus(MediaPlaybackStatus.Stopped);
            }
        }


        private void PlayNext()
            => PlayNext(autoAdvance: false);

        private void PlayNext(bool autoAdvance)
        {
            // 优先按播放队列续播；若队列为空（例如直接从媒体库/文件夹双击播放），
            // 则按媒体库当前列表 (_playlist) 顺序推进，保证播完能自动连续下一首。
            if (_queue.Items.Count > 0)
            {
                int? nextIndex = _orderResolver.ResolveNextIndex(_queue.Items.Count, _queue.Index, autoAdvance);
                if (nextIndex != null)
                {
                    PlayUserPlaylistAt(nextIndex.Value);
                }

                return;
            }

            AdvanceInLibraryPlaylist(autoAdvance);
        }


        /// <summary>队列为空时，从媒体库当前列表推进到下一首（按播放顺序）。</summary>
        private void AdvanceInLibraryPlaylist(bool autoAdvance)
        {
            if (_playlist.Count == 0 || _currentIndex < 0)
            {
                return;
            }

            // 决策在 LibraryAdvanceRule（纯函数，可穷举用例），这里只负责起播。
            int? next = LibraryAdvanceRule.NextIndex(_orderResolver.Order, _playlist.Count, _currentIndex, autoAdvance, _orderResolver.NextRandomIndex);
            if (next is int index)
            {
                PlayLibraryItemAt(index, syncUserPlaylistIndex: false);
            }
        }


        private void PlayPrevious()
        {
            if (_queue.Items.Count == 0)
            {
                return;
            }

            int? prevIndex = _orderResolver.ResolvePreviousIndex(_queue.Items.Count, _queue.Index);
            if (prevIndex == null)
            {
                return;
            }

            PlayUserPlaylistAt(prevIndex.Value);
        }


        // 下一首 / 上一首的索引决策已抽到 PlaybackOrderResolver（阶段7 解耦）：
        //   ResolveNextIndex / NextIndexByOrder / ResolvePreviousIndex
        // 这三个方法原先在这里，现在由 _orderResolver 提供。


        // =====================================================================
        // 播放顺序按钮
        // =====================================================================

        private void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
        {
            PlaybackOrder[] order =
            {
                PlaybackOrder.Sequential,
                PlaybackOrder.Random,
                PlaybackOrder.ListLoop,
                PlaybackOrder.TrackLoop,
                PlaybackOrder.TrackOnce
            };

            int index = Array.IndexOf(order, _orderResolver.Order);
            int next = index < 0 ? 0 : (index + 1) % order.Length;
            SetPlaybackOrder(order[next]);
        }




        private void ShowPlaybackOrderMenu(FrameworkElement target, Windows.Foundation.Point position)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Sequential, "\uE8FD", "顺序播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Random, "\uE8B1", "随机播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.ListLoop, "\uE8EE", "列表循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackLoop, "\uE8ED", "单曲循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackOnce, "\uE72A", "单曲播放");

            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = position,
                Placement = FlyoutPlacementMode.Top
            });
        }


        private void AddPlaybackOrderMenuItem(MenuFlyout flyout, PlaybackOrder order, string glyph, string label)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
                IsChecked = _orderResolver.Order == order,
                Tag = order
            };
            item.Click += PlaybackOrderMenuItem_Click;
            flyout.Items.Add(item);
        }


        private void PlaybackOrderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: PlaybackOrder order })
            {
                SetPlaybackOrder(order);
            }
        }


        private void SetPlaybackOrder(PlaybackOrder order, bool persist = true)
        {
            _orderResolver.Order = order;
            ApplyPlaybackOrderToPlayer();
            UpdatePlaybackOrderButtonUi();
            if (persist)
            {
                AppSettingsStore.Update(s => s.PlaybackOrder = order.ToString());
            }
        }


        private void ApplyPlaybackOrderToPlayer()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            player.IsLoopingEnabled = _orderResolver.Order == PlaybackOrder.TrackLoop;
        }


        private void UpdatePlaybackOrderButtonUi()
        {
            bool trackOnce = _orderResolver.Order == PlaybackOrder.TrackOnce;


            (string glyph, string name) = _orderResolver.Order switch
            {
                PlaybackOrder.Sequential => ("\uE8FD", "顺序播放"),
                PlaybackOrder.Random => ("\uE8B1", "随机播放"),
                PlaybackOrder.ListLoop => ("\uE8EE", "列表循环"),
                PlaybackOrder.TrackLoop => ("\uE8ED", "单曲循环"),
                PlaybackOrder.TrackOnce => ("\uE72A", "单曲播放"),
                _ => ("\uE8EE", "列表循环")
            };

            if (!trackOnce)
            {
            }

            ToolTipService.SetToolTip(PlaybackOrderButton, name + "（左键切换，右键选择）");
        }


        // =====================================================================
        // 主区域：播放列表 / 右侧 可拖分割线
        // =====================================================================

        /// <summary>播放列表区域尺寸变化：保证各列完整可见且不过度拉宽。
        /// 走帧合并（ScheduleFitColumns）：缩放时每个 tick 都全量改列宽就是「改变大小卡」的根源。</summary>
        private void PlaylistListBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            ScheduleFitColumns();
        }


        private double GetPlaylistColumnsViewportWidth()
        {
            double border = PlaylistListBorder.ActualWidth;
            if (border > 0)
            {
                // Header Margin="4,4,4,4"
                return Math.Max(0, border - 8);
            }

            if (PlaylistHeaderGrid.ActualWidth > 0)
            {
                return PlaylistHeaderGrid.ActualWidth;
            }

            return PlaylistColumn.ActualWidth > 0 ? Math.Max(0, PlaylistColumn.ActualWidth - 24) : 0;
        }


        // =====================================================================
        // 单元格悬停 1 秒后显示该字段完整信息
        // =====================================================================

        private void PlaylistCell_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            HideHoverTip();
            _hoverElement = element;
            _hoverTipText = ResolveCellDetailText(element);

            if (string.IsNullOrWhiteSpace(_hoverTipText) || _hoverTipTimer == null)
            {
                return;
            }

            _hoverTipTimer.Stop();
            _hoverTipTimer.Start();
        }


        private void PlaylistCell_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // 保留事件：移动时不重置 1 秒计时，避免轻微抖动导致提示永远不出
        }


        private void PlaylistCell_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverTipTimer?.Stop();
            HideHoverTip();
            _hoverElement = null;
            _hoverTipText = null;
        }


        private void HoverTipTimer_Tick(dynamic sender, dynamic e)
        {
            sender.Stop();
            if (_hoverElement == null || string.IsNullOrWhiteSpace(_hoverTipText))
            {
                return;
            }

            HideHoverTip();

            _activeHoverTip = new ToolTip
            {
                Content = _hoverTipText,
                Placement = FlyoutPlacementMode.Auto
            };
            ToolTipService.SetToolTip(_hoverElement, _activeHoverTip);
            _activeHoverTip.IsOpen = true;
        }


    }
}
