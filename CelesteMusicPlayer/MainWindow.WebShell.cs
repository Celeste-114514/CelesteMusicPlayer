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


        // ⚠ 弹出网页窗口（托盘菜单 / 任务栏播放条）**一律各建各的 WebView2 环境**。
        // 2026-10-28 22:43/22:53 实测：托盘菜单复用主界面控件的环境后界面画得出来，
        // 但鼠标点击全部进不了网页（连 pointerdown 探针都没有）；改回自己建就正常。
        // 判断是 WinUI 控件走 composition hosting，拿它的环境建窗口版 controller 输入链路对不上。

        // Activate is still required to load the legacy playback owner, but its
        // first native frame must never become a visible second window.
        internal void CloakLegacyOwnerBeforeActivation()
        {
            const int DwmwaCloak = 13;
            int cloak = 1;
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
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

        private void WebShellButton_Click(object sender, RoutedEventArgs args) => OpenWebShell();

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


    }
}
