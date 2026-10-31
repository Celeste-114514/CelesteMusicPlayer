using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十七刀（2026-10-31）：播放/播放列表杂项宿主。
/// 从壳 <c>MainWindow.Misc.cs</c> / <c>EqDsp.cs</c> / <c>Features.cs</c> 搬出：
/// 按路径回填当前索引、移入回收站、专辑墙对应的专辑集合、睡眠定时器到点判定。
/// 依赖（播放列表集合与索引字段、StopForSleep、SHFileOperation 声明）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{

        // W13 第二十七刀：路径回填 / 回收站

        private void UpdateCurrentIndexByPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentIndex = i;
                    return;
                }
            }
        }

        private static bool MoveToRecycleBin(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x40 | 0x10 | 0x04 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                return SHFileOperation(ref op) == 0;
            }
            catch
            {
                return false;
            }
        }

        private ObservableCollection<AlbumEntry> GetAlbumCollectionForGrid(GridView grid)
            => ReferenceEquals(grid, AlbumGridView) ? _albums : _artistAlbums;

        /// <summary>播放结束拦截：返回 true 表示已按睡眠定时器停止，调用方不应继续切歌。</summary>
        private bool ConsumeSleepStopIfDue()
        {
            if (_sleepMode == SleepStopMode.AfterTrack)
            {
                StopForSleep("睡眠定时器：当前曲目已播完，播放已停止");
                return true;
            }

            if (_sleepMode == SleepStopMode.AfterTracks)
            {
                _sleepTracksRemaining--;
                if (_sleepTracksRemaining <= 0)
                {
                    StopForSleep("睡眠定时器：指定曲目数已播完，播放已停止");
                    return true;
                }
            }

            return false;
        }

        // W13 第二十八刀：封面背景铺/撤

        private void ClearAlbumArtBackground()
        {

            // 压暗层跟着归零：只清封面图不清 Scrim 的话，53% 深色 Border 会残留继续压暗窗口
        }

        private async Task ApplyAlbumArtBackgroundAsync(byte[]? coverBytes, string forPath)
        {
            if (AlbumArtBackgroundImage == null)
            {
                return;
            }

            // 经典不透明界面：封面背景与它完全无关，切歌时不允许重新铺上
            // 经典 / 极客（不透明界面）与封面背景完全无关：切歌换封面也不许把背景图铺回来
            if (IsOpaqueUiStyleActive())
            {
                ClearAlbumArtBackground();
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                ClearAlbumArtBackground();
                return;
            }

            // 设置里选了自定义背景图片：自定义优先，封面背景让位（清掉 + 压暗层归零）。
            if (!string.IsNullOrWhiteSpace(settings.CustomBackgroundPath)
                && System.IO.File.Exists(settings.CustomBackgroundPath))
            {
                ClearAlbumArtBackground();
                return;
            }

            if (coverBytes == null || coverBytes.Length == 0)
            {
                ClearAlbumArtBackground();
                return;
            }

            // 这一层是「整个主窗口」的背景（XAML 里的 AlbumArtBackgroundImage），不是播放页内部的图。
            // 除「经典」外的所有布局（水面 / 黑胶 / 剧场 / 歌词 / 镜像 / 居中）都固定带一层轻模糊
            // —— 用户明确认可这个观感（"原来那套水面布局的模糊效果就挺好的"）；
            // 「经典」严格跟随设置里的「背景高斯模糊」开关，你关掉它就是全程序都不糊。
            // ⚠️ 曾经试过改成"播放页单独加一层背景图"，用户反馈像"在主程序上加了一块板子"，
            //    已回退 —— 背景只有这一张整窗图，别再另起一层。
            // 布局名直接读设置：求值 _nowPlayingLayoutName 的原生播放页已下线（W13），这里只要那个值。
            bool immersiveLayout = !string.Equals(AppSettingsStore.Load().NowPlayingLayout, "Classic", StringComparison.OrdinalIgnoreCase);
            int blurRadius = immersiveLayout
                ? 1
                : (settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0);
            byte[]? blurred = await Task.Run(() =>
                blurRadius > 0
                    ? AlbumArtBackground.CreateHeavilyBlurredPng(coverBytes, blurRadius: blurRadius)
                    : coverBytes);
            if (_nowPlayingPath != forPath || blurred == null || blurred.Length == 0)
            {
                return;
            }

            BitmapImage? image = await CreateBitmapFromBytesAsync(blurred);
            if (_nowPlayingPath != forPath)
            {
                return;
            }

        }

        /// <summary>波形(Poweramp)：波形条已播主题色/未播灰色，当前位置竖线。</summary>
        private void DrawWaveformStyle(Canvas canvas, double w, double h, double ratio, Color accent)
        {
            if (_waveformData == null || _waveformData.Length == 0)
            {
                // 波形未就绪:只画一条中性细线(加载完成后再显示真实波形,不闪占位)
                var idleLine = new Shapes.Rectangle
                {
                    Width = w,
                    Height = 2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128))
                };
                Canvas.SetTop(idleLine, (h - 2) / 2);
                canvas.Children.Add(idleLine);
                return;
            }

            int n = _waveformData.Length;
            double barW = w / n;
            var unplayedBrush = new SolidColorBrush(Color.FromArgb(70, 150, 150, 150));
            double playedEdge = w * ratio;
            Color light = Lighten(accent, 0.55);

            // 已播部分配色：Solid = 整段统一色（白色度可调），Gradient = 左端主题色 → 右端浅色（原行为）。
            AppSettingsState waveSettings = AppSettingsStore.Load();
            bool solidMode = waveSettings.WaveColorMode == "Solid";
            // 纯色模式所有柱子同色，只建一个画刷复用（避免几百根柱子各建一个画刷）
            Brush? solidBrush = solidMode
                ? new SolidColorBrush(Lighten(accent, waveSettings.WaveSolidWhiteness))
                : null;

            for (int i = 0; i < n; i++)
            {
                double bh = Math.Max(2, _waveformData[i] * h * 0.95);
                var rect = new Shapes.Rectangle
                {
                    // 柱间留白（2026-10-14 用户要求"每条之间稍微宽一点点"）：原来是 1px 缝。
                    // ⚠ 别改太大：进度条总宽固定后 barW 本来就不大，缝一宽柱子就被压成细线
                    // （试过 2px，柱子全变成 1px 细丝）。
                    // 2026-10-16 用户反馈"每根竖线太细"：桶数从 180 降到 120（barW 由约 4 涨到 6），
                    // 缝同步从 1.5 收到 1.2，柱子净宽约 4.8px。下限提到 1.5 防止窄窗口下退化成细丝。
                    Width = Math.Max(1.5, barW - 1.2),
                    Height = bh,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(rect, i * barW);
                Canvas.SetTop(rect, (h - bh) / 2);

                double centerX = (i + 0.5) * barW;
                if (centerX <= playedEdge)
                {
                    if (solidBrush != null)
                    {
                        // 纯色：整段一个颜色，不随播放位置变化
                        rect.Fill = solidBrush;
                    }
                    else
                    {
                        // 已播部分:主题色(两端浅色渐变)
                        double t = centerX / Math.Max(1, playedEdge);
                        rect.Fill = new SolidColorBrush(Color.FromArgb(
                            255,
                            (byte)(accent.R + (light.R - accent.R) * t),
                            (byte)(accent.G + (light.G - accent.G) * t),
                            (byte)(accent.B + (light.B - accent.B) * t)));
                    }
                }
                else
                {
                    rect.Fill = unplayedBrush;
                }

                canvas.Children.Add(rect);
            }

            // 当前位置细线(echo next 风格:波形上一条细竖线)
            var line = new Shapes.Rectangle
            {
                Width = 2,
                Height = h * 0.96,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(Colors.White)
            };
            Canvas.SetLeft(line, Math.Clamp(playedEdge - 1, 0, Math.Max(0, w - 2)));
            Canvas.SetTop(line, (h - h * 0.96) / 2);
            canvas.Children.Add(line);
        }

        /// <summary>读取并显示当前曲目的音频格式信息（采样率/位深/码率/声道）。</summary>
        private async System.Threading.Tasks.Task UpdateAudioInfoTextAsync(string path)
        {
            try
            {
                string? info = await System.Threading.Tasks.Task.Run(() => AudioInfoFormatter.Format(path));
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (NowPlayingAudioInfoText != null && string.Equals(_nowPlayingPath, path, System.StringComparison.OrdinalIgnoreCase))
                    {
                    }
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        /// <summary>引擎开播淡入（约 320ms 渐入到当前音量）。</summary>
        private async Task FadeInEngineAsync()
        {
            try
            {
                // 独占/ASIO（bit-perfect）：设备主音量由用户拖动音量条设置，切歌不重置（避免拉回 100%）。
                if (IsHiFiModeSelected())
                {
                    return;
                }

                // 共享用「用户持久化的真实音量」（防切模式后滑块被独占锁成 100% 导致巨声）。
                // 共享引擎音量 = NAudio 软件增益 0..1。
                double target = Math.Clamp(AppSettingsStore.Load().Volume, 0, 100) / 100.0;
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * i / steps);
                    await Task.Delay(40);
                }

                _audioEngine?.SetVolume(target);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private async Task MaybeAutoDownloadExtrasAsync(PlaylistItem item, List<LyricLine> existingLyrics, byte[]? coverBytes)
        {
            AppSettingsState settings = AppSettingsStore.Load();
            bool tagsFull = !string.IsNullOrWhiteSpace(item.Title)
                && !string.Equals(item.Title, Path.GetFileNameWithoutExtension(item.FilePath), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Artist)
                && !string.Equals(item.Artist, "未知艺术家", StringComparison.Ordinal);

            if (settings.AutoDownloadOnlyWhenTagFull && !tagsFull)
            {
                return;
            }

            if (settings.AutoDownloadLyrics && existingLyrics.Count == 0)
            {
                string? lyricPath = await OnlineMusicApi.SearchAndDownloadLyricAsync(item.Title, item.Artist, item.FilePath);
                if (lyricPath != null && string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
                    BuildLyricsUi(lyrics);
                }
            }

            if (settings.AutoDownloadCover && (coverBytes == null || coverBytes.Length == 0))
            {
                bool ok = await OnlineMusicApi.DownloadAndEmbedCoverAsync(item.Title, item.Artist, item.FilePath);
                if (ok)
                {
                    InvalidateCoverCache(item.FilePath);
                }

                if (ok && string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    await UpdateNowPlayingPanelAsync(item);
                }
            }
        }
    }
