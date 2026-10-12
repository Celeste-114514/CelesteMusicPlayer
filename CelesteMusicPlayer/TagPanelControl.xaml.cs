using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
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

        private readonly ObservableCollection<TagFileItem> _files = new();
        private readonly ShellPageHost _subNav;
        private readonly Func<Brush>? _accentProvider;

        /// <summary>
        /// 宿主窗口句柄（由 MainWindow 构造时传入，WinRT 选取器 InitializeWithWindow 用）。
        /// ⚠ 不能在本类里调 WindowNative.GetWindowHandle(this)：UserControl 没实现 IWindowNative，
        /// 会抛 "Failed to create a CCW … the specified cast is not valid"（2026-10-13 用户实测）。
        /// </summary>
        private readonly nint _hostHwnd;

        /// <summary>面板就绪标记：Loaded 之后才允许响应选择变化（防初始化期 NRE）。</summary>
        private bool _panelReady;

        /// <summary>预览加载代数：快速切换选择时只认最后一次，避免旧文件的标签覆盖新文件的。</summary>
        private int _previewGeneration;

        public TagPanelControl(Func<Brush>? accentProvider = null, nint hostHwnd = default)
        {
            _accentProvider = accentProvider;
            _hostHwnd = hostHwnd;
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
                if (_hostHwnd == IntPtr.Zero)
                {
                    TagStatusText.Text = "窗口句柄缺失，无法打开选取器。";
                    return;
                }
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hostHwnd);

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
                if (_hostHwnd == IntPtr.Zero)
                {
                    TagStatusText.Text = "窗口句柄缺失，无法打开选取器。";
                    return;
                }
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hostHwnd);

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
                TagFileItem?[] entries = await Task.Run(() => batch.Select(BuildEntry).ToArray());

                foreach (TagFileItem? entry in entries)
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
            List<TagFileItem> selected = TagFileListView.SelectedItems.Cast<TagFileItem>().ToList();
            if (selected.Count == 0)
            {
                TagStatusText.Text = "先在上方列表里选中要移除的文件（可多选）。";
                return;
            }

            foreach (TagFileItem item in selected)
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

            List<TagFileItem> selected = TagFileListView.SelectedItems.Cast<TagFileItem>().ToList();
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
        private static TagFileItem? BuildEntry(string path)
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

            TagFileItem item = new()
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

            // 文件派生列（格式 / 位深 / 采样率 / 码率 / 大小）：本方法就在后台线程里跑，
            // AudioInfoFormatter 自带按路径缓存；任何一项读不到就留 "-"，不影响整行。
            try
            {
                string ext = System.IO.Path.GetExtension(path);
                item.FormatName = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "-";

                if (AudioInfoFormatter.TryGetFacts(path, out int rate, out int bits, out int kbps, out _))
                {
                    item.BitDepthText = bits > 0 ? bits + "bit" : "-";
                    item.SampleRateText = rate > 0 ? FormatSampleRate(rate) : "-";
                    item.BitrateText = kbps > 0 ? kbps + "kbps" : "-";
                }

                try
                {
                    long len = new FileInfo(path).Length;
                    if (len > 0)
                    {
                        item.FileSizeText = FormatFileSize(len);
                    }
                }
                catch
                {
                    // 文件被独占/无权限时大小列留 "-"
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BuildEntry.facts", caught);
            }

            return item;
        }

        /// <summary>44100 → 44.1kHz；48000 → 48kHz；2822400 → 2.82MHz。</summary>
        private static string FormatSampleRate(int rate)
        {
            if (rate >= 1_000_000)
            {
                return (rate / 1_000_000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "MHz";
            }
            return (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "kHz";
        }

        /// <summary>字节数 → B / KB / MB。</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }
            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
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

        // =====================================================================
        // 文件表格：列宽（表头拖拽）与表头横向滚动同步
        //
        // 布局约定（改列数/默认宽必须三处同步）：
        //   ① XAML 表头 TagListHeaderGrid 的 ColumnDefinitions
        //   ② XAML 行模板 Grid 的 ColumnDefinitions
        //   ③ 下面的 ColDefaultWidths / ColMinWidths
        // 运行期以 ③ 为唯一真相：Loaded 时按 ③ 刷一遍表头和所有可见行，
        // 之后拖拽只改 ③ + 同步刷（行模板 Loaded 时也会按 ③ 盖章，新行不会错位）。
        // =====================================================================

        /// <summary>总列数（与 XAML 两处 ColumnDefinitions 严格一致）。</summary>
        private const int ColCount = 14;

        /// <summary>「标题」列：唯一星号列，自动吸收剩余宽度；拖它的把手改的是 MinWidth。</summary>
        private const int StarCol = 1;

        private const double ColSpacing = 8;
        private const double RowPaddingX = 10;

        private static readonly double[] ColDefaultWidths =
            { 36, 100, 130, 130, 52, 44, 84, 60, 150, 64, 52, 76, 68, 76 };

        private static readonly double[] ColMinWidths =
            { 28, 100, 60, 60, 36, 32, 48, 44, 80, 44, 36, 48, 48, 48 };

        private readonly double[] _colWidths = (double[])ColDefaultWidths.Clone();
        private double _starMinWidth = ColDefaultWidths[StarCol];
        private double _gridMinWidth;
        private double _listViewportW;
        private double _lastViewportW = -1;
        private ScrollViewer? _listScroller;
        private TranslateTransform? _headerSlide;

        // 拖拽状态（_dragCol < 0 表示没在拖）
        private int _dragCol = -1;
        private double _dragStartX;
        private double _dragStartW;

        private void TagPanelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _panelReady = true;
            try
            {
                SetupTableScrolling();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.Loaded.table", caught);
            }
        }

        /// <summary>表格增强初始化：横向滚动同步、表头裁剪、列宽把手。Loaded 后调用一次。</summary>
        private void SetupTableScrolling()
        {
            // 1) ListView 内部 ScrollViewer：横向滚动时把表头往左同等位移
            //    （表头在 ListView 外面，不会自己跟着滚）
            _listScroller = FindDescendant<ScrollViewer>(TagFileListView);
            if (_listScroller != null)
            {
                _listViewportW = _listScroller.ViewportWidth;
                _lastViewportW = _listViewportW;
                _listScroller.ViewChanged += ListScroller_ViewChanged;
            }

            // 2) 表头位移变换 + 裁剪：列太多内容宽于视口时不裁会溢出到右列外
            _headerSlide = new TranslateTransform();
            TagListHeaderGrid.RenderTransform = _headerSlide;
            UpdateHeaderClip();

            // 3) 表头列边界生成拖拽把手（6px 透明热区，代码生成免得 XAML 写 14 遍）
            BuildColumnGrips();

            TagHeaderHost.SizeChanged += TagHeaderHost_SizeChanged;
            TagFileListView.SizeChanged += TagFileListView_SizeChanged;

            ApplyColumnWidths();
        }

        private void ListScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            try
            {
                if (_headerSlide != null && _listScroller != null)
                {
                    _headerSlide.X = -_listScroller.HorizontalOffset;
                }

                // 竖滚动条出现/消失会改变视口宽 → 行宽阈值跟着变
                double vw = _listScroller?.ViewportWidth ?? 0;
                if (vw > 0 && Math.Abs(vw - _lastViewportW) > 0.5)
                {
                    _lastViewportW = vw;
                    _listViewportW = vw;
                    ApplyColumnWidths();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ViewChanged", caught);
            }
        }

        private void UpdateHeaderClip()
        {
            double w = TagHeaderHost.ActualWidth;
            double h = TagHeaderHost.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            TagListHeaderGrid.Clip = new RectangleGeometry
            {
                Rect = new global::Windows.Foundation.Rect(0, 0, w, h)
            };
        }

        private void BuildColumnGrips()
        {
            for (int i = 0; i < ColCount; i++)
            {
                ColumnResizeGrip grip = new();
                Grid.SetColumn(grip, i);
                grip.PointerPressed += ColumnGrip_PointerPressed;
                grip.PointerMoved += ColumnGrip_PointerMoved;
                grip.PointerReleased += ColumnGrip_PointerReleased;
                grip.PointerCanceled += ColumnGrip_PointerReleased;
                TagListHeaderGrid.Children.Add(grip);
            }
        }

        private void ColumnGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not ColumnResizeGrip grip)
            {
                return;
            }

            int col = Grid.GetColumn(grip);
            _dragCol = col;
            _dragStartX = e.GetCurrentPoint(TagListHeaderGrid).Position.X;
            _dragStartW = col == StarCol ? _starMinWidth : _colWidths[col];
            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void ColumnGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragCol < 0 || sender is not ColumnResizeGrip grip)
            {
                return;
            }

            double x = e.GetCurrentPoint(TagListHeaderGrid).Position.X;
            SetColumnWidth(_dragCol, _dragStartW + (x - _dragStartX));
            e.Handled = true;
        }

        private void ColumnGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not ColumnResizeGrip grip)
            {
                return;
            }

            try
            {
                grip.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
                // 指针已经丢了就算了
            }
            _dragCol = -1;
            e.Handled = true;
        }

        private void SetColumnWidth(int col, double width)
        {
            if (width < ColMinWidths[col])
            {
                width = ColMinWidths[col];
            }

            if (col == StarCol)
            {
                _starMinWidth = width;
            }
            else
            {
                _colWidths[col] = width;
            }

            ApplyColumnWidths();
        }

        /// <summary>按当前列宽刷新表头 + 所有已实现（可见）的行。</summary>
        private void ApplyColumnWidths()
        {
            double fixedSum = 0;
            for (int i = 0; i < ColCount; i++)
            {
                fixedSum += i == StarCol ? _starMinWidth : _colWidths[i];
            }
            _gridMinWidth = fixedSum + ColSpacing * (ColCount - 1) + RowPaddingX * 2;

            ApplyWidthsToGrid(TagListHeaderGrid, TagHeaderHost.ActualWidth);

            foreach (TagFileItem item in _files)
            {
                if (TagFileListView.ContainerFromItem(item) is ListViewItem container)
                {
                    Grid? row = FindRowGrid(container);
                    if (row != null)
                    {
                        ApplyWidthsToGrid(row, _listViewportW);
                    }
                }
            }
        }

        private void ApplyWidthsToGrid(Grid grid, double viewportW)
        {
            ColumnDefinitionCollection cols = grid.ColumnDefinitions;
            for (int i = 0; i < cols.Count && i < ColCount; i++)
            {
                if (i == StarCol)
                {
                    cols[i].MinWidth = _starMinWidth;
                }
                else
                {
                    cols[i].Width = new GridLength(_colWidths[i]);
                }
            }

            grid.MinWidth = _gridMinWidth;
            // 视口比总宽窄 → 内容按总宽渲染（超出部分靠裁剪/横向滚动）；
            // 否则交回自动拉伸（星号列填满剩余空间）。NaN = 清除显式宽度。
            grid.Width = viewportW > 0 && viewportW < _gridMinWidth ? _gridMinWidth : double.NaN;
        }

        /// <summary>行模板 Grid 实现（新行/回收行）时按当前列宽盖章，防止用回 XAML 默认值错位。</summary>
        private void TagFileRow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid row)
            {
                ApplyWidthsToGrid(row, _listViewportW);
            }
        }

        private void TagHeaderHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHeaderClip();
            ApplyColumnWidths();
        }

        private void TagFileListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double vw = _listScroller?.ViewportWidth ?? 0;
            if (vw > 0 && Math.Abs(vw - _lastViewportW) > 0.5)
            {
                _lastViewportW = vw;
                _listViewportW = vw;
            }
            ApplyColumnWidths();
        }

        /// <summary>行容器里找行模板 Grid：靠列数匹配（ListViewItem 内部模板的 Grid 没这么多列）。</summary>
        private static Grid? FindRowGrid(ListViewItem container)
        {
            return FindDescendantWhere<Grid>(container, g => g.ColumnDefinitions.Count == ColCount);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : class
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }
                T? found = FindDescendant<T>(child);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static T? FindDescendantWhere<T>(DependencyObject root, Func<T, bool> predicate) where T : class
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && predicate(match))
                {
                    return match;
                }
                T? found = FindDescendantWhere<T>(child, predicate);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 标签板块文件表格行模型：PlaylistItem + 文件派生列（格式 / 位深 / 采样率 / 码率 / 大小）。
    /// 这些列是文件属性不是标签，只读展示；阶段 8 按列排序时就是排这些字段。
    /// 值在 BuildEntry 的后台线程里一次填好（走 AudioInfoFormatter 缓存），界面线程不做文件 I/O。
    /// </summary>
    public sealed class TagFileItem : PlaylistItem
    {
        /// <summary>扩展名大写（MP3 / FLAC / DSF…）；读不到显示 -。</summary>
        public string FormatName { get; set; } = "-";

        /// <summary>位深（16bit / 24bit）；压缩格式可能没有。</summary>
        public string BitDepthText { get; set; } = "-";

        /// <summary>采样率（44.1kHz / 96kHz / 2.82MHz）。</summary>
        public string SampleRateText { get; set; } = "-";

        /// <summary>码率（1411kbps / 320kbps）。</summary>
        public string BitrateText { get; set; } = "-";

        /// <summary>文件大小（4.2 MB / 812 KB / 340 B）。</summary>
        public string FileSizeText { get; set; } = "-";
    }

    /// <summary>表头列宽拖拽把手：6px 透明热区 + 东西向光标（ProtectedCursor 只有子类能设）。</summary>
    internal sealed class ColumnResizeGrip : Grid
    {
        public ColumnResizeGrip()
        {
            Width = 6;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Stretch;
            // 透明但可命中（null Background 不参与命中测试）
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            try
            {
                ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                    Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
            }
            catch
            {
                // 老系统没有该光标形状就算了，不影响拖拽
            }
        }
    }
}
