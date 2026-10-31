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

        /// <summary>
        /// ⚠ 桌面歌词**不能**复用主界面的 CoreWebView2Environment：它在自己的专用线程上，
        /// 跨线程碰这个对象会让 CLR 报 0x80131506 直接杀进程。它自己按同样的参数另建一个
        /// （同一份用户数据目录，WebView2 会复用同一个浏览器进程），见 WebDesktopLyricsOverlay.Initialize。
        /// </summary>

        // Activate is still required to load the legacy playback owner, but its
        // first native frame must never become a visible second window.
        internal void CloakLegacyOwnerBeforeActivation()
        {
            const int DwmwaCloak = 13;
            int cloak = 1;
            nint hwnd = NativeHandle;
            int result = DwmSetWindowAttributeInt(hwnd, DwmwaCloak, ref cloak, sizeof(int));
            if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
        }

        internal void WebTransportAction(string action)
        {
            switch (action)
            {
                case "seekBack": SeekBySeconds(-5); break;
                case "seekForward": SeekBySeconds(5); break;
                case "cycleOrder": CyclePlaybackOrderPublic(); break;
                case "desktopLyrics": SetDesktopLyricsEnabled(!_desktopLyricsEnabled); break;
                case "toggleTaskbarPlayer": ToggleTaskbarPlayer(); break;
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


        internal void WebPlayTrack(PlaylistItem item)
        {
            if (item.SourceTrackId.Length == 0 && !_playlist.Contains(item)) throw new InvalidOperationException("曲库已变化，请刷新后重试。");
            StartupLog.Write("[WebShell] PlayTrack id=" + WebTrackIdentity.Of(item) + " output=" + AppSettingsStore.Load().OutputMode);
            PlayPlaylistItem(item);
        }

        internal void WebPlayAll(IReadOnlyList<PlaylistItem> tracks)
        {
            if (tracks.Count == 0 || tracks.Any(item => item.SourceTrackId.Length == 0 && !_playlist.Contains(item)))
                throw new InvalidOperationException("曲库已变化，请刷新后重试。");
            _queue.Items.Clear();
            _queue.Index = -1;
            foreach (var track in tracks.Where(t => t.SourceTrackId.Length > 0)) NetworkLibraryStore.Pin(track.SourceTrackId);
            AddSongsToUserPlaylist(tracks);
            PlayUserPlaylistAt(0);
        }

        internal void NotifyWebHomeChanged()
        {
            if (_webShellWindow == null) return;
            DispatcherQueue.TryEnqueue(() => _webShellWindow?.NotifyHomeChanged());
        }


    }
}
