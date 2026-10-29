using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 曲目在网页协议里的身份。
    ///
    /// 规则只有一条，但**必须全进程唯一**：网页侧用它做标签页、队列、封面、播放状态的键，
    /// 算法一变，用户的队列和标签就全对不上。所以它单独成一个类型，谁都不许在别处再写一遍。
    ///
    /// W13：从 MainWindow.WebShell.cs 的静态方法搬出来（纯函数，无状态）。
    /// </summary>
    internal static class WebTrackIdentity
    {
        /// <summary>
        /// 网络/来源曲目直接用它的 SourceTrackId；本地曲目用「路径大写 + 起始秒」的 SHA-256。
        /// 起始秒必须进哈希：CUE 分轨会把同一个文件的多个片段当成不同曲目。
        /// </summary>
        internal static string Of(PlaylistItem item)
        {
            if (item.SourceTrackId.Length > 0) return item.SourceTrackId;
            string key = item.FilePath.ToUpperInvariant() + "\n" + item.StartTimeSeconds.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        }
    }
}
