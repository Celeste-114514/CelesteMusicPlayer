using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>启动轨迹日志（写在 exe 同目录，便于排查闪退）。</summary>
    internal static class StartupLog
    {
        private static readonly object Gate = new();
        private static string? _path;

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
                catch
                {
                }

                _path = Path.Combine(dir, "CelesteMusicPlayer.log");
                return _path;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(
                        LogPath,
                        DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        public static void WriteException(string where, Exception ex)
            => Write(where + ": " + ex);
    }
}
