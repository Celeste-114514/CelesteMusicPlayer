using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 应用程序入口。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            StartupLog.Write("App ctor");
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
                catch
                {
                }
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
                catch
                {
                }
            };
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            StartupLog.Write("OnLaunched");
            // 应用资源在启动回调里再注册，供播放列表列宽 Binding 使用
            Resources["PlaylistColumns"] = PlaylistColumnWidths.Instance;

            // 主题色：必须在窗口创建前覆盖系统强调色资源键（渲染后修改会触发 WinUI 原生崩溃）
            try
            {
                ThemeColorService.ApplyThemeResources(AppSettingsStore.Load());
            }
            catch
            {
            }

            try
            {
                _window = new MainWindow();
                StartupLog.Write("MainWindow created");
                _window.Activate();
                StartupLog.Write("MainWindow activated");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OnLaunched", ex);
                throw;
            }
        }
    }
}
