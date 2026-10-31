// ============================================================================
// CoverCacheLayout —— 封面磁盘缓存的"路径与键"真值（W6 第五小步）
//
// 2026-10-29 从 CoverArtService.cs 搬出。搬出前这套规则和 WinRT 解码写在同一个
// 文件里，导致任何想知道"封面缓存放在哪、某个键对应哪个文件"的地方都得拖上
// 整个图像管线（连测试宿主都链接不进去）。这里只做纯字符串/哈希运算。
//
// 规则（与搬出前逐字等价，参考 ECHO 的 CoverCacheManager）：
//   · 根目录 %LOCALAPPDATA%\CelesteMusicPlayer\CoverCache\v{版本号}
//   · 键 = 源图字节的 SHA-256 小写十六进制（同一张图换文件名也命中，换图自动失效）
//   · 分桶 <根>\{键前两位}\{键}\{档位}.jpg（前两位分桶，避免单目录几十万文件）
//   · 先写 .tmp 再改名，生成一半被杀不会留下半个坏文件
//
// ⚠️ 改档位尺寸或改名规则必须 CacheVersion +1：旧目录自然不再被读，不用迁移。
// ============================================================================

using System;
using System.IO;
using System.Security.Cryptography;

namespace CelesteMusicPlayer
{
    internal static class CoverCacheLayout
    {
        /// <summary>
        /// 缓存版本号。档位尺寸一改就必须 +1 —— 目录按版本号分开，
        /// 旧版本目录自然失效（不再被读），不用做迁移。
        /// v1 = Thumb 96；v2 = Thumb 160（2026-10-19）；
        /// v3 = 三档整体上调 + 按屏幕缩放取档（2026-11-01：Thumb 240 / Album 480 / Large 1024）。
        /// </summary>
        public const int CacheVersion = 3;

        private const string AppFolderName = "CelesteMusicPlayer";
        private const string CacheFolderName = "CoverCache";
        private const string VariantExtension = ".jpg";
        private const string TempSuffix = ".tmp";

        /// <summary>当前版本的缓存根目录。</summary>
        public static string GetRoot() => GetRoot(GetDefaultBaseDirectory(), CacheVersion);

        /// <summary>指定基准目录与版本的缓存根目录（自检注入临时目录用，避免碰真实缓存）。</summary>
        public static string GetRoot(string baseDirectory, int version)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                throw new ArgumentException("baseDirectory 不能为空", nameof(baseDirectory));
            }

            return Path.Combine(baseDirectory, AppFolderName, CacheFolderName, "v" + version);
        }

        /// <summary>默认基准目录（%LOCALAPPDATA%）。取不到时退回临时目录，保证不抛。</summary>
        public static string GetDefaultBaseDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local;
        }

        /// <summary>源图字节 → 缓存键（SHA-256 小写十六进制，64 字符）。</summary>
        public static string GetKey(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                throw new ArgumentException("source 不能为空", nameof(source));
            }

            byte[] hash = SHA256.HashData(source);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>某一档的缓存文件路径。</summary>
        public static string GetVariantFile(string root, string key, CoverVariant variant)
            => Path.Combine(GetEntryDirectory(root, key), GetVariantFileName(variant));

        /// <summary>缓存键专属目录（&lt;root&gt;\{键前两位}\{键}）。</summary>
        public static string GetEntryDirectory(string root, string key)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("root 不能为空", nameof(root));
            }

            if (string.IsNullOrEmpty(key) || key.Length < 2)
            {
                throw new ArgumentException("key 至少 2 个字符", nameof(key));
            }

            return Path.Combine(root, key.Substring(0, 2), key);
        }

        /// <summary>档位对应的文件名（Thumb.jpg / Album.jpg / Large.jpg）。</summary>
        public static string GetVariantFileName(CoverVariant variant) => variant.ToString() + VariantExtension;

        /// <summary>生成过程中的临时文件路径（同一目录，改名是原子的）。</summary>
        public static string GetTempFile(string variantFile) => variantFile + TempSuffix;

        /// <summary>同一张图同一档位的并发生成去重键。</summary>
        public static string GetInFlightKey(string key, CoverVariant variant) => key + "|" + variant;
    }
}
