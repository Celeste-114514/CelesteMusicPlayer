using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CelesteMusicPlayer
{
    /// <summary>播放队列窗口：独立查看接下来的待播曲目，支持双击跳转、拖拽重排（复用主播放列表）。</summary>
    public sealed partial class PlayQueueWindow : Window
    {
        private readonly MainWindow _owner;
        private DispatcherQueueTimer? _refreshTimer;

        public PlayQueueWindow(MainWindow owner)
        {
            _owner = owner;
            _owner.QueueWindow = this;
            InitializeComponent();
            WindowIconHelper.Apply(this);

            ExtendsContentIntoTitleBar = true;
            Title = "播放队列";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 560));
            ConfigureTitleBarButtons();

            QueueListView.ItemsSource = _owner.UserPlaylist;
            RefreshQueueState();

            // 周期刷新当前项/计数（播放推进时队列前进）
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
            _refreshTimer.Tick += (_, _) => RefreshQueueState();
            _refreshTimer.Start();

            Closed += (_, _) =>
            {
                _refreshTimer?.Stop();
                if (_owner.QueueWindow == this)
                {
                    _owner.QueueWindow = null;
                }
            };
        }

        private void ConfigureTitleBarButtons()
        {
            try
            {
                AppWindowTitleBar titleBar = AppWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ExtendsContentIntoTitleBar = true;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("PlayQueueWindow.xaml.cs", caught); }
        }

        /// <summary>刷新：高亮当前播放项 + 更新"接下来 N 首"计数。</summary>
        private void RefreshQueueState()
        {
            try
            {
                int idx = _owner.UserPlaylistPlayingIndex;
                int count = _owner.UserPlaylist.Count;
                int upcoming = idx >= 0 && idx < count ? count - idx - 1 : 0;
                QueueCountText.Text = count.ToString() + " 首";
                BottomHintText.Text = upcoming > 0
                    ? "接下来 " + upcoming + " 首"
                    : "已是最后一首，无待播曲目";

                if (idx >= 0 && idx < count)
                {
                    var playing = _owner.UserPlaylist[idx];
                    bool needScroll = !ReferenceEquals(QueueListView.SelectedItem, playing);
                    QueueListView.SelectedItem = playing;
                    if (needScroll)
                    {
                        QueueListView.ScrollIntoView(playing);
                    }
                }
                else
                {
                    QueueListView.SelectedItem = null;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("PlayQueueWindow.xaml.cs", caught); }
        }

        private void QueueListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            PlaylistItem? target = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistItem
                ?? QueueListView.SelectedItem as PlaylistItem;
            if (target == null)
            {
                return;
            }

            int index = _owner.FindUserPlaylistIndexPublic(target.FilePath);
            if (index >= 0)
            {
                _owner.PlayUserPlaylistAtPublic(index);
            }
        }

        private void QueueListView_DragItemsCompleted(object sender, Microsoft.UI.Xaml.Controls.DragItemsCompletedEventArgs args)
        {
            // 拖拽重排由共享的 ObservableCollection 自动处理
            _owner.RefreshFromPlaylistReorder();
        }

        private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
        {
            _owner.ClearUserPlaylistPublicFromQueue();
        }
    }
}
