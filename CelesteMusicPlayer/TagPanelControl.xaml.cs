using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「标签」板块（2026-10-13 阶段 5a 骨架）：mp3tag 式左标签面板 + 右文件列表。
    ///
    /// 本阶段范围（刻意做小、可独立验证）：
    ///   ✓ 主导航「标签」进入；左列 4 个子页（快速编辑 / 批量工具 / 排序编号 / 在线补全）可切换；
    ///   ✓ 右列文件表格：添加文件 / 添加文件夹 / 移除所选 / 清空，多选，列与主曲库同语义；
    ///   ✓ 快速编辑页 = 选中文件的只读标签预览（含封面缩略图）。
    /// 下一阶段（阶段 6）才接：字段编辑、保存、改前快照整体回滚。
    ///
    /// 复用既有件：ShellPageHost（子页导航）、TagEditorService（读写标签）、
    /// PlaylistItem（行模型，字段语义与主曲库严格一致）。
    /// 取色铁律：强调色由外部注入（accentProvider → DspAccentBrush），面板自己不读资源。
    /// </summary>
    public sealed partial class TagPanelControl : UserControl
    {
        /// <summary>面板支持添加的音频格式（与主界面「打开文件」挑选器同一份清单）。</summary>
        private static readonly string[] TagAudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac",
            ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus",
            ".dsf", ".dff",
            ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm"
        };

        private readonly ObservableCollection<PlaylistItem> _files = new();
        private readonly ShellPageHost _subNav;
        private readonly Func<Brush>? _accentProvider;

        /// <summary>面板就绪标记：Loaded 之后才允许响应选择变化（防初始化期 NRE）。</summary>
        private bool _panelReady;

        /// <summary>预览加载代数：快速切换选择时只认最后一次，避免旧文件的标签覆盖新文件的。</summary>
        private int _previewGeneration;

        public TagPanelControl(Func<Brush>? accentProvider = null)
        {
            _accentProvider = accentProvider;
            InitializeComponent();

            TagFileListView.ItemsSource = _files;

            // 4 个子页全部登记给 ShellPageHost（显隐由它统一管；组名为空 = 不渲染组标题）
            _subNav = new ShellPageHost(TagSubNavPanel, () => _accentProvider?.Invoke() ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)));
            _subNav.Register("快速编辑", string.Empty, QuickEditPanel);
            _subNav.Register("批量工具", string.Empty, BatchToolsPanel);
            _subNav.Register("排序编号", string.Empty, SortNumberPanel);
            _subNav.Register("在线补全", string.Empty, OnlineFillPanel);
            _subNav.Build();

            Loaded += TagPanelControl_Loaded;
            Unloaded += TagPanelControl_Unloaded;

            RefreshCountUi();
            ClearQuickEditPreview();
        }

        /// <summary>主题 / 皮肤切换后重刷子导航强调条（不刷会停在旧色上）。</summary>
        internal void ReapplyAccent()
        {
            try
            {
                _subNav.ReapplyAccent();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ReapplyAccent", caught); }
        }

        private void TagPanelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _panelReady = true;
        }

        private void TagPanelControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _panelReady = false;
        }

        // =====================================================================
        // 右列：文件列表操作
        // =====================================================================

        private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                foreach (string ext in TagAudioExtensions)
                {
                    picker.FileTypeFilter.Add(ext);
                }

                IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                await AddFilesAsync(files.Select(f => f.Path));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.AddFilesButton_Click", caught);
                TagStatusText.Text = "添加文件失败：" + caught.Message;
            }
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FolderPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                // FolderPicker 至少要加一个过滤器，常用 "*"
                picker.FileTypeFilter.Add("*");

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return;
                }

                TagStatusText.Text = "正在扫描文件夹…";
                string[] paths = await Task.Run(() => EnumerateAudioFiles(folder.Path).ToArray());
                if (paths.Length == 0)
                {
                    TagStatusText.Text = "该文件夹（含子文件夹）里没有支持的音频文件。";
                    return;
                }

                await AddFilesAsync(paths);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.AddFolderButton_Click", caught);
                TagStatusText.Text = "添加文件夹失败：" + caught.Message;
            }
        }

        /// <summary>把一批路径加进列表（自动去重；读标签在后台线程做，不卡界面）。</summary>
        private async Task AddFilesAsync(IEnumerable<string> paths)
        {
            string[] candidates = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            HashSet<string> existing = new(_files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
            string[] fresh = candidates.Where(p => !existing.Contains(p)).ToArray();
            int skipped = candidates.Length - fresh.Length;

            if (fresh.Length == 0)
            {
                TagStatusText.Text = skipped > 0
                    ? $"选中的文件都已在列表中（跳过 {skipped} 个）。"
                    : "没有可添加的文件。";
                return;
            }

            TagStatusText.Text = $"正在读取标签 0/{fresh.Length}…";

            // 逐批读：每批 50 个，读一批报一批进度（大文件夹不会假死一片）
            const int BatchSize = 50;
            int done = 0;
            for (int i = 0; i < fresh.Length; i += BatchSize)
            {
                string[] batch = fresh.Skip(i).Take(BatchSize).ToArray();
                PlaylistItem?[] entries = await Task.Run(() => batch.Select(BuildEntry).ToArray());

                foreach (PlaylistItem? entry in entries)
                {
                    if (entry != null)
                    {
                        _files.Add(entry);
                    }
                }

                done += batch.Length;
                TagStatusText.Text = $"正在读取标签 {done}/{fresh.Length}…";
            }

            Renumber();
            RefreshCountUi();
            TagStatusText.Text = $"已添加 {fresh.Length} 个文件" + (skipped > 0 ? $"（跳过重复 {skipped} 个）" : string.Empty) + "。";
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> selected = TagFileListView.SelectedItems.Cast<PlaylistItem>().ToList();
            if (selected.Count == 0)
            {
                TagStatusText.Text = "先在上方列表里选中要移除的文件（可多选）。";
                return;
            }

            foreach (PlaylistItem item in selected)
            {
                _files.Remove(item);
            }

            Renumber();
            RefreshCountUi();
            TagStatusText.Text = $"已移除 {selected.Count} 个文件（只是移出本列表，不动硬盘上的文件）。";
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0)
            {
                return;
            }

            int count = _files.Count;
            _files.Clear();
            RefreshCountUi();
            ClearQuickEditPreview();
            TagStatusText.Text = $"已清空 {count} 个文件（只是移出本列表，不动硬盘上的文件）。";
        }

        // =====================================================================
        // 快速编辑页：选中文件的只读标签预览
        // =====================================================================

        private async void TagFileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_panelReady)
            {
                return;
            }

            List<PlaylistItem> selected = TagFileListView.SelectedItems.Cast<PlaylistItem>().ToList();
            RefreshCountUi();

            if (selected.Count == 0)
            {
                ClearQuickEditPreview();
                return;
            }

            if (selected.Count > 1)
            {
                // 多选：本阶段只提示，批量填写是阶段 6/7 的事
                _previewGeneration++;
                ShowQuickEditBatchCard(selected.Count);
                return;
            }

            string path = selected[0].FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                ClearQuickEditPreview();
                return;
            }

            int generation = ++_previewGeneration;
            QuickEditTitle.Text = "读取中…";
            QuickEditPath.Text = path;

            (TagEditModel Tag, byte[]? Cover)? loaded = await Task.Run<(TagEditModel Tag, byte[]? Cover)?>(() =>
            {
                try
                {
                    return (TagEditorService.ReadTag(path), TagEditorService.ReadCoverBytes(path));
                }
                catch
                {
                    return null;
                }
            });

            // 异步期间用户又选了别的 → 丢弃这次结果
            if (generation != _previewGeneration || !_panelReady)
            {
                return;
            }

            if (loaded == null)
            {
                QuickEditTitle.Text = "标签读取失败";
                return;
            }

            ApplyQuickEditPreview(path, loaded.Value.Tag, loaded.Value.Cover);
        }

        private void ApplyQuickEditPreview(string path, TagEditModel model, byte[]? cover)
        {
            QuickEditFileCard.Visibility = Visibility.Visible;
            QuickEditTitle.Text = string.IsNullOrWhiteSpace(model.Title) ? System.IO.Path.GetFileName(path) : model.Title;
            QuickEditPath.Text = path;

            QuickTitleBox.Text = model.Title;
            QuickArtistBox.Text = model.Artist;
            QuickAlbumBox.Text = model.Album;
            QuickAlbumArtistBox.Text = model.AlbumArtist;
            QuickYearBox.Text = model.Year > 0 ? model.Year.ToString() : string.Empty;
            QuickTrackBox.Text = model.Track > 0 ? model.Track.ToString() : string.Empty;
            QuickGenreBox.Text = model.Genre;
            QuickCommentBox.Text = model.Comment;
            QuickLyricsBox.Text = model.Lyrics;

            ApplyCoverToImage(cover);
        }

        private void ShowQuickEditBatchCard(int count)
        {
            // 卡片保持可见：多选时标题位显示「已选 N 个文件」
            QuickEditFileCard.Visibility = Visibility.Visible;
            QuickEditCover.Source = null;
            QuickEditNoCoverIcon.Visibility = Visibility.Visible;

            QuickTitleBox.Text = string.Empty;
            QuickArtistBox.Text = string.Empty;
            QuickAlbumBox.Text = string.Empty;
            QuickAlbumArtistBox.Text = string.Empty;
            QuickYearBox.Text = string.Empty;
            QuickTrackBox.Text = string.Empty;
            QuickGenreBox.Text = string.Empty;
            QuickCommentBox.Text = string.Empty;
            QuickLyricsBox.Text = string.Empty;

            QuickEditTitle.Text = $"已选 {count} 个文件";
            QuickEditPath.Text = "批量填写字段在下一阶段开放";
        }

        private void ClearQuickEditPreview()
        {
            // 卡片保持可见：空状态下也要让人看出「这里是显示选中文件标签的地方」
            QuickEditFileCard.Visibility = Visibility.Visible;
            QuickEditCover.Source = null;
            QuickEditNoCoverIcon.Visibility = Visibility.Visible;

            QuickTitleBox.Text = string.Empty;
            QuickArtistBox.Text = string.Empty;
            QuickAlbumBox.Text = string.Empty;
            QuickAlbumArtistBox.Text = string.Empty;
            QuickYearBox.Text = string.Empty;
            QuickTrackBox.Text = string.Empty;
            QuickGenreBox.Text = string.Empty;
            QuickCommentBox.Text = string.Empty;
            QuickLyricsBox.Text = string.Empty;

            QuickEditTitle.Text = "未选择文件";
            QuickEditPath.Text = "从右侧文件列表选中一个文件";
        }

        private void ApplyCoverToImage(byte[]? bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0)
                {
                    QuickEditCover.Source = null;
                    QuickEditNoCoverIcon.Visibility = Visibility.Visible;
                    return;
                }

                BitmapImage bmp = new();
                using (MemoryStream ms = new(bytes))
                using (var stream = ms.AsRandomAccessStream())
                {
                    bmp.SetSource(stream);
                }

                QuickEditCover.Source = bmp;
                QuickEditNoCoverIcon.Visibility = Visibility.Collapsed;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ApplyCoverToImage", caught);
                QuickEditCover.Source = null;
                QuickEditNoCoverIcon.Visibility = Visibility.Visible;
            }
        }

        // =====================================================================
        // 内部工具
        // =====================================================================

        /// <summary>
        /// 由一个音频文件路径建一行。字段语义与主曲库的 CreatePlaylistItemFromPath 严格一致
        /// （同样的缺省值：未知艺术家 / 未知专辑 / 未知流派），不碰评分统计。
        /// 只打开一次 TagLib：标签 + 时长一趟拿完。
        /// </summary>
        private static PlaylistItem? BuildEntry(string path)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string artist = "未知艺术家";
            string albumArtist = "未知艺术家";
            string album = "未知专辑";
            uint track = 0;
            uint year = 0;
            string genre = "未知流派";
            TimeSpan duration = TimeSpan.Zero;

            try
            {
                using TagLib.File tagFile = TagLib.File.Create(path);
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                {
                    title = tagFile.Tag.Title.Trim();
                }

                string? performer = tagFile.Tag.FirstPerformer;
                if (string.IsNullOrWhiteSpace(performer))
                {
                    performer = tagFile.Tag.JoinedPerformers;
                }

                if (!string.IsNullOrWhiteSpace(performer))
                {
                    artist = performer.Trim();
                }

                string? albumPerformer = tagFile.Tag.FirstAlbumArtist;
                if (string.IsNullOrWhiteSpace(albumPerformer))
                {
                    albumPerformer = tagFile.Tag.JoinedAlbumArtists;
                }

                if (!string.IsNullOrWhiteSpace(albumPerformer))
                {
                    albumArtist = albumPerformer.Trim();
                }
                else
                {
                    albumArtist = artist;
                }

                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                {
                    album = tagFile.Tag.Album.Trim();
                }

                track = tagFile.Tag.Track;
                year = tagFile.Tag.Year;
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                {
                    genre = tagFile.Tag.FirstGenre.Trim();
                }

                duration = tagFile.Properties.Duration;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BuildEntry", caught);
                return null;
            }

            return new PlaylistItem
            {
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Track = track,
                Year = year,
                Genre = genre,
                Duration = duration,
                FilePath = path
            };
        }

        /// <summary>递归枚举文件夹里的音频文件；内部缓存目录（DSD DoP / 转码 / WebDAV 缓存）一律跳过。</summary>
        private static IEnumerable<string> EnumerateAudioFiles(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                yield break;
            }

            if (LibraryPathGuard.IsInternalCacheDir(folderPath))
            {
                yield break;
            }

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(folderPath);
                files = Directory.GetFiles(folderPath);
            }
            catch
            {
                // 某个受保护子目录（无权限）只跳过该目录，不影响其它文件
                yield break;
            }

            foreach (string path in files)
            {
                string ext = System.IO.Path.GetExtension(path);
                if (TagAudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                    && !LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    yield return path;
                }
            }

            foreach (string sub in subDirs)
            {
                foreach (string path in EnumerateAudioFiles(sub))
                {
                    yield return path;
                }
            }
        }

        private void Renumber()
        {
            for (int i = 0; i < _files.Count; i++)
            {
                _files[i].Index = i + 1;
            }
        }

        private void RefreshCountUi()
        {
            int total = _files.Count;
            int selected = TagFileListView?.SelectedItems.Count ?? 0;

            FileCountText.Text = total > 0 ? $"共 {total} 个文件" : "0 个文件";
            TagListEmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (selected > 0)
            {
                TagStatusText.Text = $"已选 {selected} / {total} 个文件。";
            }
            else if (total > 0 && string.IsNullOrEmpty(TagStatusText.Text))
            {
                TagStatusText.Text = $"共 {total} 个文件。";
            }
        }
    }
}
