// ============================================================================
// CoverArtService —— 封面分档预生成 + 磁盘缓存（2026-10-16，2026-10-29 两次改造）
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
// ---------------------------------------------------------------------------
// 2026-10-29 第五小步：把与图像无关的三件事搬出本文件。
//   · 档位数值与只缩不放的几何   → CoverVariants.cs（CoverVariant / CoverSizing）
//   · 缓存根目录、分桶路径、SHA256 键 → CoverCacheLayout.cs
//   · 从音频文件抠封面原始字节    → CoverSourceReader.cs（原 MainWindow.Misc.cs 的实现）
//
// 2026-10-29 第六小步：图像管线从 WinRT 换成 ImageSharp（纯托管、跨平台）。
//   · 解码/缩放/编码的整体实现     → CoverThumbnailer.cs
//   本文件现在只剩：查缓存 → 有界并发生成 → 把缓存文件装配成 BitmapImage 交给界面。
//   界面拿到的已经是**缩好的缓存文件**，不再让 XAML 去解一张 1400px 的大图。
//   整个文件里只剩 "BitmapImage" 这一处平台绑定（它本来就是 XAML 的类型），
//   将来网页壳/跨平台壳直接用 <img src=缓存文件> 即可，不需要改本文件之外的任何逻辑。
// ---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

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

        private static Task<string?> GetCachedFileForBytesAsync(byte[] source, CoverVariant variant)
            => CoverCacheService.GetAsync(source, variant);

        /// <summary>
        /// 把缓存文件装配成 BitmapImage（须在 UI 线程）。
        /// 2026-10-29 第六小步起直接喂文件流 —— 文件已是准尺寸的 jpg，
        /// 不再像从前那样"先读全字节、再塞进内存流、再由 XAML 解一张大图"。
        /// </summary>
        private static async Task<BitmapImage?> DecodeFileAsync(string path)
        {
            try
            {
                var image = new BitmapImage();
                using FileStream fs = File.OpenRead(path);
                await image.SetSourceAsync(fs.AsRandomAccessStream());
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
