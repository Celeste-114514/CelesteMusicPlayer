using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 应用程序入口。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        private const int SwRestore = 9;

        public App()
        {
            StartupLog.Write("App ctor");

            // 修复 WinUI3 已知 bug（microsoft-ui-xaml #10805/#11024）：
            // MenuFlyoutItem 等右键菜单控件在非中文首选语言下，中文会走错误字形
            // 渲染路径，显示成「瘦长/斜」的日文样式字形（如「添」「复」等）。
            // 显式把首选语言设为简体中文，让菜单控件回退到正确的中文字形路径。
            // 注意：必须在 InitializeComponent() 之前设置——XAML 模板/资源在
            // InitializeComponent 阶段就按当前语言解析字形，之后再设不会生效。
            // 标签必须是标准 BCP-47 的 "zh-CN"（简体中文·中国），而非无效的
            // "zh-Hans-CN"（脚本标签与区域标签不能拼接）。
            try
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
            }
            catch (Exception ex) { StartupLog.WriteException("App.PrimaryLanguageOverride", ex); }

            InitializeComponent();
            StartupLog.Write("App InitializeComponent done");

            // 注意：不要在构造函数里访问 Application.Resources，
            // WinUI 此时尚未就绪，会抛 COMException (0x8000FFFF)。

            // 非 UI 线程的未处理异常:记录详情以便定位(否则弹实时调试器)
            AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
            {
                try
                {
                    StartupLog.WriteException("AppDomain.UnhandledException", exArgs.ExceptionObject as Exception);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("App.xaml.cs", caught); }
            };

            UnhandledException += (sender, e) =>
            {
                e.Handled = true;
                StartupLog.WriteException("App.UnhandledException", e.Exception);
                Debug.WriteLine("======= 未处理异常 =======");
                Debug.WriteLine(e.Exception.ToString());
                Debug.WriteLine("=========================");

                try
                {
                    if (_window?.Content?.XamlRoot != null)
                    {
                        ContentDialog dialog = new()
                        {
                            Title = "程序出错",
                            Content = e.Exception.GetType().Name + "\n\n" + e.Exception.Message,
                            CloseButtonText = "确定",
                            XamlRoot = _window.Content.XamlRoot
                        };
                        _ = dialog.ShowAsync();
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("App.xaml.cs", caught); }
            };
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 醒目 startup banner:让用户/排错者一眼看出当前跑的是哪个版本、日志在哪
            string procVer = "unknown";
            try { procVer = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"; } catch { }
            StartupLog.Write("=======================================================");
            StartupLog.Write("=== CelesteMusicPlayer v" + procVer + " 启动 ===");
            StartupLog.Write("=== 日志: " + StartupLog.CurrentFilePath + " ===");
            StartupLog.Write("=== PID=" + Environment.ProcessId + "  启动时间=" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
            StartupLog.Write("=======================================================");
            StartupLog.Write("OnLaunched");
            // 应用资源在启动回调里再注册，供播放列表列宽 Binding 使用
            Resources["PlaylistColumns"] = PlaylistColumnWidths.Instance;

            // 双击音频文件打开：命令行参数里带的那个音频文件路径（没有就是 null）
            string? externalFile = QuickPlayLauncher.TryGetFileFromCommandLine();
            StartupLog.Write("OnLaunched externalFile=" + (externalFile ?? "(none)"));

            // 单实例：已经有一个实例在跑，就把命令转交给它，本进程立刻退出，
            // 不弹第二个窗口、也不让两处同时出声。
            bool primary = QuickPlayLauncher.TryBecomePrimary();
            if (!primary)
            {
                StartupLog.Write("已有实例在运行，转交命令后退出本进程");
                QuickPlayLauncher.SendToRunningInstance(externalFile != null ? "PLAY:" + externalFile : "ACTIVATE:");
                Environment.Exit(0);
                return;
            }

            // 主实例：清掉上次异常退出残留的命令文件，然后开始监听后续双击
            QuickPlayLauncher.ClearStaleRelayFiles();
            QuickPlayLauncher.StartListening(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), HandleRelayCommand);

            // 主题色：必须在窗口创建前覆盖系统强调色资源键（渲染后修改会触发 WinUI 原生崩溃）
            try
            {
                ThemeColorService.ApplyThemeResources(AppSettingsStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("App.xaml.cs", caught); }

            // 开机自启动注册表按设置校正（幂等）：
            // 安装包（NSIS）安装时会直接写 Run 项，设置里关闭后原先不会被清理。
            // 每次启动都同步一次，设置是关的就清掉残留 → 修复「设置里关着却仍开机自启」。
            AutoRunHelper.SyncWithSettings();

            try
            {
                // Phase E：曲库 SQLite 迁移/初始化（首次建库时自动备份旧 JSON，窗口和任何 store 前先就绪）
                try
                {
                    LibraryDb.EnsureMigrated();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("App.OnLaunched.LibraryDb", caught); }

                // 双击音频文件：一律打开主界面播放（不再另开独立小窗口）。
                // 文件路径交给主窗口，等它把曲库恢复流程走完再播，避免和「启动续播」抢同一条播放链路。
                MainWindow main = new();
                main.PendingStartupFile = externalFile;
                _window = main;
                StartupLog.Write(externalFile != null
                    ? "MainWindow created（待播外部文件）"
                    : "MainWindow created");

                _window.Activate();
                StartupLog.Write("Window activated");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OnLaunched", ex);
                throw;
            }

            // 首次运行：引导用户勾选要关联哪些音频格式（Bandizip 那种做法），只弹一次。
            // 双击文件启动（externalFile != null）时不弹 —— 那种情况下用户要的是马上出声。
            if (externalFile == null)
            {
                ScheduleFirstRunAssociationPrompt();
            }
        }

        /// <summary>
        /// 首次运行弹出「文件关联」窗口。
        ///
        /// 两个刻意的处理：
        /// 1) 先写标记再弹窗 —— 万一弹窗本身失败（比如窗口创建异常），也不会每次启动都弹，
        ///    用户还有设置界面里的「关联文件…」可以手动打开。
        /// 2) 压到 Idle 优先级入队 —— 让主界面先渲染出来，避免刚启动就两个窗口抢焦点、
        ///    看起来像卡住。
        /// </summary>
        private void ScheduleFirstRunAssociationPrompt()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (settings.FileAssociationPromptShown) return;

                settings.FileAssociationPromptShown = true;
                AppSettingsStore.Save(settings);

                Microsoft.UI.Dispatching.DispatcherQueue? queue =
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    FileAssociationWindow.ShowOrActivate();
                    return;
                }

                if (!queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try { FileAssociationWindow.ShowOrActivate(); }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("App.FirstRunAssociation", caught); }
                }))
                {
                    StartupLog.WriteException("App.FirstRunAssociation", new InvalidOperationException("TryEnqueue 返回 false，首次引导未显示"));
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("App.ScheduleFirstRunAssociationPrompt", caught);
            }
        }

        /// <summary>处理其它进程（后一次双击）转交过来的命令。</summary>
        private void HandleRelayCommand(string command)
        {
            try
            {
                StartupLog.Write("relay command: " + command);

                if (command.StartsWith("PLAY:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = command.Substring("PLAY:".Length).Trim();
                    if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

                    BringCurrentWindowToFront();

                    if (_window is MainWindow main)
                    {
                        main.PlayExternalFileAsync(path);
                    }
                }
                else if (command.StartsWith("ACTIVATE:", StringComparison.OrdinalIgnoreCase))
                {
                    BringCurrentWindowToFront();
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("HandleRelayCommand", ex);
            }
        }

        /// <summary>把当前窗口提到最前（最小化时先还原）。</summary>
        private void BringCurrentWindowToFront()
        {
            try
            {
                if (_window == null) return;

                nint hWnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                if (hWnd == 0) return;

                ShowWindow(hWnd, SwRestore);
                SetForegroundWindow(hWnd);
                _window.Activate();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("BringCurrentWindowToFront", ex);
            }
        }
    }
}
