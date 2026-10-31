using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：窗口背景媒体：自定义背景图/视频、背景动效、主题色联动。</summary>
public sealed partial class MainWindow
{
        private Windows.Media.Playback.MediaPlayer? _backgroundVideoPlayer;
        private string? _backgroundVideoPath;
        private int _customBackgroundRequest;
        private bool _backgroundVideoPausedByMinimize;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _backgroundMotionStoryboard;
        private CompositeTransform? _backgroundMotionTransform;
        private static readonly string[] VideoBackgroundExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".ts", ".mpg", ".mpeg", ".flv"
        };

        // W13 第三十四刀：背景媒体

        /// <summary>把视频铺满窗口当背景：循环播放、强制静音、不进系统媒体浮层(SMTC)。</summary>
        private void ApplyCustomBackgroundVideo(string path, int request)
        {
            if (request != _customBackgroundRequest) return;

            // 视频与图片互斥：图片层退场
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;

            try
            {
                if (_backgroundVideoPlayer == null)
                {
                    _backgroundVideoPlayer = new Windows.Media.Playback.MediaPlayer
                    {
                        IsLoopingEnabled = true,
                        IsMuted = true,
                        AutoPlay = false,
                    };
                    // 关键：不让这个播放器接管系统媒体浮层(SMTC)，否则背景视频会顶掉正歌的"正在播放"卡片
                    _backgroundVideoPlayer.CommandManager.IsEnabled = false;
                    CustomBackgroundVideo.SetMediaPlayer(_backgroundVideoPlayer);

                    // 最小化省电：窗口最小化时暂停视频解码，还原时继续
                    AppWindow.Changed -= OnAppWindowChangedForBackgroundVideo;
                    AppWindow.Changed += OnAppWindowChangedForBackgroundVideo;
                }
                else if (string.Equals(_backgroundVideoPath, path, StringComparison.OrdinalIgnoreCase)
                         && _backgroundVideoPlayer.Source != null)
                {
                    // 2026-10-07 内存暴涨修复：同一个视频被重复「应用」十几次，每次都重建 MediaSource。
                    // MediaSource / MediaPlayer 靠引用计数释放——旧的从不 Close，解码器缓存就一份份堆着。
                    // 实测 14 次调用把非托管内存从 104MB 推到 4373MB（单次跳变 +3.1GB）。
                    // 调用方在曲库刷新 / 背景重算等时机反复请求同一背景，属正常行为 → 这里识别后直接跳过换源。
                    MemProbe.Log("视频背景重复应用(同路径,已跳过换源)");
                    return;
                }

                // 换源前先彻底释放旧源（Pause → Source=null → SetMediaPlayer(null) → MediaSource.Dispose）。
                // 注意 CloseBackgroundVideoSource 里有一步 SetMediaPlayer(null)，
                //   所以之后必须重新挂一次，否则元素与播放器断开、视频不再显示。
                CloseBackgroundVideoSource();
                CustomBackgroundVideo.SetMediaPlayer(_backgroundVideoPlayer);
                _backgroundVideoPath = path;

                _backgroundVideoPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
                CustomBackgroundVideo.Visibility = Visibility.Visible;
                _backgroundVideoPlayer.Play();

                MemProbe.Log("视频背景应用后");

                global::CelesteMusicPlayer.StartupLog.Write("自定义背景：视频已应用 " + path);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
                CustomBackgroundVideo.Visibility = Visibility.Collapsed;
            }

            // 与封面背景互斥（同图片逻辑）
            ClearAlbumArtBackground();
        }

        /// <summary>套用主窗口背景（内置预设优先；否则用传入的自定义图片/视频路径）。</summary>
        internal void ApplyCustomBackground(string? path)
        {
            // 经典 / 极客这类不透明界面与壁纸层完全无关：设置变更广播、切歌等任何时机调用到这里，
            // 一律保持图层清空，绝不把图景壁纸铺回窗口（切回背景图式时 ApplyUiStyleMode 会重新调用本方法恢复）。
            if (IsOpaqueUiStyleActive())
            {
                // 请求号 +1：让还在飞行中的旧壁纸任务回来时自行作废
                _customBackgroundRequest++;
                CustomBackgroundImage.Source = null;
                CustomBackgroundImage.Visibility = Visibility.Collapsed;
                StopCustomBackgroundVideo();
                StopBackgroundMotion();
                ClearAlbumArtBackground();
                StartupLog.Write("背景图层：不透明界面（经典/极客）下忽略壁纸更新");
                return;
            }

            int request = ++_customBackgroundRequest;

            // 内置预设优先级高于自定义路径：选中预设时走生成器，避免空路径/不存在路径回退到封面背景。
            string preset = AppSettingsStore.Load().BackgroundPreset;
            if (BackgroundPresetGenerator.IsKnownPreset(preset))
            {
                _ = ApplyCustomBackgroundPresetAsync(preset, request);
                return;
            }

            _ = ApplyCustomBackgroundAsync(path, request);
        }

        private async System.Threading.Tasks.Task ApplyCustomBackgroundAsync(string? path, int request)
        {
            try
            {
                // 经典不透明界面与背景图层完全无关：图片/视频一律不显示（切回现有界面时由 ApplyUiStyleMode 恢复）
                if (IsClassicUiStyleActive())
                {
                    if (request != _customBackgroundRequest) return;

                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    StopCustomBackgroundVideo();
                    return;
                }

                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    if (request != _customBackgroundRequest) return;

                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    StopCustomBackgroundVideo();
                    StopBackgroundMotion();

                    // 恢复封面背景：用最近一次的当前曲目封面重绘（没有就保持空）
                    if (AlbumArtBackgroundImage != null)
                    {
                        _ = ApplyAlbumArtBackgroundAsync(_lastCoverBytes, _nowPlayingPath ?? string.Empty);
                    }
                    return;
                }

                // 视频背景：循环静音铺满，不走图片的模糊管线（那套 GDI+ 模糊只认静态图）
                if (IsVideoBackgroundPath(path))
                {
                    ApplyCustomBackgroundVideo(path, request);
                    return;
                }

                // 图片路径：先把视频层停掉（含后续图片解码失败的情况）
                StopCustomBackgroundVideo();

                AppSettingsState settings = AppSettingsStore.Load();
                int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;

                // 高斯模糊对自定义背景图同样生效（此前漏了这一步，开关只对封面背景起作用）。
                // 顺带解决另一个隐患：4000px 的手机照片直接交给 XAML 会占掉几百 MB 显存，
                // 走模糊通道后会被压到固定尺寸输出。
                byte[]? pixels = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        byte[] raw = System.IO.File.ReadAllBytes(path);
                        if (blurRadius <= 0)
                        {
                            return raw;
                        }

                        // workSize 取 192（封面背景是 96），半径同比放大，让滑块的模糊手感在两种背景上一致
                        return AlbumArtBackground.CreateHeavilyBlurredPng(raw, workSize: 192, blurRadius: blurRadius * 2);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
                        return null;
                    }
                });

                // 走到这里还是 null，说明 GDI+ 根本解不了这张图 —— 典型是 WebP / AVIF / HEIC：
                // WinUI 的图片控件走系统解码器（WIC），这些格式都能正常显示，
                // 但 GDI+ 只认 BMP/GIF/JPEG/PNG/TIFF，碰上就抛异常、CreateHeavilyBlurredPng 只能返回 null。
                // 旧代码这时会退回「原图直出」，于是背景照常显示、却完全不模糊
                // —— 用户看到的现象就是「自己设的背景图，高斯模糊不生效」。
                // 这里先让系统解码器把图转成 PNG，再走同一套模糊流程，把格式差异抹平。
                if (pixels == null && blurRadius > 0)
                {
                    global::CelesteMusicPlayer.StartupLog.Write(
                        "自定义背景：GDI+ 解不了码，改用系统解码器兜底 " + path);

                    byte[]? normalized = await NormalizeImageToPngAsync(path);

                    if (normalized != null)
                    {
                        pixels = await System.Threading.Tasks.Task.Run(
                            () => AlbumArtBackground.CreateHeavilyBlurredPng(normalized, workSize: 192, blurRadius: blurRadius * 2));

                        global::CelesteMusicPlayer.StartupLog.Write(
                            "自定义背景：系统解码器兜底结果=" + (pixels != null ? "模糊成功" : "仍然失败"));
                    }
                }

                if (request != _customBackgroundRequest) return;

                BitmapImage? image;
                if (pixels != null && pixels.Length > 0)
                {
                    image = await CreateBitmapFromBytesAsync(pixels);
                }
                else
                {
                    // 模糊失败（图异常/格式怪）时退回直接解码原图，至少有背景可用
                    BitmapImage fallback = new() { DecodePixelWidth = 1920 };
                    using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                    {
                        fallback.SetSource(fs.AsRandomAccessStream());
                    }

                    image = fallback;
                }

                if (image == null || request != _customBackgroundRequest) return;

                CustomBackgroundImage.Source = image;
                CustomBackgroundImage.Visibility = Visibility.Visible;

                // 互斥：自定义背景显示期间，封面背景（含压暗层）清掉
                ClearAlbumArtBackground();

                // 走自定义图片路径时不需要预设的缓慢移动
                RefreshBackgroundMotion();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }

        /// <summary>
        /// 内置背景预设：调用生成器拿到缓存图片路径，再走自定义图片背景的模糊/显示流程。
        /// 失败时回退到清除自定义背景（恢复封面背景）。
        /// </summary>
        private async System.Threading.Tasks.Task ApplyCustomBackgroundPresetAsync(string preset, int request)
        {
            try
            {
                string? path = BackgroundPresetGenerator.GetPresetPath(preset);
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    StartupLog.Write($"背景预设 {preset} 生成失败，回退到封面背景");
                    await ApplyCustomBackgroundAsync(null, request);
                    return;
                }

                StopCustomBackgroundVideo();

                AppSettingsState settings = AppSettingsStore.Load();
                int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;

                byte[]? pixels = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        byte[] raw = System.IO.File.ReadAllBytes(path);
                        if (blurRadius <= 0)
                        {
                            return raw;
                        }

                        return AlbumArtBackground.CreateHeavilyBlurredPng(raw, workSize: 192, blurRadius: blurRadius * 2);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException($"ApplyCustomBackgroundPresetAsync.{preset}", caught);
                        return null;
                    }
                });

                if (request != _customBackgroundRequest) return;

                BitmapImage? image = null;
                if (pixels != null && pixels.Length > 0)
                {
                    image = await CreateBitmapFromBytesAsync(pixels);
                }

                if (image == null)
                {
                    // 模糊失败时直出原图，至少能有背景
                    BitmapImage fallback = new() { DecodePixelWidth = 1920 };
                    using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                    {
                        fallback.SetSource(fs.AsRandomAccessStream());
                    }
                    image = fallback;
                }

                if (image == null || request != _customBackgroundRequest) return;

                CustomBackgroundImage.Source = image;
                CustomBackgroundImage.Visibility = Visibility.Visible;
                ClearAlbumArtBackground();

                // 预设背景可选「缓慢移动」，按设置起停
                RefreshBackgroundMotion();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException($"ApplyCustomBackgroundPresetAsync.{preset}", caught);
            }
        }

        /// <summary>
        /// 用系统解码器（WIC）把任意格式的图片转成 PNG 字节，好让 GDI+ 那套模糊代码能处理它。
        ///
        /// 为什么需要这一层：GDI+（System.Drawing）只认 BMP/GIF/JPEG/PNG/TIFF 这些老格式，
        /// 碰上 WebP / AVIF / HEIC 会直接抛异常；而 WinUI 的图片控件走系统解码器，
        /// 这些格式都能正常显示 —— 于是出现「背景图能显示、但模糊不生效」的怪现象。
        /// 先解码再重编码成 PNG，把格式差异抹平，后面就能复用同一套模糊代码。
        ///
        /// 解码时按长边缩到 1024：模糊前的工作尺寸只有 192，没必要让 8000px 的巨图
        /// 在内存里铺成几百 MB。顺带按 EXIF 摆正手机竖拍照片。
        /// </summary>
        private static async System.Threading.Tasks.Task<byte[]?> NormalizeImageToPngAsync(string path)
        {
            try
            {
                using System.IO.FileStream file = System.IO.File.OpenRead(path);
                using var source = file.AsRandomAccessStream();

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);
                if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;

                double scale = 1024.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                if (scale > 1) scale = 1;

                BitmapTransform transform = new()
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                using InMemoryRandomAccessStream encoded = new();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();

                if (encoded.Size == 0) return null;

                encoded.Seek(0);
                Windows.Storage.Streams.Buffer buffer = new((uint)encoded.Size);
                await encoded.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);

                byte[] bytes = buffer.ToArray();
                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs.NormalizeImageToPngAsync", caught);
                return null;
            }
        }

        /// <summary>停掉并隐藏背景视频（切回图片背景 / 清空背景时调用）。</summary>
        private void StopCustomBackgroundVideo()
        {
            if (CustomBackgroundVideo != null)
            {
                CustomBackgroundVideo.Visibility = Visibility.Collapsed;
            }

            if (_backgroundVideoPlayer == null) return;
            try
            {
                _backgroundVideoPlayer.Pause();
                // 2026-10-07 内存暴涨修复：只把 Source 置 null 只是松掉引用，
                // 底层的 MediaSource（连同它的视频解码器与帧缓存）不会因此释放 ——
                //   必须显式 Close()，否则每换一次背景就漏一份解码资源。
                // 实测：不清的话 14 次换源把非托管内存推到 4.3GB。
                CloseBackgroundVideoSource();
                _backgroundVideoPausedByMinimize = false;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
            }
        }

        /// <summary>
        /// 关闭并丢弃当前的 MediaSource（若已持有）。
        ///
        /// 依据微软官方文档（MediaPlayerElement / play-audio-and-video-with-mediaplayer）：
        ///   MediaSource 是 WinRT 原生对象，靠引用计数释放。把 <c>MediaPlayer.Source</c> 置 null
        ///   **只是解除引用，不会** 释放视频解码器与帧缓存——MediaPlayerElement 仍持有它。
        ///   必须依次做：Pause → Source=null → <b>SetMediaPlayer(null)</b> → <b>mediaSource.Dispose()</b>。
        ///   注意释放方法是 Dispose()，不是 Close()（本 SDK 上 MediaSource 没有 Close）。
        ///
        /// 实测（2026-10-07）：不做这一步，14 次换背景就把非托管内存从 104MB 推到 4373MB。
        /// </summary>
        private void CloseBackgroundVideoSource()
        {
            // 先取源（置 null 后就取不到了）
            var src = _backgroundVideoPlayer?.Source;
            if (src == null)
            {
                _backgroundVideoPath = null;
                return;
            }

            try
            {
                _backgroundVideoPlayer!.Pause();
            }
            catch { /* 源可能已失效，忽略 */ }

            try
            {
                _backgroundVideoPlayer.Source = null;
                CustomBackgroundVideo.Source = null;
                // 关键一步：断开 MediaPlayerElement 与 MediaPlayer 的连接，
                //   否则元素仍持有播放器，Dispose 之后它的解码资源不会真正回收。
                CustomBackgroundVideo.SetMediaPlayer(null);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("CloseBackgroundVideoSource.detach", caught);
            }

            try
            {
                if (src is Windows.Media.Core.MediaSource ms)
                {
                    // 官方释放方式：Dispose，不是 Close（本 SDK 上没有 Close）。
                    ms.Dispose();
                    StartupLog.Write("[视频] 已 Dispose 旧 MediaSource，释放解码资源");
                }
                // IMediaPlaybackSource 接口本身不暴露 Dispose；
                //   实践中这里拿到的就是 MediaSource，上面已覆盖。
                //   真出现别的实现类型时，只能断开引用（上面已做）交由 GC/系统回收。
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("CloseBackgroundVideoSource.dispose", caught);
            }
            finally
            {
                _backgroundVideoPath = null;
            }
        }

        private static bool IsVideoBackgroundPath(string path)
        {
            string ext;
            try
            {
                ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            }
            catch (Exception)
            {
                return false;
            }

            foreach (string v in VideoBackgroundExtensions)
            {
                if (v == ext) return true;
            }

            return false;
        }

        /// <summary>按设置决定是否给当前背景加/去缓慢移动（仅对内置预设生效）。</summary>
        internal void RefreshBackgroundMotion()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                bool want = BackgroundPresetGenerator.IsKnownPreset(settings.BackgroundPreset)
                            && settings.BackgroundPresetMotion
                            && !IsOpaqueUiStyleActive();
                ApplyBackgroundMotion(want);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.RefreshBackgroundMotion", caught);
            }
        }

        private void ApplyBackgroundMotion(bool enable)
        {
            StopBackgroundMotion();
            if (!enable || CustomBackgroundImage == null)
            {
                return;
            }

            try
            {
                var transform = new CompositeTransform { CenterX = 0, CenterY = 0 };
                CustomBackgroundImage.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                CustomBackgroundImage.RenderTransform = transform;
                _backgroundMotionTransform = transform;

                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

                // 24 秒一个来回：缓慢放大到 1.06，同时轻微左右漂移，反向后回到原处。
                AddMotion(storyboard, transform, "ScaleX", 1.0, 1.06, 24);
                AddMotion(storyboard, transform, "ScaleY", 1.0, 1.06, 24);
                AddMotion(storyboard, transform, "TranslateX", -14, 14, 31);
                AddMotion(storyboard, transform, "TranslateY", -8, 8, 41);

                _backgroundMotionStoryboard = storyboard;
                storyboard.Begin();
                StartupLog.Write("背景预设：已启用缓慢移动");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyBackgroundMotion", caught);
                StopBackgroundMotion();
            }
        }

        private static void AddMotion(
            Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard,
            CompositeTransform transform,
            string property,
            double from,
            double to,
            double seconds)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                EnableDependentAnimation = false,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut
                }
            };

            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, transform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(
                animation, "CompositeTransform." + property);
            storyboard.Children.Add(animation);
        }

        private void StopBackgroundMotion()
        {
            try
            {
                _backgroundMotionStoryboard?.Stop();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.StopBackgroundMotion", caught);
            }

            _backgroundMotionStoryboard = null;
            _backgroundMotionTransform = null;

            try
            {
                if (CustomBackgroundImage != null)
                {
                    CustomBackgroundImage.RenderTransform = null;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.StopBackgroundMotion", caught);
            }
        }

        /// <summary>主题色变化事件处理:统一刷新信息卡波形/歌词/进度条/迷你播放器/桌面歌词。</summary>
        private void OnThemeColorChanged(Windows.UI.Color accent)
        {
            try
            {
                _waveAccentColor = accent;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 音量条(自绘)/进度条
            try
            {
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                TimeSpan pos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(pos, force: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(AppSettingsStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

        }
    }
