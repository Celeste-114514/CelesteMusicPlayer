using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        // 口径（对齐 mp3tag 习惯）：可编辑列是常驻文本框，**两击编辑**——
        // 第一次单击只选中所在行（TextBox 的 AllowFocusOnInteraction=False 挡住
        // 自动聚焦），第二次单击同一格才真正进编辑；回车或点别处提交并立即写盘
        // （读全量→改一个字段→写，失败把这一格退回原值）。
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

        /// <summary>「武装」状态：已被单击过一次、等待第二次单击的格子（行 + 列号）。</summary>
        private TagFileItem? _armedItem;

        /// <summary>武装格子的列号（-1 = 没有）。</summary>
        private int _armedCol = -1;

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

        // =====================================================================
        // 阶段 7：批量工具（统一快照 → 逐个写 → 失败整体回滚）
        // =====================================================================

        /// <summary>批量工具的作用范围：右侧列表选中项；一个都没选 = 整个列表。</summary>
        private (List<TagFileItem> Targets, bool WholeList) BatchTargets()
        {
            List<TagFileItem> selected = TagFileListView.SelectedItems.Cast<TagFileItem>().ToList();
            if (selected.Count > 0)
            {
                return (selected, false);
            }

            return (_files.ToList(), true);
        }

        private static string BatchScopeText(bool wholeList, int count)
        {
            return wholeList ? $"（整个列表 {count} 个）" : $"（所选 {count} 个）";
        }

        /// <summary>
        /// 批量工具统一执行器：先读每个目标文件的**磁盘真值**做快照，再逐个执行
        /// transform；返回 null = 跳过，与快照完全一样 = 没动不写盘。任何一个写失败，
        /// 已写成功的文件全部用快照写回原标签，绝不留在半改状态。
        /// </summary>
        private async Task<(List<string> Written, int Skipped, List<string> Failures)> RunBatchWithRollbackAsync(
            List<TagFileItem> targets,
            Func<string, TagEditModel, TagEditModel?> transform,
            string opName)
        {
            List<(string Path, TagEditModel Before)> snapshots = new();
            List<string> failures = new();
            List<string> written = new();
            int skipped = 0;

            await Task.Run(() =>
            {
                foreach (TagFileItem item in targets)
                {
                    string path = item.FilePath;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        skipped++;
                        continue;
                    }

                    TagEditModel before = TagEditorService.ReadTagRaw(path);
                    snapshots.Add((path, before));

                    TagEditModel? toWrite = transform(path, before);
                    if (toWrite == null || ModelsEqual(toWrite, before))
                    {
                        skipped++;
                        continue;
                    }

                    if (TagEditorService.SaveTag(path, toWrite))
                    {
                        written.Add(path);
                    }
                    else
                    {
                        failures.Add(path);
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
                        if (!written.Contains(path, StringComparer.OrdinalIgnoreCase))
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
                    $"TagPanel: 批量[{opName}]失败整体回滚：目标 {targets.Count}，失败 {failures.Count}，已还原 {rolledBack}");
                return (written, skipped, failures);
            }

            global::CelesteMusicPlayer.StartupLog.Write(
                $"TagPanel: 批量[{opName}]完成：写 {written.Count}，跳过 {skipped}，目标 {targets.Count}");
            return (written, skipped, failures);
        }

        /// <summary>批量工具收尾：通知主窗口刷新元数据、重建受影响的行、汇报结果。</summary>
        private async Task FinishBatchAsync(
            List<TagFileItem> targets, List<string> written, int skipped, List<string> failures, bool wholeList)
        {
            if (failures.Count > 0)
            {
                BatchStatusText.Text = $"{failures.Count} 个文件处理失败；已改成功的文件已整体还原到改动前的标签。详情见日志。";
                return;
            }

            if (written.Count == 0)
            {
                BatchStatusText.Text = skipped > 0
                    ? $"没有需要改的文件{BatchScopeText(wholeList, targets.Count)}（全部已是目标状态，或不满足条件被跳过）。"
                    : "没有可处理的文件。";
                return;
            }

            foreach (string path in written)
            {
                TagsSaved?.Invoke(path);
            }

            List<TagFileItem> touched = targets
                .Where(t => written.Contains(t.FilePath, StringComparer.OrdinalIgnoreCase))
                .ToList();
            await RefreshSavedRowsAsync(touched);

            BatchStatusText.Text = skipped > 0
                ? $"完成{BatchScopeText(wholeList, targets.Count)}：改了 {written.Count} 个文件，跳过 {skipped} 个。"
                : $"完成{BatchScopeText(wholeList, targets.Count)}：改了 {written.Count} 个文件。";
        }

        /// <summary>① 文件名 → 标签：按「艺术家 - 标题」解析；默认只填空，勾选后才覆盖。</summary>
        private async void BatchFileNameFillButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                bool overwrite = BatchFileNameOverwriteCheck.IsChecked == true;
                BatchStatusText.Text = "正在从文件名填充标签…";

                (List<string> written, int skipped, List<string> failures) = await RunBatchWithRollbackAsync(
                    targets,
                    (path, before) =>
                    {
                        TagEditModel parsed = TagEditorService.TryParseTagsFromFileName(System.IO.Path.GetFileName(path));
                        TagEditModel toWrite = CloneModel(before);
                        bool changed = false;

                        if (!string.IsNullOrWhiteSpace(parsed.Title)
                            && (overwrite || string.IsNullOrWhiteSpace(toWrite.Title))
                            && !string.Equals(toWrite.Title, parsed.Title, StringComparison.Ordinal))
                        {
                            toWrite.Title = parsed.Title;
                            changed = true;
                        }

                        if (!string.IsNullOrWhiteSpace(parsed.Artist)
                            && (overwrite || string.IsNullOrWhiteSpace(toWrite.Artist))
                            && !string.Equals(toWrite.Artist, parsed.Artist, StringComparison.Ordinal))
                        {
                            toWrite.Artist = parsed.Artist;
                            changed = true;
                        }

                        return changed ? toWrite : null;
                    },
                    "文件名→标签");

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchFileNameFillButton_Click", caught);
                BatchStatusText.Text = "填充失败：" + caught.Message;
            }
        }

        /// <summary>② 标签 → 文件名：先算好全部新名字（不碰文件），冲突/空标题跳过，逐个改名，失败整体改回。</summary>
        private async void BatchRenameButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                string pattern = (BatchRenamePatternCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "ArtistTitle";
                BatchStatusText.Text = "正在重命名…";

                // 第一阶段：只读标签算新名字，此阶段不碰任何文件
                List<(TagFileItem Item, string NewPath)> plan = new();
                int skipped = 0;
                await Task.Run(() =>
                {
                    foreach (TagFileItem item in targets)
                    {
                        string oldPath = item.FilePath;
                        if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath))
                        {
                            skipped++;
                            continue;
                        }

                        TagEditModel tag = TagEditorService.ReadTagRaw(oldPath);
                        string? newName = BuildBatchFileName(pattern, tag, oldPath);
                        if (newName == null)
                        {
                            skipped++;
                            continue;
                        }

                        string dir = System.IO.Path.GetDirectoryName(oldPath) ?? string.Empty;
                        string newPath = System.IO.Path.Combine(dir, newName);
                        if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }

                        // 目标名已被磁盘上现有文件占用 → 跳过（不覆盖别人的文件）
                        if (File.Exists(newPath))
                        {
                            skipped++;
                            continue;
                        }

                        // 同一批里两个文件算出同一个新名字 → 后者跳过
                        if (plan.Any(p => string.Equals(p.NewPath, newPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            skipped++;
                            continue;
                        }

                        plan.Add((item, newPath));
                    }
                });

                if (plan.Count == 0)
                {
                    BatchStatusText.Text = skipped > 0
                        ? $"没有可重命名的文件{BatchScopeText(wholeList, targets.Count)}：标题为空、目标名已被占用，或名字本来就没变。"
                        : "没有可重命名的文件。";
                    return;
                }

                // 第二阶段：逐个改名；任何一步失败，已改名的全部改回
                List<(string OldPath, string NewPath)> renamed = new();
                List<string> failures = new();
                await Task.Run(() =>
                {
                    foreach ((TagFileItem item, string newPath) in plan)
                    {
                        try
                        {
                            File.Move(item.FilePath, newPath);
                            renamed.Add((item.FilePath, newPath));
                        }
                        catch (Exception caught)
                        {
                            global::CelesteMusicPlayer.StartupLog.WriteException("TagPanel.BatchRename", caught);
                            failures.Add(item.FilePath);
                        }
                    }
                });

                if (failures.Count > 0)
                {
                    int rolledBack = 0;
                    await Task.Run(() =>
                    {
                        foreach ((string oldPath, string newPath) in renamed)
                        {
                            try
                            {
                                File.Move(newPath, oldPath);
                                rolledBack++;
                            }
                            catch (Exception caught)
                            {
                                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanel.BatchRename.Rollback", caught);
                            }
                        }
                    });

                    global::CelesteMusicPlayer.StartupLog.Write(
                        $"TagPanel: 批量[标签→文件名]失败整体回滚：计划 {plan.Count}，失败 {failures.Count}，已改回 {rolledBack}");
                    BatchStatusText.Text = $"{failures.Count} 个文件重命名失败；已改名的 {rolledBack} 个已改回原文件名。详情见日志。";
                    return;
                }

                // 第三阶段：行模型换成新路径（FileName 列才会变）+ 通知主窗口
                foreach ((TagFileItem item, string newPath) in plan)
                {
                    item.FilePath = newPath;
                    TagsSaved?.Invoke(newPath);
                }

                await RefreshSavedRowsAsync(plan.Select(p => p.Item).ToList());

                BatchStatusText.Text = skipped > 0
                    ? $"完成{BatchScopeText(wholeList, targets.Count)}：重命名 {plan.Count} 个文件，跳过 {skipped} 个（标题为空 / 目标名已被占用 / 名字没变）。"
                    : $"完成{BatchScopeText(wholeList, targets.Count)}：重命名 {plan.Count} 个文件。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchRenameButton_Click", caught);
                BatchStatusText.Text = "重命名失败：" + caught.Message;
            }
        }

        /// <summary>按模式从标签拼新文件名（不含目录）。拼不出来返回 null（标题为空）。</summary>
        private static string? BuildBatchFileName(string pattern, TagEditModel tag, string oldPath)
        {
            string ext = System.IO.Path.GetExtension(oldPath);
            string title = (tag.Title ?? string.Empty).Trim();
            string artist = (tag.Artist ?? string.Empty).Trim();
            string albumArtist = (tag.AlbumArtist ?? string.Empty).Trim();

            // 艺术家为空时用专辑艺术家顶班，避免文件名只剩「- 标题」
            if (artist.Length == 0)
            {
                artist = albumArtist;
            }

            string artistTitle = string.Join(" - ", new[] { artist, title }.Where(s => s.Length > 0));
            string baseName = pattern switch
            {
                "Title" => title,
                "TrackArtistTitle" => (tag.Track > 0 ? tag.Track + ". " : string.Empty) + artistTitle,
                _ => artistTitle
            };

            baseName = baseName.Trim();
            if (baseName.Length == 0)
            {
                return null;
            }

            // 文件系统不接受的字符换成下划线；首尾空格和点去掉（Windows 会静默剥掉尾点）
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            string safe = new string(baseName
                .Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)
                .ToArray())
                .Trim().TrimEnd('.');
            if (safe.Length == 0)
            {
                return null;
            }

            return safe + ext;
        }

        /// <summary>③ 文本替换：指定字段里查找替换（可正则，替换串支持 $1 捕获组）。</summary>
        private async void BatchReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                string field = (BatchReplaceFieldCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Title";
                string find = BatchReplaceFindBox.Text ?? string.Empty;
                if (find.Length == 0)
                {
                    BatchStatusText.Text = "先填「查找」里的文字。";
                    return;
                }

                string with = BatchReplaceWithBox.Text ?? string.Empty;
                bool useRegex = BatchReplaceRegexCheck.IsChecked == true;

                Regex? rx = null;
                if (useRegex)
                {
                    try
                    {
                        rx = new Regex(find);
                    }
                    catch (ArgumentException caught)
                    {
                        BatchStatusText.Text = "正则写法不对：" + caught.Message;
                        return;
                    }
                }

                BatchStatusText.Text = "正在替换…";

                (List<string> written, int skipped, List<string> failures) = await RunBatchWithRollbackAsync(
                    targets,
                    (path, before) =>
                    {
                        string original = GetTagField(before, field);
                        string updated = rx != null ? rx.Replace(original, with) : original.Replace(find, with);
                        if (string.Equals(updated, original, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        TagEditModel toWrite = CloneModel(before);
                        SetTagField(toWrite, field, updated);
                        return toWrite;
                    },
                    "文本替换");

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchReplaceButton_Click", caught);
                BatchStatusText.Text = "替换失败：" + caught.Message;
            }
        }

        /// <summary>④ 大小写整理：每词首字母大写 / 全大写 / 全小写。</summary>
        private async void BatchCaseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                string field = (BatchCaseFieldCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Title";
                string mode = (BatchCaseModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Title";
                BatchStatusText.Text = "正在整理大小写…";

                (List<string> written, int skipped, List<string> failures) = await RunBatchWithRollbackAsync(
                    targets,
                    (path, before) =>
                    {
                        string original = GetTagField(before, field);
                        string updated = ApplyCaseMode(original, mode);
                        if (string.Equals(updated, original, StringComparison.Ordinal))
                        {
                            return null;
                        }

                        TagEditModel toWrite = CloneModel(before);
                        SetTagField(toWrite, field, updated);
                        return toWrite;
                    },
                    "大小写整理");

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchCaseButton_Click", caught);
                BatchStatusText.Text = "大小写整理失败：" + caught.Message;
            }
        }

        /// <summary>⑤ 自动编号：按列表顺序写曲目号（起始值起，每个加步长）。</summary>
        private async void BatchNumberButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                string startText = (BatchNumberStartBox.Text ?? string.Empty).Trim();
                string stepText = (BatchNumberStepBox.Text ?? string.Empty).Trim();
                if (!uint.TryParse(startText, out uint start))
                {
                    BatchStatusText.Text = "起始值要填数字（比如 1）。";
                    return;
                }

                if (!uint.TryParse(stepText, out uint step) || step == 0)
                {
                    BatchStatusText.Text = "步长要填大于 0 的数字（比如 1）。";
                    return;
                }

                BatchStatusText.Text = "正在写入曲目号…";

                int position = 0;
                (List<string> written, int skipped, List<string> failures) = await RunBatchWithRollbackAsync(
                    targets,
                    (path, before) =>
                    {
                        ulong value = (ulong)start + (ulong)position * step;
                        uint track = value > uint.MaxValue ? uint.MaxValue : (uint)value;
                        position++;
                        if (before.Track == track)
                        {
                            return null;
                        }

                        TagEditModel toWrite = CloneModel(before);
                        toWrite.Track = track;
                        return toWrite;
                    },
                    "自动编号");

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchNumberButton_Click", caught);
                BatchStatusText.Text = "编号失败：" + caught.Message;
            }
        }

        /// <summary>⑥a 清空文字标签：标题/艺术家/专辑/年份/曲目/流派/注释/歌词全部清空，内嵌封面保留。</summary>
        private async void BatchClearTagsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                BatchStatusText.Text = "正在清空文字标签…";

                (List<string> written, int skipped, List<string> failures) = await RunBatchWithRollbackAsync(
                    targets,
                    (path, before) => new TagEditModel(),
                    "清空文字标签");

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchClearTagsButton_Click", caught);
                BatchStatusText.Text = "清空失败：" + caught.Message;
            }
        }

        /// <summary>⑥b 移除内嵌封面：快照封面字节，失败整体还原（SaveTag 不碰封面，要单独备份）。</summary>
        private async void BatchRemoveCoverButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    BatchStatusText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                BatchStatusText.Text = "正在移除内嵌封面…";

                List<(string Path, byte[] Cover)> snapshots = new();
                List<string> written = new();
                List<string> failures = new();
                int skipped = 0;

                await Task.Run(() =>
                {
                    foreach (TagFileItem item in targets)
                    {
                        string path = item.FilePath;
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        {
                            skipped++;
                            continue;
                        }

                        byte[]? cover = TagEditorService.ReadCoverBytes(path);
                        if (cover == null || cover.Length == 0)
                        {
                            skipped++;
                            continue;
                        }

                        snapshots.Add((path, cover));
                        if (TagEditorService.ClearCover(path))
                        {
                            written.Add(path);
                        }
                        else
                        {
                            failures.Add(path);
                        }
                    }
                });

                if (failures.Count > 0)
                {
                    int rolledBack = 0;
                    await Task.Run(() =>
                    {
                        foreach ((string path, byte[] cover) in snapshots)
                        {
                            if (!written.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (TagEditorService.SetCoverBytes(path, cover))
                            {
                                rolledBack++;
                            }
                        }
                    });

                    global::CelesteMusicPlayer.StartupLog.Write(
                        $"TagPanel: 批量[移除封面]失败整体回滚：目标 {targets.Count}，失败 {failures.Count}，已还原 {rolledBack}");
                    BatchStatusText.Text = $"{failures.Count} 个文件移除封面失败；已移除的 {rolledBack} 个已还原封面。详情见日志。";
                    return;
                }

                if (written.Count == 0)
                {
                    BatchStatusText.Text = "没有带内嵌封面的文件。";
                    return;
                }

                foreach (string path in written)
                {
                    TagsSaved?.Invoke(path);
                }

                List<TagFileItem> touched = targets
                    .Where(t => written.Contains(t.FilePath, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                await RefreshSavedRowsAsync(touched);

                BatchStatusText.Text = skipped > 0
                    ? $"完成{BatchScopeText(wholeList, targets.Count)}：移除了 {written.Count} 个文件的封面，跳过 {skipped} 个（本来就没有封面）。"
                    : $"完成{BatchScopeText(wholeList, targets.Count)}：移除了 {written.Count} 个文件的封面。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchRemoveCoverButton_Click", caught);
                BatchStatusText.Text = "移除封面失败：" + caught.Message;
            }
        }

        /// <summary>浅拷贝标签模型（transform 在副本上改，快照保持干净）。</summary>
        private static TagEditModel CloneModel(TagEditModel m)
        {
            return new TagEditModel
            {
                Title = m.Title,
                Artist = m.Artist,
                Album = m.Album,
                AlbumArtist = m.AlbumArtist,
                Year = m.Year,
                Track = m.Track,
                Genre = m.Genre,
                Comment = m.Comment,
                Lyrics = m.Lyrics
            };
        }

        /// <summary>批量工具字段选择（ComboBoxItem.Tag）→ 读模型字段。</summary>
        private static string GetTagField(TagEditModel m, string field)
        {
            return field switch
            {
                "Artist" => m.Artist,
                "Album" => m.Album,
                "AlbumArtist" => m.AlbumArtist,
                "Genre" => m.Genre,
                "Comment" => m.Comment,
                _ => m.Title
            };
        }

        /// <summary>批量工具字段选择（ComboBoxItem.Tag）→ 写模型字段。</summary>
        private static void SetTagField(TagEditModel m, string field, string value)
        {
            switch (field)
            {
                case "Artist": m.Artist = value; break;
                case "Album": m.Album = value; break;
                case "AlbumArtist": m.AlbumArtist = value; break;
                case "Genre": m.Genre = value; break;
                case "Comment": m.Comment = value; break;
                default: m.Title = value; break;
            }
        }

        /// <summary>大小写整理：每词首字母大写 / 全大写 / 全小写。</summary>
        private static string ApplyCaseMode(string text, string mode)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return mode switch
            {
                "Upper" => text.ToUpperInvariant(),
                "Lower" => text.ToLowerInvariant(),
                _ => CapitalizeWords(text)
            };
        }

        private static string CapitalizeWords(string text)
        {
            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0)
                {
                    continue;
                }

                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
            }

            return string.Join(" ", words);
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

        /// <summary>
        /// 单元格按下（mp3tag 式两击编辑的入口）：
        /// 第一次单击——只选中所在行，不进编辑（e.Handled=true 挡掉 ListViewItem
        /// 的默认行为之前先手动同步选中，保证视觉反馈和单击行首一样）；
        /// 第二次单击同一格——放行，交给 Focus() 进编辑（GotFocus 随后接管）。
        /// 点到别的格子 / 别处 → 武装状态作废，重新从「首击选中」开始。
        /// </summary>
        private void TagCellBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not TagFileItem item)
            {
                return;
            }

            int col = Grid.GetColumn(box);
            if (col is < 1 or > 6)
            {
                return;
            }

            if (_armedItem == item && _armedCol == col)
            {
                // 第二次单击同一格：放行，进编辑
                _armedItem = null;
                _armedCol = -1;
                box.Focus(FocusState.Pointer);
                return;
            }

            // 第一次单击（或换了格子）：只选中行，不进编辑
            e.Handled = true;
            _armedItem = item;
            _armedCol = col;

            try
            {
                if (!TagFileListView.SelectedItems.Contains(item))
                {
                    TagFileListView.SelectedItem = item;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.TagCellBox_PointerPressed", caught);
            }
        }

        /// <summary>单元格获焦（= 已进入编辑）：同步选中所在行，记录原值，全选文字，长字段展开换行。</summary>
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

            // 已进入编辑：清掉武装状态，避免残留的「再击同一格又触发一次 Focus」
            _armedItem = null;
            _armedCol = -1;

            // 进编辑全选原文：直接打字即替换，和 mp3tag / 资源管理器重命名一致
            box.SelectAll();

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
                _armedItem = null;
                _armedCol = -1;
                return;
            }

            await CommitCellEditAsync(box);

            // 编辑结束：武装状态作废（再改要重新两击）
            _armedItem = null;
            _armedCol = -1;
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
                _armedItem = null;
                _armedCol = -1;
                // 焦点移走会触发 LostFocus → 自动提交；这里只负责挪焦点
                TagFileListView.Focus(FocusState.Programmatic);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                _cellCancelled = true;
                _armedItem = null;
                _armedCol = -1;
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
