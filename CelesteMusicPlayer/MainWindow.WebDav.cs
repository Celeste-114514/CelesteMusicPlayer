using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 主窗口里的 WebDAV 网络音乐库。
    ///
    /// 目标体验（对齐 UAPP 那类网盘播放器）：左侧一层层展开文件夹，右侧列出当前文件夹里的音频，
    /// 点哪首下哪首 —— 不做整库扫描、不建索引，服务器上有多少层就展开多少层。
    ///
    /// 复用现有「文件夹」分类的那套 UI（FolderBrowserView + MediaDetailsList），只是把数据源
    /// 从磁盘换成 WebDAV：FolderBrowserItem.RemotePath 非空即代表"这一行在网络上"。
    ///
    /// 播放仍然遵守 bit-perfect 铁律：先把文件完整下载到本地缓存，再走和普通本地文件
    /// 完全一样的播放链路，不碰任何播放字节流。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const string WebDavCategory = "WebDav";

        /// <summary>当前配置的网络位置；没配过返回 null。</summary>
        private static WebDavLocation? GetWebDavLocation()
        {
            WebDavLocation loc = WebDavLocation.FromSettings(AppSettingsStore.Load());
            return loc.IsConfigured ? loc : null;
        }

        // 路径数学与失败翻译在 WebDavBrowseRules（纯函数，NetworkRegression 有用例）。
        private static string CombineRemotePath(string parent, string child) => WebDavBrowseRules.CombineRemotePath(parent, child);
        private static string GetRemoteParent(string path) => WebDavBrowseRules.GetRemoteParent(path);
        private static string DescribeWebDavFailure(Exception ex) => WebDavBrowseRules.DescribeFailure(ex);

        // =====================================================================
        // 左侧条目
        // =====================================================================

        /// <summary>
        /// 「文件夹」那套界面复用给网络音乐库：换台头、藏掉「添加文件夹」（网络库不是往里加本地目录）。
        /// 刷新按钮保留 —— 网络库更需要它（重新连一次服务器）。
        /// </summary>
        private void SetFolderBrowserMode(bool isWebDav)
        {

        }

        /// <summary>显示 / 隐藏左侧「网络音乐库」条目（供设置窗口保存 / 删除后调用）。</summary>
        private void ApplyWebDavNavEntry(AppSettingsState settings)
        {

            WebDavLocation loc = WebDavLocation.FromSettings(settings);

            if (!loc.IsConfigured)
            {
                return;
            }

        }

        /// <summary>供设置窗口保存 / 删除后调用：刷新条目，并在不再存在时把视图切回「歌曲」。</summary>
        public void RefreshWebDavNavEntry()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                ApplyWebDavNavEntry(s);

                WebDavLocation loc = WebDavLocation.FromSettings(s);
                if (!loc.IsConfigured && string.Equals(_currentCategory, WebDavCategory, StringComparison.Ordinal))
                {
                    _currentCategory = "Songs";
                    ApplyCategoryViewData();
                    return;
                }

            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebDav.cs", caught);
            }
        }

        // =====================================================================
        // 左侧树：按文件夹层级懒加载
        // =====================================================================

        /// <summary>切到网络音乐库分类时调用：列服务器根目录。</summary>
        private async void RefreshWebDavBrowserRoots()
        {

            _folderBrowserItems.Clear();
            WebDavLocation? loc = GetWebDavLocation();

            if (loc == null)
            {
                ShowWebDavTreeMessage("还没配置网络音乐库。到「选项设置 → WebDAV」里填上地址，保存后这里就有内容了。");
                return;
            }

            ShowWebDavTreeMessage("正在连接 " + loc.LibraryName + " …");

            try
            {
                IReadOnlyList<WebDavEntry> entries = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    return await client.ListAsync(string.Empty);
                });

                _folderBrowserItems.Clear();
                foreach (WebDavEntry e in entries)
                {
                    _folderBrowserItems.Add(ToFolderBrowserItem(e, depth: 0, parent: string.Empty));
                }

                if (_folderBrowserItems.Count == 0)
                {
                    ShowWebDavTreeMessage("服务器根目录下没有内容。");
                    return;
                }

            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebDav.cs", caught);
                ShowWebDavTreeMessage("连不上网络音乐库：" + Environment.NewLine + DescribeWebDavFailure(caught));
            }
        }

        private void ShowWebDavTreeMessage(string text)
        {

        }

        private static FolderBrowserItem ToFolderBrowserItem(WebDavEntry entry, int depth, string parent)
        {
            string rel = CombineRemotePath(parent, entry.RelativePath);

            return new FolderBrowserItem
            {
                DisplayName = entry.Name,
                // FullPath 这一列在树上只用于悬浮提示和详情区台头，存服务器上的路径最直观。
                FullPath = string.IsNullOrEmpty(rel) ? entry.Name : rel,
                IsFolder = entry.IsFolder,
                Depth = depth,
                RemotePath = rel
            };
        }

        // =====================================================================
        // 右侧详情：当前文件夹里的歌曲
        // =====================================================================

        // =====================================================================
        // 播放：先下载到缓存，再走现有播放链路
        // =====================================================================

    }
}
