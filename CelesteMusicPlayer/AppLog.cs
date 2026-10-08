using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>日志级别。数值越大越严重。</summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5
    }

    /// <summary>
    /// 日志分类（= 子目录名 = 各写一个文件）。
    /// 新增分类只需在这里加常量，目录会在首次写入时自动建好。
    /// </summary>
    public static class LogCategory
    {
        /// <summary>应用自身：启动/设置/窗口/托盘/主题/更新流程。</summary>
        public const string App = "app";
        /// <summary>音频链路：播放/解码/输出设备/无接缝续接/交叉淡入。</summary>
        public const string Audio = "audio";
        /// <summary>DSP 机架：EQ/压缩/交叉馈送/声场/矩阵/SRC/限幅。</summary>
        public const string Dsp = "dsp";
        /// <summary>原生内核：celeste_core / celeste_audio_core / ASIO / WASAPI 独占。</summary>
        public const string Native = "native";
        /// <summary>联网：在线曲库 / WebDAV / 更新检查与下载。</summary>
        public const string Net = "net";
        /// <summary>本地曲库：扫描 / 标签 / 播放列表 / 播放历史 / 搜索。</summary>
        public const string Lib = "lib";
    }

    /// <summary>
    /// 分模块日志系统（2026-10-08 重建）。
    ///
    /// 原来所有日志都挤在 <c>%LocalAppData%\CelesteMusicPlayer\CelesteMusicPlayer.log</c>
    /// 一个文件里：排一次卡顿要把几万行上下游的东西从头翻到尾，而且 5MB 一滚只剩下
    /// 上一份 <c>.old</c>，想对比昨天的现场早就没了。
    ///
    /// 现在的形态（参考 ECHO 的做法）：
    ///   - 按模块拆文件：logs\{app|audio|dsp|native|net|lib}\yyyy-MM-dd.log
    ///   - 按天分文件，跨天自动新建，不用"改名备份"那套
    ///   - 单个文件上限 MaxFileBytes，超了存一份 .1，防某个模块刷爆磁盘
    ///   - 保留最近 RetentionDays 天，启动时清旧的
    ///   - 五级日志（Trace/Debug/Info/Warn/Error），可按模块过滤
    ///   /// 过滤开关：环境变量 CELESTE_LOG，例如
    ///     CELESTE_LOG=info                全局 info
    ///     CELESTE_LOG=audio=debug,dsp=trace  按模块分别给
    ///   - 内存缓冲 + 后台定时落盘（300ms），不把磁盘 I/O 放热路径
    ///
    /// 旧的 <see cref="StartupLog"/> 保留为门面：全项目 1000 多处调用点一行没改，
    /// 靠 [CallerFilePath] 自动判断该进哪个模块的文件。
    /// </summary>
    public static class AppLog
    {
        private const long MaxFileBytes = 8L * 1024 * 1024;
        private const int RetentionDays = 14;
        private const int MaxBufferLines = 200;
        private const int FlushPeriodMs = 300;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Sink> Sinks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Timer FlushTimer;

        private static string _root = string.Empty;
        private static bool _ready;
        private static bool _broken;
        private static bool _cleanedThisDay;
        private static int _todayKey;
        private static string _filterSpec = string.Empty;
        private static readonly Dictionary<string, LogLevel> CategoryMin = new(StringComparer.OrdinalIgnoreCase);
        private static LogLevel _globalMin = LogLevel.Info;

        static AppLog()
        {
            // 兜底：任何一步炸了都不能让日志把程序带崩
            try
            {
                _root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "logs");
                Directory.CreateDirectory(_root);
                _ready = true;
                _todayKey = TodayKey();
                ReadFilterFromEnvironment();
                CleanupOldFiles();
                _cleanedThisDay = true;
            }
            catch
            {
                _broken = true;
            }

            FlushTimer = new Timer(_ => FlushAll(), null, FlushPeriodMs, FlushPeriodMs);
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();
            }
            catch { /* 某些宿主没有 ProcessExit */ }
        }

        // =====================================================================
        // 对外只读信息
        // =====================================================================

        /// <summary>日志根目录（logs\ 本身，不是单个文件）。</summary>
        public static string LogsDirectory
        {
            get { lock (Gate) { return _root; } }
        }

        /// <summary>兼容旧用法：返回 app 模块今天的日志文件路径。</summary>
        public static string CurrentFilePath => PathFor(LogCategory.App);

        /// <summary>某个分类今天的日志文件完整路径。</summary>
        public static string PathFor(string category)
        {
            string cat = NormalizeCategory(category);
            return Path.Combine(_root, cat, TodayFileName());
        }

        /// <summary>当前生效的过滤规则描述（写进启动 banner，便于排查"我怎么什么都没看到"）。</summary>
        public static string FilterDescription
        {
            get
            {
                lock (Gate)
                {
                    if (string.IsNullOrEmpty(_filterSpec))
                    {
                        return "全部模块 " + _globalMin.ToString().ToLowerInvariant() + " 及以上";
                    }
                    return "CELESTE_LOG=" + _filterSpec;
                }
            }
        }

        /// <summary>当前最小级别（未按模块单独指定时）。</summary>
        public static LogLevel MinLevel => _globalMin;

        // =====================================================================
        // 写入 API
        // =====================================================================

        public static void Trace(string category, string message) => Write(LogLevel.Trace, category, message);
        public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
        public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
        public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);

        public static void Error(string category, string message) => Write(LogLevel.Error, category, message);

        public static void Error(string category, string message, Exception? ex) =>
            Write(LogLevel.Error, category, BuildExceptionText(message, ex));

        public static void Fatal(string category, string message, Exception? ex) =>
            Write(LogLevel.Fatal, category, BuildExceptionText(message, ex));

        /// <summary>带 key=value 字段的信息日志。fields 会原样拼在消息后面，方便 grep。</summary>
        public static void Info(string category, string message, params object?[] fields) =>
            Write(LogLevel.Info, category, AppendFields(message, fields));

        public static void Debug(string category, string message, params object?[] fields) =>
            Write(LogLevel.Debug, category, AppendFields(message, fields));

        public static void Warn(string category, string message, params object?[] fields) =>
            Write(LogLevel.Warn, category, AppendFields(message, fields));

        public static void Error(string category, string message, params object?[] fields) =>
            Write(LogLevel.Error, category, AppendFields(message, fields));

        /// <summary>立即把所有缓冲写进磁盘。弹过窗、要退出、进程可能被杀之前必须调。</summary>
        public static void Flush() => FlushAll();

        // =====================================================================
        // 核心
        // =====================================================================

        private static void Write(LogLevel level, string category, string message)
        {
            try
            {
                if (_broken || !_ready) return;
                if (message == null) message = "(null)";

                string cat = NormalizeCategory(category);
                if (!IsEnabled(level, cat)) return;

                string line = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " " + LevelTag(level)
                    + " [" + cat + "] "
                    + message.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ')
                    + Environment.NewLine;

                bool overflow;
                lock (Gate)
                {
                    RollDayIfNeeded();
                    Sink sink = GetOrCreateSink(cat);
                    sink.Buffer.Add(line);
                    overflow = sink.Buffer.Count >= MaxBufferLines;
                }

                if (overflow) FlushSink(cat);
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }

        private static Sink GetOrCreateSink(string category)
        {
            if (!Sinks.TryGetValue(category, out Sink? sink))
            {
                sink = new Sink { Category = category };
                Sinks[category] = sink;
            }
            return sink;
        }

        /// <summary>跨天了就丢掉旧 writer，下次 flush 会按新日期新建文件。</summary>
        private static void RollDayIfNeeded()
        {
            int today = TodayKey();
            if (today == _todayKey) return;
            _todayKey = today;
            foreach (Sink sink in Sinks.Values)
            {
                CloseWriter(sink);
                sink.Length = 0;
            }
            _cleanedThisDay = false;
        }

        private static void FlushSink(string category)
        {
            lock (Gate)
            {
                if (!Sinks.TryGetValue(category, out Sink? sink) || sink.Buffer.Count == 0) return;

                string batch = string.Concat(sink.Buffer.ToArray());
                sink.Buffer.Clear();

                try
                {
                    EnsureWriter(sink);
                    if (sink.Writer == null) return;
                    sink.Writer.Write(batch);
                    sink.Writer.Flush();
                    sink.Length += batch.Length;
                    if (sink.Length > MaxFileBytes) ArchiveSink(sink);
                }
                catch
                {
                    CloseWriter(sink);
                }
            }
        }

        private static void FlushAll()
        {
            if (_broken || !_ready) return;
            string?[] keys;
            lock (Gate)
            {
                if (Sinks.Count == 0) return;
                keys = new string?[Sinks.Count];
                Sinks.Keys.CopyTo(keys, 0);
            }
            foreach (string? key in keys)
            {
                if (key == null) continue;
                try { FlushSink(key); } catch { /* 忽略单个模块的落盘失败 */ }
            }

            // 跨天后顺手清一次过期文件
            if (!_cleanedThisDay)
            {
                lock (Gate) { _cleanedThisDay = true; }
                try { CleanupOldFiles(); } catch { }
            }
        }

        private static void EnsureWriter(Sink sink)
        {
            if (sink.Writer != null) return;
            try
            {
                string dir = Path.Combine(_root, sink.Category);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, TodayFileName());
                // FileShare.Read：程序自己写，别的进程（记事本/编辑器）能同时读
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                sink.Writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                sink.Path = path;
                sink.Length = fs.Length;
            }
            catch
            {
                CloseWriter(sink);
            }
        }

        /// <summary>当前文件超过上限：存成 .1（只留一份），等下次写入时重建。</summary>
        private static void ArchiveSink(Sink sink)
        {
            try
            {
                CloseWriter(sink);
                if (string.IsNullOrEmpty(sink.Path) || !File.Exists(sink.Path)) return;
                string backup = sink.Path + ".1";
                try { File.Delete(backup); } catch { }
                try { File.Move(sink.Path, backup); } catch { }
            }
            catch
            {
                // 备份失败就算了，下次再试
            }
            finally
            {
                sink.Length = 0;
            }
        }

        private static void CloseWriter(Sink sink)
        {
            try { sink.Writer?.Flush(); } catch { }
            try { sink.Writer?.Dispose(); } catch { }
            sink.Writer = null;
        }

        // =====================================================================
        // 过滤 / 分类 / 清理
        // =====================================================================

        private static bool IsEnabled(LogLevel level, string category)
        {
            if (CategoryMin.TryGetValue(category, out LogLevel min)) return level >= min;
            return level >= _globalMin;
        }

        /// <summary>
        /// 解析 CELESTE_LOG。支持 "info" / "debug,audio=trace" / "app=none" 三种写法；
        /// "none" 表示彻底关掉某个模块（比 trace 还低一级的静默）。
        /// </summary>
        private static void ReadFilterFromEnvironment()
        {
            string spec = (Environment.GetEnvironmentVariable("CELESTE_LOG") ?? string.Empty).Trim();
            lock (Gate) { _filterSpec = spec; }
            if (spec.Length == 0) return;

            foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;

                int eq = item.IndexOf('=');
                if (eq <= 0)
                {
                    if (TryParseLevel(item, out LogLevel lv)) _globalMin = lv;
                    continue;
                }

                string cat = item.Substring(0, eq).Trim();
                string lvText = item.Substring(eq + 1).Trim();
                if (cat.Length == 0) continue;
                if (lvText.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    CategoryMin[cat] = (LogLevel)99; // 什么都不过
                    continue;
                }
                if (TryParseLevel(lvText, out LogLevel per))
                {
                    CategoryMin[cat] = per;
                }
            }
        }

        private static bool TryParseLevel(string text, out LogLevel level)
        {
            level = _globalMin;
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Equals("verbose", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("trace", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Trace; return true; }
            if (text.Equals("debug", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Debug; return true; }
            if (text.Equals("info", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("information", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Info; return true; }
            if (text.Equals("warn", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("warning", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Warn; return true; }
            if (text.Equals("error", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Error; return true; }
            if (text.Equals("fatal", StringComparison.OrdinalIgnoreCase))
            { level = LogLevel.Fatal; return true; }
            return false;
        }

        /// <summary>分类名做白名单外的兜底：只留小写字母数字，超长截断，空值归到 app。</summary>
        private static string NormalizeCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category)) return LogCategory.App;
            var sb = new StringBuilder();
            foreach (char c in category.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                if (sb.Length >= 24) break;
            }
            string text = sb.ToString();
            return text.Length == 0 ? LogCategory.App : text;
        }

        private static string TodayFileName() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

        private static int TodayKey()
        {
            DateTime now = DateTime.Now;
            return now.Year * 10000 + now.Month * 100 + now.Day;
        }

        private static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return "TRC";
                case LogLevel.Debug: return "DBG";
                case LogLevel.Info: return "INF";
                case LogLevel.Warn: return "WRN";
                case LogLevel.Error: return "ERR";
                case LogLevel.Fatal: return "FTL";
                default: return "???";
            }
        }

        private static string AppendFields(string message, object?[]? fields)
        {
            if (fields == null || fields.Length == 0) return message;
            var sb = new StringBuilder(message);
            foreach (object? field in fields)
            {
                sb.Append(' ');
                sb.Append(field == null ? "(null)" : Convert.ToString(field, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BuildExceptionText(string message, Exception? ex)
        {
            if (ex == null) return message;
            return message + " | " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
        }

        /// <summary>删掉超过 RetentionDays 的日志文件（含 .1 备份）。只动 logs 目录里的东西。</summary>
        private static void CleanupOldFiles()
        {
            try
            {
                if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root)) return;
                DateTime limit = DateTime.Now.Date.AddDays(-RetentionDays);
                foreach (string dir in Directory.EnumerateDirectories(_root))
                {
                    foreach (string file in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            DateTime stamp = ReadFileStamp(file);
                            if (stamp < limit) File.Delete(file);
                        }
                        catch { /* 单个文件删不掉就跳过，别影响其他人 */ }
                    }
                }
            }
            catch
            {
                // 清理失败无所谓，下次再说
            }
        }

        private static DateTime ReadFileStamp(string path)
        {
            string name = Path.GetFileName(path);
            // yyyy-MM-dd.log  /  yyyy-MM-dd.log.1
            if (name.Length >= 10)
            {
                string dateText = name.Substring(0, 10);
                if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime parsed))
                {
                    return parsed;
                }
            }
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.Now; }
        }

        private sealed class Sink
        {
            public string Category = string.Empty;
            public List<string> Buffer = new();
            public StreamWriter? Writer;
            public string Path = string.Empty;
            public long Length;
        }
    }
}
