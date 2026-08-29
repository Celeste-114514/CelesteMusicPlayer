using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放历史记录（Phase G3）：切歌 / 播完 / 停止时把「刚播完的曲目」写入 SQLite
    /// （<see cref="LibraryDb.RecordPlayback"/>，表 playback_history）。
    /// 展示层整合在「最近播放」分类面板（<see cref="MainWindow.Features.ApplyFavoritesOrRecentCategory"/>）：
    /// 不再用独立窗口，事件流水（时间/播放时长/是否播完）直接显示在最近播放列表里。
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
    }
}
