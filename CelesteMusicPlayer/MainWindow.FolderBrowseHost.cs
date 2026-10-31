using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十四刀（2026-10-31）：文件夹浏览与搜索导航宿主。
/// 从壳 <c>MainWindow.Library.cs</c> / <c>Library2.cs</c> / <c>Misc.cs</c> 搬出的
/// 「文件夹树 + 搜索命中跳转」一族：搜索结果上下条跳转、导航条刷新、根节点重建、
/// 按路径展开到文件、目录子项枚举、展开/收起、以及音频扩展名判定。
/// 依赖（文件夹浏览集合与字段、选中框刷新在 SelectionChromeHost、
/// 音频扩展名表在 LibraryFolderScan）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{
    // 成员在下方（W13 第二十四刀：文件夹树 + 搜索跳转 + 音频扩展名判定）。

        // W13 第二十四刀：文件夹树 + 搜索跳转

        private void NavigateFolderSearchMatch(int delta)
        {
            if (_folderSearchMatches.Count == 0)
            {
                return;
            }

            int next = _folderSearchIndex + delta;
            if (next < 0)
            {
                next = _folderSearchMatches.Count - 1;
            }
            else if (next >= _folderSearchMatches.Count)
            {
                next = 0;
            }

            NavigateFolderSearchMatchToIndex(next);
        }

        private void NavigateFolderSearchMatchToIndex(int index)
        {
            if (index < 0 || index >= _folderSearchMatches.Count)
            {
                return;
            }

            _folderSearchIndex = index;
            string targetPath = _folderSearchMatches[index];
            _folderSearchHighlightPath = targetPath;
            UpdateFolderSearchNavUi();

            // PDF 式：先收起全部，再展开到目标歌曲所在路径
            RefreshFolderBrowserRoots();
            ExpandFolderPathToFile(targetPath);

            FolderBrowserItem? item = _folderBrowserItems.FirstOrDefault(i =>
                !i.IsFolder
                && string.Equals(i.FullPath, targetPath, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                try
                {
                    FolderBrowserView.ScrollIntoView(item);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            RefreshFolderBrowserSelectionChrome();
        }

        private void ExpandFolderPathToFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_browseFolderPath) || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            string root = Path.GetFullPath(_browseFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullFile = Path.GetFullPath(filePath);
            string? parent = Path.GetDirectoryName(fullFile);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            string parentFull = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!parentFull.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = parentFull.Length == root.Length
                ? string.Empty
                : parentFull[(root.Length + 1)..];

            if (string.IsNullOrEmpty(relative))
            {
                return;
            }

            string[] segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            string current = root;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                FolderBrowserItem? folder = _folderBrowserItems.FirstOrDefault(i =>
                    i.IsFolder
                    && string.Equals(i.FullPath, current, StringComparison.OrdinalIgnoreCase));

                if (folder == null)
                {
                    break;
                }

                if (!folder.IsExpanded)
                {
                    ToggleFolderExpand(folder);
                }
            }
        }

        private void UpdateFolderSearchNavUi()
        {
            if (FolderSearchNavPanel == null || FolderSearchCountText == null)
            {
                return;
            }

            bool show = _currentCategory == "Folders"
                && !_isMultiSelectMode
                && _folderSearchMatches.Count > 0
                && !string.IsNullOrWhiteSpace(_librarySearchText);

            if (!show)
            {
                return;
            }

        }

        /// <summary>显示媒体库根（设置里的文件夹列表）或其直接子项；未配置则回退旧单根。</summary>
        private void RefreshFolderBrowserRoots()
        {
            _folderBrowserItems.Clear();

            List<string> roots = AppSettingsStore.Load().LibraryWatchFolders
                ?.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .ToList() ?? new List<string>();

            if (roots.Count == 0)
            {
                // 未配置媒体库：回退旧的单文件夹树
                if (string.IsNullOrWhiteSpace(_browseFolderPath) || !Directory.Exists(_browseFolderPath))
                {
                    return;
                }

                foreach (FolderBrowserItem child in EnumerateFolderChildren(_browseFolderPath, depth: 0))
                {
                    _folderBrowserItems.Add(child);
                }

                return;
            }

            // 多根媒体库：每个根显示完整路径，点击展开其下树
            foreach (string root in roots)
            {
                _folderBrowserItems.Add(new FolderBrowserItem
                {
                    DisplayName = root,
                    FullPath = root,
                    IsFolder = true,
                    Depth = 0
                });
            }
        }

        private void ToggleFolderExpand(FolderBrowserItem item)
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

            List<FolderBrowserItem> children = EnumerateFolderChildren(item.FullPath, item.Depth + 1);
            for (int i = 0; i < children.Count; i++)
            {
                _folderBrowserItems.Insert(index + 1 + i, children[i]);
            }

            item.ChildrenLoaded = true;
            item.IsExpanded = true;
        }

        /// <summary>枚举一层，转成界面用的节点（扫描逻辑在 LibraryFolderScan，这里只做类型映射）。</summary>
        private static List<FolderBrowserItem> EnumerateFolderChildren(string folderPath, int depth)
            => LibraryFolderScan.Children(folderPath, depth)
                .Select(entry => new FolderBrowserItem
                {
                    DisplayName = entry.DisplayName,
                    FullPath = entry.FullPath,
                    IsFolder = entry.IsFolder,
                    Depth = entry.Depth
                })
                .ToList();

        private void RefreshFolderBrowserSelectionChrome()
        {
            HashSet<object>? selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(FolderBrowserView);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(FolderBrowserView))
            {
                if (FolderBrowserView.ItemFromContainer(container) is FolderBrowserItem item)
                {
                    ApplyFolderBrowserItemSelectionChrome(container, item, selectedSet);
                }
            }
        }

        private static bool IsSupportedAudioFile(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (string supported in AudioExtensions)
            {
                if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
