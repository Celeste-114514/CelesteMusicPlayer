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

        /// <summary>拼出「相对服务器根目录」的完整路径（WebDavEntry.RelativePath 只是相对当前目录的一段）。</summary>
        private static string CombineRemotePath(string parent, string child)
        {
            string p = (parent ?? string.Empty).Trim('/');
            string c = (child ?? string.Empty).Trim('/');
            if (c.StartsWith(p + "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(p))
            {
                return c; // 服务器已经返回了完整路径，别重复拼
            }

            return string.IsNullOrEmpty(p) ? c : p + "/" + c;
        }

        private static string GetRemoteParent(string path)
        {
            string p = (path ?? string.Empty).Trim('/');
            int slash = p.LastIndexOf('/');
            return slash < 0 ? string.Empty : p.Substring(0, slash);
        }

        // =====================================================================
        // 左侧条目
        // =====================================================================

        /// <summary>
        /// 「文件夹」那套界面复用给网络音乐库：换台头、藏掉「添加文件夹」（网络库不是往里加本地目录）。
        /// 刷新按钮保留 —— 网络库更需要它（重新连一次服务器）。
        /// </summary>
        private void SetFolderBrowserMode(bool isWebDav)
        {
            if (MediaLibraryTreeTitle != null)
            {
                MediaLibraryTreeTitle.Text = isWebDav ? "网络音乐库" : "媒体库";
            }

            if (AddMediaFolderButton != null)
            {
                AddMediaFolderButton.Visibility = isWebDav ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>显示 / 隐藏左侧「网络音乐库」条目（供设置窗口保存 / 删除后调用）。</summary>
        private void ApplyWebDavNavEntry(AppSettingsState settings)
        {
            if (NavWebDavButton == null)
            {
                return;
            }

            WebDavLocation loc = WebDavLocation.FromSettings(settings);
            SetNavEntryVisibility(NavWebDavButton, loc.IsConfigured);

            if (!loc.IsConfigured)
            {
                return;
            }

            // 条目的文字就是库名（设置里填的显示名，没填就是主机名）。
            foreach (NavItemRef item in NavItems)
            {
                if (ReferenceEquals(item.Button, NavWebDavButton) && item.Label != null)
                {
                    item.Label.Text = loc.LibraryName;
                }
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
                    ApplyCategoryView();
                    return;
                }

                UpdateLibraryNavHighlight();
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
            if (FolderBrowserView == null)
            {
                return;
            }

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

                FolderBrowserEmptyHint.Visibility = Visibility.Collapsed;
                FolderBrowserView.Visibility = Visibility.Visible;

                if (MediaDetailsHeader != null)
                {
                    MediaDetailsHeader.Text = loc.LibraryName + "（双击文件夹查看歌曲）";
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
            if (FolderBrowserEmptyHint != null)
            {
                FolderBrowserEmptyHint.Text = text;
                FolderBrowserEmptyHint.Visibility = Visibility.Visible;
            }

            if (FolderBrowserView != null)
            {
                FolderBrowserView.Visibility = Visibility.Collapsed;
            }

            if (MediaDetailsList != null)
            {
                MediaDetailsList.ItemsSource = null;
                MediaDetailsList.Visibility = Visibility.Collapsed;
            }

            if (MediaDetailsEmptyHint != null)
            {
                MediaDetailsEmptyHint.Visibility = Visibility.Collapsed;
            }
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

        /// <summary>展开 / 折叠一个网络文件夹（异步：要发一次 PROPFIND）。</summary>
        private async Task ToggleWebDavFolderExpandAsync(FolderBrowserItem item)
        {
            int index = _folderBrowserItems.IndexOf(item);
            if (index < 0)
            {
                return;
            }

            if (item.IsExpanded)
            {
                CollapseFolderAt(index);
                item.IsExpanded = false;
                return;
            }

            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null)
            {
                return;
            }

            string path = item.RemotePath;
            int depth = item.Depth;

            try
            {
                IReadOnlyList<WebDavEntry> children = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    return await client.ListAsync(path);
                });

                // await 期间列表可能已被别的操作改动过（切分类 / 折叠），重新定位再插。
                int at = _folderBrowserItems.IndexOf(item);
                if (at < 0)
                {
                    return;
                }

                for (int i = 0; i < children.Count; i++)
                {
                    _folderBrowserItems.Insert(at + 1 + i, ToFolderBrowserItem(children[i], depth + 1, path));
                }

                item.ChildrenLoaded = true;
                item.IsExpanded = true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebDav.cs", caught);
                ShowWebDavStatus("展开失败：" + DescribeWebDavFailure(caught));
            }
        }

        // =====================================================================
        // 右侧详情：当前文件夹里的歌曲
        // =====================================================================

        /// <summary>双击文件夹 / 点箭头：列出该网络文件夹里的音频文件。</summary>
        private async void LoadWebDavFolderSongs(FolderBrowserItem item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null)
            {
                return;
            }

            string folderPath = item.IsFolder ? item.RemotePath : GetRemoteParent(item.RemotePath);
            string header = item.IsFolder ? item.FullPath : folderPath;

            MediaDetailsList.ItemsSource = null;
            MediaDetailsList.Visibility = Visibility.Visible;
            MediaDetailsEmptyHint.Visibility = Visibility.Collapsed;
            MediaDetailsHeader.Text = header + "（加载中…）";

            try
            {
                List<PlaylistItem> songs = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    IReadOnlyList<WebDavEntry> entries = await client.ListAsync(folderPath);

                    var list = new List<PlaylistItem>();
                    foreach (WebDavEntry e in entries)
                    {
                        if (e.IsFolder)
                        {
                            continue;
                        }

                        if (!AudioExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string rel = CombineRemotePath(folderPath, e.RelativePath);
                        list.Add(CreateRemotePlaylistItem(loc, new WebDavEntry
                        {
                            Name = e.Name,
                            RelativePath = rel,
                            IsFolder = false,
                            Size = e.Size,
                            ModifiedUtc = e.ModifiedUtc
                        }, folderPath));
                    }

                    return list;
                });

                for (int i = 0; i < songs.Count; i++)
                {
                    songs[i].Index = i + 1;
                }

                MediaDetailsList.ItemsSource = songs;
                MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                MediaDetailsHeader.Text = header + "（" + songs.Count + " 首）";
                MediaDetailsEmptyHint.Text = songs.Count == 0 ? "这个文件夹里没有音频文件" : string.Empty;
                MediaDetailsEmptyHint.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebDav.cs", caught);
                MediaDetailsHeader.Text = header + "（加载失败）";
                MediaDetailsEmptyHint.Text = "读取失败：" + DescribeWebDavFailure(caught);
                MediaDetailsEmptyHint.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 造一个网络曲目条目。已经缓存过的直接读本地标签（曲目信息、时长都是真的）；
        /// 没缓存的只显示文件名和大小 —— 不为了显示时长去下一整首歌。
        /// </summary>
        private static PlaylistItem CreateRemotePlaylistItem(WebDavLocation loc, WebDavEntry entry, string folderPath)
        {
            string localPath = WebDavCache.GetCachePath(loc, entry.RelativePath);

            if (WebDavCache.IsCached(loc, entry, out string cached))
            {
                try
                {
                    PlaylistItem tagged = CreatePlaylistItemFromPath(cached);
                    tagged.RemotePath = entry.RelativePath;
                    tagged.RemoteHint = "已缓存";
                    return tagged;
                }
                catch (Exception caught)
                {
                    // 缓存文件坏了（上次没下完等）：当作未缓存处理，下面走通用分支。
                    StartupLog.WriteException("MainWindow.WebDav.cs", caught);
                }
            }

            return new PlaylistItem
            {
                Title = Path.GetFileNameWithoutExtension(entry.Name),
                Artist = "网络音乐库",
                Album = string.IsNullOrWhiteSpace(folderPath)
                    ? loc.LibraryName
                    : Path.GetFileName(folderPath.TrimEnd('/')),
                FilePath = localPath,
                RemotePath = entry.RelativePath,
                RemoteHint = entry.Size > 0 ? WebDavCache.FormatSize(entry.Size) : "未缓存"
            };
        }

        // =====================================================================
        // 播放：先下载到缓存，再走现有播放链路
        // =====================================================================

        /// <summary>双击 / 菜单「播放」一首网络曲目：下载 → 缓存 → 播放。</summary>
        private async Task PlayWebDavItemAsync(PlaylistItem item)
        {
            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null || string.IsNullOrEmpty(item.RemotePath))
            {
                return;
            }

            string name = Path.GetFileName(item.RemotePath);
            ShowWebDavStatus("正在下载 " + name + " …");

            try
            {
                var entry = new WebDavEntry
                {
                    Name = name,
                    RelativePath = item.RemotePath,
                    IsFolder = false,
                    Size = 0
                };

                // ⚠️ 进度回调跑在下载线程上，直接改 TextBlock 会被 WinUI 拒绝（跨线程访问），
                // 必须排回 UI 线程。
                var progress = new Progress<double>(p =>
                {
                    string line = "正在下载 " + name + " … " + (int)Math.Round(p * 100) + "%";
                    DispatcherQueue?.TryEnqueue(() => ShowWebDavStatus(line));
                });

                string localPath = await Task.Run(() => WebDavCache.GetOrDownloadAsync(loc, entry, progress));

                // 本地缓存只当播放源用：WebDAV 下载缓存不进本地媒体库（用户明确要求），
                // 标签/封面/音效全走原播放链路，但 _playlist / 曲库会话都不会收录它。
                PlaylistItem track = CreatePlaylistItemFromPath(localPath);
                track.RemotePath = entry.RelativePath;

                PlayPlaylistItem(track);

                ShowWebDavStatus("已开始播放：" + name);
                WebDavCache.EnforceLimit(loc);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.WebDav.cs", caught);
                ShowWebDavStatus("下载失败：" + DescribeWebDavFailure(caught));
            }
        }

        /// <summary>把状态写到详情区台头（下载进度、报错都用它，不再另开弹窗）。</summary>
        private void ShowWebDavStatus(string text)
        {
            if (MediaDetailsHeader != null)
            {
                MediaDetailsHeader.Text = text;
            }
        }

        /// <summary>把 WebDAV 异常翻译成一句人话。</summary>
        private static string DescribeWebDavFailure(Exception ex)
        {
            if (ex is WebDavException webDav)
            {
                return webDav.Headline + "：" + webDav.Message;
            }

            if (ex is AggregateException agg && agg.InnerException != null)
            {
                return DescribeWebDavFailure(agg.InnerException);
            }

            return ex.Message;
        }
    }
}
