using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对设置服务的宿主实现（W13）。
    ///
    /// 读写、校验、对话框、凭据、导入导出都在 <see cref="WebSettingsService"/>；
    /// 这里只保留四件只能由宿主做的事：回 UI 线程、把改动落到真实服务、重启目录监视、给曲库快照。
    ///
    /// ⚠ 别再往这里加「读某个字段」之类的转发：那会把业务重新拉回 MainWindow，
    /// 搬出去就白搬了。确实需要的公共能力，放进 <see cref="ISettingsHost"/> 之前先问一句
    /// 「服务自己是不是也能做」。
    /// </summary>
    public sealed partial class MainWindow : ISettingsHost
    {
        private WebSettingsService? _webSettingsService;

        internal WebSettingsService Settings =>
            LazyInitializer.EnsureInitialized(ref _webSettingsService, () => new WebSettingsService(this, Hotkeys, Update));

        IReadOnlyList<PlaylistItem> ISettingsHost.LibraryTracks => LibraryTracks;

        Task ISettingsHost.RunOnUiAsync(Action action) => RunOnUiThreadAsync(action);

        Task ISettingsHost.RestartLibraryWatchAsync() => RunOnUiThreadAsync(RestartLibraryWatchFromSettings);

        /// <summary>把一段 UI 线程动作包装成可观await的任务。WinAppSDK 的扩展方法靠不住时这里是可回滚点。</summary>
        private Task RunOnUiThreadAsync(Action action)
        {
            var completion = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(() =>
            {
                try { action(); completion.TrySetResult(); }
                catch (Exception caught) { completion.TrySetException(caught); }
            });
            return completion.Task;
        }

        /// <summary>把改动接到真实服务上。没有对应服务的字段就只在 note 里说明，不做假动作。</summary>
        async Task ISettingsHost.ApplyChangesAsync(string[] ids)
        {
            AppSettingsState state = AppSettingsStore.Load();
            if (ids.Contains("AutoRun")) AutoRunHelper.Apply(state.AutoRun);

            await RunOnUiThreadAsync(() =>
            {
                try
                {
                    if (ids.Contains("AlwaysOnTop")) ApplyAlwaysOnTopFromSettings();
                    if (ids.Contains("TaskbarPlayerEnabled")) ApplyTaskbarPlayerPreference();
                    if (ids.Contains("EnableGlobalHotkeys") || ids.Contains("CustomHotkeys")) RestartGlobalHotkeysFromSettings();
                    if (ids.Contains("EnableSmtc")) MediaSession.ConfigureFromSettings();
                    if (ids.Contains("AutoUpdateLibrary")) RestartLibraryWatchFromSettings();
                    if (ids.Contains("OpenDesktopLyricsOnStartup")) ApplyOverlayPreferenceFromSettings(state);

                    if (ids.Any(id => id.StartsWith("DesktopLyric", StringComparison.Ordinal)))
                        _desktopLyricsWindow?.ApplySettings(state);

                    if (ids.Contains("HiFiSoftwareVolume") && _audioEngine != null)
                        _audioEngine.SoftwareVolumeEnabled = state.HiFiSoftwareVolume;

                    if (ids.Contains("PlaybackRate") || ids.Contains("PlaybackRatePreservePitch"))
                        _audioEngine?.SetPlaybackSpeed(state.PlaybackRate, state.PlaybackRatePreservePitch);

                    if (ids.Contains("WebDavUrl") || ids.Contains("WebDavDisplayName")) RefreshWebDavNavEntry();

                    // 外观/列表相关：走原生那套「设置页变更后即时生效」，不另写一套应用逻辑
                    if (ids.Any(id => id is "CelesteThemeName" or "AccentSource" or "CustomAccentColor"
                                          or "EnableBackground" or "AlbumCoverAsBackground"
                                          or "BackgroundGaussBlur" or "GaussBlurRadius" or "BackgroundPreset"
                                          or "BackgroundPresetMotion" or "CustomBackgroundPath" or "NavEmojiIcons"
                                          or "NowPlayingLayout" or "ShowNavFavorites" or "ShowNavRecent"
                                          or "ShowNavGenre" or "ShowNavYear"))
                        ApplySettingsLive(state);

                }
                catch (Exception caught) { StartupLog.WriteException("MainWindow.SettingsHost.cs", caught); }
            }).ConfigureAwait(true);
        }
    }
}
