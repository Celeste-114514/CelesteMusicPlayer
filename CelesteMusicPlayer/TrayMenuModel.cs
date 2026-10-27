using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘菜单的**数据模型**：与 WinUI 完全无关的一层。
    ///
    /// 为什么要单独抽出来：托盘菜单最终要换成「Win32 通知图标 + WebView2 独立菜单窗口」
    /// （见 collab/RESULT-20261028-tray-without-winui-small-plan.md）。菜单内容不管用什么画，
    /// 需要的都是这几样东西：显示什么字、点了做什么、现在能不能点、是不是勾上、归到哪一组。
    /// 把这些先定死，换承载方式时只换"画"的那半边，命令这半边一行不用动。
    ///
    /// ⚠ 网页只能拿到 actionId（枚举字符串），拿不到命令对象，也不能传任意路径/URL。
    /// </summary>
    internal static class TrayMenuModel
    {
        /// <summary>动作标识。与显示文案解耦：改中文不用改命令，换宿主也不用改。</summary>
        internal static class Actions
        {
            internal const string ShowMain = "showMain";
            internal const string ShowMini = "showMini";
            internal const string ShowScattered = "showScattered";
            internal const string ToggleLyrics = "toggleLyrics";
            internal const string UnlockLyrics = "unlockLyrics";
            internal const string Previous = "previous";
            internal const string TogglePlay = "togglePlay";
            internal const string Next = "next";
            internal const string Favorite = "favorite";
            internal const string OpenSettings = "openSettings";
            internal const string OpenLogs = "openLogs";
            internal const string OpenUpdate = "openUpdate";
            internal const string Exit = "exit";
        }

        internal sealed class Item
        {
            /// <summary>动作标识。separator 为空。</summary>
            public string actionId { get; set; } = "";
            public string label { get; set; } = "";
            public bool enabled { get; set; } = true;
            /// <summary>勾选态 / 开关态（桌面歌词这类）。null = 不显示勾选框。</summary>
            public bool? @checked { get; set; }
            /// <summary>分组序号，渲染时按它插分隔线。</summary>
            public int group { get; set; }
            /// <summary>分隔线不参与交互。</summary>
            public bool separator { get; set; }
        }

        /// <summary>
        /// 白名单校验。网页只能回传枚举里的 actionId —— 任何别的字符串（URL、路径、进程名）
        /// 一律拒绝，宿主不会拿它去执行任何东西。
        /// </summary>
        internal static bool IsKnownAction(string? actionId) => actionId switch
        {
            Actions.ShowMain or Actions.ShowMini or Actions.ShowScattered or Actions.ToggleLyrics
                or Actions.UnlockLyrics or Actions.Previous or Actions.TogglePlay or Actions.Next
                or Actions.Favorite or Actions.OpenSettings or Actions.OpenLogs or Actions.OpenUpdate
                or Actions.Exit => true,
            _ => false
        };

        /// <summary>
        /// 按当前真实状态生成菜单项。播放/暂停和喜欢的文案跟着实际状态走，
        /// 不写死"播放 / 暂停"这种让用户猜当前是什么状态的写法。
        /// </summary>
        internal static List<Item> Build(MainWindow owner, string? pendingUpdateVersion)
        {
            var items = new List<Item>();
            bool playing = owner?.IsEnginePlayingNow ?? false;

            items.Add(new Item { actionId = Actions.ShowMain, label = "显示主界面", group = 0 });
            items.Add(new Item { actionId = Actions.ShowMini, label = "迷你播放器", group = 0 });
            items.Add(new Item { actionId = Actions.ShowScattered, label = "零散播放", group = 0 });
            // 桌面歌词有真实开关状态（和快捷键同一个字段），所以这里可以放心显示勾选
            items.Add(new Item { actionId = Actions.ToggleLyrics, label = "桌面歌词", group = 0, @checked = owner?.DesktopLyricsEnabledPublic ?? false });

            items.Add(new Item { separator = true, group = 1 });
            items.Add(new Item { actionId = Actions.Previous, label = "上一首", group = 1 });
            items.Add(new Item { actionId = Actions.TogglePlay, label = playing ? "暂停" : "播放", group = 1 });
            items.Add(new Item { actionId = Actions.Next, label = "下一首", group = 1 });
            items.Add(new Item { actionId = Actions.Favorite, label = "添加到我喜欢", group = 1, @checked = owner == null ? null : owner.IsCurrentFavoritePublic() });

            items.Add(new Item { separator = true, group = 2 });
            // 更新/设置必须跳网页的对应位置，不能打开旧的 WinUI 关于面板
            if (!string.IsNullOrEmpty(pendingUpdateVersion))
                items.Add(new Item { actionId = Actions.OpenUpdate, label = "发现新版本 " + pendingUpdateVersion, group = 2 });
            items.Add(new Item { actionId = Actions.OpenSettings, label = "设置", group = 2 });
            items.Add(new Item { actionId = Actions.OpenLogs, label = "打开日志文件夹", group = 2 });
            items.Add(new Item { actionId = Actions.Exit, label = "退出播放器", group = 2 });
            return items;
        }
    }
}
