using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第十七刀（2026-10-31）：设置实时应用宿主。
/// 从壳（Settings/Misc2）转活的「设置变更即时生效」执行器：
/// 置顶开关、桌面歌词联动、列表表头列宽同步。
/// 网页设置页（WebSettingsHost）借用的行为入口住这里。
/// 媒体库自动重扫监听暂留壳（RescanLocalLibraryButton_Click 连着 Library.cs 深水区）。
/// </summary>
public sealed partial class MainWindow
{
        // ---- 置顶 / 桌面歌词设置联动 ----

        private void ApplyAlwaysOnTopFromSettings()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = AppSettingsStore.Load().AlwaysOnTop;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        internal void ApplyOverlayPreferenceFromSettings(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 「设置里改了开机打开迷你播放器」的联动：随迷你播放器一起移除（2026-10-19 删掉重写）

                if (settings.OpenDesktopLyricsOnStartup != _desktopLyricsEnabled)
                {
                    SetDesktopLyricsEnabled(settings.OpenDesktopLyricsOnStartup, persistPreference: false);
                }
            });
        }

        // ---- 表头列宽同步（设置/启动恢复共用）----

        private void SyncHeaderColumnsFromState()
        {
            var w = PlaylistColumnWidths.Instance;
            SetColumnWidthIfChanged(HeaderTitleCol, w.Title);
            SetColumnWidthIfChanged(HeaderArtistCol, w.Artist);
            SetColumnWidthIfChanged(HeaderAlbumCol, w.Album);
            SetColumnWidthIfChanged(HeaderYearCol, w.Year);
            SetColumnWidthIfChanged(HeaderDurationCol, w.Duration);
        }
    }
