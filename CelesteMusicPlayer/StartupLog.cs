using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>启动轨迹日志（写在 %LocalAppData%\CelesteMusicPlayer\CelesteMusicPlayer.log，便于排查闪退）。</summary>
    internal static class StartupLog
    {
        private static readonly object Gate = new();
        private static string? _path;

        /// <summary>对外暴露：启动日志的完整文件路径（不让其他模块重新计算路径）。</summary>
        public static string CurrentFilePath => LogPath;

        private static string LogPath
        {
            get
            {
                if (_path != null)
                {
                    return _path;
                }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("StartupLog.cs", caught); }

                _path = Path.Combine(dir, "CelesteMusicPlayer.log");
                return _path;
            }
        }

        private const long MaxLogBytes = 5L * 1024 * 1024; // 5MB 滚动阈值

        private static long _lastCheckedLength = -1;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    RollIfNeeded();
                    File.AppendAllText(
                        LogPath,
                        DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("StartupLog.cs", caught); }
        }

        /// <summary>日志超阈值时把当前文件备份为 CelesteMusicPlayer.log.old（保留一份），避免无限增长。</summary>
        private static void RollIfNeeded()
        {
            string path = LogPath;
            try
            {
                if (!File.Exists(path))
                {
                    _lastCheckedLength = 0;
                    return;
                }

                long len;
                try
                {
                    len = new FileInfo(path).Length;
                }
                catch
                {
                    return;
                }

                // 缓存长度避免每次写都 stat；仅当未知或明显增长时检查
                if (_lastCheckedLength < 0 || len + 8192 > MaxLogBytes)
                {
                    if (len > MaxLogBytes)
                    {
                        string backup = path + ".old";
                        try
                        {
                            File.Delete(backup);
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("StartupLog.cs", caught); }

                        try
                        {
                            File.Move(path, backup);
                        }
                        catch
                        {
                            // 备份失败则保留，下次再试
                        }

                        _lastCheckedLength = 0;
                        return;
                    }

                    _lastCheckedLength = len;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("StartupLog.cs", caught); }
        }

        public static void WriteException(string where, Exception ex)
            => Write(where + ": " + ex);
    }
}
