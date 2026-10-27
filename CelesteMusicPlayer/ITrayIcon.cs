using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 系统托盘图标的抽象。托盘正在从 H.NotifyIcon.WinUI 换成 Win32 通知图标
    /// （见 collab/RESULT-20261028-tray-without-winui-small-plan.md），
    /// 两条实现要能被同一处代码调用，所以先抽出这层最小接口。
    /// </summary>
    internal interface ITrayIcon : IDisposable
    {
        /// <summary>图标是否已真正注册到通知区。False = 右下角一定没有图标。</summary>
        bool IsCreated { get; }

        void Show();

        void Hide();

        /// <summary>发现新版本时的气泡提醒，同时记下版本号让菜单多一条入口。</summary>
        void ShowUpdateNotification(string versionTag);
    }
}
