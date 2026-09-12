using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 启动时从命令行带进来的音频文件（双击文件打开程序）。
        /// 由 App 在窗口创建后立刻赋值，等曲库恢复流程走完再播放 ——
        /// 直接播会和「启动续播」抢同一条播放链路，出现播一半被覆盖的怪现象。
        /// </summary>
        internal string? PendingStartupFile;

        /// <summary>当前以「临时播放」方式在播、且尚未加入音乐库的文件路径（没有就是 null）。
        /// 状态栏上的「添加到音乐库」按钮只在它非空时显示。</summary>
        private string? _externalPlayPath;

        /// <summary>刚刚通过按钮加入音乐库的文件路径。
        /// 用来把按钮切成「已加入音乐库」的确认态 —— 直接收起来会让用户不知道到底加没加成功。</summary>
        private string? _justAddedToLibraryPath;

        /// <summary>恢复上次曲库 → 再播放启动带进来的文件（顺序不能反）。</summary>
        private async Task RestoreLastLibraryThenPendingFileAsync()
        {
            await RestoreLastLibraryAsync();

            string? path = PendingStartupFile;
            PendingStartupFile = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            StartupLog.Write("启动带进来的外部文件: " + path);
            PlayExternalFileAsync(path);
        }

        /// <summary>
        /// 播放从程序外部传进来的音频文件（双击文件、或后一次双击由另一个进程转交过来）。
        ///
        /// 处理方式：**不加入任何列表** —— 既不进播放队列，也不进音乐库，只做一次「临时播放」。
        /// 因为主界面就是用户的音乐库，双击一个散装文件不该污染它；同理也不该往播放队列里塞外来者。
        /// 播完即止，想留下就点状态栏上的「添加到音乐库」。
        /// 播放本身仍走正常的引擎路径，电平表 / SMTC / 桌面歌词 / DSP 全部照常生效。
        /// </summary>
        internal void PlayExternalFileAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // 已经在播这个文件：只把「正在播放」面板展开，不重复起播
                    if (string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        SetNowPlayingPaneVisible(true);
                        return;
                    }

                    PlaylistItem item = await Task.Run(() => BuildItemFromTag(path));
                    _externalPlayPath = path;

                    StartPlayback(item);

                    _ = ShowNowPlayingPaneWhenReadyAsync(path);
                }
                catch (Exception ex)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
                }
            });
        }

        /// <summary>
        /// 展开「正在播放」大面板（就是点状态栏专辑封面弹出的那个）。
        /// 要等异步播放流程把 _nowPlayingPath 写好再展开，否则面板内容会和实际播的对不上。
        /// </summary>
        private async Task ShowNowPlayingPaneWhenReadyAsync(string path)
        {
            try
            {
                for (int i = 0; i < 25; i++)
                {
                    await Task.Delay(100);

                    if (!string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SetNowPlayingPaneVisible(true);
                    return;
                }
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }
        }

        /// <summary>状态栏「添加到音乐库」：把当前播放的这首歌正式收进音乐库。</summary>
        private void AddToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? path = _externalPlayPath ?? _nowPlayingPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return;
                }

                AddFilesToLibrary(new[] { path });
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }
        }

        /// <summary>
        /// 把散装音频文件加入音乐库：既放进当前列表（主界面立刻能看到），
        /// 也记进设置的 ManualLibraryFiles，这样刷新音乐库、重启程序都不会丢。
        /// </summary>
        internal void AddFilesToLibrary(IEnumerable<string> paths)
        {
            string[] valid = (paths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (valid.Length == 0)
            {
                return;
            }

            // 1) 进当前列表（音乐库），不替换已有内容
            LoadAndAddFiles(valid, persist: false);

            // 只认真正进了列表的那些（重复的、读不出标签被跳过的都不算）
            string[] added = valid.Where(IsPathInLibrary).ToArray();
            if (added.Length == 0)
            {
                return;
            }

            // 2) 记进设置，保证重启后还在（重启时由 AppendManualLibraryFilesAsync 补回列表）
            AppSettingsState settings = AppSettingsStore.Load();
            settings.ManualLibraryFiles ??= new List<string>();

            bool changed = false;
            foreach (string p in added)
            {
                if (!settings.ManualLibraryFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    settings.ManualLibraryFiles.Add(p);
                    changed = true;
                }
            }

            if (changed)
            {
                AppSettingsStore.Save(settings);
            }

            foreach (string p in added)
            {
                StartupLog.Write("加入音乐库: " + p);
            }

            // 3) 明确告诉用户加成功了：按钮切到「已加入音乐库」确认态，状态栏也留一句话 ——
            //    之前是直接收起来，看起来像"点了就没了"，不知道到底加没加进去。
            _justAddedToLibraryPath = added[^1];
            UpdateAddToLibraryButtonVisibility();

            NowPlayingText.Text = added.Length == 1
                ? $"已加入音乐库：{Path.GetFileNameWithoutExtension(added[0])}（共 {_playlist.Count} 首）"
                : $"已加入音乐库 {added.Length} 首（共 {_playlist.Count} 首）";
        }

        /// <summary>
        /// 启动时把「手动加入音乐库」的散装文件补回列表。
        /// 文件夹型曲库只会扫文件夹里的东西，这些单文件不在其中，必须单独补。
        /// </summary>
        private async Task AppendManualLibraryFilesAsync()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (!settings.RestoreLibrary)
                {
                    return;
                }

                List<string>? manual = settings.ManualLibraryFiles;
                if (manual == null || manual.Count == 0)
                {
                    return;
                }

                string[] valid = manual
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // 已经在库里的不重复加（比如它本来就在被监视的文件夹里）
                string[] missing = valid.Where(p => !IsPathInLibrary(p)).ToArray();
                if (missing.Length == 0)
                {
                    return;
                }

                LoadAndAddFiles(missing, persist: false);
                StartupLog.Write($"补入手动加入音乐库的文件 {missing.Length} 个");
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }
        }

        /// <summary>把路径从「手动加入音乐库」清单里去掉（从库中移除时调用），避免重启后又冒出来。</summary>
        internal void RemoveFromManualLibraryFiles(IEnumerable<string> paths)
        {
            try
            {
                var dead = new HashSet<string>(
                    paths ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                if (dead.Count == 0)
                {
                    return;
                }

                AppSettingsState settings = AppSettingsStore.Load();
                if (settings.ManualLibraryFiles == null || settings.ManualLibraryFiles.Count == 0)
                {
                    return;
                }

                int removed = settings.ManualLibraryFiles.RemoveAll(p => dead.Contains(p));
                if (removed > 0)
                {
                    AppSettingsStore.Save(settings);
                    StartupLog.Write($"移出音乐库 {removed} 个手动添加的文件");
                }
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }
        }

        /// <summary>
        /// 状态栏「添加到音乐库」按钮的显示与状态：
        /// ① 当前这首歌不在音乐库里 → 显示可点的「添加到音乐库」；
        /// ② 刚由这个按钮加进去 → 显示带勾的「已加入音乐库」确认态（不点就消失，用户会以为没成功）；
        /// ③ 其它情况（本来就在库里） → 收起，不占地方。
        /// </summary>
        internal void UpdateAddToLibraryButtonVisibility()
        {
            try
            {
                if (AddToLibraryButton == null)
                {
                    return;
                }

                string? path = _externalPlayPath ?? _nowPlayingPath;
                bool hasPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
                bool inLibrary = hasPath && IsPathInLibrary(path!);

                bool showAdded = inLibrary
                    && !string.IsNullOrWhiteSpace(_justAddedToLibraryPath)
                    && string.Equals(_justAddedToLibraryPath, path, StringComparison.OrdinalIgnoreCase);

                bool showAdd = hasPath && !inLibrary;

                AddToLibraryButton.Visibility = (showAdd || showAdded)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // 内容随状态切换：+「添加到音乐库」  /  ✓「已加入音乐库」
                if (AddToLibraryIcon != null)
                {
                    AddToLibraryIcon.Glyph = showAdded ? "\uE73E" : "\uE710";
                }

                if (AddToLibraryText != null)
                {
                    AddToLibraryText.Text = showAdded ? "已加入音乐库" : "添加到音乐库";
                }

                // 已加入就不可再点，避免重复入库
                AddToLibraryButton.IsEnabled = showAdd;
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }
        }

        /// <summary>这个路径是否已经在音乐库（主界面列表）里。</summary>
        private bool IsPathInLibrary(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            return _playlist.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>读标签构造一个播放列表条目；读不出来就用文件名兜底，保证一定能播。</summary>
        private static PlaylistItem BuildItemFromTag(string path)
        {
            PlaylistItem item = new()
            {
                FilePath = path,
                Title = Path.GetFileNameWithoutExtension(path),
                Artist = "未知艺术家",
                Album = "未知专辑",
                AlbumArtist = "未知艺术家",
                Genre = "未知流派"
            };

            try
            {
                using TagLib.File tagFile = TagLib.File.Create(path);
                TagLib.Tag tag = tagFile.Tag;

                if (!string.IsNullOrWhiteSpace(tag.Title))
                {
                    item.Title = tag.Title;
                }

                string? artist = tag.Performers?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(artist))
                {
                    item.Artist = artist;
                }

                string? albumArtist = tag.AlbumArtists?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                item.AlbumArtist = !string.IsNullOrWhiteSpace(albumArtist) ? albumArtist : item.Artist;

                if (!string.IsNullOrWhiteSpace(tag.Album))
                {
                    item.Album = tag.Album;
                }

                if (!string.IsNullOrWhiteSpace(tag.FirstGenre))
                {
                    item.Genre = tag.FirstGenre;
                }

                item.Track = tag.Track;
                item.Disc = tag.Disc;
                item.Year = tag.Year;
                item.Duration = tagFile.Properties.Duration;
            }
            catch (Exception ex)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ExternalFile.cs", ex);
            }

            return item;
        }
    }
}
