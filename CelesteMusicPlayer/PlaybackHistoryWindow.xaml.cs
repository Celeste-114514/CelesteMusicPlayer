using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放历史窗口：查看最近播放（SQLite playback_history，时间倒序），
    /// 双击条目重新播放（在当前播放列表中则按列表上下文播放），支持清空。
    /// </summary>
    public sealed partial class PlaybackHistoryWindow : Window
    {
        private readonly MainWindow _owner;

        /// <summary>列表行包装（绑定友好）。</summary>
        public sealed class HistoryRow
        {
            public string FilePath { get; set; } = string.Empty;
            public string DisplayTitle { get; set; } = string.Empty;
            public string DisplayTime { get; set; } = string.Empty;
            public string DisplayDuration { get; set; } = string.Empty;
            public string StatusText { get; set; } = string.Empty;
        }

        public PlaybackHistoryWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            WindowIconHelper.Apply(this);

            ExtendsContentIntoTitleBar = true;
            Title = "播放历史";
            try
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 560));
            }
            catch
            {
            }

            RefreshList();
        }

        private void RefreshList()
        {
            try
            {
                List<HistoryRow> rows = new List<HistoryRow>();
                foreach (LibraryDb.PlaybackHistoryEntry e in LibraryDb.LoadPlaybackHistory(200))
                {
                    DateTime local = e.PlayedAtUtc == DateTime.MinValue
                        ? DateTime.MinValue
                        : e.PlayedAtUtc.ToLocalTime();
                    string time = local == DateTime.MinValue
                        ? "—"
                        : local.ToString("MM-dd HH:mm");
                    string dur = e.PlayedSeconds < 1
                        ? "—"
                        : e.PlayedSeconds < 60
                            ? (int)e.PlayedSeconds + " 秒"
                            : TimeSpan.FromSeconds(e.PlayedSeconds).ToString(@"m\:ss");
                    rows.Add(new HistoryRow
                    {
                        FilePath = e.FilePath,
                        DisplayTitle = string.IsNullOrWhiteSpace(e.Title) ? System.IO.Path.GetFileNameWithoutExtension(e.FilePath) : e.Title,
                        DisplayTime = time,
                        DisplayDuration = "播放 " + dur,
                        StatusText = e.Completed ? "播完" : "未播完"
                    });
                }

                HistoryListView.ItemsSource = rows;
                HistoryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("PlaybackHistoryWindow.cs", caught);
            }
        }

        private void HistoryListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            try
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryRow row)
                {
                    _owner.PlayHistoryEntry(row.FilePath);
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("PlaybackHistoryWindow.cs", caught);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshList();

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LibraryDb.ClearPlaybackHistory();
                RefreshList();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("PlaybackHistoryWindow.cs", caught);
            }
        }
    }
}
