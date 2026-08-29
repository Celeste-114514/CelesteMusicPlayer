using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>启动轨迹日志（写在 %LocalAppData%\CelesteMusicPlayer\CelesteMusicPlayer.log，便于排查闪退）。</summary>
    internal static class StartupLog
    {
        private static readonly object Gate = new();
        private static string? _path;

        /// <summary>
        /// 日志行内存缓冲：Write 只追加到缓冲，由后台定时器周期性落盘，
        /// 避免启动期每次 Write 都同步 open/append/close 文件（之前是热路径上的同步磁盘写）。
        /// 缓冲上限超过 200 行时立即落盘，兼顾崩溃可诊断性与内存占用。
        /// </summary>
        private static List<string> _buffer = new();
        private static readonly System.Threading.Timer _flushTimer;

        static StartupLog()
        {
            // 每 300ms 落盘一次；进程退出时再补一次，尽量不丢日志
            _flushTimer = new System.Threading.Timer(_ => FlushNow(), null, 300, 300);
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushNow();
            }
            catch { /* 某些宿主无 ProcessExit，忽略 */ }
        }

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
            string line = DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine;
            bool needsFlush;
            lock (Gate)
            {
                _buffer.Add(line);
                needsFlush = _buffer.Count >= 200; // 缓冲过多立刻落盘，防内存无限增长
            }

            if (needsFlush)
            {
                FlushNow();
            }
        }

        /// <summary>把内存缓冲一次性追加到磁盘。线程安全，可被定时器/退出事件/超量 Write 并发调用。</summary>
        private static void FlushNow()
        {
            List<string>? batch = null;
            lock (Gate)
            {
                if (_buffer.Count == 0)
                {
                    return;
                }

                batch = _buffer;
                _buffer = new List<string>();
            }

            if (batch == null || batch.Count == 0)
            {
                return;
            }

            try
            {
                RollIfNeeded();
                File.AppendAllText(LogPath, string.Concat(batch));
            }
            catch
            {
                // 落盘失败静默，不影响主流程
            }
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
