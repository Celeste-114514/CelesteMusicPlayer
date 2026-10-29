using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 更新服务需要宿主配合的四件事：回 UI 线程、把状态推给网页、交接前落盘、退出程序。
    /// </summary>
    internal interface IUpdateHost
    {
        void RunOnUiThread(Action action);

        /// <summary>把状态推给网页（网页不在时静默丢弃）。</summary>
        void PublishUpdateStatus(object wire);

        /// <summary>交接给安装助手之前先把状态落盘（标记正常退出）。</summary>
        void FlushBeforeHandoff();

        /// <summary>真正退出主程序（安装助手已接手）。</summary>
        void ExitForInstall();
    }

    /// <summary>
    /// 网页设置页「关于 / 更新」的桥接层。真正的下载与安装全在 <see cref="UpdateService"/>，
    /// 这里只做两件事：把命令转进去、把状态翻译成网页认识的字段。
    ///
    /// 为什么不把下载逻辑写在这里：原生关于面板和网页都要用，写两份必然漂移
    /// （历史上一份改了另一份没改，出现过「网页能装、原生面板说没包」）。
    ///
    /// W13：从 MainWindow.WebUpdate.cs 搬出来（宿主四个口子）。
    /// </summary>
    internal sealed class WebUpdateService(IUpdateHost host)
    {
        private bool _pushHooked;

        /// <summary>状态推送只订阅一次。订阅后即使网页切到别的页面，下载照样继续、回来能看到进度。</summary>
        internal void EnsurePush()
        {
            if (_pushHooked) return;
            _pushHooked = true;

            UpdateService.Shared.Changed += status =>
            {
                try
                {
                    host.RunOnUiThread(() =>
                    {
                        try { host.PublishUpdateStatus(BuildWire(status)); }
                        catch (Exception caught) { StartupLog.WriteException("WebUpdate.Push", caught); }
                    });
                }
                catch (Exception caught) { StartupLog.WriteException("WebUpdate.Push", caught); }
            };
        }

        internal async Task<object> HandleAsync(JsonElement msg)
        {
            EnsurePush();
            string action = ReadString(msg, "action");
            if (string.IsNullOrWhiteSpace(action)) action = "check";

            switch (action)
            {
                case "check":
                    return BuildWire(await UpdateService.Shared.CheckAsync().ConfigureAwait(true));

                case "download":
                    return BuildWire(await UpdateService.Shared.DownloadAsync().ConfigureAwait(true));

                case "cancel":
                    UpdateService.Shared.CancelDownload();
                    return BuildWire(UpdateService.Shared.Current);

                case "discard":
                    UpdateService.Shared.Discard();
                    return BuildWire(UpdateService.Shared.Current);

                case "install":
                    return await InstallAsync().ConfigureAwait(true);

                case "status":
                    return BuildWire(UpdateService.Shared.Current);

                default:
                    throw new ArgumentException("未知的更新操作：" + action);
            }
        }

        /// <summary>
        /// 让助手接手安装。只有助手握手成功才退出主程序 —— 助手起不来时留在原地，
        /// 至少用户还有程序可用，安装包也还在。
        /// </summary>
        private async Task<object> InstallAsync()
        {
            bool handedOff = await UpdateService.Shared
                .BeginInstallAsync(() =>
                {
                    try
                    {
                        host.FlushBeforeHandoff();
                        StartupLog.Write("[update] 交接前落盘完成，准备退出");
                    }
                    catch (Exception caught) { StartupLog.WriteException("WebUpdate.Flush", caught); }
                    return Task.CompletedTask;
                })
                .ConfigureAwait(true);

            if (!handedOff)
                return BuildWire(UpdateService.Shared.Current);

            host.RunOnUiThread(async () =>
            {
                // 给网页一拍时间把「正在安装」画出来，再真的退出。
                await Task.Delay(600);
                try { host.ExitForInstall(); }
                catch (Exception caught) { StartupLog.WriteException("WebUpdate.Exit", caught); }
            });

            return BuildWire(UpdateService.Shared.Current);
        }

        /// <summary>
        /// 状态 → 网页字段。老字段（status/current/latest/downloadUrl/checksum…）保持不变，
        /// 免得已发布的网页脚本因为字段改名整块显示不出来；新字段只是追加。
        /// </summary>
        internal static Dictionary<string, object?> BuildWire(UpdateStatus status)
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

        private static string ReadString(JsonElement msg, string property)
        {
            if (msg.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            return "";
        }
    }
}
