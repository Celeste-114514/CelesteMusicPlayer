using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    // Transitional adapter: every audio command uses the existing owner session.
    public sealed partial class MainWindow
    {
        private WebShellWindow? _webShellWindow;
        private bool _webShellOwnerClosing;

        internal void WebTransportAction(string action)
        {
            switch (action)
            {
                case "seekBack": SeekBySeconds(-5); break;
                case "seekForward": SeekBySeconds(5); break;
                case "cycleOrder": CyclePlaybackOrderPublic(); break;
                case "desktopLyrics": SetDesktopLyricsEnabled(!_desktopLyricsEnabled); break;
                case "unlockLyrics": _desktopLyricsWindow?.SetLocked(false); break;
                default: throw new ArgumentException("未知的播放栏操作。");
            }
        }

        internal void WebSetPlaybackOrder(string value)
        {
            if (!Enum.TryParse<PlaybackOrder>(value, out var order) || !Enum.IsDefined(order))
                throw new ArgumentException("未知的播放顺序。");
            SetPlaybackOrder(order);
        }

        internal void OpenWebShell()
        {
            if (_webShellWindow != null) { EnsureWebShellVisible(); return; }
            _webShellWindow = new WebShellWindow(this);
            _webShellWindow.Closed += (_, _) => { _webShellWindow = null; if (!_webShellOwnerClosing) ExitApplication(); };
            Closed -= CloseWebShellWithOwner;
            Closed += CloseWebShellWithOwner;
            _webShellWindow.Activate();
            AppWindow.Hide();
        }

        /// <summary>
        /// 主界面唯一的"显示/恢复"实现：没有就建，最小化就还原，隐藏了就 Show，最后拉前台。
        /// 必须在 UI 线程调用。以前各入口各写一套（这里 Show+Activate、迷你那边直接 Activate 主窗口），
        /// 结果是双击迷你播放器只会激活那个已经隐藏的 WinUI 宿主，用户什么都看不到。
        /// </summary>
        private void EnsureWebShellVisible()
        {
            if (_webShellWindow == null)
            {
                OpenWebShell();
                return;
            }

            // Show() 不会把最小化的窗口还原，得先动 Presenter。
            if (_webShellWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter
                && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            _webShellWindow.AppWindow.Show();
            _webShellWindow.Activate();
        }

        /// <summary>
        /// 把主界面叫到前台。托盘「显示主界面」、迷你播放器双击、协议激活、更新气泡点击都走这里，
        /// 不要再直接调 MainWindow.Activate() —— 那个窗口是隐藏的宿主，激活它等于没反应。
        /// </summary>
        internal void RestoreMainWindow()
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                try { EnsureWebShellVisible(); }
                catch (Exception ex) { StartupLog.WriteException("MainWindow.RestoreMainWindow", ex); }
                return;
            }

            DispatcherQueue.TryEnqueue(RestoreMainWindow);
        }

        private void CloseWebShellWithOwner(object sender, WindowEventArgs args)
        {
            Closed -= CloseWebShellWithOwner;
            _webShellOwnerClosing = true;
            _webShellWindow?.Close();
        }

        private void WebShellButton_Click(object sender, RoutedEventArgs args) => OpenWebShell();

        /// <summary>
        /// 把网页外壳拉到前台并跳到指定页（托盘菜单的「设置 / 发现新版本」用）。
        /// 页面名必须是网页里真实存在的路由，宿主不认识也不校验 —— 传错只会停在主页。
        /// </summary>
        internal void ShowWebPage(string page)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    EnsureWebShellVisible();
                    _webShellWindow?.NavigateTo(page);
                }
                catch (Exception ex) { StartupLog.WriteException("MainWindow.ShowWebPage", ex); }
            });
        }

        internal static string WebTrackId(PlaylistItem item)
        {
            if (item.SourceTrackId.Length > 0) return item.SourceTrackId;
            string key = item.FilePath.ToUpperInvariant() + "\n" + item.StartTimeSeconds.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        }

        internal void WebPlayTrack(PlaylistItem item)
        {
            if (item.SourceTrackId.Length == 0 && !_playlist.Contains(item)) throw new InvalidOperationException("曲库已变化，请刷新后重试。");
            StartupLog.Write("[WebShell] PlayTrack id=" + WebTrackId(item) + " output=" + AppSettingsStore.Load().OutputMode);
            PlayPlaylistItem(item);
        }

        internal void WebPlayAll(IReadOnlyList<PlaylistItem> tracks)
        {
            if (tracks.Count == 0 || tracks.Any(item => item.SourceTrackId.Length == 0 && !_playlist.Contains(item)))
                throw new InvalidOperationException("曲库已变化，请刷新后重试。");
            _userPlaylist.Clear();
            _userPlaylistIndex = -1;
            foreach (var track in tracks.Where(t => t.SourceTrackId.Length > 0)) NetworkLibraryStore.Pin(track.SourceTrackId);
            AddSongsToUserPlaylist(tracks);
            PlayUserPlaylistAt(0);
        }

        internal void NotifyWebHomeChanged()
        {
            if (_webShellWindow == null) return;
            DispatcherQueue.TryEnqueue(() => _webShellWindow?.NotifyHomeChanged());
        }

        internal object WebPlaybackState()
        {
            var item = GetCurrentPlayingItem();
            bool active = IsEngineActiveNow;
            var player = GetPlayer();
            double duration = active ? EngineDurationValue.TotalSeconds : player?.PlaybackSession.NaturalDuration.TotalSeconds ?? item?.Duration.TotalSeconds ?? 0;
            double position = active ? EnginePositionValue.TotalSeconds : player?.PlaybackSession.Position.TotalSeconds ?? 0;
            if (duration <= 0) duration = item?.Duration.TotalSeconds ?? 0;
            if (!active && player?.Source == null)
            {
                duration = item?.Duration.TotalSeconds ?? 0;
                position = ProgressSlider?.Value ?? 0;
            }
            return new
            {
                trackId = item == null ? "" : WebTrackId(item),
                title = item?.Title ?? "未在播放", artist = item?.Artist ?? "", album = item?.Album ?? "",
                duration = Math.Max(0, duration), position = Math.Max(0, position),
                playing = IsPlaybackActuallyPlaying(), volume = GetVolumePublic(),
                volumeEnabled = !IsHiFiModeSelected() || IsHiFiSoftVolumeUiUnlocked(),
                favorite = IsCurrentFavoritePublic(),
                playbackOrder = _orderResolver.Order.ToString(),
                desktopLyrics = _desktopLyricsEnabled,
                desktopLyricsLocked = _desktopLyricsWindow?.IsLocked == true,
                coverUrl = item == null ? "" : "https://celeste-cover.internal/cover/" + WebTrackId(item),
                quality = item?.FormatInfoLine ?? "",
                source = item == null ? "" : item.IsRemote ? "NETWORK" : "LOCAL",
                trackNumber = item?.Track ?? 0,
                shuffle = _orderResolver.Order == PlaybackOrder.Random,
                repeat = _orderResolver.Order is PlaybackOrder.ListLoop or PlaybackOrder.TrackLoop,
                outputMode = _audioEngine?.CurrentOutputMode?.ToString() ?? "",
                outputDevice = _audioEngine?.OutputDeviceName ?? "",
                busy = _startPlaybackInFlight,
                error = _audioEngine?.LastError ?? ""
            };
        }
    }
}
