using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页「恢复」卡片的桥接层 —— 这张卡服务两件完全不同的事，别混：
    ///
    /// ① <b>页面崩溃</b>（mode = crash）：用户正在用的时候网页进程突然挂了。
    ///    宿主自动重载一次，回来之后推这张卡说明"刚才崩了、已经替你重载"，并给重载 / 安全模式 / 导出诊断。
    ///    触发点只有一个：WebView2 的 ProcessFailed（见 WebShellWindow.OnWebProcessFailed）。
    ///
    /// ② <b>上次没正常退出</b>（mode = unclean）：程序上次没收尾就没了（崩溃 / 强杀 / 断电）。
    ///    这条**不是**崩溃当时弹的，也不该在刚进主界面时拦人 —— 要等启动加载走完
    ///    （曲库恢复到能用）之后再告诉用户，见 WebShellWindow.NotifyStartupSettled。
    ///    判断依据只有一处：CrashReportService 的运行标记与崩溃历史。
    ///
    /// 两条都只报一次：用户关掉（dismiss）之后，本次运行不再重复弹。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>本次运行已经看过「上次没正常退出」这条，不再重复弹。
        /// 注意：它**不管**崩溃卡 —— 关掉一条告知，不该让下一次真崩溃时没人管。</summary>
        private static bool _uncleanDismissed;

        /// <summary>本次运行页面崩溃过几次（崩溃卡上要报"这是第几次"）。</summary>
        internal static int PageCrashCount { get; private set; }

        /// <summary>
        /// 本次运行是否处于安全模式：不自动续播、界面关掉背景层与动效。
        /// 只活在内存里，不写设置 —— 安全模式是"这一次"，不是用户偏好。
        /// </summary>
        internal static bool SafeModeActive { get; private set; }

        /// <summary>页面崩溃记账：宿主外壳每次崩都会调一次，只留结论给卡片和日志用。</summary>
        internal static void RecordPageCrash(string kind, int sessionCount)
        {
            PageCrashCount = Math.Max(PageCrashCount, sessionCount);
            StartupLog.Write("[crash] 页面崩溃记账 #" + sessionCount + "：" + kind);
        }

        internal async Task<object> WebRecoveryAsync(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (string.IsNullOrWhiteSpace(action)) action = "status";

            switch (action)
            {
                case "status":
                    return NoticeWire(RecoveryShouldShow() ? "unclean" : "none");

                // 「知道了」：只把"上次没正常退出"这条标记为已读，崩溃卡不受影响。
                case "dismiss":
                case "normal":
                    if (ReadString(msg, "mode") != "crash") _uncleanDismissed = true;
                    StartupLog.Write("[recovery] 用户关掉了提示卡。");
                    return NoticeWire("none");

                case "safe":
                    _uncleanDismissed = true;
                    EnterSafeMode();
                    return NoticeWire("none");

                case "reload":
                    // 重载之后如果界面还是坏的，也不该再拦 —— 用户已经做过选择。
                    _uncleanDismissed = true;
                    StartupLog.Write("[recovery] 用户要求重载界面。");
                    _webShellWindow?.ReloadShell();
                    return NoticeWire("none");

                case "export":
                    return await ExportDiagnosticsAsync("恢复页「导出诊断」").ConfigureAwait(true);

                case "openFolder":
                    OpenDiagnosticsFolder();
                    return NoticeWire("none");

                default:
                    throw new ArgumentException("未知的恢复操作：" + action);
            }
        }

        /// <summary>上次没正常退出、且这次还没告诉过用户。</summary>
        internal static bool RecoveryShouldShow()
        {
            if (_uncleanDismissed) return false;
            try { return AppSettingsStore.WasUncleanExitLastTime; }
            catch (Exception caught)
            {
                StartupLog.WriteException("Recovery.ShouldShow", caught);
                return false;
            }
        }

        /// <summary>页面崩溃卡：刚崩完、已经自动重载，说明现场并给后续动作。不该显示返回 null。</summary>
        internal static object? BuildCrashPush(int sessionCount) => CrashWire(sessionCount);

        /// <summary>「上次没正常退出」卡：启动加载走完之后才该调。不该显示返回 null。</summary>
        internal static object? BuildUncleanPush() => RecoveryShouldShow() ? UncleanWire() : null;

        /// <summary>
        /// 安全模式：把"可能惹祸的东西"这一轮都关掉。不做侵入式操作 ——
        /// 只是不自动续播、并把界面上的背景层与动效停掉，用户仍然可以手动播放。
        /// </summary>
        private void EnterSafeMode()
        {
            if (SafeModeActive) return;
            SafeModeActive = true;
            StartupLog.Write("[recovery] 本次以安全模式运行：不自动续播，界面关闭背景层与动效。");

            try
            {
                // 已经响起来的情况：先停下，让用户自己决定要不要播。
                if (IsEnginePlayingNow) TogglePlayPausePublic();
            }
            catch (Exception caught) { StartupLog.WriteException("Recovery.SafeMode.pause", caught); }
        }

        private static Dictionary<string, object?> CrashWire(int sessionCount)
        {
            Dictionary<string, object?> wire = CrashReportService.Snapshot();
            wire["ok"] = true;
            wire["show"] = true;
            wire["mode"] = "crash";
            wire["sessionCrashes"] = Math.Max(1, sessionCount);
            wire["safeMode"] = SafeModeActive;
            wire["settingsRecovered"] = AppSettingsStore.SettingsWereRecovered;
            wire["logsDirectory"] = Path.Combine(CrashReportService.DataRoot, "logs");
            wire["lastStartText"] = string.Empty;
            return wire;
        }

        private static Dictionary<string, object?> UncleanWire()
        {
            Dictionary<string, object?> wire = CrashReportService.Snapshot();
            wire["ok"] = true;
            wire["show"] = true;
            wire["mode"] = "unclean";
            wire["safeMode"] = SafeModeActive;
            wire["settingsRecovered"] = AppSettingsStore.SettingsWereRecovered;
            wire["logsDirectory"] = Path.Combine(CrashReportService.DataRoot, "logs");
            // 崩溃记账里的 Marker 是"上一次运行写下的启动时间"，比"几号发现的"更贴近用户记忆。
            wire["lastStartText"] = LastStartText();
            return wire;
        }

        /// <summary>status 动作的回包：只报"有没有话要说"，不重复弹卡片。</summary>
        private static Dictionary<string, object?> NoticeWire(string mode)
        {
            Dictionary<string, object?> wire = CrashReportService.Snapshot();
            wire["ok"] = true;
            wire["show"] = false;
            wire["mode"] = mode;
            wire["safeMode"] = SafeModeActive;
            return wire;
        }

        private static string LastStartText()
        {
            try
            {
                List<CrashReportService.CrashEntry> history = CrashReportService.ReadCrashHistory();
                if (history.Count == 0) return string.Empty;
                string marker = history[^1].Marker ?? string.Empty;
                if (marker.Length == 0) return string.Empty;

                using JsonDocument document = JsonDocument.Parse(marker);
                if (!document.RootElement.TryGetProperty("startedUtc", out JsonElement started)) return string.Empty;
                string? text = started.GetString();
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
                    ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("Recovery.LastStartText", caught);
                return string.Empty;
            }
        }
    }
}
