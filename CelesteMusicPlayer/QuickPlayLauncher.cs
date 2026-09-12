using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 双击音频文件打开时的调度中心。
    ///
    /// 目的：用户双击一个音频文件，应该"秒出声"，而不是先等重量级主界面构造完。
    /// 同时程序同一时刻只应该有一个实例在播（单实例）。
    ///
    /// 三种情况：
    /// 1) 程序没在跑 + 双击文件  → 本进程成为主实例，打开主界面并在其中播放该文件。
    /// 2) 程序已在跑（主界面或独立窗口） + 双击文件 → 把文件路径转交给已运行的实例，本进程立刻退出。
    /// 3) 程序已在跑 + 直接点快捷方式（无文件） → 转交"激活"命令，把已有窗口提到前台，本进程退出。
    ///
    /// 转交通道：在 %LocalAppData%\CelesteMusicPlayer\relay 下写一个 .cmd 文件，
    /// 已运行实例用 FileSystemWatcher 监听这个目录。用文件而不是管道/共享内存，
    /// 是因为它最简单、不需要任何权限、不会因进程崩溃留下脏状态。
    /// </summary>
    internal static class QuickPlayLauncher
    {
        /// <summary>单实例互斥体名字。带 Local\ 前缀，只对当前用户会话生效。</summary>
        private const string MutexName = @"Local\CelesteMusicPlayer_SingleInstance_2026";

        private static Mutex? _mutex;
        private static FileSystemWatcher? _watcher;

        /// <summary>本进程抢到了主实例（true）还是已有别的实例在跑（false）。</summary>
        internal static bool IsPrimaryInstance { get; private set; }

        /// <summary>本进程是不是"为了播放某个外部文件才启动的"。决定关窗时是否直接退出程序。</summary>
        internal static bool LaunchedForExternalFile { get; private set; }

        /// <summary>
        /// 程序能播放的全部音频扩展名。与 MainWindow.Playback.cs 的 AudioExtensions 对齐，
        /// 另外补上 .cue（分轨）、.iso（SACD 整盘）、.aif/.aiff。
        /// 改这里就等于同时改了"双击能否被本程序接管"和"文件关联注册哪些格式"，
        /// 保证两处永远一致，不会出现"关联了却播不了"。
        /// </summary>
        internal static readonly string[] PlayableExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac",
            ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus",
            ".dsf", ".dff", ".iso",
            ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm",
            ".aif", ".aiff", ".oga", ".mka", ".cue"
        };

        /// <summary>转交目录。</summary>
        internal static string RelayDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CelesteMusicPlayer", "relay");

        /// <summary>
        /// 尝试成为主实例。必须在任何窗口创建之前调用，且整个进程生命周期内只能调一次。
        /// 抢不到说明已经有一个实例在跑。
        /// </summary>
        internal static bool TryBecomePrimary()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                IsPrimaryInstance = createdNew;
                return createdNew;
            }
            catch (Exception ex)
            {
                // 拿不到互斥体就按"主实例"处理，最坏情况是开了两个窗口，不会开不了程序
                global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex);
                IsPrimaryInstance = true;
                return true;
            }
        }

        /// <summary>释放互斥体。进程正常退出时系统也会回收，这里只是让它更干净。</summary>
        internal static void Release()
        {
            try
            {
                _watcher?.Dispose();
                _watcher = null;
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
        }

        /// <summary>
        /// 从命令行参数里找出第一个"本程序能播且真实存在"的文件路径。
        /// 文件关联的命令是 "exe" "%1"，所以参数[1]就是被双击的文件。
        /// </summary>
        internal static string? TryGetFileFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 1; i < args.Length; i++)
                {
                    string raw = args[i];
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    // 去掉可能被二次转义留下的引号
                    string candidate = raw.Trim().Trim('"');
                    if (candidate.StartsWith('-') || candidate.StartsWith('/')) continue;

                    if (IsPlayableFile(candidate) && File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }

            return null;
        }

        /// <summary>扩展名是音频格式，且文件确实存在。</summary>
        internal static bool IsPlayableFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;

            foreach (string known in PlayableExtensions)
            {
                if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>主实例启动后开始监听转交目录，接收后续双击的文件。</summary>
        /// <param name="uiQueue">UI 线程的消息队列（回调会切回 UI 线程）。</param>
        /// <param name="onCommand">收到命令时的回调，参数形如 "PLAY:xxx.mp3" / "ACTIVATE:"。</param>
        internal static void StartListening(Microsoft.UI.Dispatching.DispatcherQueue uiQueue, Action<string> onCommand)
        {
            try
            {
                Directory.CreateDirectory(RelayDirectory);

                _watcher = new FileSystemWatcher(RelayDirectory, "*.cmd")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false
                };

                void Handle(string fullPath)
                {
                    try
                    {
                        string content = ReadWithRetry(fullPath);
                        if (string.IsNullOrWhiteSpace(content)) return;

                        try { File.Delete(fullPath); } catch { /* 删不掉无所谓，下次启动清 */ }

                        uiQueue.TryEnqueue(() =>
                        {
                            try { onCommand(content); }
                            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
                        });
                    }
                    catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
                }

                _watcher.Created += (_, e) => Handle(e.FullPath);
                _watcher.Renamed += (_, e) => Handle(e.FullPath);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
        }

        /// <summary>把命令转交给已运行的实例，然后本进程就可以退出了。</summary>
        internal static void SendToRunningInstance(string command)
        {
            try
            {
                Directory.CreateDirectory(RelayDirectory);

                // 先写 .tmp 再改名：FileSystemWatcher 的 Created 会在文件写完前就触发，
                // 直接写 .cmd 可能读到空内容。改名是原子操作，能确保监听端读到完整内容。
                string tmp = Path.Combine(RelayDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                string final = Path.Combine(RelayDirectory, Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(tmp, command);
                File.Move(tmp, final, overwrite: true);
            }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
        }

        /// <summary>清掉上次异常退出残留的命令文件，避免启动时重复播放旧文件。</summary>
        internal static void ClearStaleRelayFiles()
        {
            try
            {
                if (!Directory.Exists(RelayDirectory)) return;

                foreach (string f in Directory.GetFiles(RelayDirectory, "*.cmd"))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }

                foreach (string f in Directory.GetFiles(RelayDirectory, "*.tmp"))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }
            }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("QuickPlayLauncher.cs", ex); }
        }

        private static string ReadWithRetry(string path)
        {
            // 极端情况下文件刚改名、句柄还没释放，重试几次即可
            for (int i = 0; i < 5; i++)
            {
                try { return File.ReadAllText(path); }
                catch (IOException) { Thread.Sleep(30); }
            }

            return string.Empty;
        }
    }
}
