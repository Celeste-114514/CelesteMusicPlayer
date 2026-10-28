using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 应用内部缓存目录的路径真值（W6 服务迁出）。
    /// 原先这些路径散在 DsdPreloadService / FfmpegDecoderBackend / WebDavCache 里，
    /// 而 LibraryPathGuard 需要它们来把内部缓存挡在媒体库之外 —— 结果曲库层被迫
    /// 拖上整个音频引擎。照 SrcOptions 的先例把纯路径常量/计算抽到这个中性文件：
    /// 重类反向引用它，曲库层只依赖它，两边各取所需。值与原实现逐字一致。
    /// </summary>
    internal static class AppCachePaths
    {
        /// <summary>DSD 整轨 DoP 缓存默认根目录（D 盘存在就用 D 盘，避免占系统盘；用户可在设置里改）。</summary>
        public static string DsdDefaultCacheRoot =>
            Directory.Exists(@"D:\")
                ? @"D:\CelesteDsdCache"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "DsdCache");

        /// <summary>FFmpeg 转码缓存目录（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）。</summary>
        public static string TranscodeCacheDir
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseDir, "CelesteMusicPlayer", "TranscodeCache");
            }
        }

        /// <summary>WebDAV 下载缓存默认根目录。</summary>
        public static string WebDavCacheDefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer",
            "WebDavCache");
    }
}
