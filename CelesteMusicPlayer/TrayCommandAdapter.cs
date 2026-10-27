using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘菜单的命令层：把网页/原生菜单回传的 actionId 映射成真正的方法调用。
    ///
    /// 这一层故意薄：菜单是谁画的（WebView2 / 系统原生降级菜单）完全不影响这里，
    /// 也不需要知道任何 WinUI 类型 —— 换承载方式时这里一行都不用改。
    /// </summary>
    internal sealed class TrayCommandAdapter
    {
        private readonly MainWindow _owner;

        internal TrayCommandAdapter(MainWindow owner) => _owner = owner;

        /// <summary>返回是否真的执行了。不在白名单里的动作直接丢弃。</summary>
        internal bool Invoke(string? actionId)
        {
            if (!TrayMenuModel.IsKnownAction(actionId)) return false;
            try
            {
                switch (actionId)
                {
                    case TrayMenuModel.Actions.ShowMain:
                        _owner.RestoreFromTray();
                        break;
                    case TrayMenuModel.Actions.ShowMini:
                        _owner.ShowMiniPlayerWindow();
                        break;
                    case TrayMenuModel.Actions.ShowScattered:
                        _owner.ShowScatteredWindow();
                        break;
                    case TrayMenuModel.Actions.ToggleLyrics:
                        _owner.ToggleDesktopLyricsPublic();
                        break;
                    case TrayMenuModel.Actions.Previous:
                        _owner.PreviousPublic();
                        break;
                    case TrayMenuModel.Actions.TogglePlay:
                        _owner.TogglePlayPausePublic();
                        break;
                    case TrayMenuModel.Actions.Next:
                        _owner.NextPublic();
                        break;
                    case TrayMenuModel.Actions.Favorite:
                        _owner.FavoriteCurrentPublic();
                        break;
                    case TrayMenuModel.Actions.OpenSettings:
                        _owner.ShowWebPage("settings");
                        break;
                    case TrayMenuModel.Actions.OpenUpdate:
                        // 方案要求：更新提示必须落到网页里的目标位置，不能弹旧的 WinUI 关于面板
                        _owner.ShowWebPage("settings");
                        break;
                    case TrayMenuModel.Actions.OpenLogs:
                        OpenLogsFolder();
                        break;
                    case TrayMenuModel.Actions.Exit:
                        _owner.ExitFromTray();
                        break;
                    default:
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TrayCommandAdapter.Invoke(" + actionId + ")", ex);
                return false;
            }
        }

        /// <summary>打开日志目录（日志按模块分文件，给目录比给单个文件有用）。</summary>
        internal static void OpenLogsFolder()
        {
            try
            {
                string dir = AppLog.LogsDirectory;
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CelesteMusicPlayer");
                }
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TrayCommandAdapter.OpenLogsFolder", caught);
            }
        }
    }
}
