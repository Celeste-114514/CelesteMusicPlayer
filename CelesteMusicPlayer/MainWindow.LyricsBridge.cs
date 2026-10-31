using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using Color = Windows.UI.Color;
using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 歌词数据桥 + 桌面歌词开关（2026-10-31 从 MainWindow.Lyrics.cs 壳整族搬来）。
    /// 网页歌词、迷你播放器扩展区、托盘菜单都靠这里的 Public 接口取数；
    /// 桌面歌词是 WebView2 独立窗口（WebDesktopLyricsOverlay），是活功能。
    /// 状态字段（_lyricLines / _currentLyricIndex / _desktopLyricsWindow / _desktopLyricsEnabled）
    /// 都住在 MainWindow.xaml.cs（活文件），这里只写行为。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal string GetCurrentLyricTextPublic()
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricLines.Count)
            {
                return _lyricLines[_currentLyricIndex].Text;
            }

            return string.Empty;
        }

        /// <summary>
        /// 整首歌的歌词行（迷你播放器扩展区用，只读）。切歌时 <see cref="_lyricLines"/>
        /// 整个换引用，调用方按引用比对即可；不会原地增删，共享安全。
        /// </summary>
        internal IReadOnlyList<LyricLine> GetLyricLinesPublic() => _lyricLines;

        /// <summary>当前高亮歌词行下标（-1 = 没有歌词或没开始播）。</summary>
        internal int GetCurrentLyricIndexPublic() => _currentLyricIndex;

        /// <summary>托盘菜单用：桌面歌词当前是否开着（真实状态，不猜）。</summary>
        internal bool DesktopLyricsEnabledPublic => _desktopLyricsEnabled;

        /// <summary>
        /// 托盘菜单用：翻转桌面歌词。走的是同一个开关（和快捷键一致），
        /// 不在这里改那个窗口的任何实现 —— 那个窗口用户要自己重构。
        /// </summary>
        internal void ToggleDesktopLyricsPublic() => SetDesktopLyricsEnabled(!_desktopLyricsEnabled);
        // 桌面歌词窗口管理族（2026-10-31 从 MainWindow.Lyrics.cs 壳整族搬来）：WebDesktopLyricsOverlay 是 WebView2 独立窗口，活功能；依赖的字段都在 xaml.cs / Playback3.cs（活）。

        private void SetDesktopLyricsEnabled(bool enabled, bool persistPreference = true)
        {
            // ⚠ WebView2 的网页消息回调不在 UI 线程上（WebShellWindow.OnMessage）。
            // 直接在这里构造桌面歌词会炸：Compositor 要求当前线程有 DispatcherQueue，
            // 报「拒绝访问 / The caller must initialize DispatcherQueue on this thread」。
            // 所以先把整个开关动作切回 UI 线程，再建窗口。
            // 另注：这条守卫解决的是线程，不是根因。桌面歌词目前仍会在 Compositor 处报错，
            // 根因是 NativeDispatcherQueue.cs 用手写 Win32 泵替掉了 Windows App SDK 的
            // DispatcherQueue —— 进程里没有「真」队列，Windows.UI.Composition 起不来。
            // 单独排期修，见 collab 战报「桌面歌词」一节。
            var ui = _uiQueue;
            if (ui != null && !ui.HasThreadAccess)
            {
                ui.TryEnqueue(() => SetDesktopLyricsEnabled(enabled, persistPreference));
                return;
            }

            _desktopLyricsEnabled = enabled;
            if (_desktopLyricsEnabled)
            {
                EnsureDesktopLyricsWindow();
                _desktopLyricsWindow!.SetLyrics(_lyricLines);
                MediaPlayer? player = GetPlayer();
                if (player?.Source != null)
                {
                    _desktopLyricsWindow.Sync(player.PlaybackSession.Position);
                }

                _desktopLyricsWindow.Show();
            }
            else
            {
                // 用户关闭显示开关前保存位置
                PersistDesktopLyricPosition();
                if (_desktopLyricsWindow != null)
                {
                    WebDesktopLyricsOverlay closing = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    closing.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    closing.Close();
                    closing.Dispose();
                }
            }

            if (persistPreference)
            {
                AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = _desktopLyricsEnabled);
            }

            UpdateDesktopLyricsBadge();
        }

        private void EnsureDesktopLyricsWindow()
        {
            if (_desktopLyricsWindow != null)
            {
                return;
            }

            _desktopLyricsWindow = new WebDesktopLyricsOverlay(this)
            {
                // 引擎路径（HiFi 独占 / ASIO / DSD）下 MediaPlayer.PlaybackSession.Position 不是真实播放位置，
                // 必须改读 _audioEngine.Position。否则歌词窗口内部 50ms 定时器拿到的是错误位置，
                // 只能靠外部 200ms 的 Sync 强制推帧 → 实际只有 5fps，进度条一跳一跳地闪。
                PositionProvider = () =>
                    _usingEnginePlayback && _audioEngine != null
                        ? TimeSpan.FromSeconds(_audioEngine.Position.TotalSeconds * TempoScale())
                        : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero)
            };
            _desktopLyricsWindow.ClosedByUser += OnDesktopLyricsClosedByUser;
            _desktopLyricsWindow.ApplySettings(AppSettingsStore.Load());

            // 记忆位置：上次拖到哪，下次仍放到那（否则 PlaceInitially 居中贴底）
            AppSettingsState saved = AppSettingsStore.Load();
            if (saved.DesktopLyricPosX != int.MinValue && saved.DesktopLyricPosY != int.MinValue)
            {
                _desktopLyricsWindow.SetSavedPosition(saved.DesktopLyricPosX, saved.DesktopLyricPosY);
            }
        }

        private void OnDesktopLyricsClosedByUser()
        {
            // 关闭前保存位置，下次打开记忆
            PersistDesktopLyricPosition();
            // 可能从桌面歌词关闭按钮触发；确保主界面 badge 回到 off
            if (_desktopLyricsWindow != null)
            {
                _desktopLyricsWindow.ClosedByUser -= OnDesktopLyricsClosedByUser;
                _desktopLyricsWindow.PositionProvider = null;
                _desktopLyricsWindow.Dispose();
            }

            _desktopLyricsWindow = null;
            _desktopLyricsEnabled = false;
            AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = false);
            DispatcherQueue.TryEnqueue(UpdateDesktopLyricsBadge);
        }

        private void UpdateDesktopLyricsBadge()
        {
            if (DesktopLyricsStateBadge == null)
            {
                return;
            }

            bool on = _desktopLyricsEnabled && _desktopLyricsWindow != null;
            DesktopLyricsStateBadge.Text = on ? "on" : "off";
            DesktopLyricsStateBadge.Foreground = on
                ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
                : new SolidColorBrush(Color.FromArgb(255, 176, 176, 176));
        }
    }
}
