using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放历史（Phase G3）：切歌 / 播完 / 停止时把「刚播完的曲目」写入 SQLite
    /// （<see cref="LibraryDb.RecordPlayback"/>，表 playback_history），并提供历史窗口入口。
    /// 记录点在播放入口（切歌前）与自然播完处理处，播放秒数取引擎实时位置。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>记录当前正在播放的曲目（切歌/播完/停止时调用）。completed=true 表示自然播完。</summary>
        private void RecordCurrentTrackHistory(bool completed)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_nowPlayingPath))
                {
                    return;
                }

                string title = Path.GetFileNameWithoutExtension(_nowPlayingPath);
                double seconds = EnginePositionValue > TimeSpan.Zero ? EnginePositionValue.TotalSeconds : 0;
                LibraryDb.RecordPlayback(_nowPlayingPath, title, seconds, completed);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.PlayHistory.cs", caught);
            }
        }

        /// <summary>打开播放历史窗口。</summary>
        private void OpenPlaybackHistoryWindow()
        {
            try
            {
                var win = new PlaybackHistoryWindow(this);
                win.Activate();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.PlayHistory.cs", caught);
            }
        }

        private void NavPlaybackHistoryButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            OpenPlaybackHistoryWindow();
        }

        /// <summary>播放一条历史记录：若文件仍在当前播放列表则按列表播放（保持上下文），
        /// 否则直接以引擎模式单独播放该文件。文件不存在时静默。</summary>
        internal void PlayHistoryEntry(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return;
                }

                int idx = FindUserPlaylistIndex(filePath);
                if (idx >= 0)
                {
                    PlayUserPlaylistAt(idx);
                    return;
                }

                var item = new PlaylistItem
                {
                    FilePath = filePath,
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Artist = "未知艺术家"
                };
                _ = PlayExtendedWithEngineAsync(item);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.PlayHistory.cs", caught);
            }
        }
    }
}
