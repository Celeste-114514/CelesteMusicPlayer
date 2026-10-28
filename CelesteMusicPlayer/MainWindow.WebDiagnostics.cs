using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页「关于 / 诊断」的桥接层。收集逻辑全在 <see cref="CrashReportService"/>，
    /// 这里只负责：把命令转进去、把当前进程的现场补进报告、把结果翻成网页认识的字段。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal async Task<object> WebDiagnosticsAsync(JsonElement msg)
        {
            string action = ReadString(msg, "action");
            if (string.IsNullOrWhiteSpace(action)) action = "status";

            switch (action)
            {
                case "status":
                    return DiagnosticsWire();

                case "export":
                    return await ExportDiagnosticsAsync().ConfigureAwait(true);

                case "openFolder":
                    OpenDiagnosticsFolder();
                    return DiagnosticsWire();

                default:
                    throw new ArgumentException("未知的诊断操作：" + action);
            }
        }

        private async Task<object> ExportDiagnosticsAsync()
        {
            // 导出要读几十个日志文件，放后台线程做，别让界面卡住。
            string path = await Task.Run(() => CrashReportService.Export("设置页「导出诊断」", CollectRuntimeFacts()))
                .ConfigureAwait(true);

            var wire = DiagnosticsWire();
            wire["path"] = path;
            wire["fileName"] = Path.GetFileName(path);
            return wire;
        }

        private static void OpenDiagnosticsFolder()
        {
            string directory = CrashReportService.DiagnosticsDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + directory + "\"",
                UseShellExecute = true
            });
        }

        /// <summary>报告里的「运行现场」一节：这些值每次都不一样，只能由宿主现取。</summary>
        private List<string> CollectRuntimeFacts()
        {
            var facts = new List<string>();
            try
            {
                facts.Add("进程启动：" + Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

                using Process process = Process.GetCurrentProcess();
                facts.Add("工作集内存：" + (process.WorkingSet64 / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + " MB");

                facts.Add("曲库曲目数：" + UserPlaylist.Count.ToString(CultureInfo.InvariantCulture));

                if (AppSettingsStore.SettingsWereRecovered)
                {
                    facts.Add("注意：本次启动时设置文件损坏，已用默认值恢复（原文件有备份）。");
                }
            }
            catch (Exception caught)
            {
                facts.Add("收集运行现场时出错：" + caught.GetType().Name + " " + caught.Message);
            }

            return facts;
        }

        private static Dictionary<string, object?> DiagnosticsWire()
        {
            Dictionary<string, object?> snapshot = CrashReportService.Snapshot();
            snapshot["ok"] = true;
            return snapshot;
        }
    }
}
