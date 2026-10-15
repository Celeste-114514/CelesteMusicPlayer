// ============================================================================
// CoverArtService —— 封面分档预生成 + 磁盘缓存（2026-10-16）
//
// 参考 ECHO（用户给的 ECHO-main）的做法：
//   · 封面不是"每次显示现解码"，而是**按档位预生成**好存盘，以后直接用；
//   · 三档：Thumb 96（列表小图）/ Album 320（常规）/ Large 768（详情、播放页）；
//   · 磁盘缓存，键 = 源图字节的 SHA256（同一张图换文件名也命中，换了图自动失效）；
//   · 有界并发，避免一次滚出几百首时把 CPU 打满。
//
// 为什么这么做：
//   1) 列表里每首歌都要封面时，原来每首都要「读文件 → TagLib 抽内嵌图 → 解码」，
//      滚动一次重来一次，又慢又容易掉帧；
//   2) 用户反馈"封面糊"：一部分原因是取到了文件里被压小的那张（已在
//      MainWindow.Misc.cs 的 TryLoadInnerCover 改成取最大的一张），另一部分是
//      反复按不同尺寸解码。分档后每档只解一次，之后全是读缓存。
//
// ⚠️ 只缩不放：源图比目标档小就原样存（放大没有意义，只会更糊）。
// ⚠️ 全部异步，UI 线程只做最后的 BitmapImage 装配。
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    /// <summary>封面档位（长边像素）。数值即该档的长边目标尺寸。</summary>
    internal enum CoverVariant
    {
        /// <summary>列表小图 / 播放栏缩略图。</summary>
        Thumb = 96,

        /// <summary>常规尺寸（歌单墙、专辑卡）。</summary>
        Album = 320,

        /// <summary>详情页、播放页大图。</summary>
        Large = 768,
    }

    internal static class CoverArtService
    {
        private const int CacheVersion = 1;

        /// <summary>同一张源图的生成任务去重（并发请求同一张只生成一次）。</summary>
        private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new(StringComparer.Ordinal);

        /// <summary>有界并发：同时最多这么多个缩略图在生成（ECHO 是 1~32，这里取 8 够用）。</summary>
        private static readonly SemaphoreSlim GenerationGate = new(8, 8);

        private static string CacheRoot
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "CelesteMusicPlayer", "CoverCache", "v" + CacheVersion);
            }
        }

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

            string? file = await GetCachedFileAsync(audioPath, variant);
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                return null;
            }

            return await DecodeFileAsync(file);
        }

        /// <summary>拿到该档位的缓存文件路径（必要时生成）。</summary>
        private static async Task<string?> GetCachedFileAsync(string audioPath, CoverVariant variant)
        {
            byte[]? source = await Task.Run(() => ReadSourceBytes(audioPath));
            if (source == null || source.Length == 0)
            {
                return null;
            }

            string key = Sha256Hex(source);
            string dir = Path.Combine(CacheRoot, key.Substring(0, 2), key);
            string file = Path.Combine(dir, variant.ToString() + ".jpg");

            if (File.Exists(file) && new FileInfo(file).Length > 0)
            {
                return file;
            }

            // 并发去重：同一张图被多处同时请求时只生成一次
            Task<string?> task = InFlight.GetOrAdd(key + "|" + variant, _ => GenerateAsync(source, dir, file, (int)variant));
            try
            {
                return await task;
            }
            finally
            {
                InFlight.TryRemove(key + "|" + variant, out _);
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
                string temp = file + ".tmp";

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

        /// <summary>把源图缩到指定长边并编码成 JPEG。只缩不放。</summary>
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

                // 只缩不放
                double scale = Math.Min(1.0, (double)longEdge / Math.Max(srcW, srcH));
                uint dstW = Math.Max(1, (uint)Math.Round(srcW * scale));
                uint dstH = Math.Max(1, (uint)Math.Round(srcH * scale));

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
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
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
        /// 复用主窗口那套取图逻辑（含"取最大的一张"的修正），避免两处各写一份。
        /// </summary>
        private static byte[]? ReadSourceBytes(string audioPath)
        {
            try
            {
                byte[]? bytes = MainWindow.TryReadCoverBytes(audioPath);
                return bytes is { Length: > 0 } ? bytes : null;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverArtService.ReadSource", ex);
                return null;
            }
        }

        private static string Sha256Hex(byte[] data)
        {
            byte[] hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
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
                if (Directory.Exists(CacheRoot))
                {
                    Directory.Delete(CacheRoot, recursive: true);
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
                if (!Directory.Exists(CacheRoot))
                {
                    return 0;
                }

                return Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories)
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
