using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页「启动恢复」页的桥接层。
    ///
    /// 以前这里是一个原生弹窗（"上次可能异常退出"）—— 但它弹在已经被藏起来的主窗口上，
    /// 用户根本看不见，等于什么都没说。现在换成网页自己画的一张恢复页：
    /// 判断依据仍只有一处 —— <see cref="CrashReportService"/> 的运行标记与崩溃历史，这里不另存状态。
    ///
    /// 一次运行只问一次：用户选过怎么办（正常启动 / 安全模式 / 重载界面）之后，
    /// 即使网页重载也不会再拦一次。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>本次运行已经回答过恢复页，不再重复弹。</summary>
        private static bool _recoveryAnswered;

        /// <summary>
        /// 本次运行是否处于安全模式：不自动续播、界面关掉背景层与动效。
        /// 只活在内存里，不写设置 —— 安全模式是"这一次"，不是用户偏好。
        /// </summary>
        internal static bool SafeModeActive { get; private set; }

        internal async Task<object> WebRecoveryAsync(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (string.IsNullOrWhiteSpace(action)) action = "status";

            switch (action)
            {
                case "status":
                    return RecoveryWire(RecoveryShouldShow());

                case "normal":
                    _recoveryAnswered = true;
                    StartupLog.Write("[recovery] 用户选择正常启动。");
                    return RecoveryWire(false);

                case "safe":
                    _recoveryAnswered = true;
                    EnterSafeMode();
                    return RecoveryWire(false);

                case "reload":
                    // 重载之后如果界面还是坏的，也不该再拦一次恢复页 —— 用户已经做过选择。
                    _recoveryAnswered = true;
                    StartupLog.Write("[recovery] 用户要求重载界面。");
                    _webShellWindow?.ReloadShell();
                    return RecoveryWire(false);

                case "export":
                    return await ExportDiagnosticsAsync("启动恢复页「导出诊断」").ConfigureAwait(true);

                case "openFolder":
                    OpenDiagnosticsFolder();
                    return RecoveryWire(false);

                default:
                    throw new ArgumentException("未知的恢复操作：" + action);
            }
        }

        /// <summary>这次启动该不该弹恢复页：只看"上次退出时留下了运行标记"，且本次还没回答过。</summary>
        internal static bool RecoveryShouldShow()
        {
            if (_recoveryAnswered) return false;
            try { return AppSettingsStore.WasUncleanExitLastTime; }
            catch (Exception caught)
            {
                StartupLog.WriteException("Recovery.ShouldShow", caught);
                return false;
            }
        }

        /// <summary>给外壳在 ready 时主动推一条恢复页；不该显示返回 null。</summary>
        internal static object? BuildRecoveryPush() => RecoveryShouldShow() ? RecoveryWire(true) : null;

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
                // 已经响起来的情况（曲库比恢复页先跑完）：先停下，让用户自己决定要不要播。
                if (IsEnginePlayingNow) TogglePlayPausePublic();
            }
            catch (Exception caught) { StartupLog.WriteException("Recovery.SafeMode.pause", caught); }
        }

        private static Dictionary<string, object?> RecoveryWire(bool show)
        {
            Dictionary<string, object?> wire = CrashReportService.Snapshot();
            wire["ok"] = true;
            wire["show"] = show;
            wire["safeMode"] = SafeModeActive;
            wire["settingsRecovered"] = AppSettingsStore.SettingsWereRecovered;
            wire["logsDirectory"] = Path.Combine(CrashReportService.DataRoot, "logs");
            // 崩溃记账里的 Marker 是"上一次运行写下的启动时间"，比"几号发现的"更贴近用户记忆。
            wire["lastStartText"] = LastStartText();
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
