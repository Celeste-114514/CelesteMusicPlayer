using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第十九刀（2026-10-31）：导航条目宿主。
/// 从壳（UiTheme/Features/Library）转活的「左侧导航条目 + 目录浏览器折叠 + 加库」域：
/// NavItemRef 条目模型与懒构建、条目显隐、文件夹折叠、文件夹加入媒体库。
/// 网页 WebDav/WebFolders/ExternalFile 借用的行为入口住这里。
/// </summary>
public sealed partial class MainWindow
{

        // ---- 目录浏览器折叠 / 文件夹加入媒体库 ----

        private void CollapseFolderAt(int index)
        {
            int depth = _folderBrowserItems[index].Depth;
            int removeAt = index + 1;
            while (removeAt < _folderBrowserItems.Count && _folderBrowserItems[removeAt].Depth > depth)
            {
                _folderBrowserItems.RemoveAt(removeAt);
            }
        }

        /// <summary>
        /// 把一个文件夹加入音乐库（幂等）：记进 LibraryWatchFolders（已在列表里不重复存），
        /// 并立刻把该文件夹的音频加进当前列表。音乐库只按文件夹收录（2026-10-14 口径），
        /// 这条路不写 ManualLibraryFiles（那只是历史遗留单曲的保留通道，不再新增）。
        /// </summary>
        internal async System.Threading.Tasks.Task AddFolderToLibraryAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            settings.LibraryWatchFolders ??= new List<string>();

            bool isNew = !settings.LibraryWatchFolders.Contains(folderPath, StringComparer.OrdinalIgnoreCase);
            if (isNew)
            {
                settings.LibraryWatchFolders.Add(folderPath);
                AppSettingsStore.Save(settings);
            }

            // 把该文件夹的音频加入主库（枚举走后台线程，避免大盘文件夹卡 UI）
            string[] paths = await System.Threading.Tasks.Task.Run(() => LibraryFolderScan.AudioFiles(folderPath).ToArray());
            LoadAndAddFiles(paths, persist: false);

            if (isNew)
            {
                StartupLog.Write("加入媒体库文件夹: " + folderPath);
            }
        }
    }
