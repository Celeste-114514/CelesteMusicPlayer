// ============================================================================
// CoverArtService —— 封面分档预生成 + 磁盘缓存（2026-10-16，2026-10-29 拆分瘦身）
//
// 参考 ECHO（用户给的 ECHO-main）的做法：
//   · 封面不是"每次显示现解码"，而是**按档位预生成**好存盘，以后直接用；
//   · 三档：Thumb 160（列表小图）/ Album 320（常规）/ Large 768（详情、播放页）；
//   · 磁盘缓存，键 = 源图字节的 SHA256（同一张图换文件名也命中，换了图自动失效）；
//   · 有界并发，避免一次滚出几百首时把 CPU 打满。
//
// 为什么这么做：
//   1) 列表里每首歌都要封面时，原来每首都要「读文件 → TagLib 抽内嵌图 → 解码」，
//      滚动一次重来一次，又慢又容易掉帧；
//   2) 用户反馈"封面糊"：一部分原因是取到了文件里被压小的那张（已在
//      CoverSourceReader 改成取最大的一张），另一部分是反复按不同尺寸解码。
//      分档后每档只解一次，之后全是读缓存。
//
// ⚠️ 只缩不放：源图比目标档小就原样存（放大没有意义，只会更糊）。
// ⚠️ 全部异步，UI 线程只做最后的 BitmapImage 装配。
//
// ---------------------------------------------------------------------------
// 2026-10-29（W6 第五小步）：把与图像无关的三件事搬出本文件，本文件只剩
// "WinRT 解码/缩放 + BitmapImage 装配 + 有界并发调度"：
//   · 档位数值与只缩不放的几何   → CoverVariants.cs（CoverVariant / CoverSizing）
//   · 缓存根目录、分桶路径、SHA256 键 → CoverCacheLayout.cs
//   · 从音频文件抠封面原始字节    → CoverSourceReader.cs（原 MainWindow.Misc.cs 的实现）
// 这样封面链路的"处理"半边不再依赖 WinUI/WinRT，能单独链接进跨平台测试宿主；
// 剩下这半边的图像管线将来换成 ImageSharp/SkiaSharp 时，本文件是唯一要动的地方。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    internal static class CoverArtService
    {
        /// <summary>同一张源图的生成任务去重（并发请求同一张只生成一次）。</summary>
        private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new(StringComparer.Ordinal);

        /// <summary>有界并发：同时最多这么多个缩略图在生成（ECHO 是 1~32，这里取 8 够用）。</summary>
        private static readonly SemaphoreSlim GenerationGate = new(8, 8);

        /// <summary>当前版本的封面缓存根目录（规则见 CoverCacheLayout）。</summary>
        private static string CacheRoot => CoverCacheLayout.GetRoot();

        /// <summary>
        /// 取指定档位的封面（BitmapImage，须在 UI 线程上用）。
        /// 缓存命中直接读盘；没命中就生成一份再读。
        /// 取不到（无内嵌图、无同目录图、解码失败）返回 null，调用方自己回落占位图。
        /// </summary>
        public static async Task<BitmapImage?> GetAsync(string audioPath, CoverVariant variant)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                return null;
            }

            byte[]? source = await Task.Run(() => ReadSourceBytes(audioPath));
            if (source == null || source.Length == 0)
            {
                return null;
            }

            return await GetAsync(source, variant);
        }

        /// <summary>
        /// 取指定档位的封面（调用方已持有源图字节时用这个重载，省一次读文件）。
        /// 播放栏缩略图走这里：源字节与播放页大图同一份（尊重 UseInnerCoverFirst 的取舍），
        /// 保证两处选出同一张图；缓存键仍是源图 SHA256，与按路径调用共享同一份磁盘缓存。
        /// </summary>
        public static async Task<BitmapImage?> GetAsync(byte[] sourceBytes, CoverVariant variant)
        {
            if (sourceBytes == null || sourceBytes.Length == 0)
            {
                return null;
            }

            string? file = await GetCachedFileForBytesAsync(sourceBytes, variant);
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                return null;
            }

            return await DecodeFileAsync(file);
        }

        /// <summary>拿到该档位的缓存文件路径（必要时生成）。</summary>
        internal static Task<string?> GetDesktopCoverFileAsync(string audioPath) => GetCachedFileAsync(audioPath, CoverVariant.Thumb);

        private static async Task<string?> GetCachedFileAsync(string audioPath, CoverVariant variant)
        {
            byte[]? source = await Task.Run(() => ReadSourceBytes(audioPath));
            if (source == null || source.Length == 0)
            {
                return null;
            }

            return await GetCachedFileForBytesAsync(source, variant);
        }

        private static async Task<string?> GetCachedFileForBytesAsync(byte[] source, CoverVariant variant)
        {
            string key = CoverCacheLayout.GetKey(source);
            string dir = CoverCacheLayout.GetEntryDirectory(CacheRoot, key);
            string file = CoverCacheLayout.GetVariantFile(CacheRoot, key, variant);

            if (File.Exists(file) && new FileInfo(file).Length > 0)
            {
                return file;
            }

            // 并发去重：同一张图被多处同时请求时只生成一次
            string flightKey = CoverCacheLayout.GetInFlightKey(key, variant);
            Task<string?> task = InFlight.GetOrAdd(flightKey, _ => GenerateAsync(source, dir, file, CoverSizing.LongEdge(variant)));
            try
            {
                return await task;
            }
            finally
            {
                InFlight.TryRemove(flightKey, out _);
            }
        }

        /// <summary>生成某一档并写盘。失败返回 null（下次会重试）。</summary>
        private static async Task<string?> GenerateAsync(byte[] source, string dir, string file, int longEdge)
        {
            await GenerationGate.WaitAsync();
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length > 0)
                {
                    return file;
                }

                Directory.CreateDirectory(dir);
                string temp = CoverCacheLayout.GetTempFile(file);

                bool ok = await Task.Run(() => ResizeToJpegAsync(source, temp, longEdge).GetAwaiter().GetResult());
                if (!ok)
                {
                    TryDelete(temp);
                    return null;
                }

                // 先写 .tmp 再改名的原子替换：生成到一半被杀不会留下半个坏文件
                File.Move(temp, file, overwrite: true);
                return file;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.Generate", ex);
                return null;
            }
            finally
            {
                GenerationGate.Release();
            }
        }

        /// <summary>把源图缩到指定长边并编码成 JPEG。只缩不放（几何由 CoverSizing 算）。</summary>
        private static async Task<bool> ResizeToJpegAsync(byte[] source, string outputPath, int longEdge)
        {
            try
            {
                using var inStream = new InMemoryRandomAccessStream();
                await inStream.WriteAsync(source.AsBuffer());
                inStream.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inStream);
                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                if (srcW == 0 || srcH == 0)
                {
                    return false;
                }

                // 只缩不放（这一行算术搬到了 CoverSizing，好让测试宿主能钉住它）
                (int targetWidth, int targetHeight) = CoverSizing.ComputeTargetSize((int)srcW, (int)srcH, longEdge);
                uint dstW = (uint)targetWidth;
                uint dstH = (uint)targetHeight;

                var transform = new BitmapTransform
                {
                    ScaledWidth = dstW,
                    ScaledHeight = dstH,
                    InterpolationMode = BitmapInterpolationMode.Fant, // 缩略图质量最好
                };

                PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                byte[] data = pixels.DetachPixelData();

                using var outStream = new InMemoryRandomAccessStream();

                // JPEG 质量提到 0.92（2026-10-19）：默认的编码器质量在小图上块效应很明显，
                // 96/160 这种小图再被 UI 放到 58px 的框里，块效应会被看成"糊"。
                // 大图也一并受益，代价只是缓存文件略大一点。
                var quality = new BitmapPropertySet();
                quality.Add("ImageQuality", new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single));

                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.JpegEncoderId, outStream, quality);
                encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied, dstW, dstH, 96, 96, data);
                await encoder.FlushAsync();

                using (FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    outStream.Seek(0);
                    await outStream.AsStream().CopyToAsync(fs);
                }

                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.Resize", ex);
                return false;
            }
        }

        private static async Task<BitmapImage?> DecodeFileAsync(string path)
        {
            try
            {
                byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);

                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.Decode", ex);
                return null;
            }
        }

        /// <summary>
        /// 读封面源字节：内嵌优先，其次同目录 sidecar。
        /// 2026-10-29 起直接走 CoverSourceReader —— 原实现住在 MainWindow.Misc.cs，
        /// 本服务为了调它反向依赖了主窗口，跨平台测试宿主因此链接不进来。
        /// 规则与搬出前一致：内嵌优先，内嵌有多张时取像素最大的那张。
        /// </summary>
        private static byte[]? ReadSourceBytes(string audioPath)
        {
            try
            {
                byte[]? bytes = CoverSourceReader.Read(audioPath, preferInnerCover: true);
                return bytes is { Length: > 0 } ? bytes : null;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.ReadSource", ex);
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删不掉就算了，下次会被 .tmp 覆盖
            }
        }

        // =====================================================================
        // 缓存维护
        // =====================================================================

        /// <summary>清掉整个封面缓存（设置里「清除缓存」用，或版本号升级后旧的自然失效）。</summary>
        public static void ClearCache()
        {
            try
            {
                string root = CacheRoot;
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.ClearCache", ex);
            }
        }

        /// <summary>当前缓存占多少字节（设置里显示用）。</summary>
        public static long GetCacheSizeBytes()
        {
            try
            {
                string root = CacheRoot;
                if (!Directory.Exists(root))
                {
                    return 0;
                }

                return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p).Length)
                    .Sum();
            }
            catch
            {
                return 0;
            }
        }
    }
}
