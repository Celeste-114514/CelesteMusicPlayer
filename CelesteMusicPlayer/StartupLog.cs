using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 启动轨迹日志 —— 现在是 <see cref="AppLog"/> 的门面，自己不再写文件。
    ///
    /// 为什么留着它：全项目 1000 多处 <c>StartupLog.Write(...)</c> 调用点，
    /// 逐个改成 <c>AppLog.Info(LogCategory.Xxx, ...)</c> 既无聊又容易漏。
    /// 所以这里用 [CallerFilePath] 拿到"这一行是哪个源文件写的"，
    /// 按文件名关键词判断它该进 app / audio / dsp / native / net / lib 哪个文件。
    ///
    /// 新代码请直接写 <c>AppLog.Info(LogCategory.Audio, "...")</c>，
    /// 别再用 StartupLog —— 它只是个兼容层，判断规则不可能完全准。
    /// </summary>
    internal static class StartupLog
    {
        /// <summary>写一条 Info 日志。category 由调用方所在的源文件名自动推断。</summary>
        public static void Write(
            string message,
            [CallerFilePath] string callerPath = "")
        {
            AppLog.Info(GuessCategory(callerPath), message);
        }

        /// <summary>写一条 Error 日志（带异常详情与堆栈）。</summary>
        public static void WriteException(
            string where,
            Exception? ex,
            [CallerFilePath] string callerPath = "")
        {
            AppLog.Error(GuessCategory(callerPath), where, ex);
        }

        /// <summary>立即落盘。</summary>
        public static void Flush() => AppLog.Flush();

        /// <summary>兼容旧用法：app 模块今天的日志文件。</summary>
        public static string CurrentFilePath => AppLog.CurrentFilePath;

        /// <summary>日志根目录（托盘菜单「打开日志文件夹」用）。</summary>
        public static string LogsDirectory => AppLog.LogsDirectory;

        // =====================================================================
        // 文件名 -> 模块 的推断规则
        //
        // 顺序即优先级，先命中的赢。放在这里而不是按命名空间，是因为本项目的
        // partial 类全挤在同一个命名空间里，命名空间区分度为零。
        // =====================================================================
        private static string GuessCategory(string callerPath)
        {
            if (string.IsNullOrEmpty(callerPath)) return LogCategory.App;

            string name = Path.GetFileName(callerPath);

            // UI / 外壳层：这些文件虽然可能带 Dsp 字样（MainWindow.DspComp.cs），
            // 但它们是界面代码，出的问题十有八九是渲染/交互，不是音频算法。
            if (Contains(name, "MainWindow") || Contains(name, "Geek") ||
                Contains(name, "UiTheme") || Contains(name, "ThemeColor") ||
                Contains(name, "SettingsWindow") || Contains(name, "AppTrayIcon") ||
                Contains(name, "TaskbarThumbnail") || Contains(name, "App.xaml") ||
                Contains(name, "Program.cs") || Contains(name, "Lyrics") ||
                Contains(name, "Terminal") || Contains(name, "Visualizer"))
            {
                return LogCategory.App;
            }

            // DSP 机架与算法
            if (Contains(name, "Dsp") || Contains(name, "EqApo") || Contains(name, "Opra") ||
                Contains(name, "ManagedDsp") || Contains(name, "Crossfeed") ||
                Contains(name, "Compressor") || Contains(name, "Matrix") ||
                Contains(name, "LevelMeter"))
            {
                return LogCategory.Dsp;
            }

            // 原生内核
            if (Contains(name, "Native") || Contains(name, "EchoCore") ||
                Contains(name, "CelesteCore") || Contains(name, "Asio") ||
                Contains(name, "DspCoreInterop"))
            {
                return LogCategory.Native;
            }

            // 联网
            if (Contains(name, "Online") || Contains(name, "WebDav") ||
                Contains(name, "Http") || Contains(name, "Updater") ||
                Contains(name, "Update"))
            {
                return LogCategory.Net;
            }

            // 本地曲库
            if (Contains(name, "Library") || Contains(name, "Tag") ||
                Contains(name, "Playlist") || Contains(name, "History") ||
                Contains(name, "Search") || Contains(name, "Ffmpeg") ||
                Contains(name, "Pinyin"))
            {
                return LogCategory.Lib;
            }

            // 音频链路（兜底大类，放在最后，免得把上面更具体的吃掉）
            if (Contains(name, "HiFi") || Contains(name, "Audio") ||
                Contains(name, "SeamlessWave") || Contains(name, "Dsd") ||
                Contains(name, "Wave") || Contains(name, "Playback") ||
                Contains(name, "Output") || Contains(name, "Crossfade") ||
                Contains(name, "Src") || Contains(name, "AppSettings"))
            {
                return LogCategory.Audio;
            }

            return LogCategory.App;
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
