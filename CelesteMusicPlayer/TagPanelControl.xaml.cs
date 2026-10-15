using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using Microsoft.UI.Dispatching;
using CommunityToolkit.WinUI.UI.Controls;
using CommunityToolkit.WinUI.UI.Controls.Primitives;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「标签」板块（2026-10-13 阶段 5a 骨架）：mp3tag 式左标签面板 + 右文件列表。
    ///
    /// 子页（6 个，ShellPageHost 管显隐）：快速编辑 / 批量工具 / 排序编号 /
    /// 在线标签（MusicBrainz+iTunes+CAA+LRClib）/ 在线歌词（原「歌词下载」）/
    /// 批量下载歌词（从在线歌词页拆出）。
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

        /// <summary>封面图片可用的扩展名（右键封面「从图片文件替换封面」的挑选器用）。</summary>
        private static readonly string[] CoverImageExtensions =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
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

        /// <summary>文件加入列表的流水号（发 AddOrder；取消排序后靠它还原原顺序）。</summary>
        private int _addOrderSeq;

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

        /// <summary>外挂 .lrc 编辑器当前对准的路径（选中单曲时解析出来；null=没有选中单曲）。</summary>
        private string? _quickLrcPath;

        /// <summary>
        /// 标签写盘成功事件（每个路径触发一次）。主窗口订阅它来刷新曲库元数据——
        /// 取代旧 TagEditorWindow.TagsSaved（阶段 6 收尾后旧窗口已删）。
        /// </summary>
        public event Action<string>? TagsSaved;

        /// <summary>
        /// 「排序编号」页 → 加入播放队列（按当前列表顺序）。
        /// 播放队列允许库外的歌，所以这条路不碰音乐库；主窗口负责把路径转成
        /// 播放条目追加进队列。参数 = 文件路径（已按目标顺序排好）。
        /// </summary>
        public event Action<IReadOnlyList<string>>? PlayQueueAddRequested;

        /// <summary>
        /// 「排序编号」页 → 加入音乐库。音乐库只按文件夹收录，主窗口收到后先做
        /// 入库检查（文件夹没被收录就问要不要一起入库），再按文件夹入库。
        /// </summary>
        public event Action<IReadOnlyList<string>>? LibraryAddRequested;

        /// <summary>
        /// 「排序编号」页 → 加入播放列表（命名单）。主窗口弹选择框，
        /// 选哪个单 / 新建哪个单由用户定。
        /// </summary>
        public event Action<IReadOnlyList<string>>? NamedPlaylistAddRequested;

        // =====================================================================
        // 表格单元格内联编辑（2026-10-13 第四轮换 DataGrid 后重写）
        //
        // 口径（mp3tag 语义，2026-10-13 用户实测定稿）：单击某行 = 选中整行；
        // 再击这一行的某个可编辑格 = 进编辑（DataGrid 原生二击手势，不用自己
        // 拦指针事件）；回车 / 点别处提交并立即写盘，Esc 放弃这一格。
        //
        // 提交流转（关键设计）：编辑框是 OneWay 绑定，只负责显示初值，绝不把
        // 半成品回写到行模型——落盘值在 CellEditEnding 里从 EditingElement 直读，
        // 暂存进 _gridEditPending，再由 CellEditEnded 异步走「读全量 → 改一个
        // 字段 → 只标这一个字段脏 → 写盘」。这样既不受绑定回写时序影响，Esc
        // 取消也不会污染行模型；校验失败 / 写盘失败时行显示天然还是原值，
        // 不需要手动把格子改回去。
        // =====================================================================

        /// <summary>
        /// 待提交的单元格编辑（行 + 字段 + 编辑框里的最终文字）。
        /// CellEditEnding 同步盖章，CellEditEnded 取出后清空并异步落盘。
        /// </summary>
        private (TagFileItem Item, TagField Field, string Text)? _gridEditPending;

        /// <summary>
        /// 待提交的「文件名」格改名（行 + 编辑框里的最终名字）。
        /// 改文件名不写标签，是另一条路（改完直接重命名磁盘上的文件）。
        /// </summary>
        private (TagFileItem Item, string Text)? _gridRenamePending;

        /// <summary>单元格写盘串行闸：避免两个格子的提交并发写同一个文件。</summary>
        private readonly SemaphoreSlim _cellSaveGate = new(1, 1);

        public TagPanelControl(Func<Brush>? accentProvider = null, nint hostHwnd = default)
        {
            _accentProvider = accentProvider;
            _hostHwnd = hostHwnd;

            // 保险：在这里（界面线程）主动触碰一次静态成员，把类型的静态初始化
            // 强制留在界面线程完成。否则第一次触碰可能落在后台线程（扫文件夹），
            // 静态初始化里只要有任何界面对象就会炸，而且一炸整个类型永久报废
            // （2026-10-14 实测「添加文件夹失败」的根因）。
            _ = TagAudioExtensions.Length;

            InitializeComponent();

            _emptyHintDefault = TagListEmptyHint.Text;

            // ItemsSource 由 XAML 的 {x:Bind _files, Mode=OneWay} 接管（旧代码在这里手赋）

            // 6 个子页全部登记给 ShellPageHost（显隐由它统一管；组名为空 = 不渲染组标题）
            _subNav = new ShellPageHost(TagSubNavPanel, () => _accentProvider?.Invoke() ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)));
            _subNav.Register("快速编辑", string.Empty, QuickEditPanel);
            _subNav.Register("批量工具", string.Empty, BatchToolsPanel);
            _subNav.Register("排序编号", string.Empty, SortNumberPanel);
            _subNav.Register("在线标签", string.Empty, OnlineTagPanel);
            _subNav.Register("在线歌词", string.Empty, LyricDownloadPanel);
            _subNav.Register("批量下载歌词", string.Empty, LyricBatchPanel);
            _subNav.Build();

            // 切到「排序编号」子页时刷新当前排序状态与作用范围
            // （排序规则可能在「排序…」弹窗里刚改过，页面里的展示不能是旧的）；
            // 切到「在线标签」刷新当前文件与作用范围；切到「在线歌词」刷新当前文件、
            // 预填搜索框、恢复上次的写入口径；切到「批量下载歌词」刷新作用范围与选项。
            _subNav.PageEnter += page =>
            {
                if (ReferenceEquals(page.Page, SortNumberPanel))
                {
                    RefreshSortNumberUi();
                }
                else if (ReferenceEquals(page.Page, OnlineTagPanel))
                {
                    RefreshOnlineTagPageUi();
                }
                else if (ReferenceEquals(page.Page, LyricDownloadPanel))
                {
                    RefreshLyricEditPageUi();
                }
                else if (ReferenceEquals(page.Page, LyricBatchPanel))
                {
                    RefreshLyricBatchPageUi();
                }
            };

            Loaded += TagPanelControl_Loaded;
            Unloaded += TagPanelControl_Unloaded;

            // 列头点击排序**暂不启用**（2026-10-14 用户改主意：改用工具条上的「排序…」按钮）。
            // 三条挂法（模板内 / 列头元素 / 表格级命中测试）都试过，DataGrid 把列头的指针
            // 事件全吃掉了，一次都没进来；而且拖列宽时容易误触排序。代码留在
            // AttachColumnHeaderGestures 里没删，想复活就在这里调一下。
            // AttachColumnHeaderGestures();

            RefreshCountUi();
            ClearQuickEditPreview();
        }

        /// <summary>主题 / 皮肤切换后重刷子导航强调条（不刷会停在旧色上）。</summary>
        internal void ReapplyAccent()
        {
            try
            {
                _subNav.ReapplyAccent();
                // 选中底色是强调色：主题/皮肤切换后按新强调色重刷资源与已实现行
                SetupDataGridChrome();
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
                // 完整链另打一条：类型初始化失败这类异常的 Message 只说"某某类型初始化
                // 失败"，真正的根因藏在 InnerException 里
                global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 添加文件异常详情 → " + caught);
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

                bool withSubfolders = IncludeSubfoldersCheck.IsChecked == true;
                string scope = withSubfolders ? "（含子文件夹）" : "（只看这一层）";

                TagStatusText.Text = "正在扫描文件夹…";
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                string[] paths = await Task.Run(() => EnumerateAudioFiles(folder.Path, withSubfolders).ToArray());
                sw.Stop();
                global::CelesteMusicPlayer.StartupLog.Write(
                    $"TagPanel: 扫描文件夹 {folder.Path} {scope} → {paths.Length} 个音频（{sw.ElapsedMilliseconds}ms）");

                if (paths.Length == 0)
                {
                    ShowEmptyHint(withSubfolders
                        ? "这个文件夹（含里面的子文件夹）里没有找到支持的音频文件。换个文件夹，或把「含子文件夹」取消再试。"
                        : "这个文件夹这一层里没有找到支持的音频文件（子文件夹没扫）。勾上「含子文件夹」再试，或换个文件夹。");
                    TagStatusText.Text = withSubfolders
                        ? "该文件夹（含子文件夹）里没有支持的音频文件。"
                        : "该文件夹这一层里没有支持的音频文件（没扫子文件夹）。";
                    return;
                }

                await AddFilesAsync(paths, $"文件夹 {folder.Path}{scope}");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.AddFolderButton_Click", caught);
                // 完整链另打一条：类型初始化失败这类异常的 Message 只说"某某类型初始化
                // 失败"，真正的根因藏在 InnerException 里（2026-10-14 就是靠它才看清的）
                global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 添加文件夹异常详情 → " + caught);
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
            HashSet<string> addedPaths = new(StringComparer.OrdinalIgnoreCase);
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

            // 后台并行读；分批只为了报进度（大文件夹不会假死一片）
            //
            // ⚠ 关键（2026-10-14 大曲库"两三秒才 50 首"）：**绝不能边读边往列表里加**。
            // 每 Add 一个，ObservableCollection 就发一次通知，表格跟着重排一次可见行，
            // 上千个文件时这段 UI 开销比读文件本身还大。所以先在后台全部读完，
            // 最后一次性挂上数据源（断开 → 重填 → 挂回）。
            System.Diagnostics.Stopwatch readSw = System.Diagnostics.Stopwatch.StartNew();
            const int ProgressStep = 100;
            List<TagFileItem> loaded = new();
            for (int i = 0; i < fresh.Length; i += ProgressStep)
            {
                string[] batch = fresh.Skip(i).Take(ProgressStep).ToArray();
                TagFileItem?[] entries = await Task.Run(() =>
                {
                    // 并行读：每个文件各自开一个 TagLib 对象做 I/O + 解析，彼此不共享状态。
                    TagFileItem?[] result = new TagFileItem?[batch.Length];
                    Parallel.For(0, batch.Length,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 16)
                        },
                        idx => result[idx] = BuildEntry(batch[idx]));
                    return result;
                });

                foreach (TagFileItem? entry in entries)
                {
                    if (entry != null)
                    {
                        loaded.Add(entry);
                    }
                }

                TagStatusText.Text = $"正在读取标签 {Math.Min(i + ProgressStep, fresh.Length)}/{fresh.Length}…";
            }

            // 一次性挂上：断开源 → 重填 → 挂回，只让表格重建一次
            TagFileGrid.ItemsSource = null;
            foreach (TagFileItem item in loaded)
            {
                // 加入顺序：取消列头排序后按它回到「添加进来时的顺序」
                item.AddOrder = ++_addOrderSeq;
                _files.Add(item);
                addedPaths.Add(item.FilePath);
            }

            TagFileGrid.ItemsSource = _files;

            readSw.Stop();
            int added = loaded.Count;
            int failed = fresh.Length - added;

            // 失败的按扩展名归类：一眼能看出是"某一种格式整体读不出来"，
            // 而不是散在各处的随机失败（实测 .tta 十个全军覆没：TagLib 不支持 TTA）。
            string failDetail = string.Empty;
            if (failed > 0)
            {
                string[] failedPaths = fresh.Where(p => !addedPaths.Contains(p)).ToArray();
                IEnumerable<string> byExt = failedPaths
                    .GroupBy(p => System.IO.Path.GetExtension(p).ToLowerInvariant())
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key);
                failDetail = "（" + string.Join("、", byExt) + "）";
            }

            Renumber();
            // 已经排过序的列表：新文件要插到它该在的位置，而不是一律挂到最后
            if (_sortRules.Count > 0)
            {
                ReorderFiles();
            }

            RefreshCountUi();
            // 文件数变了 → 竖滚动条可能出现/消失（视口宽变，表头要跟），选中底色按新集合重画
            RefreshGridRowsChrome();
            global::CelesteMusicPlayer.StartupLog.Write(
                $"TagPanel: {source} 新增 {added} 个，跳过重复 {skipped} 个，读取失败 {failed} 个，读标签耗时 {readSw.ElapsedMilliseconds}ms");

            if (added == 0)
            {
                // 全部读取失败：提示区说清楚，别只留一行小字
                ShowEmptyHint($"选中的 {fresh.Length} 个文件都没能读出标签{failDetail}，一个都没加进来。详情见日志。");
                TagStatusText.Text = "标签读取失败" + failDetail + "，详见日志。";
                return;
            }

            ResetEmptyHint();
            TagStatusText.Text = $"已添加 {added} 个文件"
                + (skipped > 0 ? $"（跳过重复 {skipped} 个）" : string.Empty)
                + (failed > 0 ? $"（{failed} 个读不出标签{failDetail}）" : string.Empty)
                + "。";

            // 2026-10-16 Kid3 化：标签已经秒读完了，时长/码率这些要扫音频帧的列
            // 转到后台补算（见 EnrichAudioFactsAsync）——列表立即可用，音频信息随后陆续亮。
            _ = EnrichAudioFactsAsync(loaded);
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            List<TagFileItem> selected = CheckedItems;
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
            RefreshGridRowsChrome();
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
            RefreshGridRowsChrome();
            ResetEmptyHint();
            ClearQuickEditPreview();
            TagStatusText.Text = $"已清空 {count} 个文件（只是移出本列表，不动硬盘上的文件）。";
        }

        // =====================================================================
        // 快速编辑页：选中文件的标签编辑（阶段 6：可编辑 + 保存 + 快照整体回滚）
        // =====================================================================

        // =====================================================================
        // 选择框列 ↔ 多选（2026-10-16 第三版，前两版都失败后的定案）
        //
        // 为什么不用 DataGrid 自己的多选（SelectionMode=Extended）：
        //   toolkit DataGrid 用 AddHandler(…, handledEventsToo: true) 订阅指针事件，
        //   子元素上设 e.Handled 拦不住它；点行体时它会把选择清成单行，
        //   连带到复选框点击也一起被冲掉 →「永远只能选一首」。
        //   也没有可取消的 Selecting 事件（翻过 XML 文档，只有事后的 SelectionChanged）。
        //
        // 定案（就是 mp3tag 的行为）：
        //   · DataGrid 退化成**只管焦点行**（SelectionMode=Single）；
        //   · 多选完全自管：_checkedSet 是唯一真值源；
        //   · 点**行体** = 单选（清空多选只留当前行，与 mp3tag 一致）；
        //   · 点**复选框** = 累加/取消（不碰焦点行）。
        //   这样两套入口语义清晰，也不用跟控件内部较劲。
        // =====================================================================

        /// <summary>多选真值源（勾选框勾中的行）。顺序不重要，读取时按 _files 排。</summary>
        private readonly HashSet<TagFileItem> _checkedSet = new();

        /// <summary>选中底色（主面板同款半透明灰，SetupDataGridChrome 按主题算）。</summary>
        private Windows.UI.Color _rowSelectionColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);

        /// <summary>
        /// 「勾选行」底色的画刷（由 _rowSelectionColor 现算）。
        /// ⚠ 只能实例字段、只能在 UI 线程 new：SolidColorBrush 是 DependencyObject，
        /// 后台线程 new 会让类型初始化失败（同 TagFallbackAccentColor 那条教训）。
        /// </summary>
        private Brush? _checkedRowBrush;

        /// <summary>选中背景圆角（行高 34，取 4；动画不碰 RadiusX/Y，设一次即稳）。</summary>
        private const double RowSelectionCornerRadius = 4;

        /// <summary>
        /// 「本回合输入手势由选择框列发起」哨兵。点复选框时 DataGrid 会顺着 Tapped 改焦点行
        /// （对交互式子元素无豁免、Tapped 还是 handledEventsToo 订阅，拦不住），那次连带的
        /// SelectionChanged 必须无视，否则 _checkedSet 被清成单选（前两版失败的根因）。
        /// 每次指针按下重新判定：落在复选框上=竖起，落在别处=放倒，所以不会残留。
        /// </summary>
        private bool _rowCheckGuard;

        /// <summary>指针哨兵是否已挂（SetupDataGridChrome 会被 ReapplyAccent 重复调）。</summary>
        private bool _rowCheckGuardHooked;

        /// <summary>
        /// 抑制一次"行焦点变化 → 收敛多选"（SelectRowsByPaths 恢复选中时用）。
        /// 否则 SelectedItem 一设就触发 SelectionChanged，把刚恢复的多选冲成单选。
        /// </summary>
        private bool _suppressSelectionSync;

        /// <summary>指针按下哨兵：判定本手势是否从选择框列发起（见 _rowCheckGuard）。</summary>
        private void TagFileGrid_PointerPressed_WatchRowCheck(object sender, PointerRoutedEventArgs e)
        {
            _rowCheckGuard = FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null;
        }

        /// <summary>当前多选结果（按列表顺序，供批量工具/预览/移除读取）。</summary>
        private List<TagFileItem> CheckedItems =>
            _files.Where(_checkedSet.Contains).ToList();

        /// <summary>
        /// 按路径集合重建多选（_checkedSet），焦点行设到第一个。
        /// 保存后恢复选中 / 跳转编辑 / 排序后保选中都走它。
        /// </summary>
        private void SelectRowsByPaths(IEnumerable<string> paths)
        {
            HashSet<string> want = new(paths, StringComparer.OrdinalIgnoreCase);
            _checkedSet.Clear();
            TagFileItem? first = null;
            foreach (TagFileItem item in _files)
            {
                if (want.Contains(item.FilePath))
                {
                    _checkedSet.Add(item);
                    first ??= item;
                }
            }

            _suppressSelectionSync = true;
            try
            {
                TagFileGrid.SelectedItem = first;
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            RefreshSelectionChromeAndPreview();
        }

        /// <summary>
        /// 勾选框点击：切换该行的勾选态（累加/取消），不碰 DataGrid 焦点行。
        /// IsChecked 由 TwoWay 绑定已翻好，这里按新值同步 _checkedSet 并全线刷新。
        /// </summary>
        private void TagFileRowCheck_Click(object sender, RoutedEventArgs e)
        {
            if (!_panelReady || (sender as FrameworkElement)?.DataContext is not TagFileItem item)
            {
                return;
            }

            if (item.IsChecked)
            {
                _checkedSet.Add(item);
            }
            else
            {
                _checkedSet.Remove(item);
            }

            RefreshSelectionChromeAndPreview();
        }

        /// <summary>勾选态变化后：底色 + 勾选框 + 计数 + 快速编辑预览一起刷新。</summary>
        private void RefreshSelectionChromeAndPreview()
        {
            RefreshGridRowsChrome();
            SyncRowChecksWithSelection();
            List<TagFileItem> selected = CheckedItems;
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

            LoadQuickEditPreviewAsync(selected[0]);
        }

        /// <summary>
        /// 行点击（DataGrid 单选焦点变化）：按 mp3tag 语义把多选收敛成"只有当前行"。
        /// 这是用户主动点行体时的预期行为——勾选累加归复选框管。
        ///
        /// ⚠️ _rowCheckGuard：点**复选框**时 DataGrid 也会顺着 Tapped 改焦点行
        /// （DataGridRow_Tapped → UpdateStateOnTapped，实测对交互式子元素没有任何豁免，
        /// 且 Tapped 是 handledEventsToo 订阅的，子元素拦不住）。Click 先跑、Tapped 随后到，
        /// 没有守卫的话这次连带选中会把 _checkedSet 清成单选——正是前两版
        /// 「还是只能选一首」的根因（v2 在 Click 里恢复快照，也被紧接着的 Tapped 冲掉）。
        /// 守卫只无视「本手势由复选框发起」期间的选中变化，行体点击的正常收敛不受影响。
        /// </summary>
        private void TagFileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_panelReady || _rowCheckGuard || _suppressSelectionSync)
            {
                return;
            }

            _checkedSet.Clear();
            if (TagFileGrid.SelectedItem is TagFileItem focus)
            {
                _checkedSet.Add(focus);
            }

            RefreshSelectionChromeAndPreview();
        }

        /// <summary>把 _checkedSet 的勾选态写回每一行的选择框（只在不一致时写，避免连环通知）。</summary>
        private void SyncRowChecksWithSelection()
        {
            foreach (TagFileItem item in _files)
            {
                bool want = _checkedSet.Contains(item);
                if (item.IsChecked != want)
                {
                    item.IsChecked = want;
                }
            }
        }

        /// <summary>
        /// 单选焦点行的快速编辑预览：读标签 + 封面填进右侧编辑卡。
        /// generation 守卫防异步期间用户又点了别的行。
        /// </summary>
        private async void LoadQuickEditPreviewAsync(TagFileItem item)
        {
            string path = item.FilePath;
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
            QuickTrackCountBox.Text = model.TrackCount > 0 ? model.TrackCount.ToString() : string.Empty;
            QuickDiscBox.Text = model.Disc > 0 ? model.Disc.ToString() : string.Empty;
            QuickDiscCountBox.Text = model.DiscCount > 0 ? model.DiscCount.ToString() : string.Empty;
            QuickBpmBox.Text = model.Bpm ?? string.Empty;
            QuickGenreBox.Text = model.Genre;
            QuickComposerBox.Text = model.Composer;
            QuickCommentBox.Text = model.Comment;
            QuickLyricsBox.Text = model.Lyrics;
            QuickSyncedLyricsBox.Text = model.SyncedLyrics;
            LoadExternalLrcForPath(path);
            ApplyFileInfoCard(path);

            QuickEditModeHint.Text = _quickEditSelection.Count > 1
                ? $"多选模式：填好的字段会写入全部 {_quickEditSelection.Count} 个文件，留空的字段保持原值。想把某个字段清空，请单选该文件后编辑。"
                : "「还原」= 放弃还没保存的改动，恢复成磁盘上的当前值。";
        }

        /// <summary>
        /// 只读文件信息卡（mp3tag 的"扩展标签"那一组）：全部取自文件本身，只能看不能改。
        /// 列表里那份数据已经读好了（格式/时长/位深/采样率/码率/大小），直接拿来显示；
        /// 修改日期走一次 FileInfo（很快）。
        /// </summary>
        private void ApplyFileInfoCard(string path)
        {
            string format = "-";
            string duration = "-";
            string depthRate = "-";
            string bitrate = "-";
            string size = "-";
            if (_quickEditSelection.Count == 1)
            {
                TagFileItem item = _quickEditSelection[0];
                format = string.IsNullOrEmpty(item.FormatName) ? "-" : item.FormatName;
                duration = item.DurationText ?? "-";
                depthRate = (item.BitDepthText ?? "-") + " / " + (item.SampleRateText ?? "-");
                bitrate = item.BitrateText ?? "-";
                size = item.FileSizeText ?? "-";
            }

            string modified;
            try
            {
                modified = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                modified = "-";
            }

            QuickFileInfoText.Text =
                $"文件名：{System.IO.Path.GetFileName(path)}\n" +
                $"格式：{format}\n" +
                $"时长：{duration}\n" +
                $"位深 / 采样率：{depthRate}\n" +
                $"码率：{bitrate}\n" +
                $"大小：{size}\n" +
                $"修改日期：{modified}\n" +
                $"路径：{path}";
        }

        private void ClearFileInfoCard()
        {
            QuickFileInfoText.Text = "选中一个文件后，这里显示它的格式、时长、码率等文件本身的信息。";
        }

        // =====================================================================
        // 外挂 .lrc 编辑器（快速编辑页）
        //
        // 编辑对象 = 磁盘上的独立 .lrc 文件（和「在线歌词」页「下载并保存 → 外挂 .lrc」
        // 写的是同一个位置），不碰音频文件本身的标签，所以有独立的保存按钮，
        // 不走 QuickSave 那条标签保存链路。
        //
        // 路径解析优先级：① 歌曲同目录同名 .lrc（已存在）→ ② 设置里的歌词文件夹
        // 同名 .lrc（已存在）→ ③ 都没有时按设置的默认落点给一个目标路径
        // （保存时落那里，目录不存在会自动建）。
        // =====================================================================

        /// <summary>解析某个音频文件对应的外挂 .lrc 路径（读优先已存在的，保存用默认落点）。</summary>
        private static string ResolveSidecarLrcForEdit(string audioPath)
        {
            string dir = System.IO.Path.GetDirectoryName(audioPath) ?? string.Empty;
            string name = System.IO.Path.GetFileNameWithoutExtension(audioPath);
            string inSongDir = System.IO.Path.Combine(dir, name + ".lrc");
            if (File.Exists(inSongDir))
            {
                return inSongDir;
            }

            string lyricFolder = AppSettingsStore.Load().LyricFolder ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(lyricFolder))
            {
                string inLyricDir = System.IO.Path.Combine(lyricFolder, name + ".lrc");
                if (File.Exists(inLyricDir))
                {
                    return inLyricDir;
                }
            }

            // 都没有现成的：按「在线歌词」页同一套默认口径决定保存落点
            bool saveToSongFolder = AppSettingsStore.Load().SaveLyricToSongFolder
                || string.IsNullOrWhiteSpace(lyricFolder);
            return saveToSongFolder ? inSongDir : System.IO.Path.Combine(lyricFolder, name + ".lrc");
        }

        /// <summary>选中单曲时把同名 .lrc 的内容读进编辑框（没有就空着，让人直接写新的）。</summary>
        private void LoadExternalLrcForPath(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                ClearExternalLrcEditor();
                return;
            }

            try
            {
                _quickLrcPath = ResolveSidecarLrcForEdit(audioPath);
                if (File.Exists(_quickLrcPath))
                {
                    QuickLrcBox.Text = File.ReadAllText(_quickLrcPath);
                    QuickLrcPathText.Text = "当前编辑：" + _quickLrcPath;
                    QuickLrcStatusText.Text = string.Empty;
                }
                else
                {
                    QuickLrcBox.Text = string.Empty;
                    QuickLrcPathText.Text = "保存到：" + _quickLrcPath + "（还没有这个文件，保存时会新建）";
                    QuickLrcStatusText.Text = string.Empty;
                }
            }
            catch (Exception caught)
            {
                _quickLrcPath = null;
                QuickLrcBox.Text = string.Empty;
                QuickLrcPathText.Text = "读取 .lrc 失败";
                QuickLrcStatusText.Text = caught.Message;
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.LoadExternalLrcForPath", caught);
            }
        }

        private void ClearExternalLrcEditor()
        {
            _quickLrcPath = null;
            QuickLrcBox.Text = string.Empty;
            QuickLrcPathText.Text = "选中一个文件后，这里显示它同名 .lrc 的内容。";
            QuickLrcStatusText.Text = string.Empty;
        }

        private void QuickLrcSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_quickLrcPath))
            {
                QuickLrcStatusText.Text = "先选中一个文件，再保存 .lrc。";
                return;
            }

            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_quickLrcPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 与「在线歌词」页外挂保存同口径：UTF-8 无 BOM（带 BOM 部分老播放器不认）
                File.WriteAllText(_quickLrcPath, QuickLrcBox.Text ?? string.Empty, new System.Text.UTF8Encoding(false));
                QuickLrcPathText.Text = "当前编辑：" + _quickLrcPath;
                QuickLrcStatusText.Text = "已保存 → " + _quickLrcPath;
                global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 外挂 .lrc 已保存 → " + _quickLrcPath);
            }
            catch (Exception caught)
            {
                QuickLrcStatusText.Text = "保存失败：" + caught.Message;
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.QuickLrcSaveButton_Click", caught);
            }
        }

        private void QuickLrcReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_quickLrcPath))
            {
                QuickLrcStatusText.Text = "先选中一个文件，再重新读取。";
                return;
            }

            if (!File.Exists(_quickLrcPath))
            {
                QuickLrcBox.Text = string.Empty;
                QuickLrcStatusText.Text = "磁盘上还没有这个 .lrc，当前内容保持不变。";
                return;
            }

            try
            {
                QuickLrcBox.Text = File.ReadAllText(_quickLrcPath);
                QuickLrcStatusText.Text = "已重新读取（未保存的改动已丢弃）。";
            }
            catch (Exception caught)
            {
                QuickLrcStatusText.Text = "读取失败：" + caught.Message;
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.QuickLrcReloadButton_Click", caught);
            }
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
                string trackCountText = (QuickTrackCountBox.Text ?? string.Empty).Trim();
                string discText = (QuickDiscBox.Text ?? string.Empty).Trim();
                string discCountText = (QuickDiscCountBox.Text ?? string.Empty).Trim();
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

                if (trackCountText.Length > 0 && !uint.TryParse(trackCountText, out _))
                {
                    TagStatusText.Text = "曲目总数要填数字，也可以留空。";
                    return;
                }

                if (discText.Length > 0 && !uint.TryParse(discText, out _))
                {
                    TagStatusText.Text = "碟片号要填数字（比如 1），也可以留空。";
                    return;
                }

                if (discCountText.Length > 0 && !uint.TryParse(discCountText, out _))
                {
                    TagStatusText.Text = "碟片总数要填数字，也可以留空。";
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
                    TrackCount = trackCountText.Length > 0 ? uint.Parse(trackCountText) : 0,
                    Disc = discText.Length > 0 ? uint.Parse(discText) : 0,
                    DiscCount = discCountText.Length > 0 ? uint.Parse(discCountText) : 0,
                    Bpm = (QuickBpmBox.Text ?? string.Empty).Trim(),
                    Genre = QuickGenreBox.Text ?? string.Empty,
                    Composer = QuickComposerBox.Text ?? string.Empty,
                    Comment = QuickCommentBox.Text ?? string.Empty,
                    Lyrics = QuickLyricsBox.Text ?? string.Empty
                    // 同步歌词（QuickSyncedLyricsBox）是只读显示，不进保存表单：
                    // 写 SYLT 要重建时间轴事件，风险大又极少用（XAML 里有注释说明）。
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

                        // 新后端契约：模型自带脏标记，只写标记过的字段。
                        // 批量=只填空（模板留空的字段保持原值）；单选=表单 9 字段整体覆盖（留空=清空）。
                        TagEditModel toWrite = batch ? MergeForBatch(template, before) : ApplyFormToModel(template, before);
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

                            before.MarkAllDirty(); // 回滚=整份快照写回
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

        /// <summary>
        /// 单选保存：以磁盘真值为底（保住作曲者/碟片总数等表单不编辑的字段），
        /// 覆盖表单 9 个字段（留空=清空），只标记这 9 个字段为脏。
        /// </summary>
        private static TagEditModel ApplyFormToModel(TagEditModel template, TagEditModel before)
        {
            TagEditModel m = CloneModel(before);
            m.Title = template.Title;
            m.Artist = template.Artist;
            m.Album = template.Album;
            m.AlbumArtist = template.AlbumArtist;
            m.Year = template.Year;
            m.Track = template.Track;
            m.TrackCount = template.TrackCount;
            m.Disc = template.Disc;
            m.DiscCount = template.DiscCount;
            m.Bpm = template.Bpm;
            m.Genre = template.Genre;
            m.Composer = template.Composer;
            m.Comment = template.Comment;
            m.Lyrics = template.Lyrics;
            m.MarkDirty(TagField.Title);
            m.MarkDirty(TagField.Performers);
            m.MarkDirty(TagField.Album);
            m.MarkDirty(TagField.AlbumArtists);
            m.MarkDirty(TagField.Year);
            m.MarkDirty(TagField.Track);
            m.MarkDirty(TagField.TrackCount);
            m.MarkDirty(TagField.Disc);
            m.MarkDirty(TagField.DiscCount);
            m.MarkDirty(TagField.Bpm);
            m.MarkDirty(TagField.Genres);
            m.MarkDirty(TagField.Composers);
            m.MarkDirty(TagField.Comment);
            m.MarkDirty(TagField.Lyrics);
            return m;
        }

        /// <summary>批量模式合并：模板里留空的字段保持原值（想清空字段请单选该文件编辑）。</summary>
        private static TagEditModel MergeForBatch(TagEditModel template, TagEditModel before)
        {
            TagEditModel m = CloneModel(before);
            if (!string.IsNullOrEmpty(template.Title))
            {
                m.Title = template.Title;
                m.MarkDirty(TagField.Title);
            }

            if (!string.IsNullOrEmpty(template.Artist))
            {
                m.Artist = template.Artist;
                m.MarkDirty(TagField.Performers);
            }

            if (!string.IsNullOrEmpty(template.Album))
            {
                m.Album = template.Album;
                m.MarkDirty(TagField.Album);
            }

            if (!string.IsNullOrEmpty(template.AlbumArtist))
            {
                m.AlbumArtist = template.AlbumArtist;
                m.MarkDirty(TagField.AlbumArtists);
            }

            if (template.Year != 0)
            {
                m.Year = template.Year;
                m.MarkDirty(TagField.Year);
            }

            if (template.Track != 0)
            {
                m.Track = template.Track;
                m.MarkDirty(TagField.Track);
            }

            if (template.TrackCount != 0)
            {
                m.TrackCount = template.TrackCount;
                m.MarkDirty(TagField.TrackCount);
            }

            if (template.Disc != 0)
            {
                m.Disc = template.Disc;
                m.MarkDirty(TagField.Disc);
            }

            if (template.DiscCount != 0)
            {
                m.DiscCount = template.DiscCount;
                m.MarkDirty(TagField.DiscCount);
            }

            if (!string.IsNullOrEmpty(template.Bpm))
            {
                m.Bpm = template.Bpm;
                m.MarkDirty(TagField.Bpm);
            }

            if (!string.IsNullOrEmpty(template.Genre))
            {
                m.Genre = template.Genre;
                m.MarkDirty(TagField.Genres);
            }

            if (!string.IsNullOrEmpty(template.Composer))
            {
                m.Composer = template.Composer;
                m.MarkDirty(TagField.Composers);
            }

            if (!string.IsNullOrEmpty(template.Comment))
            {
                m.Comment = template.Comment;
                m.MarkDirty(TagField.Comment);
            }

            if (!string.IsNullOrEmpty(template.Lyrics))
            {
                m.Lyrics = template.Lyrics;
                m.MarkDirty(TagField.Lyrics);
            }

            return m;
        }

        private static bool ModelsEqual(TagEditModel a, TagEditModel b)
        {
            return a.Title == b.Title
                && a.Performers.SequenceEqual(b.Performers)
                && a.Album == b.Album
                && a.AlbumArtists.SequenceEqual(b.AlbumArtists)
                && a.Year == b.Year
                && a.Track == b.Track
                && a.TrackCount == b.TrackCount
                && a.Disc == b.Disc
                && a.DiscCount == b.DiscCount
                && (a.Bpm ?? string.Empty) == (b.Bpm ?? string.Empty)
                && a.Genres.SequenceEqual(b.Genres)
                && a.Composers.SequenceEqual(b.Composers)
                && a.Comment == b.Comment
                && a.Lyrics == b.Lyrics;
        }

        // =====================================================================
        // 阶段 7：批量工具（统一快照 → 逐个写 → 失败整体回滚）
        // =====================================================================

        /// <summary>批量工具的作用范围：右侧列表选中项；一个都没选 = 整个列表。</summary>
        private (List<TagFileItem> Targets, bool WholeList) BatchTargets()
        {
            List<TagFileItem> selected = CheckedItems;
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

                    // 新后端契约：transform 改过的模型标全脏再存（等同旧版「写全量」语义）
                    toWrite.MarkAllDirty();
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

                        before.MarkAllDirty(); // 回滚=整份快照写回
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
        /// <param name="statusText">结果汇报到哪个状态条；不传 = 批量工具页那条
        /// （「排序编号」页的写入曲目号也走这个收尾，传自己页的状态条）。</param>
        private async Task FinishBatchAsync(
            List<TagFileItem> targets, List<string> written, int skipped, List<string> failures, bool wholeList,
            TextBlock? statusText = null)
        {
            TextBlock status = statusText ?? BatchStatusText;

            if (failures.Count > 0)
            {
                status.Text = $"{failures.Count} 个文件处理失败；已改成功的文件已整体还原到改动前的标签。详情见日志。";
                return;
            }

            if (written.Count == 0)
            {
                status.Text = skipped > 0
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

            status.Text = skipped > 0
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

                if (!TryParseNumberBoxes(BatchNumberStartBox, BatchNumberStepBox,
                        out uint start, out uint step, msg => BatchStatusText.Text = msg))
                {
                    return;
                }

                BatchStatusText.Text = "正在写入曲目号…";

                (List<string> written, int skipped, List<string> failures) =
                    await WriteTrackNumbersCoreAsync(targets, start, step);

                await FinishBatchAsync(targets, written, skipped, failures, wholeList);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BatchNumberButton_Click", caught);
                BatchStatusText.Text = "编号失败：" + caught.Message;
            }
        }

        /// <summary>
        /// 按列表从上到下的顺序写曲目号：第一个文件 = 起始值，之后每个加一步长。
        /// 批量工具⑤ 和「排序编号」页① 共用这一个内核（先快照、失败整体还原）。
        /// </summary>
        private async Task<(List<string> Written, int Skipped, List<string> Failures)> WriteTrackNumbersCoreAsync(
            List<TagFileItem> targets, uint start, uint step)
        {
            int position = 0;
            return await RunBatchWithRollbackAsync(
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
        }

        /// <summary>解析「起始值 / 步长」两个框；不合法时调 fail 汇报并返回 false。</summary>
        private static bool TryParseNumberBoxes(TextBox startBox, TextBox stepBox,
            out uint start, out uint step, Action<string> fail)
        {
            start = 1;
            step = 1;

            string startText = (startBox.Text ?? string.Empty).Trim();
            if (!uint.TryParse(startText, out start))
            {
                fail("起始值要填数字（比如 1）。");
                return false;
            }

            string stepText = (stepBox.Text ?? string.Empty).Trim();
            if (!uint.TryParse(stepText, out step) || step == 0)
            {
                fail("步长要填大于 0 的数字（比如 1）。");
                return false;
            }

            return true;
        }

        // =====================================================================
        // 「排序编号」页（2026-10-14 第 3 项）
        //
        // 把排好的顺序用出价值：写曲目号 / 加入播放队列 / 加入音乐库 / 加入播放列表。
        // 冲突处理（用户拍板的方案）：播放队列和播放列表都允许库外的歌，不冲突；
        // 音乐库是持久化的且只按文件夹收录，所以「加入音乐库」先做入库检查——
        // 检查与对话框都在主窗口（那里有 XamlRoot 和设置），本页只发事件。
        // =====================================================================

        /// <summary>切进本页 / 排序规则变化后刷新：当前排序描述 + 作用范围。</summary>
        private void RefreshSortNumberUi()
        {
            if (_sortRules.Count == 0)
            {
                SortNumberStatusText.Text = "未排序（按添加顺序）。";
            }
            else
            {
                SortNumberStatusText.Text = "当前排序：" + string.Join("  →  ",
                    _sortRules.Select((r, i) =>
                        $"{(i + 1)}. {ColumnLabelOf(r.Key)}{(r.Descending ? " 降序" : " 升序")}"));
            }

            (List<TagFileItem> targets, bool wholeList) = BatchTargets();
            SortNumberScopeText.Text = targets.Count == 0
                ? "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。"
                : BatchScopeText(wholeList, targets.Count);
        }

        /// <summary>按列 Key 取列头文字（排序状态描述用）。</summary>
        private string ColumnLabelOf(string key)
        {
            return _columnLabels.TryGetValue(key, out string? label) ? label : key;
        }

        /// <summary>① 把当前顺序写入曲目号。</summary>
        private async void SortNumberWriteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (List<TagFileItem> targets, bool wholeList) = BatchTargets();
                if (targets.Count == 0)
                {
                    SortNumberActionText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                    return;
                }

                if (!TryParseNumberBoxes(SortNumberStartBox, SortNumberStepBox,
                        out uint start, out uint step, msg => SortNumberActionText.Text = msg))
                {
                    return;
                }

                SortNumberActionText.Text = "正在写入曲目号…";

                (List<string> written, int skipped, List<string> failures) =
                    await WriteTrackNumbersCoreAsync(targets, start, step);

                await FinishBatchAsync(targets, written, skipped, failures, wholeList, SortNumberActionText);
                RefreshSortNumberUi();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.SortNumberWriteButton_Click", caught);
                SortNumberActionText.Text = "编号失败：" + caught.Message;
            }
        }

        /// <summary>②a 加入播放队列（按当前列表顺序；队列允许库外的歌，不碰音乐库）。</summary>
        private void SortNumberQueueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSortNumberPaths(out List<string> paths))
            {
                return;
            }

            PlayQueueAddRequested?.Invoke(paths);
            SortNumberActionText.Text = $"已按当前顺序加入播放队列：{paths.Count} 首。";
        }

        /// <summary>②b 加入音乐库（主窗口做入库检查后按文件夹入库）。</summary>
        private void SortNumberLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSortNumberPaths(out List<string> paths))
            {
                return;
            }

            LibraryAddRequested?.Invoke(paths);
            SortNumberActionText.Text = "正在检查音乐库收录情况…（结果看主界面底部状态栏）";
        }

        /// <summary>②c 加入播放列表（主窗口弹选择框）。</summary>
        private void SortNumberPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSortNumberPaths(out List<string> paths))
            {
                return;
            }

            NamedPlaylistAddRequested?.Invoke(paths);
            SortNumberActionText.Text = "请在弹出的对话框里选择要加入的播放列表。";
        }

        /// <summary>取本页三个「加入」按钮的共同输入：目标文件路径（保持列表顺序）。
        /// 列表为空时在状态条说明并返回 false。</summary>
        private bool TryGetSortNumberPaths(out List<string> paths)
        {
            paths = new List<string>();

            (List<TagFileItem> targets, bool _) = BatchTargets();
            if (targets.Count == 0)
            {
                SortNumberActionText.Text = "列表是空的，先点右侧「添加文件…」或「添加文件夹…」把歌曲加进来。";
                return false;
            }

            paths = targets
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            return true;
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

        /// <summary>深拷贝标签模型（transform 在副本上改，快照保持干净；多值字段拷数组副本）。</summary>
        private static TagEditModel CloneModel(TagEditModel m)
        {
            return new TagEditModel
            {
                Title = m.Title,
                Performers = m.Performers.ToArray(),
                Album = m.Album,
                AlbumArtists = m.AlbumArtists.ToArray(),
                Year = m.Year,
                Track = m.Track,
                TrackCount = m.TrackCount,
                Disc = m.Disc,
                DiscCount = m.DiscCount,
                Genres = m.Genres.ToArray(),
                Composers = m.Composers.ToArray(),
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
                CheckedItems.Select(i => i.FilePath),
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
                SelectRowsByPaths(selectedPaths);
            }

            // 换进来的新实例序号是 0，重排一次（行模型 INPC，可见行实时更新）
            Renumber();
            // 行实例整体换过：列宽盖章 + 选中底色都要按新实例重来
            RefreshGridRowsChrome();
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
            QuickTrackCountBox.Text = string.Empty;
            QuickDiscBox.Text = string.Empty;
            QuickDiscCountBox.Text = string.Empty;
            QuickBpmBox.Text = string.Empty;
            QuickGenreBox.Text = string.Empty;
            QuickComposerBox.Text = string.Empty;
            QuickCommentBox.Text = string.Empty;
            QuickLyricsBox.Text = string.Empty;
            QuickSyncedLyricsBox.Text = string.Empty;
            ClearExternalLrcEditor();

            QuickEditTitle.Text = $"已选 {count} 个文件";
            QuickEditPath.Text = "批量填写：只填想改的字段，留空的不动";
            QuickEditModeHint.Text = $"多选模式：填好的字段会写入全部 {count} 个文件，留空的字段保持原值。想把某个字段清空，请单选该文件后编辑。";
            ClearFileInfoCard();
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
            QuickTrackCountBox.Text = string.Empty;
            QuickDiscBox.Text = string.Empty;
            QuickDiscCountBox.Text = string.Empty;
            QuickBpmBox.Text = string.Empty;
            QuickGenreBox.Text = string.Empty;
            QuickComposerBox.Text = string.Empty;
            QuickCommentBox.Text = string.Empty;
            QuickLyricsBox.Text = string.Empty;
            QuickSyncedLyricsBox.Text = string.Empty;
            ClearExternalLrcEditor();
            ClearFileInfoCard();

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
        // 封面右键菜单（2026-10-14，对标 mp3tag：在封面上点右键出菜单）
        //
        // 后端接口是现成的：TagEditorService.ReadCoverBytes / SetCoverBytes /
        // ClearCover / ExtractCover。菜单作用在右侧列表当前选中的那一首上（多选
        // 或未选时明说，不闷头改一堆文件）。
        // =====================================================================

        /// <summary>封面操作的对象 = 右侧列表当前选中的那一首；没选 / 多选时提示并返回 null。</summary>
        private string? CoverTargetPath()
        {
            if (string.IsNullOrWhiteSpace(_quickEditPath))
            {
                TagStatusText.Text = "先在右侧列表里选中一首歌（只选一个），再右键封面。";
                return null;
            }

            return _quickEditPath;
        }

        private async void CoverMenuReplace_Click(object sender, RoutedEventArgs e)
        {
            string? path = CoverTargetPath();
            if (path == null)
            {
                return;
            }

            try
            {
                FileOpenPicker picker = new();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hostHwnd);
                foreach (string ext in CoverImageExtensions)
                {
                    picker.FileTypeFilter.Add(ext);
                }

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                byte[] bytes = await System.IO.File.ReadAllBytesAsync(file.Path);
                bool ok = await Task.Run(() => TagEditorService.SetCoverBytes(path, bytes));
                if (!ok)
                {
                    TagStatusText.Text = "封面没写进去（这种音频格式可能不支持内嵌封面），详情见日志。";
                    return;
                }

                ApplyCoverToImage(bytes);
                TagsSaved?.Invoke(path);
                TagStatusText.Text = "封面已替换。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CoverMenuReplace", caught);
                TagStatusText.Text = "替换封面失败：" + caught.Message;
            }
        }

        private async void CoverMenuExport_Click(object sender, RoutedEventArgs e)
        {
            string? path = CoverTargetPath();
            if (path == null)
            {
                return;
            }

            try
            {
                byte[]? bytes = await Task.Run(() => TagEditorService.ReadCoverBytes(path));
                if (bytes == null || bytes.Length == 0)
                {
                    TagStatusText.Text = "这个文件里没有内嵌封面，没有可导出的。";
                    return;
                }

                FileSavePicker picker = new();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hostHwnd);
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeChoices.Add("图片", new List<string> { ".jpg", ".png", ".bmp" });
                picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(path);
                StorageFile? dest = await picker.PickSaveFileAsync();
                if (dest == null)
                {
                    return;
                }

                await System.IO.File.WriteAllBytesAsync(dest.Path, bytes);
                TagStatusText.Text = "封面已导出。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CoverMenuExport", caught);
                TagStatusText.Text = "导出封面失败：" + caught.Message;
            }
        }

        private async void CoverMenuCopy_Click(object sender, RoutedEventArgs e)
        {
            string? path = CoverTargetPath();
            if (path == null)
            {
                return;
            }

            try
            {
                byte[]? bytes = await Task.Run(() => TagEditorService.ReadCoverBytes(path));
                if (bytes == null || bytes.Length == 0)
                {
                    TagStatusText.Text = "这个文件里没有内嵌封面。";
                    return;
                }

                MemoryStream ms = new(bytes);
                DataPackage package = new();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(ms.AsRandomAccessStream()));
                Clipboard.SetContent(package);
                TagStatusText.Text = "封面已复制到剪贴板（到另一首歌上右键封面 → 粘贴封面）。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CoverMenuCopy", caught);
                TagStatusText.Text = "复制封面失败：" + caught.Message;
            }
        }

        private async void CoverMenuPaste_Click(object sender, RoutedEventArgs e)
        {
            string? path = CoverTargetPath();
            if (path == null)
            {
                return;
            }

            try
            {
                DataPackageView view = Clipboard.GetContent();
                if (view == null || !view.Contains(StandardDataFormats.Bitmap))
                {
                    TagStatusText.Text = "剪贴板里没有图片（先在别的歌上右键封面 → 复制封面）。";
                    return;
                }

                IRandomAccessStreamReference? reference = await view.GetBitmapAsync();
                if (reference == null)
                {
                    TagStatusText.Text = "剪贴板里的图片取不出来。";
                    return;
                }

                byte[] bytes;
                using (IRandomAccessStreamWithContentType src = await reference.OpenReadAsync())
                {
                    MemoryStream ms = new();
                    await src.AsStreamForRead().CopyToAsync(ms);
                    bytes = ms.ToArray();
                }

                bool ok = await Task.Run(() => TagEditorService.SetCoverBytes(path, bytes));
                if (!ok)
                {
                    TagStatusText.Text = "封面没写进去（这种音频格式可能不支持内嵌封面），详情见日志。";
                    return;
                }

                ApplyCoverToImage(bytes);
                TagsSaved?.Invoke(path);
                TagStatusText.Text = "封面已粘贴。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CoverMenuPaste", caught);
                TagStatusText.Text = "粘贴封面失败：" + caught.Message;
            }
        }

        private async void CoverMenuRemove_Click(object sender, RoutedEventArgs e)
        {
            string? path = CoverTargetPath();
            if (path == null)
            {
                return;
            }

            try
            {
                bool ok = await Task.Run(() => TagEditorService.ClearCover(path));
                if (!ok)
                {
                    TagStatusText.Text = "移除封面失败，详情见日志。";
                    return;
                }

                ApplyCoverToImage(null);
                TagsSaved?.Invoke(path);
                TagStatusText.Text = "已移除内嵌封面（只是删掉文件里嵌的图，音乐本身没动）。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CoverMenuRemove", caught);
                TagStatusText.Text = "移除封面失败：" + caught.Message;
            }
        }

        // =====================================================================
        // 表格单元格内联编辑（DataGrid 事件入口）
        // =====================================================================

        /// 单元格获焦（= 已进入编辑）：全选原文，直接打字即替换（和 mp3tag /
        /// 资源管理器重命名一致）；顺带保证所在行是选中项（F2 / 直接打字
        /// 进入编辑时行可能没选中）。
        /// </summary>
        private void TagCellEditBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || !_panelReady)
            {
                return;
            }

            try
            {
                if (FindAncestor<DataGridRow>(box) is DataGridRow row
                    && row.DataContext is TagFileItem item
                    && !_checkedSet.Contains(item))
                {
                    TagFileGrid.SelectedItem = item;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.TagCellEditBox_GotFocus", caught);
            }

            box.SelectAll();
        }

        /// <summary>
        /// 编辑即将结束：提交（Commit）时把编辑框里的最终文字 + 行 + 字段盖章暂存；
        /// 取消（Cancel，Esc）时清空暂存——行模型是 OneWay 绑定、从没被写过，
        /// 显示天然还是原值，不用手动恢复。真正的「读全量 → 改一个字段 →
        /// 只标这一个字段脏 → 写盘」在 CellEditEnded 里异步做。
        /// </summary>
        private void TagFileGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            _gridEditPending = null;
            _gridRenamePending = null;

            if (e.EditAction != DataGridEditAction.Commit)
            {
                return;
            }

            if (e.Row?.DataContext is not TagFileItem item)
            {
                return;
            }

            // 「文件名」列不写标签，走改文件名的路（mp3tag 口径：改完立刻重命名磁盘上的文件）
            if (string.Equals(e.Column?.Tag as string, "FileName", StringComparison.Ordinal))
            {
                if (e.EditingElement is TextBox nameBox)
                {
                    _gridRenamePending = (item, nameBox.Text ?? string.Empty);
                }

                return;
            }

            if (FieldFromColumnTag(e.Column?.Tag) is not TagField field)
            {
                return;
            }

            if (e.EditingElement is not TextBox box)
            {
                return;
            }

            _gridEditPending = (item, field, box.Text ?? string.Empty);
        }

        /// <summary>编辑结束：取出暂存值异步落盘（串行闸内排队；失败只在状态条说明原因）。</summary>
        private async void TagFileGrid_CellEditEnded(object sender, DataGridCellEditEndedEventArgs e)
        {
            // 文件名改名优先：它不写标签，跟标签提交是两条路
            (TagFileItem Item, string Text)? rename = _gridRenamePending;
            _gridRenamePending = null;
            if (rename != null && _panelReady)
            {
                await CommitFileNameEditAsync(rename.Value.Item, rename.Value.Text);
                return;
            }

            (TagFileItem Item, TagField Field, string Text)? pending = _gridEditPending;
            _gridEditPending = null;

            if (pending is null || !_panelReady)
            {
                return;
            }

            await CommitCellEditAsync(pending.Value.Item, pending.Value.Field, pending.Value.Text);
        }

        /// <summary>
        /// 单元格提交：把这一格的新值写进文件。安全口径与左侧「保存」一致——
        /// 先读全量原标签做底稿，只改这一个字段、只标这一个字段脏再写；
        /// 和磁盘现值相同的（没动过）一个字节都不写。写失败 / 校验失败不动
        /// 文件，行显示天然还是原值（OneWay 绑定没被污染），只在状态条说明原因。
        /// </summary>
        private async Task CommitCellEditAsync(TagFileItem item, TagField field, string rawText)
        {
            string path = item.FilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fieldName = FieldDisplayName(field);
            string newText = rawText.Trim();

            // 数字列先校验（失败不碰文件）
            if (field is TagField.Year or TagField.Track
                && newText.Length > 0 && !uint.TryParse(newText, out _))
            {
                TagStatusText.Text = field == TagField.Year
                    ? "年份要填数字（比如 2024），也可以留空；已恢复原值。"
                    : "曲目号要填数字（比如 3），也可以留空；已恢复原值。";
                return;
            }

            await _cellSaveGate.WaitAsync();
            try
            {
                TagStatusText.Text = $"正在保存{fieldName}…";

                // 0 = 没动不写；1 = 写成功；-1 = 写失败
                int outcome = await Task.Run(() =>
                {
                    // 新后端契约：读全量 → 改一个字段 → 只标记这一个字段为脏。
                    // 多值字段（艺术家/流派）走数组 round-trip，不再压扁。
                    TagEditModel before = TagEditorService.ReadTag(path);
                    TagEditModel toWrite = CloneModel(before);

                    // 和磁盘现值相同 → 没动，不写盘
                    if (string.Equals(newText, ReadModelFieldText(toWrite, field), StringComparison.Ordinal))
                    {
                        return 0;
                    }

                    ApplyModelField(toWrite, field, newText);
                    toWrite.MarkDirty(field);
                    return TagEditorService.SaveTag(path, toWrite) ? 1 : -1;
                });

                if (outcome == 0)
                {
                    return;
                }

                if (outcome < 0)
                {
                    TagStatusText.Text = $"{fieldName}保存失败；原值未动，详情见日志。";
                    return;
                }

                global::CelesteMusicPlayer.StartupLog.Write($"TagPanel: 单元格保存 {fieldName} → {path}");
                TagsSaved?.Invoke(path);

                // 刷新行（换实例，绑定重新求值）；顺带刷新左栏预览（若正在看这个文件）
                await RefreshSavedRowsAsync(new List<TagFileItem> { item });

                TagStatusText.Text = $"已保存{fieldName}。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CommitCellEdit", caught);
                TagStatusText.Text = $"{fieldName}保存失败：" + caught.Message;
            }
            finally
            {
                _cellSaveGate.Release();
            }
        }

        /// <summary>
        /// 「文件名」格提交：把磁盘上的文件改成新名字（mp3tag 口径）。
        /// 安全口径：目标名已存在 / 名字里有非法字符 / 文件被占用 → 一律不动，
        /// 只在状态条说清原因；行显示是靠换实例重绑刷新，失败时自然还是原值。
        /// </summary>
        private async Task CommitFileNameEditAsync(TagFileItem item, string rawName)
        {
            string oldPath = item.FilePath;
            if (string.IsNullOrWhiteSpace(oldPath))
            {
                return;
            }

            string newName = (rawName ?? string.Empty).Trim();
            string ext = System.IO.Path.GetExtension(oldPath);
            // 用户没带扩展名就补上原来的（mp3tag 也是这个口径）
            if (ext.Length > 0 && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                newName += ext;
            }

            if (newName.Length == 0)
            {
                return;
            }

            if (string.Equals(newName, item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                return; // 没改
            }

            string? dir = System.IO.Path.GetDirectoryName(oldPath);
            if (string.IsNullOrEmpty(dir))
            {
                TagStatusText.Text = "取不到这个文件所在的文件夹，没能改名。";
                return;
            }

            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            if (newName.IndexOfAny(invalid) >= 0)
            {
                TagStatusText.Text = "文件名里有不能用的字符（\\ / : * ? \" < > | 都不行），已恢复原名。";
                return;
            }

            string newPath = System.IO.Path.Combine(dir, newName);

            await _cellSaveGate.WaitAsync();
            try
            {
                if (System.IO.File.Exists(newPath))
                {
                    TagStatusText.Text = $"同一个文件夹里已经有「{newName}」了，为避免覆盖，没有改名。";
                    return;
                }

                TagStatusText.Text = "正在改文件名…";
                bool moved = await Task.Run(() =>
                {
                    try
                    {
                        System.IO.File.Move(oldPath, newPath);
                        return true;
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.RenameFile", caught);
                        return false;
                    }
                });

                if (!moved)
                {
                    TagStatusText.Text = "改名失败（文件可能被别的程序占用，或没有写权限）；详情见日志。";
                    return;
                }

                // 换实例重绑：文件路径 / 文件名都变了，重读一遍最省事也最准
                int index = _files.IndexOf(item);
                TagFileItem? refreshed = await Task.Run(() => BuildEntry(newPath));
                if (refreshed == null)
                {
                    refreshed = item;
                    refreshed.FilePath = newPath;
                }

                refreshed.AddOrder = item.AddOrder;
                if (index >= 0)
                {
                    _files[index] = refreshed;
                }

                Renumber();
                RefreshCountUi();
                global::CelesteMusicPlayer.StartupLog.Write($"TagPanel: 改名 {oldPath} → {newPath}");

                // 通知主窗口：曲库里这条的路径变了，元数据要跟着换
                TagsSaved?.Invoke(newPath);
                TagStatusText.Text = $"已改名为「{newName}」。";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.CommitFileNameEdit", caught);
                TagStatusText.Text = "改名失败：" + caught.Message;
            }
            finally
            {
                _cellSaveGate.Release();
            }
        }

        /// <summary>列 Tag（XAML 里盖章的字符串）→ 标签字段；只读列返回 null。</summary>
        private static TagField? FieldFromColumnTag(object? tag) => tag switch
        {
            "Title" => TagField.Title,
            "Artist" => TagField.Performers,
            "Album" => TagField.Album,
            "Year" => TagField.Year,
            "Track" => TagField.Track,
            "Genre" => TagField.Genres,
            _ => null
        };

        /// <summary>字段的人话名字（状态条提示用）。</summary>
        private static string FieldDisplayName(TagField field) => field switch
        {
            TagField.Title => "标题",
            TagField.Performers => "艺术家",
            TagField.Album => "专辑",
            TagField.Year => "年份",
            TagField.Track => "曲目号",
            TagField.Genres => "流派",
            _ => "标签"
        };

        /// <summary>读模型字段的显示文字（与表格显示口径一致："; " 连接、0 = 空）。</summary>
        private static string ReadModelFieldText(TagEditModel m, TagField field) => field switch
        {
            TagField.Title => m.Title ?? string.Empty,
            TagField.Performers => TagEditModel.Join(m.Performers),
            TagField.Album => m.Album ?? string.Empty,
            TagField.Year => m.Year == 0 ? string.Empty : m.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TagField.Track => m.Track == 0 ? string.Empty : m.Track.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TagField.Genres => TagEditModel.Join(m.Genres),
            _ => string.Empty
        };

        /// <summary>把这一格的文字写进模型（多值字段走 Artist/Genre 兼容视图，";" 拆分）。</summary>
        private static void ApplyModelField(TagEditModel m, TagField field, string text)
        {
            switch (field)
            {
                case TagField.Title: m.Title = text; break;
                case TagField.Performers: m.Artist = text; break;
                case TagField.Album: m.Album = text; break;
                case TagField.Year: m.Year = text.Length == 0 ? 0 : uint.Parse(text); break;
                case TagField.Track: m.Track = text.Length == 0 ? 0 : uint.Parse(text); break;
                case TagField.Genres: m.Genre = text; break;
            }
        }

        /// <summary>沿视觉树往上找指定类型的祖先（格内编辑框 → 所在行用）。</summary>
        private static T? FindAncestor<T>(DependencyObject root) where T : class
        {
            DependencyObject? current = root;
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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
            SelectRowsByPaths(want);
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
            // TTA：TagLib 没有 TTA 容器解析器（File.Create 直接抛），单独一条路
            if (TtaTagAccess.IsTtaPath(path))
            {
                return BuildTtaEntry(path);
            }

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

            // 可选列用的附加标签（默认隐藏，用户在「列…」里勾出来才显示）
            string composer = string.Empty;
            string comment = string.Empty;
            string bpm = string.Empty;
            string lyrics = string.Empty;
            uint trackCount = 0;
            uint disc = 0;
            uint discCount = 0;

            try
            {
                // 2026-10-16 Kid3 化（用户实测「3 秒 100 首」，mp3tag/Kid3 都是秒开几千首）：
                // ReadStyle.None = 只解析标签帧，**不碰音频帧**。Properties.Duration / 码率 /
                // 采样率全都要扫音频帧才知道（MP3 甚至要遍历全部帧头算时长），这就是慢的主因。
                // 这些派生列改为扫描结束后由 EnrichAudioFactsAsync 后台补算：列表瞬间可读，
                // 时长/码率随后陆续亮（补算只读不写，与 bit-perfect 无关）。
                using TagLib.File tagFile = TagLib.File.Create(path, TagLib.ReadStyle.None);
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
                trackCount = tagFile.Tag.TrackCount;
                disc = tagFile.Tag.Disc;
                discCount = tagFile.Tag.DiscCount;
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstComposer))
                {
                    composer = tagFile.Tag.FirstComposer.Trim();
                }

                comment = tagFile.Tag.Comment ?? string.Empty;
                lyrics = tagFile.Tag.Lyrics ?? string.Empty;
                if (tagFile.Tag.BeatsPerMinute > 0)
                {
                    bpm = tagFile.Tag.BeatsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                {
                    genre = tagFile.Tag.FirstGenre.Trim();
                }

                // 时长 / 采样率 / 位深 / 码率：这里一律不读（ReadStyle.None 下拿不到；
                // 且 TryGetFactsFast 发现码率缺口会回退 ffmpeg 完整探测，反而更慢）。
                // 扫描结束后由 EnrichAudioFactsAsync 用完整 ReadStyle 后台补算，见该方法注释。
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

            // 格内编辑代理初值（未知留空，不显示 "-"）
            item.YearEdit = year > 0 ? year.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            item.TrackEdit = track > 0 ? track.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

            // 可选列用的附加标签（默认隐藏，用户在「列…」里勾出来才显示）
            item.ComposerText = composer;
            item.CommentText = comment;
            item.LyricsText = lyrics;
            item.BpmText = bpm;
            item.TrackCountText = trackCount > 0
                ? trackCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            item.DiscText = disc > 0
                ? (discCount > 0
                    ? disc + "/" + discCount
                    : disc.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : string.Empty;

            // 排序用原始数值：文本是给人看的，数值是给列头排序的
            if (uint.TryParse(bpm, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint bpmValue))
            {
                item.BpmSortKey = bpmValue;
            }

            item.TrackCountSortKey = trackCount > 0 ? trackCount : long.MinValue;
            item.DiscSortKey = disc > 0 ? disc : long.MinValue;

            // 文件派生列（格式 / 位深 / 采样率 / 码率 / 大小）：格式走扩展名，规格走上面那一趟
            // TagLib 读取（TryGetFactsFast 内部对 DSD 等 TagLib 读不了的格式会回退完整探测）。
            try
            {
                string ext = System.IO.Path.GetExtension(path);
                item.FormatName = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() : "-";

                item.BitDepthText = bits > 0 ? bits + "bit" : "-";
                item.SampleRateText = rate > 0 ? FormatSampleRate(rate) : "-";
                item.BitrateText = kbps > 0 ? kbps + "kbps" : "-";

                // 排序用的原始数值（列头排序按数值排，不按格式化文本排）
                item.BitDepthBits = bits;
                item.SampleRateHz = rate;
                item.Kbps = kbps;

                try
                {
                    long len = new FileInfo(path).Length;
                    item.FileSizeBytes = len;
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

        /// <summary>音频属性补算的取消源：新一批读取到达时取消上一批还没跑完的补算。</summary>
        private CancellationTokenSource? _enrichCts;

        /// <summary>
        /// 后台补全「时长 / 采样率 / 位深 / 码率」（2026-10-16 Kid3 化第二半）。
        ///
        /// BuildEntry 用 <see cref="TagLib.ReadStyle.None"/> 只解析标签帧，这些要扫音频帧的列
        /// 一律留空——这就是「列表秒开」的代价。这里扫描结束后用完整 ReadStyle 重开一次文件
        /// 补齐（TagLib 本地解析，不起 ffmpeg 进程；只有 TagLib 读不到的格式才回退探测）。
        ///
        /// 设计要点：
        ///  · 有界并发 4：I/O 密集，给机械硬盘和系统留活路（再多也不会更快，只会更卡）；
        ///  · 每补一条就在 UI 线程赋值并发通知：用户看到时长码率**陆续亮**，不是干等全部读完；
        ///  · 新一批读取到达即取消上一批（用户连续拖文件进来时不会叠了好几套补算在跑）；
        ///  · 只读文件、只改行模型显示字段，不碰任何音频输出（bit-perfect 不受影响）。
        /// </summary>
        private async Task EnrichAudioFactsAsync(List<TagFileItem> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            _enrichCts?.Cancel();
            _enrichCts?.Dispose();
            CancellationTokenSource cts = new();
            _enrichCts = cts;
            CancellationToken token = cts.Token;

            try
            {
                int done = 0;
                await Task.Run(async () =>
                {
                    using var gate = new SemaphoreSlim(4, 4);
                    IEnumerable<Task> jobs = items.Select(async item =>
                    {
                        await gate.WaitAsync(token);
                        try
                        {
                            if (token.IsCancellationRequested || !System.IO.File.Exists(item.FilePath))
                            {
                                return;
                            }

                            TimeSpan duration = TimeSpan.Zero;
                            int rate = 0;
                            int bits = 0;
                            int kbps = 0;
                            try
                            {
                                using TagLib.File f = TagLib.File.Create(item.FilePath);
                                duration = f.Properties.Duration;
                                AudioInfoFormatter.TryGetFactsFast(f, item.FilePath, out rate, out bits, out kbps);
                            }
                            catch (Exception caught)
                            {
                                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.Enrich", caught);
                                return;
                            }

                            if (token.IsCancellationRequested)
                            {
                                return;
                            }

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (token.IsCancellationRequested)
                                {
                                    return;
                                }

                                item.Duration = duration;
                                item.BitDepthText = bits > 0 ? bits + "bit" : "-";
                                item.SampleRateText = rate > 0 ? FormatSampleRate(rate) : "-";
                                item.BitrateText = kbps > 0 ? kbps + "kbps" : "-";
                                item.BitDepthBits = bits;
                                item.SampleRateHz = rate;
                                item.Kbps = kbps;
                                item.NotifyAudioFactsChanged();
                            });

                            int n = Interlocked.Increment(ref done);
                            if (n % 100 == 0 || n == items.Count)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (!token.IsCancellationRequested)
                                    {
                                        TagStatusText.Text = $"正在补全音频信息 {n}/{items.Count}…";
                                    }
                                });
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });
                    await Task.WhenAll(jobs);
                }, token);

                if (!token.IsCancellationRequested)
                {
                    TagStatusText.Text = "音频信息补全完成。";
                }
            }
            catch (OperationCanceledException)
            {
                // 被新一批取消：正常路径，不提示
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.EnrichAudioFactsAsync", caught);
            }
        }

        /// <summary>44100 → 44.1kHz；48000 → 48kHz；2822400 → 2.82MHz。</summary>
        /// <summary>
        /// TTA 专用建行（2026-10-14 方案 1）：TagLib 打不开 TTA 容器，所以
        ///  · 标签 → TtaTagAccess.Read（读文件头 ID3v2 / 文件尾 APEv2）；
        ///  · 时长 → TTA 文件头里就有（数据长度 ÷ 每秒字节数，精确到样本级）；
        ///  · 采样率 / 位深 / 码率 → AudioInfoFormatter 完整探测（含 ffmpeg）。
        /// 读不到标签不算失败：按「未知艺术家/未知专辑」建行，跟其它格式一个口径。
        /// </summary>
        private static TagFileItem BuildTtaEntry(string path)
        {
            TagEditModel tag = TtaTagAccess.Read(path);
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);

            string title = string.IsNullOrWhiteSpace(tag.Title) ? fileName : tag.Title.Trim();
            string artist = tag.Performers.Length > 0 ? tag.Performers[0] : "未知艺术家";
            string albumArtist = tag.AlbumArtists.Length > 0 ? tag.AlbumArtists[0] : artist;
            string album = string.IsNullOrWhiteSpace(tag.Album) ? "未知专辑" : tag.Album.Trim();
            string genre = tag.Genres.Length > 0 ? tag.Genres[0] : "未知流派";
            string composer = tag.Composers.Length > 0 ? string.Join("; ", tag.Composers) : string.Empty;

            // 规格：TTA 没有 TagLib.Properties，走完整探测（ffmpeg）
            int rate = 0;
            int bits = 0;
            int kbps = 0;
            try
            {
                AudioInfoFormatter.TryGetFacts(path, out rate, out bits, out kbps, out _);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.BuildTtaEntry", caught);
            }

            TagFileItem item = new()
            {
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Track = tag.Track,
                Year = tag.Year,
                Genre = genre,
                Duration = ReadTtaDuration(path),
                FilePath = path,
                FormatName = "TTA",
                BitDepthText = bits > 0 ? bits + "bit" : "-",
                SampleRateText = rate > 0 ? FormatSampleRate(rate) : "-",
                BitrateText = kbps > 0 ? kbps + "kbps" : "-",
                BitDepthBits = bits,
                SampleRateHz = rate,
                Kbps = kbps,
                ComposerText = composer,
                CommentText = tag.Comment ?? string.Empty,
                LyricsText = tag.Lyrics ?? string.Empty,
                BpmText = tag.Bpm ?? string.Empty,
                TrackCountText = tag.TrackCount > 0
                    ? tag.TrackCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty,
                DiscText = tag.Disc > 0
                    ? (tag.DiscCount > 0
                        ? tag.Disc + "/" + tag.DiscCount
                        : tag.Disc.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : string.Empty,
                YearEdit = tag.Year > 0 ? tag.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                TrackEdit = tag.Track > 0 ? tag.Track.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            };

            if (uint.TryParse(tag.Bpm, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out uint bpmValue))
            {
                item.BpmSortKey = bpmValue;
            }

            item.TrackCountSortKey = tag.TrackCount > 0 ? tag.TrackCount : long.MinValue;
            item.DiscSortKey = tag.Disc > 0 ? tag.Disc : long.MinValue;

            try
            {
                long len = new FileInfo(path).Length;
                item.FileSizeBytes = len;
                if (len > 0)
                {
                    item.FileSizeText = FormatFileSize(len);
                }
            }
            catch
            {
                // 被独占 / 无权限：大小列留 "-"
            }

            return item;
        }

        /// <summary>
        /// 从 TTA 文件头算时长（精确，不用 ffmpeg）。头 22 字节（大端）：
        /// "TTA1" + 音频格式(2) + 声道(2) + 位深(2) + 采样率(4) + 数据长度(4) + CRC(4)。
        /// 读不出来返回 Zero（显示 00:00，不编）。
        /// </summary>
        private static TimeSpan ReadTtaDuration(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                byte[] head = new byte[22];
                if (stream.Read(head, 0, 22) != 22)
                {
                    return TimeSpan.Zero;
                }

                if (head[0] != 'T' || head[1] != 'T' || head[2] != 'A' || head[3] != '1')
                {
                    return TimeSpan.Zero;
                }

                ushort channels = (ushort)((head[6] << 8) | head[7]);
                ushort bits = (ushort)((head[8] << 8) | head[9]);
                uint rate = ((uint)head[10] << 24) | ((uint)head[11] << 16) | ((uint)head[12] << 8) | head[13];
                uint dataLength = ((uint)head[14] << 24) | ((uint)head[15] << 16) | ((uint)head[16] << 8) | head[17];

                if (rate == 0 || channels == 0 || bits == 0 || dataLength == 0)
                {
                    return TimeSpan.Zero;
                }

                double seconds = dataLength * 8.0 / ((double)rate * channels * bits);
                return seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
                    ? TimeSpan.FromSeconds(seconds)
                    : TimeSpan.Zero;
            }
            catch
            {
                return TimeSpan.Zero;
            }
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

        /// <summary>
        /// 枚举文件夹里的音频文件；内部缓存目录（DSD DoP / 转码 / WebDAV 缓存）一律跳过。
        /// <paramref name="recursive"/> = 是否连子文件夹一起扫（界面上「含子文件夹」勾选框）。
        /// </summary>
        private static IEnumerable<string> EnumerateAudioFiles(string folderPath, bool recursive)
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
                // 不扫子文件夹时连目录列举都省掉（大目录能快一点）
                subDirs = recursive ? Directory.GetDirectories(folderPath) : Array.Empty<string>();
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

            if (!recursive)
            {
                yield break; // 只要这一层：子文件夹不进
            }

            foreach (string sub in subDirs)
            {
                foreach (string path in EnumerateAudioFiles(sub, true))
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

        // =====================================================================
        // 列头排序（2026-10-14 新增）：mp3tag 口径的多列累积排序
        //
        // 手势（点列头即可，不需要进菜单）：
        //   · 直接点列头 = 只按这一列排：升序 → 再点降序 → 再点取消排序（回到添加顺序）；
        //   · Ctrl / Shift + 点列头 = 把这一列追加成下一级排序键，列头上显示 1、2、3…
        //     已经排过的列再 Ctrl 点 = 切换升 / 降，再点一次 = 把这一级去掉。
        // 排序键的先后 = 点击的先后（先点的优先），这就是 mp3tag 的「按点击顺序排」。
        //
        // 列头是 DataGrid 原生控件（只接管它的内容模板，不自己画表头）：
        // 原生 CanUserSortColumns 只支持单列排序，多列必须自己排，所以这里关掉
        // 原生排序、自己维护排序键并把结果写回 ObservableCollection。
        // =====================================================================

        /// <summary>列头显示模型（每列一个，挂在 DataGridColumn.Header 上）。</summary>
        private readonly List<TagColumnHeaderVm> _headerVms = new();

        /// <summary>当前排序键，列表顺序 = 优先级顺序。</summary>
        private readonly List<TagSortRule> _sortRules = new();

        /// <summary>列头按下时的指针位置（用来区分「点击」和「拖列宽」）。</summary>
        private Windows.Foundation.Point _headerPressPoint;

        private bool _headerPressed;

        /// <summary>列头按下时的 Ctrl / Shift 状态（Tapped 事件里拿不到修饰键，先存下来）。</summary>
        private Windows.System.VirtualKeyModifiers _headerPressModifiers;

        /// <summary>
        /// 上次排序的时刻。点击同时走 PointerReleased 和 Tapped 两条路，
        /// 500ms 内只认一次，免得一次点击切了两级（升→降，看着像"没反应"）。
        /// </summary>
        private long _lastSortTick = long.MinValue;

        /// <summary>列头手势是否已经挂好（挂一次就够，避免每帧遍历可视化树）。</summary>
        private bool _headerGesturesAttached;

        /// <summary>拖列宽：正在拖的那一列（自绘把手，见 TagHeaderGripper_*）。</summary>
        private DataGridColumn? _resizeColumn;

        private double _resizeStartX;

        private double _resizeStartWidth;

        private bool _resizingColumns;

        /// <summary>上次拖完列宽的时刻：拖完会紧跟一个 Tapped，300ms 内不当成"点列头排序"。</summary>
        private long _lastResizeTick = long.MinValue;

        // =====================================================================
        // 列显隐 + 前后顺序（2026-10-14 用户要求：mp3tag 能读的标签也要能加进列表；
        // 每个字段都要能选显不显示、还能前移后移）
        //
        // 全部 21 个真实列都归这里管（末尾星号填充列除外——它恒在末行吸收剩余宽度）。
        // 顺序落地走 DataGridColumn.DisplayIndex（不动 Columns 集合本身）：
        //   · DisplayIndex 是官方给的「显示位置」入口，设置时网格会自动把别的列
        //     让开的位子补上（WPF 同款让位校正，toolkit 源码 DataGridColumns.cs
        //     OnColumnDisplayIndexChanging 实锤），所以每列给一个互不相同的绝对
        //     位置即可，不需要手工挪集合；
        //   · 集合顺序永远保持 XAML 默认顺序，「恢复默认」因此永远不会漂。
        // 状态存配置目录 tag-panel-columns.json：
        //   { "order": [全部列 Key（含隐藏的，按显示顺序）], "hidden": [隐藏的 Key] }
        // 旧格式（只有 6 个可选列 Key 的数组）自动兼容：顺序按默认、没列进去 = 隐藏。
        // =====================================================================

        /// <summary>默认隐藏的列（Key 集合；老口径的 6 个可选列）。单一真相在 TagColumnLayout。</summary>
        private static readonly HashSet<string> DefaultHiddenColumns =
            new(TagColumnLayout.DefaultHiddenColumns, StringComparer.Ordinal);

        /// <summary>末尾填充列的 Key（不参与显隐和顺序，恒在末行）。</summary>
        private const string FillerColumnKey = "Filler";

        /// <summary>最左侧的选择框列（2026-10-15 新增）：恒在首位、不可隐藏、不可拖宽、不参与列设置。</summary>
        private const string SelectColumnKey = "Select";

        /// <summary>全部真实列的 Key（XAML 默认顺序，加载时从列头模型收集）。</summary>
        private List<string> _allColumnKeys = new();

        /// <summary>各列显示名（Key → 列头文字）。</summary>
        private readonly Dictionary<string, string> _columnLabels = new(StringComparer.Ordinal);

        /// <summary>当前列顺序（含被隐藏的列——它们占位但不显示）。</summary>
        private List<string> _columnOrder = new();

        /// <summary>当前隐藏的列。</summary>
        private HashSet<string> _hiddenColumns = new(StringComparer.Ordinal);

        private static string ColumnStateFilePath =>
            System.IO.Path.Combine(AppSettingsStore.GetConfigDirectory(), "tag-panel-columns.json");

        /// <summary>
        /// 面板加载时调：从列头模型收集全部列（此时列头还是 XAML 原文，顺序 = 默认顺序），
        /// 读盘上存的「顺序 + 显隐」并应用到表格。
        /// </summary>
        private void LoadColumnLayout()
        {
            // 1) 收集全部列（_headerVms 在 Loaded 里已按集合顺序建好 = XAML 默认顺序）
            _allColumnKeys = new List<string>();
            _columnLabels.Clear();
            foreach (TagColumnHeaderVm vm in _headerVms)
            {
                if (string.IsNullOrEmpty(vm.Key)
                    || string.Equals(vm.Key, FillerColumnKey, StringComparison.Ordinal)
                    || string.Equals(vm.Key, SelectColumnKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!_allColumnKeys.Contains(vm.Key))
                {
                    _allColumnKeys.Add(vm.Key);
                }

                _columnLabels[vm.Key] = vm.Label;
            }

            List<string> order = new(_allColumnKeys);
            HashSet<string> hidden = new(DefaultHiddenColumns, StringComparer.Ordinal);
            bool legacyFormat = false;

            // 2) 读盘：新格式是对象 { order, hidden }；旧格式（数组 = 勾出来的可选列）兼容
            try
            {
                string file = ColumnStateFilePath;
                if (File.Exists(file))
                {
                    using System.Text.Json.JsonDocument doc =
                        System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));

                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        // 旧格式（2026-10-14 之前的「列…」勾选面板）：数组里只可能躺着
                        // 6 个可选列的 Key——默认列（标题/艺术家/…）当年根本不参与勾选、恒可见。
                        // ⚠ 兼容口径：数组里有的可选列 = 可见，没有的可选列 = 隐藏；
                        //    默认列一律保持可见。第一版这里写的是「数组里没有的全隐藏」，
                        //    用户盘上存着旧版空数组 [] 时 21 列全被藏掉——表格有行、能选中，
                        //    但一个字都不显示（2026-10-14 用户实测）。
                        legacyFormat = true;
                        hidden = TagColumnLayout.InterpretLegacy(
                            doc.RootElement.GetRawText(), _allColumnKeys, DefaultHiddenColumns);
                    }
                    else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        List<string> loadedOrder = new();
                        if (doc.RootElement.TryGetProperty("order", out System.Text.Json.JsonElement orderEl)
                            && orderEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (System.Text.Json.JsonElement element in orderEl.EnumerateArray())
                            {
                                string? key = element.GetString();
                                if (!string.IsNullOrEmpty(key) && _columnLabels.ContainsKey(key)
                                    && !loadedOrder.Contains(key))
                                {
                                    loadedOrder.Add(key);
                                }
                            }
                        }

                        // 盘上没提过的列（以后新加的列）按默认顺序补到末尾
                        foreach (string key in _allColumnKeys)
                        {
                            if (!loadedOrder.Contains(key))
                            {
                                loadedOrder.Add(key);
                            }
                        }

                        order = loadedOrder;

                        HashSet<string> loadedHidden = new(StringComparer.Ordinal);
                        if (doc.RootElement.TryGetProperty("hidden", out System.Text.Json.JsonElement hiddenEl)
                            && hiddenEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (System.Text.Json.JsonElement element in hiddenEl.EnumerateArray())
                            {
                                string? key = element.GetString();
                                if (!string.IsNullOrEmpty(key) && _columnLabels.ContainsKey(key))
                                {
                                    loadedHidden.Add(key);
                                }
                            }
                        }

                        hidden = loadedHidden;
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.LoadColumnLayout", caught);
                order = new List<string>(_allColumnKeys);
                hidden = new HashSet<string>(DefaultHiddenColumns, StringComparer.Ordinal);
            }

            _columnOrder = order;
            _hiddenColumns = hidden;

            ApplyColumnLayout();

            // 旧格式文件就地升级成新格式：下次加载直接走对象路径，
            // 不用每次重猜兼容口径（升级失败不影响本次使用，日志留证）。
            if (legacyFormat)
            {
                try
                {
                    SaveColumnLayout();
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException(
                        "TagPanelControl.LoadColumnLayout.migrate", caught);
                }
            }

            // 诊断：可见/隐藏列数 + 是否刚做过旧格式升级。「表格不显示字」类问题先看这行
            global::CelesteMusicPlayer.StartupLog.Write(
                $"[tagpanel] 列布局：可见 {_columnOrder.Count(k => !_hiddenColumns.Contains(k))} 列，" +
                $"隐藏 {_hiddenColumns.Count} 列{(legacyFormat ? "（旧格式配置已升级）" : string.Empty)}");
        }

        /// <summary>按 Key 找表格列（找不到 = 盘上状态引用了不存在的列，调用方跳过）。</summary>
        private DataGridColumn? FindColumnByKey(string key)
        {
            return TagFileGrid.Columns.FirstOrDefault(
                c => string.Equals(c.Tag as string, key, StringComparison.Ordinal));
        }

        /// <summary>
        /// 把「顺序 + 显隐」落到表格上：可见列按 _columnOrder 的相对顺序占前，
        /// 隐藏列排在可见列之后（只占位、不显示），星号填充列恒在末行。
        /// 每一列都拿一个互不相同的 DisplayIndex，网格的让位校正就无从触发。
        /// </summary>
        private void ApplyColumnLayout()
        {
            List<string> visible = _columnOrder.Where(k => !_hiddenColumns.Contains(k)).ToList();
            List<string> hidden = _columnOrder.Where(k => _hiddenColumns.Contains(k)).ToList();

            List<string> desired = new(visible);
            desired.AddRange(hidden);

            for (int i = 0; i < desired.Count; i++)
            {
                DataGridColumn? column = FindColumnByKey(desired[i]);
                if (column == null)
                {
                    continue;
                }

                // 选择框列恒在最左（DisplayIndex=0），真实列从 1 开始排
                column.Visibility = i < visible.Count ? Visibility.Visible : Visibility.Collapsed;

                try
                {
                    if (column.DisplayIndex != i + 1)
                    {
                        column.DisplayIndex = i + 1;
                    }
                }
                catch (Exception caught)
                {
                    // 显示位置设失败不该连带把显隐也搅黄（日志留证，顺序保持原样）
                    global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ApplyColumnLayout", caught);
                }
            }

            // 选择框列恒在最左
            DataGridColumn? select = FindColumnByKey(SelectColumnKey);
            if (select != null)
            {
                select.Visibility = Visibility.Visible;
                try
                {
                    if (select.DisplayIndex != 0)
                    {
                        select.DisplayIndex = 0;
                    }
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ApplyColumnLayout.select", caught);
                }
            }

            // 星号填充列永远最后（吸收剩余宽度）
            DataGridColumn? filler = FindColumnByKey(FillerColumnKey);
            if (filler != null)
            {
                filler.Visibility = Visibility.Visible;
                try
                {
                    int last = desired.Count + 1; // 选择框列 + 真实列之后
                    if (filler.DisplayIndex != last)
                    {
                        filler.DisplayIndex = last;
                    }
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ApplyColumnLayout.filler", caught);
                }
            }
        }

        private void SaveColumnLayout()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    order = _columnOrder,
                    hidden = _hiddenColumns.ToList()
                });
                File.WriteAllText(ColumnStateFilePath, json);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.SaveColumnLayout", caught);
            }
        }

        /// <summary>「列…」弹窗每次打开都按当前状态重建（勾选框 + 前移后移，避免状态过期）。</summary>
        private void ColumnsFlyout_Opening(object sender, object e)
        {
            RebuildColumnToggleList();
        }

        /// <summary>按 _columnOrder 重建勾选列表：每行 = 复选框（显隐）+ ↑ 前移 + ↓ 后移。</summary>
        private void RebuildColumnToggleList()
        {
            ColumnToggleList.Children.Clear();

            for (int i = 0; i < _columnOrder.Count; i++)
            {
                string key = _columnOrder[i];
                if (!_columnLabels.TryGetValue(key, out string? label))
                {
                    continue;
                }

                Grid row = new() { ColumnSpacing = 4, Padding = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                CheckBox box = new()
                {
                    Content = label,
                    FontSize = 12,
                    IsChecked = !_hiddenColumns.Contains(key),
                };

                string captured = key;
                box.Checked += (s, args) =>
                {
                    _hiddenColumns.Remove(captured);
                    ApplyColumnLayout();
                    SaveColumnLayout();
                };
                box.Unchecked += (s, args) =>
                {
                    _hiddenColumns.Add(captured);
                    ApplyColumnLayout();
                    SaveColumnLayout();
                };
                Grid.SetColumn(box, 0);

                Button upButton = new()
                {
                    Content = new FontIcon { Glyph = "\uE70E", FontSize = 12 },
                    Padding = new Microsoft.UI.Xaml.Thickness(6, 3, 6, 3),
                    IsEnabled = i > 0,
                };
                // 附加属性在 C# 里要用 SetToolTip（不能写在对象初始化器里）
                ToolTipService.SetToolTip(upButton, "前移（往左挪一列）");
                int capturedIndex = i;
                upButton.Click += (s, args) => MoveColumn(capturedIndex, -1);
                Grid.SetColumn(upButton, 1);

                Button downButton = new()
                {
                    Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 },
                    Padding = new Microsoft.UI.Xaml.Thickness(6, 3, 6, 3),
                    IsEnabled = i < _columnOrder.Count - 1,
                };
                ToolTipService.SetToolTip(downButton, "后移（往右挪一列）");
                downButton.Click += (s, args) => MoveColumn(capturedIndex, +1);
                Grid.SetColumn(downButton, 2);

                row.Children.Add(box);
                row.Children.Add(upButton);
                row.Children.Add(downButton);
                ColumnToggleList.Children.Add(row);
            }
        }

        /// <summary>把某一列前移（delta=-1）或后移（delta=+1）一格，立即生效并落盘。</summary>
        private void MoveColumn(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= _columnOrder.Count || target < 0 || target >= _columnOrder.Count)
            {
                return;
            }

            (_columnOrder[target], _columnOrder[index]) = (_columnOrder[index], _columnOrder[target]);
            ApplyColumnLayout();
            SaveColumnLayout();
            RebuildColumnToggleList();
        }

        /// <summary>「恢复默认列与顺序」：15 个默认列显示在原顺序，6 个可选列重新隐藏。</summary>
        private void ColumnsResetButton_Click(object sender, RoutedEventArgs e)
        {
            _columnOrder = new List<string>(_allColumnKeys);
            _hiddenColumns = new HashSet<string>(DefaultHiddenColumns, StringComparer.Ordinal);
            ApplyColumnLayout();
            SaveColumnLayout();
            RebuildColumnToggleList();
        }

        /// <summary>
        /// 列头模型按当前显示顺序（DisplayIndex）排——「排序…」面板里的字段下拉
        /// 跟表格里的实际列序保持一致（用户调过列序后尤其明显）。
        /// </summary>
        private IEnumerable<TagColumnHeaderVm> OrderedHeaderVms()
        {
            return _headerVms
                .Where(v => !string.IsNullOrEmpty(v.Key)
                    && !string.Equals(v.Key, FillerColumnKey, StringComparison.Ordinal))
                .OrderBy(v => FindColumnByKey(v.Key)?.DisplayIndex ?? int.MaxValue);
        }

        /// <summary>
        /// 把每列的 Header 换成列头显示模型。列头文字仍以 XAML 里写的 Header 为准
        /// （不在这里另建一份标签表，避免两处不同步）。
        /// </summary>
        private void SetupColumnHeaderModels()
        {
            _headerVms.Clear();
            foreach (DataGridColumn column in TagFileGrid.Columns)
            {
                string key = column.Tag as string ?? string.Empty;
                string label = column.Header as string ?? string.Empty;
                TagColumnHeaderVm vm = new(key, label);
                _headerVms.Add(vm);
                column.Header = vm;
            }
        }

        private void TagHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 修饰键先存下来：真正排序可能在 Tapped 里执行，那里拿不到 KeyModifiers
            _headerPressModifiers = e.KeyModifiers;
            if (sender is UIElement element)
            {
                _headerPressPoint = e.GetCurrentPoint(element).Position;
                _headerPressed = true;
            }
            // 不设 e.Handled：列头右侧的拖列宽把手要能照常工作
        }

        private void TagHeader_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_headerPressed || sender is not FrameworkElement element)
            {
                return;
            }

            _headerPressed = false;

            // 拖列宽也是在列头上按下 → 移动 → 松手：位移超过 4px 判为拖拽，不排序
            Windows.Foundation.Point p = e.GetCurrentPoint(element).Position;
            if (Math.Abs(p.X - _headerPressPoint.X) > 4 || Math.Abs(p.Y - _headerPressPoint.Y) > 4)
            {
                return;
            }

            TagColumnHeaderVm? vm = VmOfHeaderSender(sender);
            if (vm != null)
            {
                bool additive = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0
                    || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
                SortFromHeader(vm, additive);
            }
        }

        /// <summary>
        /// 从事件 sender 反查是哪一列：列头元素（DataGridColumnHeader）的 Content 就是
        /// 列头显示模型；列头内容模板里的 Grid 则走 DataContext。两种来源都支持——
        /// 手势同时挂在「列头元素」和「模板内的 Grid」上，哪条路到了都能排上。
        /// </summary>
        private static TagColumnHeaderVm? VmOfHeaderSender(object sender)
        {
            if (sender is DataGridColumnHeader header)
            {
                return header.Content as TagColumnHeaderVm;
            }

            return (sender as FrameworkElement)?.DataContext as TagColumnHeaderVm;
        }

        /// <summary>
        /// 兜底的一条路：万一 PointerReleased 被 DataGrid 自己吞掉（列头本来就要处理
        /// 拖列宽等手势），Tapped 仍然能到。两条路都汇到这里，500ms 内只执行一次。
        /// </summary>
        private void TagHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 刚拖完列宽会补一个 Tapped：拖宽不是排序，直接忽略
            if (_resizingColumns || Environment.TickCount64 - _lastResizeTick < 300)
            {
                return;
            }

            TagColumnHeaderVm? vm = VmOfHeaderSender(sender);
            if (vm != null)
            {
                bool additive = (_headerPressModifiers & Windows.System.VirtualKeyModifiers.Control) != 0
                    || (_headerPressModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
                SortFromHeader(vm, additive);
            }
        }

        // =====================================================================
        // 拖列宽（2026-10-15）：列头模板换成自绘之后 toolkit 自带把手失效，这里自己实现。
        //   按下把手 → 记下列/起始指针 X/起始宽度并捕获指针；
        //   移动 → 按位移改 column.Width（夹在 MinWidth..MaxWidth 之间，位移不足 0.5px 不动，
        //         免得每帧都触发布局重算）；
        //   松手 → 释放捕获。末尾星号填充列（Tag=Filler）不参与拖宽。
        // =====================================================================

        private void TagHeaderGripper_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            DataGridColumn? column = FindResizableColumn(VmOfHeaderSender(sender)?.Key);
            if (column == null)
            {
                return;
            }

            _resizeColumn = column;
            _resizeStartWidth = column.ActualWidth > 0 ? column.ActualWidth : column.Width.DisplayValue;
            _resizeStartX = e.GetCurrentPoint(TagFileGrid).Position.X;
            _resizingColumns = true;
            if (sender is UIElement element)
            {
                element.CapturePointer(e.Pointer);
            }

            e.Handled = true;
        }

        private void TagHeaderGripper_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_resizingColumns || _resizeColumn == null)
            {
                return;
            }

            e.Handled = true;
            double target = _resizeStartWidth + (e.GetCurrentPoint(TagFileGrid).Position.X - _resizeStartX);
            if (target < _resizeColumn.MinWidth)
            {
                target = _resizeColumn.MinWidth;
            }

            if (_resizeColumn.MaxWidth > 0 && target > _resizeColumn.MaxWidth)
            {
                target = _resizeColumn.MaxWidth;
            }

            if (Math.Abs(target - _resizeColumn.ActualWidth) < 0.5)
            {
                return;
            }

            _resizeColumn.Width = new DataGridLength(target, DataGridLengthUnitType.Pixel);
        }

        private void TagHeaderGripper_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_resizingColumns)
            {
                return;
            }

            _resizingColumns = false;
            _resizeColumn = null;
            _lastResizeTick = Environment.TickCount64;
            if (sender is UIElement element)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            e.Handled = true;
        }

        /// <summary>
        /// 拖宽专用：按列标识找列，但排除两类「宽度固定」的列——
        /// 末尾星号填充列（Filler，宽度 = 剩余空间）和最左选择框列（Select，恒定窄列）。
        /// 这两列拖了也没意义，还会让布局打架。
        /// </summary>
        private DataGridColumn? FindResizableColumn(string? key)
        {
            if (string.IsNullOrEmpty(key)
                || string.Equals(key, FillerColumnKey, StringComparison.Ordinal)
                || string.Equals(key, SelectColumnKey, StringComparison.Ordinal))
            {
                return null;
            }

            return FindColumnByKey(key);
        }

        /// <summary>
        /// 列头点击排序 —— 事件挂在**整张表格**上，再用命中测试判断点中的是哪一列。
        ///
        /// 为什么不挂在列头元素（DataGridColumnHeader）上：三条路都试过，一次都没进来——
        ///   ① 挂在列头内容模板里的元素上：默认模板根本不认 ContentTemplate，模板没被采用；
        ///   ② 挂在列头元素上（普通 += ）：DataGrid 自己处理列头指针事件后标成"已处理"，
        ///      普通订阅收不到；
        ///   ③ 挂在列头元素上（AddHandler + handledEventsToo）：日志写着"已挂到 17 个列头"，
        ///      点击照样一次都不进来（2026-10-14 实测）。
        /// 而且列头实例会随数据变化被重建，挂在它上面迟早失效。
        /// 挂在表格根上（handledEventsToo）+ 按坐标算列，与列头实例无关，稳。
        /// </summary>
        private void AttachColumnHeaderGestures()
        {
            TagFileGrid.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(TagGrid_PointerPressed), true);
            TagFileGrid.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(TagGrid_PointerReleased), true);
            global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 列头排序手势已挂（表格级命中测试）");
        }

        private void TagGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _headerPressModifiers = e.KeyModifiers;
            _headerPressPoint = e.GetCurrentPoint(TagFileGrid).Position;
            _headerPressed = true;
        }

        private void TagGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_headerPressed)
            {
                return;
            }

            _headerPressed = false;

            Windows.Foundation.Point p = e.GetCurrentPoint(TagFileGrid).Position;
            // 位移超过 4px = 拖列宽（或框选），不是点列头
            if (Math.Abs(p.X - _headerPressPoint.X) > 4 || Math.Abs(p.Y - _headerPressPoint.Y) > 4)
            {
                return;
            }

            TagColumnHeaderVm? vm = HitTestHeaderColumn(p);
            if (vm == null)
            {
                return; // 点在数据区，不管
            }

            bool additive = (_headerPressModifiers & Windows.System.VirtualKeyModifiers.Control) != 0
                || (_headerPressModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
            SortFromHeader(vm, additive);
        }

        /// <summary>
        /// 表格坐标 → 列头显示模型：先看点在不在列头那一行（高度 = ColumnHeaderHeight），
        /// 再按各列实际宽度累加定位到列。列头面板的横向偏移用 TransformToVisual 现取，
        /// 所以横向滚动 / 列宽拖动之后依然算得准。
        /// </summary>
        private TagColumnHeaderVm? HitTestHeaderColumn(Windows.Foundation.Point pointInGrid)
        {
            if (pointInGrid.Y < 0 || pointInGrid.Y > TagFileGrid.ColumnHeaderHeight)
            {
                return null;
            }

            double originX = 0;
            if (FindDescendant<DataGridColumnHeadersPresenter>(TagFileGrid) is DataGridColumnHeadersPresenter presenter)
            {
                GeneralTransform transform = presenter.TransformToVisual(TagFileGrid);
                originX = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            }

            double x = pointInGrid.X - originX;
            foreach (DataGridColumn column in TagFileGrid.Columns)
            {
                double width = column.ActualWidth;
                if (x < width)
                {
                    return column.Header as TagColumnHeaderVm;
                }

                x -= width;
            }

            return null;
        }

        private void SortFromHeader(TagColumnHeaderVm vm, bool additive)
        {
            long now = Environment.TickCount64;
            if (now - _lastSortTick < 500)
            {
                return;
            }

            _lastSortTick = now;
            ToggleSortRule(vm, additive);
        }

        /// <summary>点一次列头：按上面的手势规则更新排序键并立即重排。</summary>
        private void ToggleSortRule(TagColumnHeaderVm vm, bool additive)
        {
            // 诊断用：点列头到底进没进来、进来的是哪一列，日志里一眼能看到
            global::CelesteMusicPlayer.StartupLog.Write($"TagPanel: 列头点击 → Key={vm.Key ?? "(null)"} additive={additive}");

            if (string.IsNullOrEmpty(vm.Key) || vm.Key == "Filler")
            {
                return; // 末尾填充列不是真列，不参与排序（# 列允许：按当前行号排）
            }

            TagSortRule? existing = _sortRules.FirstOrDefault(r => string.Equals(r.Key, vm.Key, StringComparison.Ordinal));

            if (!additive)
            {
                // 单键模式：这一列已经是唯一键时升 → 降 → 取消，否则重置为这一列
                if (_sortRules.Count == 1 && existing != null)
                {
                    if (!existing.Descending)
                    {
                        existing.Descending = true;
                    }
                    else
                    {
                        _sortRules.Clear();
                    }
                }
                else
                {
                    _sortRules.Clear();
                    _sortRules.Add(new TagSortRule(vm.Key));
                }
            }
            else if (existing == null)
            {
                _sortRules.Add(new TagSortRule(vm.Key));
            }
            else if (!existing.Descending)
            {
                existing.Descending = true;
            }
            else
            {
                _sortRules.Remove(existing);
            }

            ApplySort();
        }

        /// <summary>按当前排序键重排列表并刷新状态条（列头点击走这条）。</summary>
        private void ApplySort()
        {
            ReorderFiles();
            WriteSortStatus();
        }

        /// <summary>
        /// 按当前排序键重排（不动状态条）。新文件加进来时走这条：
        /// 已经排过序的列表，新文件要落到它该在的位置，而不是挂在最后。
        /// </summary>
        private void ReorderFiles()
        {
            // 1) 列头标记
            foreach (TagColumnHeaderVm vm in _headerVms)
            {
                int ordinal = _sortRules.FindIndex(r => string.Equals(r.Key, vm.Key, StringComparison.Ordinal));
                vm.SetSort(ordinal + 1, ordinal >= 0 && _sortRules[ordinal].Descending, _sortRules.Count);
            }

            // 2) 排
            IEnumerable<TagFileItem> seq = _files;
            if (_sortRules.Count == 0)
            {
                seq = _files.OrderBy(f => f.AddOrder);
            }
            else
            {
                bool first = true;
                foreach (TagSortRule rule in _sortRules)
                {
                    Func<TagFileItem, object?> selector = SortSelector(rule.Key);
                    if (first)
                    {
                        seq = rule.Descending
                            ? seq.OrderByDescending(selector, TagSortComparer.Instance)
                            : seq.OrderBy(selector, TagSortComparer.Instance);
                        first = false;
                    }
                    else
                    {
                        IOrderedEnumerable<TagFileItem> ordered = (IOrderedEnumerable<TagFileItem>)seq;
                        seq = rule.Descending
                            ? ordered.ThenByDescending(selector, TagSortComparer.Instance)
                            : ordered.ThenBy(selector, TagSortComparer.Instance);
                    }
                }
            }

            List<TagFileItem> sorted = seq.ToList();
            bool unchanged = sorted.Count == _files.Count;
            if (unchanged)
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (!ReferenceEquals(sorted[i], _files[i]))
                    {
                        unchanged = false;
                        break;
                    }
                }
            }

            if (!unchanged)
            {
                // 保住选中：换数据源会清掉一切选择状态
                HashSet<string> selected = new(
                    CheckedItems.Select(f => f.FilePath),
                    StringComparer.OrdinalIgnoreCase);

                // 先断开再挂回：避免 n 次集合通知把表格反复重建（文件多时很卡）
                TagFileGrid.ItemsSource = null;
                _files.Clear();
                foreach (TagFileItem item in sorted)
                {
                    _files.Add(item);
                }

                TagFileGrid.ItemsSource = _files;
                Renumber();

                // 重挂后按路径重建多选 + 焦点行（内部带抑制，不会被自己的
                // SelectionChanged 收敛成单选）
                SelectRowsByPaths(selected);
            }

            global::CelesteMusicPlayer.StartupLog.Write("TagPanel: 排序 → "
                + (_sortRules.Count == 0 ? "取消（回到添加顺序）" : string.Join(" , ", _sortRules.Select(r => r.Key + (r.Descending ? " desc" : " asc")))));
        }

        // =====================================================================
        // 排序面板（2026-10-14）：列头点击那条路放弃了，改成工具条上的「排序…」按钮
        // 弹出小面板。面板里每一行 = 一级排序键：选字段 + 升/降 + 上移 + 删除，
        // 行在列表里的上下顺序就是排序的优先顺序（mp3tag 的排序对话框也是这个意思）。
        // =====================================================================

        private void SortFlyout_Opening(object sender, object e)
        {
            RebuildSortRuleList();
        }

        private void RebuildSortRuleList()
        {
            SortRuleList.Children.Clear();

            if (_sortRules.Count == 0)
            {
                SortRuleList.Children.Add(new TextBlock
                {
                    Text = "还没设置排序，现在是添加进来时的顺序。",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.WrapWholeWords
                });
                return;
            }

            for (int i = 0; i < _sortRules.Count; i++)
            {
                SortRuleList.Children.Add(BuildSortRuleRow(_sortRules[i], i));
            }
        }

        private FrameworkElement BuildSortRuleRow(TagSortRule rule, int index)
        {
            Grid grid = new() { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 160 };
            foreach (TagColumnHeaderVm vm in OrderedHeaderVms())
            {
                combo.Items.Add(new ComboBoxItem { Content = vm.Label, Tag = vm.Key });
                if (string.Equals(vm.Key, rule.Key, StringComparison.Ordinal))
                {
                    combo.SelectedIndex = combo.Items.Count - 1;
                }
            }

            combo.SelectionChanged += (_, _) =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is string key && !string.Equals(key, rule.Key, StringComparison.Ordinal))
                {
                    rule.Key = key;
                    ApplySort();
                    RebuildSortRuleList();
                }
            };
            Grid.SetColumn(combo, 0);

            Button dirButton = new()
            {
                Content = rule.Descending ? "降序 ↓" : "升序 ↑",
                Padding = new Microsoft.UI.Xaml.Thickness(8, 4, 8, 4)
            };
            dirButton.Click += (_, _) =>
            {
                rule.Descending = !rule.Descending;
                ApplySort();
                RebuildSortRuleList();
            };
            Grid.SetColumn(dirButton, 1);

            Button upButton = new()
            {
                Content = "↑",
                Padding = new Microsoft.UI.Xaml.Thickness(8, 4, 8, 4),
                IsEnabled = index > 0
            };
            // 附加属性在 C# 里要用 SetToolTip（不能写在对象初始化器里）
            ToolTipService.SetToolTip(upButton, "提高一级（行号越小越优先）");
            upButton.Click += (_, _) =>
            {
                if (index <= 0 || index >= _sortRules.Count || !ReferenceEquals(_sortRules[index], rule))
                {
                    return;
                }

                _sortRules.RemoveAt(index);
                _sortRules.Insert(index - 1, rule);
                ApplySort();
                RebuildSortRuleList();
            };
            Grid.SetColumn(upButton, 2);

            Button delButton = new()
            {
                Content = "×",
                Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4)
            };
            ToolTipService.SetToolTip(delButton, "去掉这一级");
            delButton.Click += (_, _) =>
            {
                _sortRules.Remove(rule);
                ApplySort();
                RebuildSortRuleList();
            };
            Grid.SetColumn(delButton, 3);

            grid.Children.Add(combo);
            grid.Children.Add(dirButton);
            grid.Children.Add(upButton);
            grid.Children.Add(delButton);
            return grid;
        }

        private void SortAddRuleButton_Click(object sender, RoutedEventArgs e)
        {
            string? nextKey = null;
            foreach (TagColumnHeaderVm vm in OrderedHeaderVms())
            {
                if (!_sortRules.Any(r => string.Equals(r.Key, vm.Key, StringComparison.Ordinal)))
                {
                    nextKey = vm.Key;
                    break;
                }
            }

            if (nextKey == null)
            {
                return; // 每一列都排上了，没得再加
            }

            _sortRules.Add(new TagSortRule(nextKey));
            ApplySort();
            RebuildSortRuleList();
        }

        private void SortClearRulesButton_Click(object sender, RoutedEventArgs e)
        {
            _sortRules.Clear();
            ApplySort();
            RebuildSortRuleList();
        }

        /// <summary>状态条说清「现在按什么排」（用户看得见，不用猜）。</summary>
        private void WriteSortStatus()
        {
            if (_sortRules.Count == 0)
            {
                TagStatusText.Text = "排序已取消，列表回到添加时的顺序。";
                return;
            }

            IEnumerable<string> parts = _sortRules.Select(r =>
                HeaderLabelOf(r.Key) + (r.Descending ? " ↓" : " ↑"));
            TagStatusText.Text = "排序：" + string.Join(" → ", parts)
                + "（Ctrl / Shift + 点列头 = 加下一级排序）";
        }

        /// <summary>列 Tag → 排序取值（数值列返回数值，不按格式化文本排）。</summary>
        private static Func<TagFileItem, object?> SortSelector(string key) => key switch
        {
            "Index" => f => f.Index,
            "Title" => f => f.Title,
            "Artist" => f => f.Artist,
            "Album" => f => f.Album,
            "Year" => f => f.Year,
            "Track" => f => f.Track,
            "Genre" => f => f.Genre,
            "Duration" => f => f.Duration,
            "FileName" => f => f.FileName,
            "Format" => f => f.FormatName,
            "BitDepth" => f => f.BitDepthBits,
            "SampleRate" => f => f.SampleRateHz,
            "Bitrate" => f => f.Kbps,
            "FileSize" => f => f.FileSizeBytes,

            // 可选列（默认隐藏，「列…」里勾出来）：文字列按文字排，数值列按原始数值排
            "AlbumArtist" => f => f.AlbumArtist,
            "Composer" => f => f.ComposerText,
            "Disc" => f => f.DiscSortKey,
            "TrackCount" => f => f.TrackCountSortKey,
            "Bpm" => f => f.BpmSortKey,
            "Comment" => f => f.CommentText,
            _ => f => f.AddOrder
        };

        private string HeaderLabelOf(string key)
        {
            foreach (TagColumnHeaderVm vm in _headerVms)
            {
                if (string.Equals(vm.Key, key, StringComparison.Ordinal))
                {
                    return vm.Label;
                }
            }

            return key;
        }

        /// <summary>
        /// 排序比较：文字按中文习惯忽略大小写比，数值 / 时长按大小比，
        /// 空值（读不出来的字段）始终排在升序的最前面。
        /// </summary>
        private sealed class TagSortComparer : IComparer<object?>
        {
            public static readonly TagSortComparer Instance = new();

            public int Compare(object? a, object? b)
            {
                if (a is string sa && b is string sb)
                {
                    return string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
                }

                if (a is null)
                {
                    return b is null ? 0 : -1;
                }

                if (b is null)
                {
                    return 1;
                }

                return (a as IComparable)?.CompareTo(b) ?? 0;
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
            // ⚠️ 计数必须读多选真值源 _checkedSet，**不要读 TagFileGrid.SelectedItems.Count**。
            // 本表是 SelectionMode=Single（只管焦点行），多选完全自管在 _checkedSet；
            // 读 SelectedItems 会跟真实勾选数对不上（2026-10-16 用户反馈「只选了一首，
            // 却显示选中了很多」）。
            int selected = _checkedSet.Count;

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
        // 文件表格：DataGrid 外观（2026-10-13 第四轮换 DataGrid）
        //
        // 旧自绘表格的三处同步（XAML 表头 + 行模板 + cs 数组）、表头位移裁剪、
        // 列边界把手、拖拽帧合并……整块删除：列宽 / 列头 / 横向滚动 / 行选中
        // 全部是 DataGrid 原生能力，列宽只在 XAML 一处定义。这里只剩三件事：
        //   ① 选中底色染成注入的强调色（覆盖 DataGridRowSelected* 主题资源）；
        //   ② 已实现行的选中前景（LoadingRow / UnloadingRow / 选择变化时刷）；
        //   ③ 面板就绪守卫（_panelReady，Loaded 之后才响应）。
        // =====================================================================

        private void TagPanelControl_Loaded(object sender, RoutedEventArgs e)
        {
            _panelReady = true;
            try
            {
                // 先给列头装上显示模型（列头文字 + 排序标记），排序手势才有落点
                SetupColumnHeaderModels();
                SetupDataGridChrome();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.Loaded.table", caught);
            }

            try
            {
                // 恢复上次的列顺序与显隐（mp3tag 式「列显示」，含前移后移）
                LoadColumnLayout();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.Loaded.columns", caught);
            }
        }

        /// <summary>
        /// DataGrid 外观初始化：选中底色染成主程序歌曲面板同款的「半透明灰」。
        /// 2026-10-16 第三版定案——前两版为什么「背景还是没变」：
        ///   DataGridRow 模板的选中底是一块 Rectangle（x:Name="BackgroundRectangle"），
        ///   它的 Fill 绑的是主题资源、**根本不绑 row.Background**；选中态由 VisualState
        ///   动画直接写 (Fill).(SolidColorBrush.Color)，读的就是下面这四个 Color+Opacity
        ///   主题资源。所以在行上设 Background/画刷统统无效，唯一有效的杠杆就是这四个资源。
        /// 颜色取 MainWindow.ResolveClassicRowSelectionColor（深色=白 23% / 浅色=灰 16%，
        /// 令牌皮肤走 Selection.Background 令牌）——即主面板 TryApplyBarStyleRowSelection
        /// 那一款；左侧 3px 竖条按用户要求去掉，文字保持原色不反白。
        /// 四组状态（选中 / 悬停选中 / 失焦选中 / 失焦悬停选中）都要覆盖，只改一组会漏态。
        /// Loaded 后调一次；主题 / 皮肤切换时由 ReapplyAccent 再调。
        /// </summary>
        private void SetupDataGridChrome()
        {
            _rowSelectionColor = MainWindow.ResolveClassicRowSelectionColor(TagFileGrid);
            _checkedRowBrush = new SolidColorBrush(_rowSelectionColor);

            TagFileGrid.Resources["DataGridRowSelectedBackgroundColor"] = _rowSelectionColor;
            TagFileGrid.Resources["DataGridRowSelectedBackgroundOpacity"] = 1.0;
            TagFileGrid.Resources["DataGridRowSelectedHoveredBackgroundColor"] = _rowSelectionColor;
            TagFileGrid.Resources["DataGridRowSelectedHoveredBackgroundOpacity"] = 1.0;
            TagFileGrid.Resources["DataGridRowSelectedUnfocusedBackgroundColor"] = _rowSelectionColor;
            TagFileGrid.Resources["DataGridRowSelectedUnfocusedBackgroundOpacity"] = 1.0;
            TagFileGrid.Resources["DataGridRowSelectedHoveredUnfocusedBackgroundColor"] = _rowSelectionColor;
            TagFileGrid.Resources["DataGridRowSelectedHoveredUnfocusedBackgroundOpacity"] = 1.0;

            // 复选框交互哨兵：点选择框时 DataGrid 会顺着 Tapped 改焦点行（见下方注释），
            // 这里竖起/放倒守卫，保证多选不被那次连带选中冲掉。
            // 本方法 ReapplyAccent 也会调（换肤重刷资源），事件只许挂一次。
            if (!_rowCheckGuardHooked)
            {
                TagFileGrid.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(TagFileGrid_PointerPressed_WatchRowCheck),
                    true);
                _rowCheckGuardHooked = true;
            }

            RefreshGridRowsChrome();
        }

        /// <summary>
        /// 兜底强调色（纯色值，**不是画刷**）。
        ///
        /// ⚠ 这里绝不能写 <c>static readonly Brush X = new SolidColorBrush(...)</c>：
        /// 静态字段初始化跑在「第一次触碰本类型」的那条线程上，而本类型的首次触碰
        /// 很可能发生在后台线程（AddFolderButton_Click → Task.Run → EnumerateAudioFiles
        /// 里读 TagAudioExtensions）。DependencyObject（含 SolidColorBrush）只能在 UI
        /// 线程创建，后台线程创建会抛异常 → 类型初始化失败 → CLR 把这个类型永久标记
        /// 为不可用 → 之后连 UI 线程上的「添加文件 / 添加文件夹」都跟着
        /// TypeInitializationException（2026-10-14 用户实测「读不出文件夹」的真根因：
        /// lib 日志里连着 6 条 TagPanelControl 类型初始化失败）。
        /// 所以静态成员只留纯值，画刷在用到的时候 new（调用点全在 UI 线程）。
        /// </summary>
        private static readonly Windows.UI.Color TagFallbackAccentColor =
            Windows.UI.Color.FromArgb(255, 0, 120, 212);

        /// <summary>取注入的强调色刷（拿不到 / 抛异常时退回默认蓝）。</summary>
        private Brush ResolveTagAccentBrush()
        {
            try
            {
                Brush? accent = _accentProvider?.Invoke();
                if (accent != null)
                {
                    return accent;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ResolveTagAccentBrush", caught);
            }
            return new SolidColorBrush(TagFallbackAccentColor);
        }

        /// <summary>
        /// 给一棵子树里的文字统一上前景色。选中态要把行内每个 TextBlock/TextBox
        /// 都显式设值——有些默认样式自带前景，单靠属性继承压不住（本地值 > 样式）。
        /// </summary>
        private static void ApplyTagRowForeground(DependencyObject root, Brush foreground)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.Foreground = foreground;
                }
                else if (child is Control control)
                {
                    control.Foreground = foreground;
                }

                ApplyTagRowForeground(child, foreground);
            }
        }

        /// <summary>收回上面的本地值（恢复主题继承）。</summary>
        private static void ClearTagRowForeground(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.ClearValue(TextBlock.ForegroundProperty);
                }
                else if (child is Control control)
                {
                    control.ClearValue(Control.ForegroundProperty);
                }

                ClearTagRowForeground(child);
            }
        }

        /// <summary>注入强调色取纯色值（取不到 / 不是纯色刷退回默认蓝）。</summary>
        private Windows.UI.Color ResolveTagAccentColor()
        {
            try
            {
                if (ResolveTagAccentBrush() is SolidColorBrush solid)
                {
                    return solid.Color;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ResolveTagAccentColor", caught);
            }
            return TagFallbackAccentColor;
        }

        /// <summary>行实现（新行 / 回收行复用）时按当前选中态盖章选中外观。</summary>
        private void TagFileGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            ApplyGridRowChrome(e.Row);
        }

        /// <summary>行被回收（滚出视口 / 复用时）收回前景，防止残留到下一条数据。</summary>
        /// <remarks>
        /// ⚠ 不要在这里（或任何地方）碰 BackgroundRectangle 的 Fill——
        /// 选中态动画的目标就是 (Shape.Fill).(SolidColorBrush.Color)，替换实例或
        /// ClearValue 都会让动画 Begin 时解析失败，抛
        /// "Cannot resolve TargetProperty" COMException（0x80040154，2026-10-16 实测崩）。
        /// </remarks>
        private void TagFileGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.ClearValue(Control.ForegroundProperty);

            // 勾选底色也一起收掉：DataGridCell 会随行复用，留在单元格上的本地 Background
            // 如果不清，行被回收去显示别的数据时会把底色带过去（表现为「没勾选的行也有底色」
            // 或「勾选框只勾了一首、底色却铺了很多行」）。真正勾选的行由 LoadingRow /
            // RefreshGridRowsChrome 重刷回来，这里只管别残留。
            // ⚠️ 清的是 cell.Background（无动画目标，安全），绝不碰 BackgroundRectangle.Fill。
            var cells = new List<DataGridCell>();
            CollectDescendants(e.Row, cells);
            foreach (DataGridCell cell in cells)
            {
                cell.ClearValue(Control.BackgroundProperty);
            }
        }

        /// <summary>
        /// 选中外观（承接 2026-10-13 用户实测「选中背景就是一个矩形，一点不好看」，
        /// 2026-10-16 第三轮定位到真根因并定稿）：
        ///
        /// 行模板的选中底是一块 <c>Rectangle x:Name="BackgroundRectangle"</c>，Fill 绑主题
        /// 资源、选中态由 VisualState 动画写 (Fill).(SolidColorBrush.Color)——**不碰
        /// row.Background**。所以前两版在行上设 Background 全是无效功，「背景还是没变」。
        /// 现在两件事各就各位：
        ///   ① 焦点行（DataGrid 自己选中的那行）：颜色由 SetupDataGridChrome 覆盖的四个
        ///      DataGridRowSelected* 主题资源决定（主面板同款半透明灰），动画自己会上；
        ///   ② 圆角：Rectangle 自带 RadiusX/RadiusY，设一次即稳（动画不动这两个属性），
        ///      不用整套重抄 DataGridRow 模板。
        /// 左侧竖条按用户要求不加；文字保持原色（模板动画不动前景，本地值就够）。
        ///
        /// ⚠️ 非焦点勾选行的底色（2026-10-16 第二轮补上，用户反馈「只有最新一首变」）：
        /// **不能**给 BackgroundRectangle 设本地 Fill（每行一支新画刷）或 ClearValue(Fill)，
        /// 那会让选中态动画 Begin 时解析目标属性链失败，直接抛 COMException 0x80040154
        /// 「没有检测到已安装的组件 / Cannot resolve TargetProperty
        /// (Shape.Fill).(SolidColorBrush.Color)」，程序崩（2026-10-16 实测）。
        /// 根因：动画按属性链定位到的是**模板原始那个画刷实例**，替换/清除后目标丢失。
        /// 改用**每个 DataGridCell 的 Background**：模板里 CellRoot.Background 绑的是
        /// {TemplateBinding Background}，没有任何动画碰它 → 设了安全、也不会崩。
        /// </summary>
        private void ApplyGridRowChrome(DataGridRow row)
        {
            Rectangle? background = FindRowBackgroundRectangle(row);
            if (background != null)
            {
                background.RadiusX = RowSelectionCornerRadius;
                background.RadiusY = RowSelectionCornerRadius;
            }

            // 勾选行底色（2026-10-16 用户反馈「被选中的歌曲背景都要变，现在只有最新一首变」）。
            // 为什么不用 BackgroundRectangle.Fill：那块矩形的 Fill 是选中态动画的目标属性链
            //   (Shape.Fill).(SolidColorBrush.Color)，运行时替换实例 / ClearValue 会让动画 Begin
            //   找不到目标，直接抛 COMException 0x80040154（已实测崩过，见下方备注）。
            // 改用每个 DataGridCell 的 Background —— 模板里 CellRoot.Background 是
            //   {TemplateBinding Background}，**没有任何动画碰它**，设了最稳。
            // 单元格彼此紧贴（只有竖网格线在内层），整行刷完就是一条连续色带。
            // 焦点行的原生选中底色在下面一层，被 cell 底色盖住；两者同色，视觉无差别。
            bool isChecked = row.DataContext is TagFileItem checkedItem && _checkedSet.Contains(checkedItem);
            Brush? fill = isChecked ? _checkedRowBrush : null;
            var cells = new List<DataGridCell>();
            CollectDescendants(row, cells);
            foreach (DataGridCell cell in cells)
            {
                if (fill != null)
                {
                    cell.Background = fill;
                }
                else
                {
                    // 取消勾选 / 行被回收复用到未勾选的数据：收掉本地值，恢复模板的透明底色。
                    cell.ClearValue(Control.BackgroundProperty);
                }
            }
        }

        /// <summary>
        /// 深度优先收集子树里所有 <typeparamref name="T"/> 类型元素（顺序 = 可视树顺序）。
        /// 用可视树遍历而不是 DataGridCellsPresenter.Children：toolkit 的 presenter 是
        /// ItemsControl 派生，有没有 Children 属性依版本而定，不能依赖（踩过类型不存在编译不过的坑）。
        /// </summary>
        private static void CollectDescendants<T>(DependencyObject root, List<T> sink) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    sink.Add(match);
                }

                CollectDescendants(child, sink);
            }
        }

        /// <summary>
        /// 找行模板里的选中背景矩形。模板结构固定两层：
        /// DataGridRow → DataGridFrozenGrid → Rectangle(x:Name="BackgroundRectangle")。
        /// 找不到（模板变了）就返回 null，调用方静默跳过圆角/上色，不影响其它逻辑。
        /// </summary>
        private static Rectangle? FindRowBackgroundRectangle(DataGridRow row)
        {
            int count = VisualTreeHelper.GetChildrenCount(row);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(row, i) is not Panel panel)
                {
                    continue;
                }

                foreach (UIElement child in panel.Children)
                {
                    if (child is Rectangle rect && string.Equals(rect.Name, "BackgroundRectangle", StringComparison.Ordinal))
                    {
                        return rect;
                    }
                }

                break;
            }

            return null;
        }

        /// <summary>刷新所有已实现（可见）行的选中外观。只遍历行呈送器子节点，不做全量。</summary>
        private void RefreshGridRowsChrome()
        {
            if (!_panelReady)
            {
                return;
            }

            if (FindDescendant<DataGridRowsPresenter>(TagFileGrid) is not DataGridRowsPresenter presenter)
            {
                return;
            }

            foreach (UIElement child in presenter.Children)
            {
                if (child is DataGridRow row)
                {
                    ApplyGridRowChrome(row);
                }
            }
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

        // =====================================================================
        // 页 5：歌词下载
        // =====================================================================

        private string? _lyricTargetPath;

        private readonly List<LyricCandidate> _lyricCandidates = new();

        private readonly Dictionary<string, string> _lyricTextCache = new(StringComparer.Ordinal);

        private readonly ObservableCollection<LyricBatchItem> _lyricBatchItems = new();

        private CancellationTokenSource? _lyricBatchCts;

        private bool _lyricUiGuard;

        private void RefreshLyricEditPageUi()
        {
            _lyricUiGuard = true;
            try
            {
                AppSettingsState s = AppSettingsStore.Load();

                LyricSourceCombo.SelectedIndex = string.Equals(s.LyricPageSource, OnlineLyricDownload.SourceQQ, StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;

                TagFileItem? current = CheckedItems.FirstOrDefault();
                _lyricTargetPath = current?.FilePath;
                if (current == null || string.IsNullOrEmpty(current.FilePath))
                {
                    LyricCurrentFileText.Text = "还没选文件——先去右边文件列表点一首歌（单击即选中整行），这里会按它的标签自动填好搜索框。";
                    LyricTitleBox.Text = string.Empty;
                    LyricArtistBox.Text = string.Empty;
                }
                else
                {
                    string name = System.IO.Path.GetFileName(current.FilePath);
                    string title = string.IsNullOrWhiteSpace(current.Title)
                        ? System.IO.Path.GetFileNameWithoutExtension(current.FilePath)
                        : current.Title;
                    string artist = string.IsNullOrWhiteSpace(current.Artist) || current.Artist == "未知艺术家"
                        ? string.Empty
                        : current.Artist;
                    LyricCurrentFileText.Text = $"{name}    标题：{title}    艺术家：{(artist.Length == 0 ? "（没写）" : artist)}";
                    LyricTitleBox.Text = title;
                    LyricArtistBox.Text = artist;
                }

                LyricTargetSidecar.IsChecked = string.Equals(s.LyricPageWriteTarget, "Sidecar", StringComparison.OrdinalIgnoreCase);
                LyricTargetTagPlain.IsChecked = string.Equals(s.LyricPageWriteTarget, "TagPlain", StringComparison.OrdinalIgnoreCase);
                LyricTargetTagTs.IsChecked = string.Equals(s.LyricPageWriteTarget, "TagTimestamped", StringComparison.OrdinalIgnoreCase);
                LyricTargetBoth.IsChecked = string.Equals(s.LyricPageWriteTarget, "Both", StringComparison.OrdinalIgnoreCase);
                if (LyricTargetSidecar.IsChecked != true && LyricTargetTagPlain.IsChecked != true
                    && LyricTargetTagTs.IsChecked != true && LyricTargetBoth.IsChecked != true)
                {
                    LyricTargetSidecar.IsChecked = true;
                }

                bool hasLyricFolder = !string.IsNullOrWhiteSpace(s.LyricFolder);
                LyricSaveLyricFolder.IsEnabled = hasLyricFolder;
                if (s.SaveLyricToSongFolder || !hasLyricFolder)
                {
                    LyricSaveSongFolder.IsChecked = true;
                }
                else
                {
                    LyricSaveLyricFolder.IsChecked = true;
                }

                LyricTranslateCheck.IsChecked = s.LyricPageIncludeTranslation;
            }
            finally
            {
                _lyricUiGuard = false;
            }
        }

        private void RefreshLyricBatchPageUi()
        {
            _lyricUiGuard = true;
            try
            {
                AppSettingsState s = AppSettingsStore.Load();

                LyricBatchSourceCombo.SelectedIndex = string.Equals(s.LyricBatchPageSource, OnlineLyricDownload.SourceQQ, StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;

                LyricBatchSkipExistCheck.IsChecked = true;
                LyricBatchTranslateCheck.IsChecked = s.LyricPageIncludeTranslation;

                bool hasLyricFolder = !string.IsNullOrWhiteSpace(s.LyricFolder);
                LyricBatchSaveLyricFolder.IsEnabled = hasLyricFolder;
                if (s.SaveLyricToSongFolder || !hasLyricFolder)
                {
                    LyricBatchSaveSongFolder.IsChecked = true;
                }
                else
                {
                    LyricBatchSaveLyricFolder.IsChecked = true;
                }

                UpdateLyricBatchScopeText();
            }
            finally
            {
                _lyricUiGuard = false;
            }
        }

        private void SetLyricPageStatus(string text)
        {
            LyricPageStatusText.Text = text;
        }

        private void SetLyricBatchStatus(string text)
        {
            LyricBatchStatusText.Text = text;
        }

        private async void LyricSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchLyricsAsync().ConfigureAwait(true);
        }

        private async Task SearchLyricsAsync()
        {
            string title = LyricTitleBox.Text?.Trim() ?? string.Empty;
            string artist = LyricArtistBox.Text?.Trim() ?? string.Empty;
            string keyword = (artist + " " + title).Trim();
            if (keyword.Length == 0)
            {
                SetLyricPageStatus("标题和艺术家都空着——先在上面填一个（只填标题也能搜）。");
                return;
            }

            string source = (LyricSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OnlineLyricDownload.SourceLrclib;

            SetLyricPageStatus($"正在用 {(source == OnlineLyricDownload.SourceQQ ? "QQ 音乐" : "LRClib")} 搜索「{keyword}」…");
            LyricSearchButton.IsEnabled = false;
            try
            {
                IReadOnlyList<LyricCandidate> hits =
                    await OnlineLyricDownload.SearchAsync(source, keyword).ConfigureAwait(true);

                _lyricCandidates.Clear();
                _lyricCandidates.AddRange(hits);
                for (int i = 0; i < _lyricCandidates.Count; i++)
                {
                    _lyricCandidates[i].Index = i + 1;
                }

                _lyricTextCache.Clear();
                _lyricUiGuard = true;
                LyricResultList.ItemsSource = null;
                LyricResultList.ItemsSource = _lyricCandidates;
                LyricResultList.SelectedIndex = -1;
                _lyricUiGuard = false;
                LyricPreviewBox.Text = string.Empty;

                if (_lyricCandidates.Count == 0)
                {
                    SetLyricPageStatus("一个候选都没有。换个关键词试试，或在上面换另一个来源（LRClib / QQ 音乐）。");
                    return;
                }

                TagFileItem? current = CheckedItems.FirstOrDefault();
                string fileName = current != null ? System.IO.Path.GetFileNameWithoutExtension(current.FilePath) : string.Empty;
                string album = current != null && current.Album != "未知专辑" ? current.Album : string.Empty;
                int best = OnlineLyricDownload.PickBestMatch(_lyricCandidates, title, artist, album, fileName);

                _lyricUiGuard = true;
                LyricResultList.SelectedIndex = best >= 0 ? best : -1;
                _lyricUiGuard = false;
                if (best >= 0)
                {
                    LyricResultList.ScrollIntoView(_lyricCandidates[best]);
                }

                SetLyricPageStatus(best >= 0
                    ? $"找到 {_lyricCandidates.Count} 个候选，已自动选中第 {best + 1} 个（算出来最相似的一个）。点别的行可以改选。"
                    : $"找到 {_lyricCandidates.Count} 个候选，但没有足够相似的——需要你手动点一个（看标题/艺术家/时长对不上的就换）。");
            }
            finally
            {
                LyricSearchButton.IsEnabled = true;
            }
        }

        private async void LyricResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lyricUiGuard)
            {
                return;
            }

            await ShowLyricPreviewAsync().ConfigureAwait(true);
        }

        private async Task ShowLyricPreviewAsync()
        {
            if (LyricResultList.SelectedItem is not LyricCandidate candidate)
            {
                LyricPreviewBox.Text = string.Empty;
                return;
            }

            bool wantTrans = LyricTranslateCheck.IsChecked == true;
            string key = candidate.Source + "|" + candidate.SongId + "|" + wantTrans;
            if (_lyricTextCache.TryGetValue(key, out string? cached))
            {
                LyricPreviewBox.Text = cached;
                SetLyricPageStatus(cached.Length == 0
                    ? "这个候选没有歌词，换一个试试。"
                    : $"已显示歌词（{cached.Length} 字）。确认无误就点下面「下载并保存」。");
                return;
            }

            SetLyricPageStatus("正在取歌词…");
            string lyric = await OnlineLyricDownload.FetchLyricAsync(candidate, wantTrans).ConfigureAwait(true);
            _lyricTextCache[key] = lyric;

            if (LyricResultList.SelectedItem is LyricCandidate still && ReferenceEquals(still, candidate))
            {
                LyricPreviewBox.Text = lyric;
                SetLyricPageStatus(lyric.Length == 0
                    ? "这个候选没有歌词，换一个试试。"
                    : $"已显示歌词（{lyric.Length} 字）。确认无误就点下面「下载并保存」。");
            }
        }

        private async void LyricDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            await DownloadLyricAsync().ConfigureAwait(true);
        }

        private async Task DownloadLyricAsync()
        {
            if (string.IsNullOrEmpty(_lyricTargetPath) || !File.Exists(_lyricTargetPath))
            {
                SetLyricPageStatus("还没有选中文件——先去右边文件列表点一首歌。");
                return;
            }

            if (LyricResultList.SelectedItem is not LyricCandidate candidate)
            {
                SetLyricPageStatus("先在上面的搜索结果里点一个候选。");
                return;
            }

            AppSettingsState s = AppSettingsStore.Load();
            bool wantTrans = LyricTranslateCheck.IsChecked == true;

            string key = candidate.Source + "|" + candidate.SongId + "|" + wantTrans;
            if (!_lyricTextCache.TryGetValue(key, out string? lyric) || string.IsNullOrEmpty(lyric))
            {
                SetLyricPageStatus("正在取歌词…");
                lyric = await OnlineLyricDownload.FetchLyricAsync(candidate, wantTrans).ConfigureAwait(true);
                _lyricTextCache[key] = lyric;
            }

            if (string.IsNullOrWhiteSpace(lyric))
            {
                SetLyricPageStatus("这个候选没有歌词，换一个试试。");
                return;
            }

            LyricWriteTarget target = LyricTargetTagPlain.IsChecked == true ? LyricWriteTarget.TagPlain
                : LyricTargetTagTs.IsChecked == true ? LyricWriteTarget.TagTimestamped
                : LyricTargetBoth.IsChecked == true ? LyricWriteTarget.Both
                : LyricWriteTarget.Sidecar;
            string targetName = target switch
            {
                LyricWriteTarget.TagPlain => "TagPlain",
                LyricWriteTarget.TagTimestamped => "TagTimestamped",
                LyricWriteTarget.Both => "Both",
                _ => "Sidecar"
            };

            bool saveToSongFolder = LyricSaveSongFolder.IsChecked == true;
            string lyricFolder = s.LyricFolder ?? string.Empty;
            if (!saveToSongFolder && string.IsNullOrWhiteSpace(lyricFolder))
            {
                SetLyricPageStatus("选了「歌词文件夹」但设置里还没配——去 设置→歌词 配一个文件夹，或改选「歌曲所在目录」。");
                return;
            }

            if (!string.Equals(s.LyricPageWriteTarget, targetName, StringComparison.OrdinalIgnoreCase)
                || s.LyricPageIncludeTranslation != wantTrans
                || s.SaveLyricToSongFolder != saveToSongFolder)
            {
                s.LyricPageWriteTarget = targetName;
                s.LyricPageIncludeTranslation = wantTrans;
                s.SaveLyricToSongFolder = saveToSongFolder;
                AppSettingsStore.Save(s);
            }

            LyricDownloadButton.IsEnabled = false;
            try
            {
                (bool ok, string message) = await OnlineLyricDownload.WriteLyricAsync(
                    _lyricTargetPath, lyric, target, saveToSongFolder, lyricFolder).ConfigureAwait(true);
                SetLyricPageStatus(message);

                if (ok && target is not LyricWriteTarget.Sidecar)
                {
                    TagsSaved?.Invoke(_lyricTargetPath);
                    TagFileItem? row = CheckedItems
                        .FirstOrDefault(i => string.Equals(i.FilePath, _lyricTargetPath, StringComparison.OrdinalIgnoreCase));
                    if (row != null)
                    {
                        await RefreshSavedRowsAsync(new List<TagFileItem> { row }).ConfigureAwait(true);
                    }
                }
            }
            finally
            {
                LyricDownloadButton.IsEnabled = true;
            }
        }

        private void LyricOnlineViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (LyricResultList.SelectedItem is not LyricCandidate candidate || string.IsNullOrEmpty(candidate.OnlineUrl))
            {
                SetLyricPageStatus("这个来源没有能打开的在线页面（LRClib 是接口库，没有单曲网页；QQ 音乐才可以）。");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(candidate.OnlineUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.LyricOnlineViewButton_Click", caught);
                SetLyricPageStatus("打不开浏览器：" + caught.Message);
            }
        }

        private void LyricSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lyricUiGuard)
            {
                return;
            }

            string source = (LyricSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OnlineLyricDownload.SourceLrclib;
            AppSettingsState s = AppSettingsStore.Load();
            if (!string.Equals(s.LyricPageSource, source, StringComparison.OrdinalIgnoreCase))
            {
                s.LyricPageSource = source;
                AppSettingsStore.Save(s);
            }
        }

        private void UpdateLyricBatchScopeText()
        {
            (List<TagFileItem> targets, bool wholeList) = BatchTargets();
            LyricBatchScopeText.Text = wholeList
                ? $"作用范围：整个列表（{targets.Count} 首）。在右侧选中文件就是只下选中的。"
                : $"作用范围：当前选中的 {targets.Count} 首。";
        }

        private async void LyricBatchStartButton_Click(object sender, RoutedEventArgs e)
        {
            await RunLyricBatchAsync().ConfigureAwait(true);
        }

        private void LyricBatchStopButton_Click(object sender, RoutedEventArgs e)
        {
            _lyricBatchCts?.Cancel();
            SetLyricBatchStatus("正在停止……当前这首结束后就停。");
        }

        private void LyricBatchSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lyricUiGuard)
            {
                return;
            }

            string source = (LyricBatchSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OnlineLyricDownload.SourceLrclib;
            AppSettingsState s = AppSettingsStore.Load();
            if (!string.Equals(s.LyricBatchPageSource, source, StringComparison.OrdinalIgnoreCase))
            {
                s.LyricBatchPageSource = source;
                AppSettingsStore.Save(s);
            }
        }

        private async Task RunLyricBatchAsync()
        {
            (List<TagFileItem> targets, _) = BatchTargets();
            if (targets.Count == 0)
            {
                SetLyricBatchStatus("列表是空的——先在右边添加文件。");
                return;
            }

            AppSettingsState s = AppSettingsStore.Load();
            bool saveToSongFolder = LyricBatchSaveSongFolder.IsChecked == true;
            string lyricFolder = s.LyricFolder ?? string.Empty;
            if (!saveToSongFolder && string.IsNullOrWhiteSpace(lyricFolder))
            {
                SetLyricBatchStatus("选了「歌词文件夹」但设置里还没配——去 设置→歌词 配一个文件夹，或改选「歌曲所在目录」。");
                return;
            }

            string source = (LyricBatchSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OnlineLyricDownload.SourceLrclib;

            List<LyricBatchSong> songs = new();
            foreach (TagFileItem item in targets)
            {
                string fileName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                string title = string.IsNullOrWhiteSpace(item.Title) ? fileName : item.Title;
                string artist = string.IsNullOrWhiteSpace(item.Artist) || item.Artist == "未知艺术家"
                    ? string.Empty
                    : item.Artist;
                string album = string.IsNullOrWhiteSpace(item.Album) || item.Album == "未知专辑"
                    ? string.Empty
                    : item.Album;
                songs.Add(new LyricBatchSong(item.FilePath, title, artist, album, fileName));
            }

            LyricBatchList.ItemsSource = null;
            _lyricBatchItems.Clear();
            for (int i = 0; i < songs.Count; i++)
            {
                _lyricBatchItems.Add(new LyricBatchItem(i + 1, songs[i].Title, songs[i].Artist));
            }

            LyricBatchList.ItemsSource = _lyricBatchItems;
            LyricBatchProgressBar.Value = 0;
            UpdateLyricBatchScopeText();

            var options = new LyricBatchOptions(
                source,
                LyricBatchTranslateCheck.IsChecked == true,
                LyricBatchSkipExistCheck.IsChecked == true,
                saveToSongFolder,
                lyricFolder);

            _lyricBatchCts = new CancellationTokenSource();
            LyricBatchStartButton.IsEnabled = false;
            LyricBatchStopButton.IsEnabled = true;

            var progress = new Progress<LyricBatchProgress>(p =>
            {
                LyricBatchProgressBar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                if (p.Index >= 0 && p.Index < _lyricBatchItems.Count)
                {
                    LyricBatchItem row = _lyricBatchItems[p.Index];
                    row.Status = p.Status;
                    LyricBatchList.ScrollIntoView(row);
                }
            });

            try
            {
                LyricBatchSummary summary = await OnlineLyricDownload.BatchDownloadAsync(
                    songs, options, progress, _lyricBatchCts.Token).ConfigureAwait(true);

                LyricBatchProgressBar.Value = 100;
                int processed = summary.Saved + summary.Skipped + summary.NoLyric + summary.NoMatch + summary.Failed;
                SetLyricBatchStatus(summary.Canceled
                    ? $"已停止。已处理 {processed}/{summary.Total}：保存 {summary.Saved}，跳过 {summary.Skipped}，没歌词 {summary.NoLyric}，没匹配 {summary.NoMatch}，失败 {summary.Failed}。"
                    : $"批量完成，共 {summary.Total} 首：保存 {summary.Saved}，跳过 {summary.Skipped}，没歌词 {summary.NoLyric}，没匹配 {summary.NoMatch}，失败 {summary.Failed}（失败原因见日志）。");
            }
            finally
            {
                _lyricBatchCts.Dispose();
                _lyricBatchCts = null;
                LyricBatchStartButton.IsEnabled = true;
                LyricBatchStopButton.IsEnabled = false;
            }
        }

        // =====================================================================
        // 页 4：在线标签（MusicBrainz + iTunes + Cover Art Archive + LRClib）
        // 搜索 → 点结果看详情（封面/字段/歌词预览）→ 勾要写的字段 → 写入所选文件。
        // 写入前快照、失败整体回滚（与快速编辑页同一套安全底线）。
        // =====================================================================

        private readonly List<OnlineTagCandidate> _onlineTagCandidates = new();

        private readonly Dictionary<string, string> _onlineTagLyricCache = new(StringComparer.Ordinal);

        private byte[]? _onlineTagCoverBytes;

        private bool _onlineTagGuard;

        private void RefreshOnlineTagPageUi()
        {
            TagFileItem? current = CheckedItems.FirstOrDefault();
            if (current == null || string.IsNullOrEmpty(current.FilePath))
            {
                OnlineTagFileText.Text = "还没选文件——先去右边文件列表点一首歌（单击即选中整行），搜索框会按它的标签自动填好。";
                OnlineTagTitleBox.Text = string.Empty;
                OnlineTagArtistBox.Text = string.Empty;
                OnlineTagAlbumBox.Text = string.Empty;
            }
            else
            {
                string name = System.IO.Path.GetFileName(current.FilePath);
                string title = string.IsNullOrWhiteSpace(current.Title)
                    ? System.IO.Path.GetFileNameWithoutExtension(current.FilePath)
                    : current.Title;
                string artist = string.IsNullOrWhiteSpace(current.Artist) || current.Artist == "未知艺术家"
                    ? string.Empty
                    : current.Artist;
                string album = string.IsNullOrWhiteSpace(current.Album) || current.Album == "未知专辑"
                    ? string.Empty
                    : current.Album;
                OnlineTagFileText.Text = $"{name}    标题：{title}    艺术家：{(artist.Length == 0 ? "（没写）" : artist)}";
                OnlineTagTitleBox.Text = title;
                OnlineTagArtistBox.Text = artist;
                OnlineTagAlbumBox.Text = album;
            }

            UpdateOnlineTagScopeText();
        }

        private void UpdateOnlineTagScopeText()
        {
            (List<TagFileItem> targets, bool wholeList) = BatchTargets();
            OnlineTagScopeText.Text = wholeList
                ? $"作用范围：整个列表（{targets.Count} 个）。在右侧选中文件就是只写选中的。"
                : $"作用范围：当前选中的 {targets.Count} 个。";
        }

        private void SetOnlineTagStatus(string text)
        {
            OnlineTagStatusText.Text = text;
        }

        private async void OnlineTagSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchOnlineTagsAsync().ConfigureAwait(true);
        }

        private async Task SearchOnlineTagsAsync()
        {
            string title = (OnlineTagTitleBox.Text ?? string.Empty).Trim();
            string artist = (OnlineTagArtistBox.Text ?? string.Empty).Trim();
            string album = (OnlineTagAlbumBox.Text ?? string.Empty).Trim();
            if (title.Length == 0 && artist.Length == 0 && album.Length == 0)
            {
                SetOnlineTagStatus("标题、艺术家、专辑至少填一个。");
                return;
            }

            string source = (OnlineTagSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? OnlineTagFill.SourceBoth;

            SetOnlineTagStatus("正在搜索…");
            OnlineTagSearchButton.IsEnabled = false;
            try
            {
                IReadOnlyList<OnlineTagCandidate> hits =
                    await OnlineTagFill.SearchAsync(source, title, artist, album).ConfigureAwait(true);
                string? keyNote = OnlineTagFill.CheckKeyAvailability(source);

                _onlineTagCandidates.Clear();
                _onlineTagCandidates.AddRange(hits);
                _onlineTagLyricCache.Clear();
                _onlineTagCoverBytes = null;

                _onlineTagGuard = true;
                OnlineTagResultList.ItemsSource = null;
                OnlineTagResultList.ItemsSource = _onlineTagCandidates;
                OnlineTagResultList.SelectedIndex = -1;
                _onlineTagGuard = false;

                OnlineTagCoverImage.Source = null;
                OnlineTagDetailText.Text = string.Empty;
                OnlineTagLyricText.Text = string.Empty;
                OnlineTagSourceCredit.Visibility = Visibility.Collapsed;

                if (_onlineTagCandidates.Count == 0)
                {
                    SetOnlineTagStatus("一个结果都没有。换个关键词试试，或在上面的来源里只选一边（MusicBrainz / iTunes）。"
                        + (keyNote != null ? " " + keyNote : string.Empty));
                    return;
                }

                int best = OnlineTagFill.PickBestMatch(_onlineTagCandidates, title, artist, album, string.Empty);

                _onlineTagGuard = true;
                OnlineTagResultList.SelectedIndex = best >= 0 ? best : -1;
                _onlineTagGuard = false;

                if (best >= 0)
                {
                    OnlineTagResultList.ScrollIntoView(_onlineTagCandidates[best]);
                    await ShowOnlineTagDetailAsync().ConfigureAwait(true);
                    if (keyNote != null)
                    {
                        SetOnlineTagStatus("详情已加载。（" + keyNote + "）");
                    }
                }
                else
                {
                    SetOnlineTagStatus($"找到 {_onlineTagCandidates.Count} 个结果，但没有足够相似的——需要你手动点一个（看标题/艺术家/专辑对不上的就换）。"
                        + (keyNote != null ? " " + keyNote : string.Empty));
                }
            }
            finally
            {
                OnlineTagSearchButton.IsEnabled = true;
            }
        }

        private async void OnlineTagResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_onlineTagGuard)
            {
                return;
            }

            await ShowOnlineTagDetailAsync().ConfigureAwait(true);
        }

        private async Task ShowOnlineTagDetailAsync()
        {
            if (OnlineTagResultList.SelectedItem is not OnlineTagCandidate candidate)
            {
                OnlineTagCoverImage.Source = null;
                OnlineTagDetailText.Text = string.Empty;
                OnlineTagLyricText.Text = string.Empty;
                OnlineTagSourceCredit.Visibility = Visibility.Collapsed;
                return;
            }

            TagFileItem? current = CheckedItems.FirstOrDefault();
            string localTitle = current != null
                ? (string.IsNullOrWhiteSpace(current.Title)
                    ? System.IO.Path.GetFileNameWithoutExtension(current.FilePath)
                    : current.Title)
                : string.Empty;

            SetOnlineTagStatus("正在加载详情…");
            OnlineTagDetailText.Text = BuildOnlineTagDetailText(candidate);
            OnlineTagLyricText.Text = string.Empty;
            ApplyOnlineTagSourceCredit(candidate);
            OnlineTagCoverImage.Source = null;
            _onlineTagCoverBytes = null;

            OnlineTagCandidate? loaded = string.Equals(candidate.Source, OnlineTagFill.SourceDiscogs, StringComparison.OrdinalIgnoreCase)
                ? await OnlineTagFill.LoadDiscogsDetailAsync(candidate).ConfigureAwait(true)
                : await OnlineTagFill.LoadMusicBrainzDetailAsync(candidate, localTitle).ConfigureAwait(true);
            if (loaded == null || !ReferenceEquals(OnlineTagResultList.SelectedItem, candidate))
            {
                return;
            }

            OnlineTagDetailText.Text = BuildOnlineTagDetailText(candidate);

            if (!string.IsNullOrWhiteSpace(candidate.CoverUrl))
            {
                byte[]? cover = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl).ConfigureAwait(true);
                if (cover is { Length: > 0 } && ReferenceEquals(OnlineTagResultList.SelectedItem, candidate))
                {
                    _onlineTagCoverBytes = cover;
                    ApplyCoverToOnlineTagImage(cover);
                }
            }
            else if (string.Equals(candidate.Source, OnlineTagFill.SourceMusicBrainz, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.Id))
            {
                // CAA 没封面时，用 fanart.tv 按 MBID 取高清封面（需 Key）
                string? fanart = await OnlineTagFill
                    .FetchFanartCoverAsync(candidate.Id, false).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(fanart)
                    && ReferenceEquals(OnlineTagResultList.SelectedItem, candidate))
                {
                    byte[]? fcover = await OnlineTagFill.DownloadCoverBytesAsync(fanart).ConfigureAwait(true);
                    if (fcover is { Length: > 0 } && ReferenceEquals(OnlineTagResultList.SelectedItem, candidate))
                    {
                        _onlineTagCoverBytes = fcover;
                        ApplyCoverToOnlineTagImage(fcover);
                    }
                }
            }

            string lyricKey = candidate.Source + "|" + candidate.Id;
            if (!_onlineTagLyricCache.TryGetValue(lyricKey, out string? lyric))
            {
                double duration = current?.Duration.TotalSeconds ?? 0;
                lyric = await OnlineTagFill.FetchLyricAsync(
                    candidate.Artist, candidate.Title, candidate.Album, duration).ConfigureAwait(true);
                _onlineTagLyricCache[lyricKey] = lyric;
            }

            if (ReferenceEquals(OnlineTagResultList.SelectedItem, candidate))
            {
                OnlineTagLyricText.Text = lyric.Length == 0
                    ? "（LRClib 没找到这首歌的歌词）"
                    : lyric;
            }

            ApplyOnlineTagFieldDefaults(candidate, lyric);
            SetOnlineTagStatus("详情已加载。在下面勾要写入的字段，然后点「写入所选文件」。");
        }

        private static string BuildOnlineTagDetailText(OnlineTagCandidate c)
        {
            string TrackText()
            {
                string no = c.Track == 0 ? "-" : c.Track.ToString(CultureInfo.InvariantCulture);
                return c.TrackCount > 0 ? $"{no}/{c.TrackCount}" : no;
            }

            string DiscText()
            {
                string no = c.Disc == 0 ? "-" : c.Disc.ToString(CultureInfo.InvariantCulture);
                return c.DiscCount > 0 ? $"{no}/{c.DiscCount}" : no;
            }

            return string.Join('\n', new[]
            {
                $"标题：{c.Title}",
                $"艺术家：{(c.Artist.Length == 0 ? "-" : c.Artist)}",
                $"专辑：{(c.Album.Length == 0 ? "-" : c.Album)}",
                $"专辑艺术家：{(c.AlbumArtist.Length == 0 ? "-" : c.AlbumArtist)}",
                $"年份：{c.YearText}",
                $"曲目：{TrackText()}",
                $"碟片：{DiscText()}",
                $"流派：{(c.Genre.Length == 0 ? "-" : c.Genre)}",
                $"时长：{c.DurationText}",
                $"来源：{c.SourceLabel}"
            });
        }

        private void ApplyCoverToOnlineTagImage(byte[] bytes)
        {
            try
            {
                BitmapImage bmp = new();
                using MemoryStream ms = new(bytes);
                using var stream = ms.AsRandomAccessStream();
                bmp.SetSource(stream);
                OnlineTagCoverImage.Source = bmp;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagPanelControl.ApplyCoverToOnlineTagImage", caught);
                OnlineTagCoverImage.Source = null;
            }
        }

        /// <summary>详情区「数据旁」署名：按来源给出署名文字与指向该条数据的页面链接。
        /// Discogs ToS 要求「Data provided by Discogs」紧贴数据且链接须能传递 SEO 权重（不能用 nofollow，
        /// HyperlinkButton 走系统默认浏览器打开，天然满足）。拿不到精确页面时退回站点首页。</summary>
        private void ApplyOnlineTagSourceCredit(OnlineTagCandidate c)
        {
            string text;
            string url;
            if (string.Equals(c.Source, OnlineTagFill.SourceDiscogs, StringComparison.OrdinalIgnoreCase))
            {
                text = "Data provided by Discogs";
                url = string.IsNullOrWhiteSpace(c.Id)
                    ? "https://www.discogs.com"
                    : "https://www.discogs.com/release/" + Uri.EscapeDataString(c.Id);
            }
            else if (string.Equals(c.Source, OnlineTagFill.SourceLastFm, StringComparison.OrdinalIgnoreCase))
            {
                text = "Data provided by Last.fm";
                url = string.IsNullOrWhiteSpace(c.Artist)
                    ? "https://www.last.fm"
                    : "https://www.last.fm/music/" + Uri.EscapeDataString(c.Artist);
            }
            else if (string.Equals(c.Source, OnlineTagFill.SourceMusicBrainz, StringComparison.OrdinalIgnoreCase))
            {
                text = "Data provided by MusicBrainz";
                url = string.IsNullOrWhiteSpace(c.Id)
                    ? "https://musicbrainz.org"
                    : "https://musicbrainz.org/release/" + Uri.EscapeDataString(c.Id);
            }
            else if (string.Equals(c.Source, OnlineTagFill.SourceItunes, StringComparison.OrdinalIgnoreCase))
            {
                text = "Metadata from iTunes Search API";
                url = "https://music.apple.com";
            }
            else
            {
                OnlineTagSourceCredit.Visibility = Visibility.Collapsed;
                return;
            }

            OnlineTagSourceCredit.Content = text;
            OnlineTagSourceCredit.NavigateUri = new Uri(url);
            OnlineTagSourceCredit.Visibility = Visibility.Visible;
        }

        private void ApplyOnlineTagFieldDefaults(OnlineTagCandidate c, string lyric)
        {
            OnlineTagFieldTitle.IsChecked = c.Title.Length > 0;
            OnlineTagFieldArtist.IsChecked = c.Artist.Length > 0;
            OnlineTagFieldAlbum.IsChecked = c.Album.Length > 0;
            OnlineTagFieldAlbumArtist.IsChecked = c.AlbumArtist.Length > 0;
            OnlineTagFieldYear.IsChecked = c.Year != 0;
            OnlineTagFieldTrack.IsChecked = c.Track != 0 || c.TrackCount != 0;
            OnlineTagFieldDisc.IsChecked = c.Disc != 0 || c.DiscCount != 0;
            OnlineTagFieldGenre.IsChecked = c.Genre.Length > 0;
            OnlineTagFieldCover.IsChecked = !string.IsNullOrWhiteSpace(c.CoverUrl);
            OnlineTagFieldLyrics.IsChecked = lyric.Length > 0;
        }

        private async void OnlineTagWriteButton_Click(object sender, RoutedEventArgs e)
        {
            await WriteOnlineTagAsync().ConfigureAwait(true);
        }

        private async Task WriteOnlineTagAsync()
        {
            if (OnlineTagResultList.SelectedItem is not OnlineTagCandidate candidate)
            {
                SetOnlineTagStatus("先在上面点一个搜索结果。");
                return;
            }

            (List<TagFileItem> targets, bool wholeList) = BatchTargets();
            if (targets.Count == 0)
            {
                SetOnlineTagStatus("列表是空的——先在右边添加文件。");
                return;
            }

            bool wantTitle = OnlineTagFieldTitle.IsChecked == true;
            bool wantArtist = OnlineTagFieldArtist.IsChecked == true;
            bool wantAlbum = OnlineTagFieldAlbum.IsChecked == true;
            bool wantAlbumArtist = OnlineTagFieldAlbumArtist.IsChecked == true;
            bool wantYear = OnlineTagFieldYear.IsChecked == true;
            bool wantTrack = OnlineTagFieldTrack.IsChecked == true;
            bool wantDisc = OnlineTagFieldDisc.IsChecked == true;
            bool wantGenre = OnlineTagFieldGenre.IsChecked == true;
            bool wantCover = OnlineTagFieldCover.IsChecked == true;
            bool wantLyrics = OnlineTagFieldLyrics.IsChecked == true;
            if (!wantTitle && !wantArtist && !wantAlbum && !wantAlbumArtist && !wantYear
                && !wantTrack && !wantDisc && !wantGenre && !wantCover && !wantLyrics)
            {
                SetOnlineTagStatus("一个字段都没勾——先勾要写入的字段。");
                return;
            }

            string lyric = string.Empty;
            if (wantLyrics)
            {
                string lyricKey = candidate.Source + "|" + candidate.Id;
                if (!_onlineTagLyricCache.TryGetValue(lyricKey, out lyric))
                {
                    TagFileItem? current = CheckedItems.FirstOrDefault();
                    double duration = current?.Duration.TotalSeconds ?? 0;
                    lyric = await OnlineTagFill.FetchLyricAsync(
                        candidate.Artist, candidate.Title, candidate.Album, duration).ConfigureAwait(true);
                    _onlineTagLyricCache[lyricKey] = lyric;
                }

                if (lyric.Length == 0)
                {
                    SetOnlineTagStatus("LRClib 没找到这首歌的歌词——把「歌词」的勾去掉，或换一个搜索结果。");
                    return;
                }
            }

            byte[]? coverBytes = null;
            if (wantCover)
            {
                coverBytes = _onlineTagCoverBytes;
                if (coverBytes == null && !string.IsNullOrWhiteSpace(candidate.CoverUrl))
                {
                    coverBytes = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl).ConfigureAwait(true);
                    _onlineTagCoverBytes = coverBytes;
                }

                if (coverBytes == null)
                {
                    SetOnlineTagStatus("封面下载失败——把「封面」的勾去掉再写，或换个结果。");
                    return;
                }
            }

            SetOnlineTagStatus($"正在写入 {targets.Count} 个文件…");
            OnlineTagWriteButton.IsEnabled = false;

            List<(string Path, TagEditModel Before, byte[]? CoverBefore)> snapshots = new();
            List<string> written = new();
            List<string> failures = new();
            List<string> coverFailures = new();

            await Task.Run(() =>
            {
                foreach (TagFileItem item in targets)
                {
                    string path = item.FilePath;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        failures.Add(path);
                        continue;
                    }

                    TagEditModel before = TagEditorService.ReadTagRaw(path);
                    byte[]? coverBefore = TagEditorService.ReadCoverBytes(path);
                    snapshots.Add((path, before, coverBefore));

                    TagEditModel toWrite = CloneModel(before);
                    if (wantTitle && candidate.Title.Length > 0)
                    {
                        toWrite.Title = candidate.Title;
                        toWrite.MarkDirty(TagField.Title);
                    }

                    if (wantArtist && candidate.Artist.Length > 0)
                    {
                        toWrite.Artist = candidate.Artist;
                        toWrite.MarkDirty(TagField.Performers);
                    }

                    if (wantAlbum && candidate.Album.Length > 0)
                    {
                        toWrite.Album = candidate.Album;
                        toWrite.MarkDirty(TagField.Album);
                    }

                    if (wantAlbumArtist && candidate.AlbumArtist.Length > 0)
                    {
                        toWrite.AlbumArtist = candidate.AlbumArtist;
                        toWrite.MarkDirty(TagField.AlbumArtists);
                    }

                    if (wantYear && candidate.Year != 0)
                    {
                        toWrite.Year = candidate.Year;
                        toWrite.MarkDirty(TagField.Year);
                    }

                    if (wantTrack && (candidate.Track != 0 || candidate.TrackCount != 0))
                    {
                        if (candidate.Track != 0)
                        {
                            toWrite.Track = candidate.Track;
                            toWrite.MarkDirty(TagField.Track);
                        }

                        if (candidate.TrackCount != 0)
                        {
                            toWrite.TrackCount = candidate.TrackCount;
                            toWrite.MarkDirty(TagField.TrackCount);
                        }
                    }

                    if (wantDisc && (candidate.Disc != 0 || candidate.DiscCount != 0))
                    {
                        if (candidate.Disc != 0)
                        {
                            toWrite.Disc = candidate.Disc;
                            toWrite.MarkDirty(TagField.Disc);
                        }

                        if (candidate.DiscCount != 0)
                        {
                            toWrite.DiscCount = candidate.DiscCount;
                            toWrite.MarkDirty(TagField.DiscCount);
                        }
                    }

                    if (wantGenre && candidate.Genre.Length > 0)
                    {
                        toWrite.Genre = candidate.Genre;
                        toWrite.MarkDirty(TagField.Genres);
                    }

                    if (wantLyrics && lyric.Length > 0)
                    {
                        toWrite.Lyrics = lyric;
                        toWrite.MarkDirty(TagField.Lyrics);
                    }

                    if (toWrite.Dirty.Count == 0)
                    {
                        continue;
                    }

                    if (!TagEditorService.SaveTag(path, toWrite))
                    {
                        failures.Add(path);
                        continue;
                    }

                    written.Add(path);
                    if (coverBytes != null && !TagEditorService.SetCoverBytes(path, coverBytes))
                    {
                        coverFailures.Add(path);
                    }
                }
            });

            if (failures.Count > 0 || coverFailures.Count > 0)
            {
                int rolledBack = 0;
                await Task.Run(() =>
                {
                    foreach ((string path, TagEditModel before, byte[]? coverBefore) in snapshots)
                    {
                        if (!written.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        before.MarkAllDirty();
                        if (TagEditorService.SaveTag(path, before))
                        {
                            rolledBack++;
                        }

                        if (coverBytes != null)
                        {
                            if (coverBefore is { Length: > 0 })
                            {
                                TagEditorService.SetCoverBytes(path, coverBefore);
                            }
                            else
                            {
                                TagEditorService.ClearCover(path);
                            }
                        }
                    }
                });

                global::CelesteMusicPlayer.StartupLog.Write(
                    $"TagPanel: 在线标签写入失败整体回滚：目标 {targets.Count}，标签失败 {failures.Count}，封面失败 {coverFailures.Count}，已还原 {rolledBack}");
                SetOnlineTagStatus($"{failures.Count + coverFailures.Count} 个文件写入失败（标签 {failures.Count}，封面 {coverFailures.Count}）；已写成功的 {rolledBack} 个已还原到改动前。详情见日志。");
                OnlineTagWriteButton.IsEnabled = true;
                return;
            }

            if (written.Count == 0)
            {
                SetOnlineTagStatus("没有需要写入的字段——这个结果里你勾的字段都是空的（比如 MB 结果没有流派）。换一个结果或改勾选的字段。");
                OnlineTagWriteButton.IsEnabled = true;
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

            SetOnlineTagStatus($"已写入 {written.Count} 个文件{BatchScopeText(wholeList, targets.Count)}。");
            OnlineTagWriteButton.IsEnabled = true;
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

        private bool _isChecked;

        /// <summary>
        /// 最左侧选择框的勾选状态（2026-10-15）。它与表格的 SelectedItems 双向同步：
        /// 勾框 → 加入/移出选中；选中行变化 → 回写勾选。两套状态只认一个真值源。
        /// </summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <summary>
        /// 音频属性补算落定后一次性发齐通知（2026-10-16 Kid3 化）。
        /// 这些列的值是后台补算写的，行模板 x:Bind 只有收到通知才会刷新；
        /// DurationText 是基类计算属性，也按名字发一次即可。
        /// </summary>
        public void NotifyAudioFactsChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BitDepthText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SampleRateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BitrateText)));
        }

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

        // ---------- 可选列（默认隐藏，「列…」里勾出来才显示） ----------
        // 显示文本与排序数值分开：文本给界面，数值给列头排序（不然 "10" 会排到 "9" 前）。

        /// <summary>作曲家（多值取第一个；空 = 这个文件没写）。</summary>
        public string ComposerText { get; set; } = string.Empty;

        /// <summary>注释。</summary>
        public string CommentText { get; set; } = string.Empty;

        /// <summary>非同步歌词（UNSYNCEDLYRICS）。</summary>
        public string LyricsText { get; set; } = string.Empty;

        /// <summary>BPM（速度）。</summary>
        public string BpmText { get; set; } = string.Empty;

        /// <summary>曲目总数（"5/10" 的 10）。</summary>
        public string TrackCountText { get; set; } = string.Empty;

        /// <summary>碟片号（有碟片总数时显示成 "1/2"）。</summary>
        public string DiscText { get; set; } = string.Empty;

        /// <summary>排序用的原始数值（没有就 long.MinValue，永远排最后）。</summary>
        public long BpmSortKey { get; set; } = long.MinValue;

        public long TrackCountSortKey { get; set; } = long.MinValue;

        public long DiscSortKey { get; set; } = long.MinValue;

        /// <summary>
        /// 格内编辑代理：年份 / 曲目号编辑框的显示值。YearText/TrackText 未知时
        /// 显示 "-"，编辑框里不能是 "-"，所以单列这两个代理；BuildEntry 按真实值
        /// 填，保存后整行换实例重填。OneWay 绑定只读不写，不走属性通知。
        /// </summary>
        public string YearEdit { get; set; } = string.Empty;

        public string TrackEdit { get; set; } = string.Empty;

        // =====================================================================
        // 列头排序用（2026-10-14 新增）
        // =====================================================================

        /// <summary>加入列表的先后顺序（1 起）。取消排序后回到这个顺序。</summary>
        public int AddOrder { get; set; }

        /// <summary>
        /// 排序用的原始数值。派生列（采样率 / 位深 / 码率 / 大小）显示的是格式化
        /// 文本（"44.1kHz"、"1411kbps"、"4.2 MB"），按文本排会把 1411 排到 320 前
        /// 面、2.82MHz 排到 96kHz 前面，所以这几个列必须按原始数值排。
        /// </summary>
        public int SampleRateHz { get; set; }

        public int BitDepthBits { get; set; }

        public int Kbps { get; set; }

        public long FileSizeBytes { get; set; }
    }

    /// <summary>
    /// 列头显示模型：列名 + 排序标记（箭头；多列排序时前面带 1、2、3 优先级序号）。
    /// 挂在 DataGridColumn.Header 上，由 TagGridHeaderTemplate 渲染。
    /// 为什么要它：DataGrid 原生排序只认单列，多列排序要自己排，那么「现在按哪几列排、
    /// 各自升还是降」这个状态也得自己画在列头上——列头文字继续用 XAML 里写的 Header
    /// 原文，这里只加排序标记，不另建一份列名表。
    /// </summary>
    public sealed class TagColumnHeaderVm : INotifyPropertyChanged
    {
        private string _sortMark = string.Empty;
        private bool _isSorted;

        public TagColumnHeaderVm(string key, string label)
        {
            Key = key;
            Label = label;
        }

        /// <summary>列标识（= XAML 里 DataGridColumn 的 Tag）。</summary>
        public string Key { get; }

        /// <summary>列名（= XAML 里写的 Header 原文）。</summary>
        public string Label { get; }

        /// <summary>排序标记文本："" / "▲" / "▼" / "1▲" / "2▼"。</summary>
        public string SortMark
        {
            get => _sortMark;
            private set
            {
                if (_sortMark == value)
                {
                    return;
                }

                _sortMark = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortMark)));
            }
        }

        /// <summary>这一列当前是否参与排序（列头文字加亮用）。</summary>
        public bool IsSorted
        {
            get => _isSorted;
            private set
            {
                if (_isSorted == value)
                {
                    return;
                }

                _isSorted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSorted)));
            }
        }

        /// <summary>
        /// 兜底：万一某个列头没走我们的内容模板（直接把对象当文本渲染），
        /// 至少显示的是列名，而不是类名。
        /// </summary>
        public override string ToString() => Label;

        public void SetSort(int ordinal, bool descending, int totalRules)
        {
            if (ordinal <= 0)
            {
                IsSorted = false;
                SortMark = string.Empty;
                return;
            }

            IsSorted = true;
            string arrow = descending ? "▼" : "▲";
            SortMark = totalRules > 1 ? ordinal + arrow : arrow;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>一条排序规则（哪列 + 降序与否）。列表里的先后顺序 = 优先级。</summary>
    internal sealed class TagSortRule
    {
        public TagSortRule(string key, bool descending = false)
        {
            Key = key;
            Descending = descending;
        }

        public string Key { get; set; }

        public bool Descending { get; set; }
    }
}
