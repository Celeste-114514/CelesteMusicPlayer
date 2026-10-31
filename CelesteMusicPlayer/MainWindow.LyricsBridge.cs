using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using Color = Windows.UI.Color;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
            // 先把开关动作切回 UI 线程：这里要动 _desktopLyricsWindow 等宿主状态、还要刷新角标。
            // （窗口本身的创建/合成在桌面歌词自己的专用线程上，见 WebDesktopLyricsOverlay.EnsureLyricsThread。）
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

        /// <summary>
        /// 桌面歌词「样式导入 / 导出」的文件选择器。
        /// 必须回到主 UI 线程开：桌面歌词窗口在自己的专用线程上跑（那个线程归系统所有、不是 UI 线程），
        /// 直接在那里开 Picker 会因为缺 UI 线程 / STA 而挂住或抛异常。
        /// 返回 null 表示用户取消或失败。
        /// </summary>
        internal Task<string?> PickDesktopLyricStylePathAsync(bool save)
        {
            var tcs = new TaskCompletionSource<string?>();
            RunOnUi(async () =>
            {
                try
                {
                    if (save)
                    {
                        var picker = new Windows.Storage.Pickers.FileSavePicker();
                        picker.FileTypeChoices.Add("桌面歌词样式", new[] { ".json" });
                        picker.SuggestedFileName = "celeste-lyrics-style";
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, _mainWindowHwnd);
                        var file = await picker.PickSaveFileAsync();
                        tcs.TrySetResult(file?.Path);
                    }
                    else
                    {
                        var picker = new Windows.Storage.Pickers.FileOpenPicker();
                        picker.FileTypeFilter.Add(".json");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, _mainWindowHwnd);
                        var file = await picker.PickSingleFileAsync();
                        tcs.TrySetResult(file?.Path);
                    }
                }
                catch (Exception ex)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("DesktopLyrics.Picker", ex);
                    tcs.TrySetResult(null);
                }
            });
            return tcs.Task;
        }

        private void UpdateDesktopLyricsBadge()        {
            if (DesktopLyricsStateBadge == null)
            {
                return;
            }

            bool on = _desktopLyricsEnabled && _desktopLyricsWindow != null;
        }
    }
}
