using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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

        /// <summary>空提示区的默认引导语（XAML 初值，ctor 里收下来；失败原因临时替换它）。</summary>
        private string _emptyHintDefault = string.Empty;

        /// <summary>预览加载代数：快速切换选择时只认最后一次，避免旧文件的标签覆盖新文件的。</summary>
        private int _previewGeneration;

        /// <summary>当前选中的行（单选编辑 / 多选批量填写都按它来）。</summary>
        private List<TagFileItem> _quickEditSelection = new();

        /// <summary>单选模式正在编辑的文件路径；多选 / 未选时为 null。</summary>
        private string? _quickEditPath;

        /// <summary>单选模式加载时的原值（「还原」与「没有改动」判断用）。</summary>
        private TagEditModel? _quickEditLoaded;

        /// <summary>
        /// 标签写盘成功事件（每个路径触发一次）。主窗口订阅它来刷新曲库元数据——
        /// 取代旧 TagEditorWindow.TagsSaved（阶段 6 收尾后旧窗口已删）。
        /// </summary>
        public event Action<string>? TagsSaved;

        // =====================================================================
        // 表格单元格内联编辑（2026-10-13 用户实测要求：单击单元格直接改）
        //
        // 口径（对齐 mp3tag 习惯）：可编辑列是常驻文本框，单击即改；回车或点别处
        // 提交并立即写盘（读全量→改一个字段→写，失败把这一格退回原值）。
        // 长字段（标题/艺术家/专辑/流派）编辑时临时开换行「展开」，失焦收回单行。
        // Esc = 放弃这一格的改动。不可编辑列（#/时长/文件名/格式/位深/采样率/码率/大小）
        // 保持只读文字。
        // =====================================================================

        /// <summary>正在编辑的单元格所属行（提交时以 box.DataContext 为准，这里只作辅助）。</summary>
        private TagFileItem? _cellItem;

        /// <summary>正在编辑的列号（1..6；-1 = 没有）。</summary>
        private int _cellCol = -1;

        /// <summary>这一格获焦时的原文字（「没动不写盘」与 Esc 恢复用）。</summary>
        private string _cellOriginal = string.Empty;

        /// <summary>Esc 取消标记：置位后随后的 LostFocus 只收回不提交。</summary>
        private bool _cellCancelled;

        /// <summary>单元格写盘串行闸：避免两个格子的提交并发写同一个文件。</summary>
        private readonly SemaphoreSlim _cellSaveGate = new(1, 1);

        public TagPanelControl(Func<Brush>? accentProvider = null, nint hostHwnd = default)
        {
            _accentProvider = accentProvider;
            _hostHwnd = hostHwnd;
            InitializeComponent();

            _emptyHintDefault = TagListEmptyHint.Text;

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
                    return; // 用户取消
                }

                await AddFilesAsync(files.Select(f => f.Path), "文件选取器");
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
                if (folder == null)
                {
                    return; // 用户取消
                }

                if (string.IsNullOrWhiteSpace(folder.Path))
                {
                    // 特殊 shell 位置（库文件夹等）解析不出磁盘路径：明说，别静默
                    global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 添加文件夹返回了空路径");
                    ShowEmptyHint("没能解析刚才所选文件夹的位置，请换一个普通磁盘文件夹再试。");
                    TagStatusText.Text = "所选文件夹无法解析出路径。";
                    return;
                }

                TagStatusText.Text = "正在扫描文件夹…";
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                string[] paths = await Task.Run(() => EnumerateAudioFiles(folder.Path).ToArray());
                sw.Stop();
                global::CelesteMusicPlayer.StartupLog.Write(
                    $"TagPanel: 扫描文件夹 {folder.Path} → {paths.Length} 个音频（{sw.ElapsedMilliseconds}ms）");

                if (paths.Length == 0)
                {
                    ShowEmptyHint("这个文件夹（含里面的子文件夹）里没有找到支持的音频文件。换个文件夹试试。");
                    TagStatusText.Text = "该文件夹（含子文件夹）里没有支持的音频文件。";
                    return;
                }

                await AddFilesAsync(paths, $"文件夹 {folder.Path}");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.AddFolderButton_Click", caught);
                TagStatusText.Text = "添加文件夹失败：" + caught.Message;
            }
        }

        /// <summary>把一批路径加进列表（自动去重；读标签在后台线程做，不卡界面）。</summary>
        private async Task AddFilesAsync(IEnumerable<string> paths, string source = "文件")
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
                if (_files.Count == 0)
                {
                    ShowEmptyHint(skipped > 0
                        ? "选中的文件都已经在列表里了，没有新文件可加。"
                        : "没有选中可添加的文件。");
                }
                return;
            }

            TagStatusText.Text = $"正在读取标签 0/{fresh.Length}…";
            // 导入一开始就收起空提示：文件正在来的路上，别让用户对着「列表还是空的」干等
            TagListEmptyHint.Visibility = Visibility.Collapsed;

            // 逐批读：每批 50 个，读一批报一批进度（大文件夹不会假死一片）
            System.Diagnostics.Stopwatch readSw = System.Diagnostics.Stopwatch.StartNew();
            const int BatchSize = 50;
            int done = 0;
            int added = 0;
            for (int i = 0; i < fresh.Length; i += BatchSize)
            {
                string[] batch = fresh.Skip(i).Take(BatchSize).ToArray();
                TagFileItem?[] entries = await Task.Run(() => batch.Select(BuildEntry).ToArray());

                foreach (TagFileItem? entry in entries)
                {
                    if (entry != null)
                    {
                        _files.Add(entry);
                        added++;
                    }
                }

                done += batch.Length;
                TagStatusText.Text = $"正在读取标签 {done}/{fresh.Length}…";
            }

            readSw.Stop();
            int failed = fresh.Length - added;
            Renumber();
            RefreshCountUi();
            global::CelesteMusicPlayer.StartupLog.Write(
                $"TagPanel: {source} 新增 {added} 个，跳过重复 {skipped} 个，读取失败 {failed} 个，读标签耗时 {readSw.ElapsedMilliseconds}ms");

            if (added == 0)
            {
                // 全部读取失败：提示区说清楚，别只留一行小字
                ShowEmptyHint($"选中的 {fresh.Length} 个文件都没能读出标签（详情见日志），一个都没加进来。");
                TagStatusText.Text = "标签读取失败，详见日志。";
                return;
            }

            ResetEmptyHint();
            TagStatusText.Text = $"已添加 {added} 个文件"
                + (skipped > 0 ? $"（跳过重复 {skipped} 个）" : string.Empty)
                + (failed > 0 ? $"（{failed} 个读取失败）" : string.Empty)
                + "。";
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
            if (_files.Count == 0)
            {
                ResetEmptyHint();
            }

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
            ResetEmptyHint();
            ClearQuickEditPreview();
            TagStatusText.Text = $"已清空 {count} 个文件（只是移出本列表，不动硬盘上的文件）。";
        }

        // =====================================================================
        // 快速编辑页：选中文件的标签编辑（阶段 6：可编辑 + 保存 + 快照整体回滚）
        // =====================================================================

        private async void TagFileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_panelReady)
            {
                return;
            }

            List<TagFileItem> selected = TagFileListView.SelectedItems.Cast<TagFileItem>().ToList();
            _quickEditSelection = selected;
            RefreshCountUi();

            if (selected.Count == 0)
            {
                ClearQuickEditPreview();
                return;
            }

            if (selected.Count > 1)
            {
                // 多选：批量填写模式（只填想改的字段，留空的不动）
                _previewGeneration++;
                _quickEditPath = null;
                _quickEditLoaded = null;
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
                _quickEditPath = null;
                _quickEditLoaded = null;
                QuickEditTitle.Text = "标签读取失败";
                return;
            }

            _quickEditPath = path;
            _quickEditLoaded = loaded.Value.Tag;
            ApplyQuickEditPreview(path, loaded.Value.Tag, loaded.Value.Cover);
        }

        private void ApplyQuickEditPreview(string path, TagEditModel model, byte[]? cover)
        {
            QuickEditFileCard.Visibility = Visibility.Visible;
            ApplyQuickEditFields(path, model);
            ApplyCoverToImage(cover);
        }

        /// <summary>只填字段框（不动封面）——「还原」和落盘后刷新预览都用它。</summary>
        private void ApplyQuickEditFields(string path, TagEditModel model)
        {
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

            QuickEditModeHint.Text = _quickEditSelection.Count > 1
                ? $"多选模式：填好的字段会写入全部 {_quickEditSelection.Count} 个文件，留空的字段保持原值。想把某个字段清空，请单选该文件后编辑。"
                : "「还原」= 放弃还没保存的改动，恢复成磁盘上的当前值。";
        }

        // =====================================================================
        // 阶段 6：保存 / 还原 / 改动前快照整体回滚
        // =====================================================================

        /// <summary>「还原」：放弃还没保存的改动。单选=恢复成加载时的值；多选=清空（留空即不动）。</summary>
        private void QuickRevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_quickEditLoaded != null && _quickEditSelection.Count == 1)
            {
                // 只还原字段，不碰封面（封面要重看得重新选一次文件）
                ApplyQuickEditFields(_quickEditPath ?? string.Empty, _quickEditLoaded);
                TagStatusText.Text = "已还原成磁盘上的当前值（封面未动）。";
                return;
            }

            if (_quickEditSelection.Count > 1)
            {
                ShowQuickEditBatchCard(_quickEditSelection.Count);
                TagStatusText.Text = "已清空批量填写框（留空的字段不会被修改）。";
                return;
            }

            TagStatusText.Text = "还没有选中文件。";
        }

        /// <summary>
        /// 「保存」：把字段写进选中的文件。安全底线 = 改动前快照 + 失败整体回滚：
        /// 先读每个目标文件的原标签存起来，再逐个写；任何一个写失败，
        /// 已经把成功写过的文件全部用快照写回原标签，绝不留在半改状态。
        /// </summary>
        private async void QuickSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_quickEditSelection.Count == 0)
                {
                    TagStatusText.Text = "先在右侧文件列表里选中要保存的文件。";
                    return;
                }

                string yearText = (QuickYearBox.Text ?? string.Empty).Trim();
                string trackText = (QuickTrackBox.Text ?? string.Empty).Trim();
                if (yearText.Length > 0 && !uint.TryParse(yearText, out _))
                {
                    TagStatusText.Text = "年份要填数字（比如 2024），也可以留空。";
                    return;
                }

                if (trackText.Length > 0 && !uint.TryParse(trackText, out _))
                {
                    TagStatusText.Text = "曲目号要填数字（比如 3），也可以留空。";
                    return;
                }

                bool batch = _quickEditSelection.Count > 1;
                TagEditModel template = new()
                {
                    Title = QuickTitleBox.Text ?? string.Empty,
                    Artist = QuickArtistBox.Text ?? string.Empty,
                    Album = QuickAlbumBox.Text ?? string.Empty,
                    AlbumArtist = QuickAlbumArtistBox.Text ?? string.Empty,
                    Year = yearText.Length > 0 ? uint.Parse(yearText) : 0,
                    Track = trackText.Length > 0 ? uint.Parse(trackText) : 0,
                    Genre = QuickGenreBox.Text ?? string.Empty,
                    Comment = QuickCommentBox.Text ?? string.Empty,
                    Lyrics = QuickLyricsBox.Text ?? string.Empty
                };

                // 单选且一个字段都没动 → 不写盘
                if (!batch && _quickEditLoaded != null && ModelsEqual(template, _quickEditLoaded))
                {
                    TagStatusText.Text = "没有改动，不需要保存。";
                    return;
                }

                List<TagFileItem> targets = _quickEditSelection.ToList();
                TagStatusText.Text = batch
                    ? $"正在保存到 {targets.Count} 个文件…"
                    : "正在保存…";

                // 改动前快照（每个文件读一遍原标签）→ 逐个写 → 有失败整体回滚
                List<(string Path, TagEditModel Before)> snapshots = new();
                List<string> failures = new();
                await Task.Run(() =>
                {
                    foreach (TagFileItem item in targets)
                    {
                        TagEditModel before = TagEditorService.ReadTag(item.FilePath);
                        snapshots.Add((item.FilePath, before));

                        TagEditModel toWrite = batch ? MergeForBatch(template, before) : template;
                        if (!TagEditorService.SaveTag(item.FilePath, toWrite))
                        {
                            failures.Add(item.FilePath);
                        }
                    }
                });

                if (failures.Count > 0)
                {
                    int rolledBack = 0;
                    await Task.Run(() =>
                    {
                        foreach ((string path, TagEditModel before) in snapshots)
                        {
                            if (failures.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (TagEditorService.SaveTag(path, before))
                            {
                                rolledBack++;
                            }
                        }
                    });

                    global::CelesteMusicPlayer.StartupLog.Write(
                        $"TagPanel: 保存失败整体回滚：目标 {targets.Count}，失败 {failures.Count}，已还原 {rolledBack}");
                    TagStatusText.Text = $"{failures.Count} 个文件保存失败；已写成功的 {rolledBack} 个已还原到改动前的标签。详情见日志。";
                    return;
                }

                global::CelesteMusicPlayer.StartupLog.Write(
                    $"TagPanel: 已保存 {targets.Count} 个文件（{(batch ? "批量" : "单选")}）");

                // 通知主窗口刷新曲库元数据（取代旧 TagEditorWindow.TagsSaved）
                foreach (TagFileItem item in targets)
                {
                    TagsSaved?.Invoke(item.FilePath);
                }

                // 写成功了：刷新受影响的行（行模型换新实例，OneTime 绑定重新求值）
                await RefreshSavedRowsAsync(targets);

                TagStatusText.Text = batch
                    ? $"已保存到 {targets.Count} 个文件。"
                    : "已保存。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.QuickSaveButton_Click", caught);
                TagStatusText.Text = "保存失败：" + caught.Message;
            }
        }

        /// <summary>批量模式合并：模板里留空的字段保持原值（想清空字段请单选该文件编辑）。</summary>
        private static TagEditModel MergeForBatch(TagEditModel template, TagEditModel before)
        {
            return new TagEditModel
            {
                Title = string.IsNullOrEmpty(template.Title) ? before.Title : template.Title,
                Artist = string.IsNullOrEmpty(template.Artist) ? before.Artist : template.Artist,
                Album = string.IsNullOrEmpty(template.Album) ? before.Album : template.Album,
                AlbumArtist = string.IsNullOrEmpty(template.AlbumArtist) ? before.AlbumArtist : template.AlbumArtist,
                Year = template.Year != 0 ? template.Year : before.Year,
                Track = template.Track != 0 ? template.Track : before.Track,
                Genre = string.IsNullOrEmpty(template.Genre) ? before.Genre : template.Genre,
                Comment = string.IsNullOrEmpty(template.Comment) ? before.Comment : template.Comment,
                Lyrics = string.IsNullOrEmpty(template.Lyrics) ? before.Lyrics : template.Lyrics
            };
        }

        private static bool ModelsEqual(TagEditModel a, TagEditModel b)
        {
            return a.Title == b.Title
                && a.Artist == b.Artist
                && a.Album == b.Album
                && a.AlbumArtist == b.AlbumArtist
                && a.Year == b.Year
                && a.Track == b.Track
                && a.Genre == b.Genre
                && a.Comment == b.Comment
                && a.Lyrics == b.Lyrics;
        }

        /// <summary>
        /// 保存成功后刷新行：BuildEntry 重读磁盘生成新行模型替换旧实例
        /// （x:Bind 默认 OneTime，不换实例行上的文字不会变），并尽量恢复原选中。
        /// </summary>
        private async Task RefreshSavedRowsAsync(List<TagFileItem> saved)
        {
            HashSet<string> selectedPaths = new(
                TagFileListView.SelectedItems.Cast<TagFileItem>().Select(i => i.FilePath),
                StringComparer.OrdinalIgnoreCase);

            (TagFileItem Old, TagFileItem? New)[] rebuilt = await Task.Run(() => saved
                .Select(i => (i, BuildEntry(i.FilePath)))
                .ToArray());

            foreach ((TagFileItem old, TagFileItem? neo) in rebuilt)
            {
                if (neo == null)
                {
                    continue;
                }

                int idx = _files.IndexOf(old);
                if (idx >= 0)
                {
                    _files[idx] = neo;
                }
            }

            // 恢复选中（单选时顺带让预览重读磁盘，确认落盘结果）
            if (selectedPaths.Count > 0)
            {
                TagFileListView.SelectedItems.Clear();
                foreach (TagFileItem item in _files)
                {
                    if (selectedPaths.Contains(item.FilePath))
                    {
                        TagFileListView.SelectedItems.Add(item);
                    }
                }
            }

            // 换进来的新实例序号是 0，重排一次（行模型 INPC，可见行实时更新）
            Renumber();
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
            QuickEditPath.Text = "批量填写：只填想改的字段，留空的不动";
            QuickEditModeHint.Text = $"多选模式：填好的字段会写入全部 {count} 个文件，留空的字段保持原值。想把某个字段清空，请单选该文件后编辑。";
        }

        private void ClearQuickEditPreview()
        {
            _quickEditPath = null;
            _quickEditLoaded = null;
            _quickEditSelection = new List<TagFileItem>();

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
            QuickEditModeHint.Text = "「还原」= 放弃还没保存的改动，恢复成磁盘上的当前值。";
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
        // 表格单元格内联编辑（事件入口）
        // =====================================================================

        /// <summary>单元格获焦：同步选中所在行（TextBox 会吞掉 ListViewItem 的选中点击），记录原值，长字段展开换行。</summary>
        private void TagCellBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not TagFileItem item)
            {
                return;
            }

            // 点单元格也要选中所在行；多选时收敛到这一行（编辑本来就是单文件动作）
            try
            {
                if (!TagFileListView.SelectedItems.Contains(item))
                {
                    TagFileListView.SelectedItem = item;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.TagCellBox_GotFocus", caught);
            }

            int col = Grid.GetColumn(box);
            _cellItem = item;
            _cellCol = col;
            _cellOriginal = box.Text ?? string.Empty;
            _cellCancelled = false;

            // 长字段编辑时「展开」：临时开换行，行高随内容长高，失焦收回单行
            if (col is 1 or 2 or 3 or 6)
            {
                box.TextWrapping = TextWrapping.Wrap;
            }
        }

        /// <summary>单元格失焦：收回单行 + 提交写盘（Esc 取消过就只收回）。</summary>
        private async void TagCellBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box)
            {
                return;
            }

            int col = Grid.GetColumn(box);
            if (col is 1 or 2 or 3 or 6)
            {
                box.TextWrapping = TextWrapping.NoWrap;
            }

            if (_cellCancelled)
            {
                _cellCancelled = false;
                return;
            }

            await CommitCellEditAsync(box);
        }

        /// <summary>回车 = 提交（焦点交还列表，随后的失焦负责落盘）；Esc = 放弃这一格。</summary>
        private void TagCellBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox box)
            {
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                // 焦点移走会触发 LostFocus → 自动提交；这里只负责挪焦点
                TagFileListView.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                _cellCancelled = true;
                box.Text = _cellOriginal;
                TagFileListView.Focus(FocusState.Programmatic);
            }
        }

        /// <summary>
        /// 单元格提交：把这一格的新值写进文件。安全口径与左侧「保存」一致——
        /// 先读全量原标签做快照，只改这一个字段再写；写失败这一格退回原值
        /// （磁盘上的原标签没动过，不需要回滚写盘）。没动过的一个字节都不写。
        /// </summary>
        private async Task CommitCellEditAsync(TextBox box)
        {
            if (box.DataContext is not TagFileItem item)
            {
                return;
            }

            int col = Grid.GetColumn(box);
            if (col is < 1 or > 6)
            {
                return;
            }

            string path = item.FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string newText = (box.Text ?? string.Empty).Trim();
            string original = _cellOriginal;

            // 文字没动 → 不写盘
            if (string.Equals(newText, original, StringComparison.Ordinal))
            {
                return;
            }

            string fieldName = col switch
            {
                1 => "标题",
                2 => "艺术家",
                3 => "专辑",
                4 => "年份",
                5 => "曲目号",
                _ => "流派"
            };

            // 数字列先校验（失败只退回这一格，不碰文件）
            if (col == 4 && newText.Length > 0 && !uint.TryParse(newText, out _))
            {
                box.Text = original;
                TagStatusText.Text = "年份要填数字（比如 2024），也可以留空；已恢复原值。";
                return;
            }

            if (col == 5 && newText.Length > 0 && !uint.TryParse(newText, out _))
            {
                box.Text = original;
                TagStatusText.Text = "曲目号要填数字（比如 3），也可以留空；已恢复原值。";
                return;
            }

            await _cellSaveGate.WaitAsync();
            try
            {
                TagStatusText.Text = $"正在保存{fieldName}…";

                bool ok = await Task.Run(() =>
                {
                    TagEditModel before = TagEditorService.ReadTag(path);
                    TagEditModel toWrite = new()
                    {
                        Title = before.Title,
                        Artist = before.Artist,
                        Album = before.Album,
                        AlbumArtist = before.AlbumArtist,
                        Year = before.Year,
                        Track = before.Track,
                        Genre = before.Genre,
                        Comment = before.Comment,
                        Lyrics = before.Lyrics
                    };

                    switch (col)
                    {
                        case 1: toWrite.Title = newText; break;
                        case 2: toWrite.Artist = newText; break;
                        case 3: toWrite.Album = newText; break;
                        case 4: toWrite.Year = newText.Length == 0 ? 0 : uint.Parse(newText); break;
                        case 5: toWrite.Track = newText.Length == 0 ? 0 : uint.Parse(newText); break;
                        default: toWrite.Genre = newText; break;
                    }

                    return TagEditorService.SaveTag(path, toWrite);
                });

                if (!ok)
                {
                    box.Text = original;
                    TagStatusText.Text = $"{fieldName}保存失败；已恢复原值，详情见日志。";
                    return;
                }

                global::CelesteMusicPlayer.StartupLog.Write($"TagPanel: 单元格保存 {fieldName} → {path}");
                TagsSaved?.Invoke(path);

                // 刷新行（换实例，OneTime 绑定重新求值）；顺带刷新左栏预览（若正在看这个文件）
                await RefreshSavedRowsAsync(new List<TagFileItem> { item });

                TagStatusText.Text = $"已保存{fieldName}。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CommitCellEdit", caught);
                box.Text = original;
                TagStatusText.Text = $"{fieldName}保存失败：" + caught.Message;
            }
            finally
            {
                _cellSaveGate.Release();
            }
        }

        /// <summary>
        /// 把指定文件招进标签板块：不在列表里的先加（读标签在后台线程），
        /// 全部选中并确保停在「快速编辑」子页。旧 TagEditorWindow 的各处入口
        /// （右键菜单 / 播放页 / 设置 / WebUI 命令）阶段 6 收尾后统一走这里。
        /// </summary>
        public async void RevealFiles(IReadOnlyList<string>? paths)
        {
            List<string> wanted = (paths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wanted.Count == 0)
            {
                return;
            }

            // 确保停在「快速编辑」子页（注册顺序 0）
            _subNav.Select(0);

            await AddFilesAsync(wanted, "跳转编辑");

            HashSet<string> want = new(wanted, StringComparer.OrdinalIgnoreCase);
            TagFileListView.SelectedItems.Clear();
            foreach (TagFileItem item in _files)
            {
                if (want.Contains(item.FilePath))
                {
                    TagFileListView.SelectedItems.Add(item);
                }
            }
        }

        // =====================================================================
        // 内部工具
        // =====================================================================

        /// <summary>
        /// 由一个音频文件路径建一行。字段语义与主曲库的 CreatePlaylistItemFromPath 严格一致
        /// （同样的缺省值：未知艺术家 / 未知专辑 / 未知流派），不碰评分统计。
        /// 性能口径（2026-10-13 用户实测「几秒才 50 首」后重做）：
        /// **整个文件只开一次 TagLib**——标签、时长、采样率/位深/码率全部从同一个 File.Properties 拿；
        /// 不起 ffmpeg 子进程（编码器名表格用不到；只有 TagLib 什么都没读到的格式才回退完整探测）。
        /// 旧实现每个文件要开两遍 TagLib + mp3/m4a/ogg 再各起一个 ffmpeg，50 个文件就是上百次进程创建。
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
            int rate = 0;
            int bits = 0;
            int kbps = 0;

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

                // 派生列直接吃刚打开的 Properties：零额外 I/O、零子进程
                AudioInfoFormatter.TryGetFactsFast(tagFile, path, out rate, out bits, out kbps);
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

            // 文件派生列（格式 / 位深 / 采样率 / 码率 / 大小）：格式走扩展名，规格走上面那一趟
            // TagLib 读取（TryGetFactsFast 内部对 DSD 等 TagLib 读不了的格式会回退完整探测）。
            try
            {
                string ext = System.IO.Path.GetExtension(path);
                item.FormatName = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "-";

                item.BitDepthText = bits > 0 ? bits + "bit" : "-";
                item.SampleRateText = rate > 0 ? FormatSampleRate(rate) : "-";
                item.BitrateText = kbps > 0 ? kbps + "kbps" : "-";

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

        /// <summary>空提示区换成一条具体原因（导入失败 / 扫描为空时调用，列表空着才看得见）。</summary>
        private void ShowEmptyHint(string reason)
        {
            TagListEmptyHint.Text = reason;
            TagListEmptyHint.Visibility = Visibility.Visible;
        }

        /// <summary>空提示区恢复默认引导语。</summary>
        private void ResetEmptyHint()
        {
            TagListEmptyHint.Text = _emptyHintDefault;
        }

        private void RefreshCountUi()
        {
            int total = _files.Count;
            int selected = TagFileListView?.SelectedItems.Count ?? 0;

            FileCountText.Text = total > 0 ? $"共 {total} 个文件" : "0 个文件";
            // 只切显隐，不改文字：文字由 ShowEmptyHint / ResetEmptyHint 管
            // （失败原因要在列表空时一直显示，不能被这里冲掉）
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
        //
        // 拖拽语义（2026-10-13 用户实测定稿）：竖线右侧的区域一起跟着动、左侧不动。
        // 实现 = 14 个真实列全部定宽 + 末尾 1 个不可见星号填充列（FillerCol）吸收
        // 「视口比内容宽」时的剩余宽度。星号列若混在真实列中间（旧设计放在标题列），
        // 它会把被拖列的变化量吞掉，出现「往左拖、字往右去」的怪事。
        // =====================================================================

        /// <summary>总列数（与 XAML 两处 ColumnDefinitions 严格一致）。</summary>
        private const int ColCount = 15;

        /// <summary>末尾填充列下标（不可见、不拖拽，只负责吸收剩余宽度）。</summary>
        private const int FillerCol = 14;

        private const double ColSpacing = 8;
        private const double RowPaddingX = 10;

        private static readonly double[] ColDefaultWidths =
            { 36, 100, 130, 130, 52, 44, 84, 60, 150, 64, 52, 76, 68, 76 };

        private static readonly double[] ColMinWidths =
            { 28, 100, 60, 60, 36, 32, 48, 44, 80, 44, 36, 48, 48, 48 };

        private readonly double[] _colWidths = (double[])ColDefaultWidths.Clone();
        private double _gridMinWidth;
        private double _listViewportW;
        private double _lastViewportW = -1;
        private ScrollViewer? _listScroller;
        private TranslateTransform? _headerSlide;

        /// <summary>上次应用列宽时是否处于「内容宽于视口」（溢出滚动）状态。只有状态翻转才需要全量重刷。</summary>
        private bool _overflowState;

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

            // 3) 表头列边界生成拖拽把手（6px 热区 + 1px 可见竖线，代码生成免得 XAML 写 14 遍）
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

                // 竖滚动条出现/消失会改变视口宽 → 行宽阈值跟着变（只有溢出状态翻转才全量重刷）
                double vw = _listScroller?.ViewportWidth ?? 0;
                if (vw > 0 && Math.Abs(vw - _lastViewportW) > 0.5)
                {
                    _lastViewportW = vw;
                    _listViewportW = vw;
                    RefreshOverflowState();
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
            for (int i = 0; i < FillerCol; i++)
            {
                ColumnResizeGrip grip = new(_accentProvider);
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
            _dragStartW = _colWidths[col];
            grip.CapturePointer(e.Pointer);
            grip.SetDragging(true);
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
            grip.SetDragging(false);
            _dragCol = -1;
            e.Handled = true;
        }

        private void SetColumnWidth(int col, double width)
        {
            if (col < 0 || col >= FillerCol)
            {
                return;
            }

            if (width < ColMinWidths[col])
            {
                width = ColMinWidths[col];
            }

            if (Math.Abs(_colWidths[col] - width) < 0.5)
            {
                return;
            }

            _colWidths[col] = width;
            ApplyColumnWidths();
        }

        /// <summary>按当前列宽刷新表头 + 所有已实现（可见）的行。</summary>
        private void ApplyColumnWidths()
        {
            double fixedSum = 0;
            for (int i = 0; i < FillerCol; i++)
            {
                fixedSum += _colWidths[i];
            }
            _gridMinWidth = fixedSum + ColSpacing * (ColCount - 1) + RowPaddingX * 2;

            // 表头与行用同一个参照视口（列表的内部视口，含竖滚动条占位），
            // 否则竖滚动条出现时表头会比行宽一点，列就对不齐
            ApplyWidthsToGrid(TagListHeaderGrid, _listViewportW);

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

            _overflowState = _listViewportW > 0 && _listViewportW < _gridMinWidth;
        }

        /// <summary>
        /// 视口宽变化后调用。只有「溢出/不溢出」状态翻转才全量重刷：
        /// 不溢出时末尾星号填充列自动吸收剩余宽度，行会自己拉伸；
        /// 每个 tick 都逐行设宽正是拖窗口卡顿的根源（2026-10-13 用户实测）。
        /// </summary>
        private void RefreshOverflowState()
        {
            bool overflow = _listViewportW > 0 && _listViewportW < _gridMinWidth;
            if (overflow != _overflowState)
            {
                ApplyColumnWidths();
            }
        }

        private void ApplyWidthsToGrid(Grid grid, double viewportW)
        {
            ColumnDefinitionCollection cols = grid.ColumnDefinitions;
            for (int i = 0; i < cols.Count && i < FillerCol; i++)
            {
                cols[i].Width = new GridLength(_colWidths[i]);
            }

            // 末尾填充列不碰（XAML 里就是 *，自动吸收剩余宽度）
            grid.MinWidth = _gridMinWidth;
            // 视口比总宽窄 → 内容按总宽渲染（超出部分靠裁剪/横向滚动）；
            // 否则交回自动拉伸（填充列填满剩余空间）。NaN = 清除显式宽度。
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
            RefreshOverflowState();
        }

        private void TagFileListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double vw = _listScroller?.ViewportWidth ?? 0;
            if (vw > 0 && Math.Abs(vw - _lastViewportW) > 0.5)
            {
                _lastViewportW = vw;
                _listViewportW = vw;
            }
            RefreshOverflowState();
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
    public sealed class TagFileItem : PlaylistItem, INotifyPropertyChanged
    {
        private int _index;

        /// <summary>
        /// 行序号（1 起）。override 基类属性是为了赋值时通知界面：行模板的序号列是
        /// x:Bind OneWay，只有行模型实现 INPC 并为 Index/IndexLabel 发通知，Renumber
        /// 重排后可见行的序号才会变（旧实现 OneTime 绑定在行实现时就求值，序号永远
        /// 停在 0——2026-10-13 用户实测「# 号下方全是 0」）。
        /// </summary>
        public override int Index
        {
            get => _index;
            set
            {
                int v = value < 0 ? 0 : value;
                if (_index == v)
                {
                    return;
                }

                _index = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexLabel)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>
    /// 表头列宽拖拽把手：6px 热区 + 1px 可见竖线（贴在列边界上，与行内淡竖线同一根线）。
    /// 悬停 / 拖拽中竖线加粗并染强调色（外部注入，遵守取色铁律）。
    /// 拖拽语义：改的是本列（竖线左边）的宽，右侧所有列整体跟随平移，左侧不动。
    /// </summary>
    internal sealed class ColumnResizeGrip : Grid
    {
        private readonly Rectangle _line;
        private readonly SolidColorBrush _normalBrush;
        private readonly Func<Brush>? _accentProvider;
        private bool _hover;
        private bool _drag;

        public ColumnResizeGrip(Func<Brush>? accentProvider = null)
        {
            _accentProvider = accentProvider;
            Width = 6;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Stretch;
            // 透明但可命中（null Background 不参与命中测试）
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));

            _normalBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80));
            _line = new Rectangle
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Fill = _normalBrush
            };
            Children.Add(_line);

            PointerEntered += (s, e) => { _hover = true; RefreshVisual(); };
            PointerExited += (s, e) => { _hover = false; RefreshVisual(); };
            PointerCanceled += (s, e) => { _hover = false; RefreshVisual(); };

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

        /// <summary>拖拽开始/结束：期间指针被把手捕获，PointerExited 不一定可靠，显式压住高亮。</summary>
        internal void SetDragging(bool dragging)
        {
            _drag = dragging;
            RefreshVisual();
        }

        private void RefreshVisual()
        {
            bool hot = _hover || _drag;
            _line.Width = hot ? 2 : 1;

            Brush? accent = null;
            if (hot)
            {
                try
                {
                    accent = _accentProvider?.Invoke();
                }
                catch
                {
                    // 取色失败退回灰色加粗，别让把手失效
                }
            }

            _line.Fill = accent ?? _normalBrush;
        }
    }
}
