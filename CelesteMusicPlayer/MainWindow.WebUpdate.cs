using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页设置页「关于 / 更新」的桥接层。真正的下载与安装全在 <see cref="UpdateService"/>，
    /// 这里只做两件事：把命令转进去、把状态翻译成网页认识的字段。
    ///
    /// 为什么不把下载逻辑写在这里：原生关于面板和网页都要用，写两份必然漂移
    /// （历史上一份改了另一份没改，出现过「网页能装、原生面板说没包」）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _updatePushHooked;

        /// <summary>
        /// 状态推送只订阅一次。订阅后即使网页切到别的页面，下载照样继续、回来能看到进度。
        /// </summary>
        private void EnsureUpdatePush()
        {
            if (_updatePushHooked) return;
            _updatePushHooked = true;

            UpdateService.Shared.Changed += status =>
            {
                try
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try { _webShellWindow?.NotifyUpdateStatus(BuildUpdateWire(status)); }
                        catch (Exception caught) { StartupLog.WriteException("MainWindow.WebUpdate.Push", caught); }
                    });
                }
                catch (Exception caught) { StartupLog.WriteException("MainWindow.WebUpdate.Push", caught); }
            };
        }

        private async Task<object> WebUpdateAsync(JsonElement msg)
        {
            EnsureUpdatePush();
            string action = ReadString(msg, "action");
            if (string.IsNullOrWhiteSpace(action)) action = "check";

            switch (action)
            {
                case "check":
                    return BuildUpdateWire(await UpdateService.Shared.CheckAsync().ConfigureAwait(true));

                case "download":
                    return BuildUpdateWire(await UpdateService.Shared.DownloadAsync().ConfigureAwait(true));

                case "cancel":
                    UpdateService.Shared.CancelDownload();
                    return BuildUpdateWire(UpdateService.Shared.Current);

                case "discard":
                    UpdateService.Shared.Discard();
                    return BuildUpdateWire(UpdateService.Shared.Current);

                case "install":
                    return await InstallUpdateAsync().ConfigureAwait(true);

                case "status":
                    return BuildUpdateWire(UpdateService.Shared.Current);

                default:
                    throw new ArgumentException("未知的更新操作：" + action);
            }
        }

        /// <summary>
        /// 让助手接手安装。只有助手握手成功才退出主程序 —— 助手起不来时留在原地，
        /// 至少用户还有程序可用，安装包也还在。
        /// </summary>
        private async Task<object> InstallUpdateAsync()
        {
            bool handedOff = await UpdateService.Shared
                .BeginInstallAsync(() =>
                {
                    try
                    {
                        AppSettingsStore.MarkAppCleanExit();
                        StartupLog.Write("[update] 交接前落盘完成，准备退出");
                    }
                    catch (Exception caught) { StartupLog.WriteException("MainWindow.WebUpdate.Flush", caught); }
                    return Task.CompletedTask;
                })
                .ConfigureAwait(true);

            if (!handedOff)
                return BuildUpdateWire(UpdateService.Shared.Current);

            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                // 给网页一拍时间把「正在安装」画出来，再真的退出。
                await Task.Delay(600);
                try { ExitApplication(); }
                catch (Exception caught) { StartupLog.WriteException("MainWindow.WebUpdate.Exit", caught); }
            });

            return BuildUpdateWire(UpdateService.Shared.Current);
        }

        /// <summary>
        /// 状态 → 网页字段。老字段（status/current/latest/downloadUrl/checksum…）保持不变，
        /// 免得已发布的网页脚本因为字段改名整块显示不出来；新字段只是追加。
        /// </summary>
        private static Dictionary<string, object?> BuildUpdateWire(UpdateStatus status)
        {
            string legacyStatus = status.Stage switch
            {
                UpdateStage.Available when status.ErrorCode == "unsupported" => "unsupported",
                UpdateStage.Available when status.HasUpdate => "available",
                UpdateStage.Failed => "failed",
                UpdateStage.Available => "available",
                _ => "up-to-date"
            };

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = legacyStatus,
                ["stage"] = status.StageName,
                ["current"] = status.CurrentVersion,
                ["latest"] = status.Version,
                ["latestTag"] = status.LatestTag,
                ["hasUpdate"] = status.HasUpdate,
                // 老字段语义是「有没有可以下手的安装包」，不是「现在能不能装」
                ["canInstall"] = status.CanDownload,
                ["canDownload"] = status.CanDownload,
                ["canCancel"] = status.CanCancel,
                ["canInstallNow"] = status.CanInstall,
                ["packageKind"] = status.PackageKind,
                ["downloadUrl"] = status.DownloadUrl,
                ["releaseUrl"] = status.ReleaseUrl,
                ["checksum"] = status.ChecksumState,
                ["errorCategory"] = status.ErrorCode,
                ["error"] = status.ErrorMessage,
                ["prereleaseIgnored"] = status.PrereleaseIgnored,
                ["stale"] = status.Stale,
                ["lastKnownTag"] = status.LastKnownTag,
                ["lastKnownReleaseUrl"] = status.LastKnownReleaseUrl,
                ["lastKnownDownloadUrl"] = string.Empty,
                ["checkedAt"] = status.CheckedAt,
                ["bytesReceived"] = status.BytesReceived,
                ["bytesTotal"] = status.BytesTotal,
                ["transactionId"] = status.TransactionId,
                ["installDirectory"] = status.InstallDirectory
            };
        }
    }
}
