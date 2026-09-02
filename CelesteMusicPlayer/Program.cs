using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using WinRT;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自定义入口：先固定工作目录到 exe 所在目录，避免资源管理器/快捷方式启动时
    /// 当前目录不是输出目录导致依赖查找失败（VS 调试时常不复现）。
    /// </summary>
    public static class Program
    {
        private const uint MbIconError = 0x00000010;
        private const uint MbIconWarning = 0x00000030;

        private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/9.0";
        private const string WinAppSdkDownloadUrl = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

        // ---- 缺少运行环境时的可点击链接提示（TaskDialog）----
        // MessageBoxW 无法渲染可点击链接，故改用 comctl32 的 TaskDialog：
        // 内容里用 <A HREF="...">文字</A> 标记超链接，点击时在回调里用 ShellExecute 拉起浏览器。
        private const uint TdfEnableHyperlinks = 0x0040;
        private const uint TdfAllowDialogCancellation = 0x0008;
        private const uint TdcbfOkButton = 0x0001;
        private const uint TdnHyperlinkClicked = 0x04CC; // 1228
        private static readonly nint TdWarningIcon = (nint)0xFFFF;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TaskDialogConfig
        {
            public uint cbSize;
            public nint hwndParent;
            public nint hInstance;
            public uint dwFlags;
            public uint dwCommonButtons;
            public nint pszWindowTitle;
            public nint pszMainIcon;       // union(HICON / PCWSTR)
            public nint pszMainInstruction;
            public nint pszContent;
            public uint cButtons;
            public nint pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public nint pRadioButtons;
            public nint pszVerificationText;
            public nint pszExpandedInformation;
            public nint pszExpandedControlText;
            public nint pszCollapsedControlText;
            public TaskDialogCallback pfnCallback;
            public nint lpCallbackData;
            public uint cxWidth;
            public nint pszFooter;
            public nint pszFooterIcon;     // union(HICON / PCWSTR)
        }

        private delegate int TaskDialogCallback(nint hwnd, uint msg, nint wParam, nint lParam, nint lpRefData);

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int TaskDialogIndirect(ref TaskDialogConfig pTaskConfig, out int pnButton, out int pnRadioButton, out bool pfVerificationFlagChecked);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern nint ShellExecuteW(nint hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        // 务必以静态字段持有回调委托，避免 TaskDialog  Pump 消息期间被 GC 回收。
        private static readonly TaskDialogCallback s_taskDialogCallback = OnTaskDialogLinkClicked;

        [STAThread]
        private static void Main(string[] args)
        {
            string baseDir = AppContext.BaseDirectory;
            try
            {
                Directory.SetCurrentDirectory(baseDir);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }

            StartupLog.Write("Main begin, BaseDirectory=" + baseDir);

            // 框架依赖 + 非打包(Unpackaged) 发布时，Windows App SDK 运行时由目标机安装提供，
            // 不能把 BASE_DIRECTORY 指向 exe 目录（那里没有运行时）；自包含发布、或打包(MSIX)发布时
            // 运行时随包/在包内可寻址，沿用旧行为指过去。
            bool runtimeBundled = File.Exists(Path.Combine(baseDir, "Microsoft.WindowsAppRuntime.dll"));
            bool packaged = IsPackaged();
            if (runtimeBundled || packaged)
            {
                Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
            }

            // 框架依赖模式下运行时的可用性由系统引导层保证：
            // - 缺 .NET 9 桌面运行时 → apphost 启动前弹系统错误框（含官方下载链接）；
            // - 缺 Windows App SDK 运行时 → Bootstrap 自动初始化在 Main 之前失败并弹引导框。
            // 能走到这里即代表运行时已就绪，无需（也无法）再自行枚举系统包——
            // 非打包应用枚举 MSIX 包无权限（UnauthorizedAccessException），且 1.8 运行时不写注册表，
            // 此前曾因此把“已安装”误判为“未安装”导致启动即弹窗退出。
            try
            {
                ComWrappersSupport.InitializeComWrappers();
                StartupLog.Write("ComWrappers initialized");
                Application.Start(static _ =>
                {
                    DispatcherQueueSynchronizationContext context =
                        new(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    StartupLog.Write("Creating App");
                    new App();
                });
                StartupLog.Write("Application.Start returned");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Main", ex);
                // 启动期异常若与运行时缺失相关（框架依赖模式常见），给出安装指引而不是笼统的“启动失败”。
                if (!runtimeBundled && IsRuntimeMissingException(ex))
                {
                    ShowRuntimePrompt("未能初始化 Windows App SDK / .NET 运行环境。");
                    return;
                }

                try
                {
                    MessageBoxW(
                        0,
                        "启动失败：\n" + ex.Message + "\n\n请把同目录下 CelesteMusicPlayer.log 发给开发者。",
                        "CelesteMusicPlayer",
                        MbIconError);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
            }
        }

        /// <summary>是否以打包(MSIX)方式运行。非打包(Unpackaged)下访问 Package.Current 会抛异常，属预期。</summary>
        private static bool IsPackaged()
        {
            try
            {
                return Windows.ApplicationModel.Package.Current != null;
            }
            catch
            {
                // 非打包模式必然抛异常（E_NOTIMPL），静默即可，不写日志。
                return false;
            }
        }

        private static bool IsRuntimeMissingException(Exception ex)
        {
            if (ex is COMException com && (uint)com.HResult == 0x8000FFFF)
            {
                return true;
            }

            string msg = (ex.Message ?? string.Empty) + " " + (ex.GetType().FullName ?? string.Empty);
            return msg.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Windows App SDK", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("framework", StringComparison.OrdinalIgnoreCase);
        }

        private static void ShowRuntimePrompt(string detail)
        {
            string content =
                "CelesteMusicPlayer 需要以下运行环境才能启动：\n\n" +
                "• .NET 9 桌面运行时：<A HREF=\"" + DotNetDownloadUrl + "\">点此下载</A>\n" +
                "• Windows App SDK 运行时（1.8）：<A HREF=\"" + WinAppSdkDownloadUrl + "\">点此下载</A>\n\n" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail + "\n\n") +
                "点击上面的链接即可在浏览器打开下载页，安装后重新启动本程序。";

            if (TryShowRuntimePromptWithLinks("缺少运行环境", "请先安装所需的运行环境", content))
            {
                return;
            }

            // 兜底：环境中没有 comctl32 v6（TaskDialog 不可用）时，退回普通消息框（链接为纯文本）。
            string plain = "CelesteMusicPlayer 需要以下运行环境才能启动：\n\n"
                + "• .NET 9 桌面运行时\n  下载：" + DotNetDownloadUrl + "\n\n"
                + "• Windows App SDK 运行时（1.8）\n  下载：" + WinAppSdkDownloadUrl + "\n\n"
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail + "\n\n")
                + "请安装以上运行环境后重新启动本程序。";
            try
            {
                MessageBoxW(0, plain, "缺少运行环境", MbIconWarning);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
        }

        private static bool TryShowRuntimePromptWithLinks(string title, string instruction, string content)
        {
            nint titlePtr = nint.Zero;
            nint instructionPtr = nint.Zero;
            nint contentPtr = nint.Zero;
            try
            {
                titlePtr = Marshal.StringToCoTaskMemUni(title);
                instructionPtr = Marshal.StringToCoTaskMemUni(instruction);
                contentPtr = Marshal.StringToCoTaskMemUni(content);

                var cfg = new TaskDialogConfig
                {
                    cbSize = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                    dwFlags = TdfEnableHyperlinks | TdfAllowDialogCancellation,
                    dwCommonButtons = TdcbfOkButton,
                    pszWindowTitle = titlePtr,
                    pszMainInstruction = instructionPtr,
                    pszContent = contentPtr,
                    pszMainIcon = TdWarningIcon,
                    pfnCallback = s_taskDialogCallback,
                };

                int hr = TaskDialogIndirect(ref cfg, out _, out _, out _);
                return hr >= 0; // S_OK = 0；负数（如 E_NOTIMPL）表示不支持，走兜底
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught);
                return false;
            }
            finally
            {
                if (titlePtr != nint.Zero) Marshal.FreeCoTaskMem(titlePtr);
                if (instructionPtr != nint.Zero) Marshal.FreeCoTaskMem(instructionPtr);
                if (contentPtr != nint.Zero) Marshal.FreeCoTaskMem(contentPtr);
            }
        }

        private static int OnTaskDialogLinkClicked(nint hwnd, uint msg, nint wParam, nint lParam, nint lpRefData)
        {
            if (msg == TdnHyperlinkClicked && lParam != nint.Zero)
            {
                string? url = Marshal.PtrToStringUni(lParam);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    OpenUrlInBrowser(url);
                }
            }

            return 0;
        }

        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                ShellExecuteW(0, "open", url, null, null, 1 /* SW_SHOWNORMAL */);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
        }
    }
}
