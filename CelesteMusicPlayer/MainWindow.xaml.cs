using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>列表排序依据</summary>
    public enum SortField
    {
        Title,
        Artist,
        Album,
        Year,
        Duration,
        Genre,
        Track,
        FilePath
    }

    /// <summary>
    /// 播放列表一行：标题、艺术家、专辑、年份、时长 + 本地路径。
    /// </summary>
    public sealed class PlaylistItem
    {
        public int Index { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = "未知艺术家";

        /// <summary>专辑艺术家（Tag AlbumArtist）；缺省时与 Artist 相同</summary>
        public string AlbumArtist { get; set; } = "未知艺术家";

        public string Album { get; set; } = "未知专辑";

        /// <summary>音轨号（Tag.Track）；0 表示未知</summary>
        public uint Track { get; set; }

        /// <summary>碟片号（Tag.Disc）；0 表示未知</summary>
        public uint Disc { get; set; }


        /// <summary>年份数值；0 表示未知</summary>
        public uint Year { get; set; }

        /// <summary>流派（Tag.Genre）；空视为未知流派</summary>
        public string Genre { get; set; } = "未知流派";

        /// <summary>列表上显示的年份文字（由 Year 推导，避免复制条目时漏设）</summary>
        public string YearText => Year > 0 ? Year.ToString() : "-";

        /// <summary>时长文本（由 Duration 只读推导，保证恒有值、不空白）。</summary>
        public string DurationText
        {
            get
            {
                var t = Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration;
                return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            }
        }

        /// <summary>歌曲面板第三行的格式胶囊：格式 / 位深·采样率 / 比特率（如 ["FLAC","16bit/44kHz","1411kbps"]）。
        /// 懒计算 + AudioInfoFormatter 按路径缓存，避免启动/建条目时同步解析。</summary>
        private IReadOnlyList<string>? _formatChips;
        public IReadOnlyList<string> FormatChips
        {
            get
            {
                _formatChips ??= string.IsNullOrWhiteSpace(FilePath)
                    ? System.Array.Empty<string>()
                    : AudioInfoFormatter.FormatChips(FilePath);
                return _formatChips;
            }
        }

        /// <summary>歌曲小封面（延迟异步加载，空则显示占位图标）。</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? CoverImage { get; set; }

        public TimeSpan Duration { get; set; }

        public string FilePath { get; set; } = string.Empty;

        /// <summary>用户评分 0..5（0 = 未评分）。</summary>
        public int Rating { get; set; }

        /// <summary>仅文件名（媒体库/文件夹详情列表显示用）。</summary>
        public string FileName => System.IO.Path.GetFileName(FilePath);

        /// <summary>CUE 分轨起始秒；0 表示整曲。</summary>
        public double StartTimeSeconds { get; set; }

        public string DisplayName => Title;

        /// <summary>
        /// 播放歌曲状态条第三行显示的短格式信息："ALAC · 24bit/44kHz · 1411kbps"（由路径实时读取，读失败为空）。
        /// </summary>
        public string FormatInfoLine =>
            string.IsNullOrWhiteSpace(FilePath) ? string.Empty : AudioInfoFormatter.FormatShortLine(FilePath);

        /// <summary>
        /// 歌曲面板显示的标题：一律使用音频内嵌标题标签（Tag.Title），
        /// 缺失时回退文件名。不再受"播放列表显示格式"设置影响。
        /// </summary>
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title)
                ? Path.GetFileNameWithoutExtension(FilePath)
                : Title.Trim();

        /// <summary>歌曲面板第二行："艺术家 - 专辑"（用曲目/演出艺术家，而非专辑艺术家）。</summary>
        public string ArtistAlbumText
        {
            get
            {
                string artist = string.IsNullOrWhiteSpace(Artist) || string.Equals(Artist, "未知艺术家", StringComparison.Ordinal)
                    ? "未知艺术家"
                    : Artist;
                string album = string.IsNullOrWhiteSpace(Album) || string.Equals(Album, "未知专辑", StringComparison.Ordinal)
                    ? "未知专辑"
                    : Album;
                return artist + " - " + album;
            }
        }

        /// <summary>专辑详情列表显示的音轨号（纯音轨号；碟片号由独立标题行表达）</summary>
        public string TrackText => Track > 0 ? Track.ToString() : "-";
    }

    /// <summary>CollectionView 分组的单个碟片组（Apple Music 分组头，含 Key 与歌曲）。</summary>
    public sealed class AlbumDiscGroup : System.Collections.ObjectModel.ObservableCollection<PlaylistItem>
    {
        public string Key { get; set; } = string.Empty;
    }

    /// <summary>标签排序分类墙卡片：一个分类（某字段值），封面取该分类第一首曲目封面。</summary>
    public sealed class TagSortCategoryEntry : INotifyPropertyChanged
    {
        private BitmapImage? _cover;
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public string FirstFilePath { get; set; } = string.Empty;
        /// <summary>面板内网格的额外语义（如 "Album"/"Artist"），用于指定该网格项的钻取字段。</summary>
        public string Sub { get; set; } = string.Empty;

        public BitmapImage? Cover
        {
            get => _cover;
            set { _cover = value; PropertyChanged?.Invoke(this, new(nameof(Cover))); }
        }

        public string CountText => Count + " 首";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>专辑浏览项：唯一专辑名 + 封面（优先取音轨 1 的内嵌图）</summary>
    public sealed class AlbumEntry : INotifyPropertyChanged
    {
        private BitmapImage? _coverImage;

        public string Name { get; set; } = string.Empty;

        public string Artist { get; set; } = "未知艺术家";

        /// <summary>发行年份（取专辑内曲目年份的最大值；0 表示未知）</summary>
        public uint Year { get; set; }

        public string YearText => Year > 0 ? Year.ToString() : "未知年份";

        public int TrackCount { get; set; }

        /// <summary>专辑墙小字行右侧文本（专辑歌曲数）。</summary>
        public string TrackCountText => TrackCount + " 首歌";

        /// <summary>添加顺序号（专辑首次在库中出现的位置，用于“按添加时间”排序）。</summary>
        public int SortIndex { get; set; }

        public TimeSpan TotalDuration { get; set; }

        public string TotalDurationText { get; set; } = "00:00";

        /// <summary>是否为 DSD 专辑（专辑内全部曲目都是 DSF/DFF）。</summary>
        public bool IsDsd { get; set; }

        /// <summary>DSD 角标文字（"DSF" 或 "DFF"）；非全 DSD 时为 null。</summary>
        public string? DsdTagText => IsDsd ? DsdContainerExt : null;

        /// <summary>DSD 角标可见性（供 x:Bind 绑定）。</summary>
        public Microsoft.UI.Xaml.Visibility DsdTagVisibility
            => IsDsd ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        private string? _dsdContainerExt;
        internal void SetDsdContainer(string? ext) => _dsdContainerExt = ext;
        private string? DsdContainerExt => _dsdContainerExt;

        /// <summary>用来提取封面的音频路径（优先音轨 1）</summary>
        public string CoverSourcePath { get; set; } = string.Empty;

        public BitmapImage? CoverImage
        {
            get => _coverImage;
            set
            {
                if (_coverImage == value)
                {
                    return;
                }

                _coverImage = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>艺术家浏览项：唯一艺人 + 可自定义圆形头像</summary>
    public sealed class ArtistEntry : INotifyPropertyChanged
    {
        private BitmapImage? _avatarImage;

        public string Name { get; set; } = string.Empty;

        public int TrackCount { get; set; }

        public BitmapImage? AvatarImage
        {
            get => _avatarImage;
            set
            {
                if (_avatarImage == value)
                {
                    return;
                }

                _avatarImage = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>专辑墙排序方式</summary>
    public enum AlbumSortMode
    {
        /// <summary>按专辑名称</summary>
        Title,
        /// <summary>按艺术家</summary>
        Artist,
        /// <summary>按发行年份</summary>
        Year,
        /// <summary>按添加时间</summary>
        Added,
        /// <summary>随机</summary>
        Random,
        /// <summary>按专辑曲目数</summary>
        TrackCount,
        /// <summary>按专辑总时长</summary>
        TotalDuration
    }

    /// <summary>艺术家详情内歌曲列表排序</summary>
    public enum ArtistSongSortMode
    {
        Title,
        AlbumTitleThenTrack,
        AlbumYearThenTrack
    }

    /// <summary>艺术家详情内专辑列表排序</summary>
    public enum ArtistAlbumSortMode
    {
        Title,
        Year
    }

    /// <summary>播放顺序 / 循环方式</summary>
    public enum PlaybackOrder
    {
        /// <summary>顺序播放：按列表顺序播完即停</summary>
        Sequential,
        /// <summary>随机播放：每首结束后随机选下一首</summary>
        Random,
        /// <summary>列表循环：按列表顺序循环</summary>
        ListLoop,
        /// <summary>单曲循环：重复当前曲目</summary>
        TrackLoop,
        /// <summary>单曲播放：播完当前曲目后停止</summary>
        TrackOnce
    }

    /// <summary>文件夹浏览树中的一行（文件夹或音频文件）</summary>
    public sealed class FolderBrowserItem : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public string DisplayName { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public bool IsFolder { get; init; }

        public int Depth { get; init; }

        /// <summary>子项是否已从磁盘枚举过</summary>
        public bool ChildrenLoaded { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChevronGlyph));
            }
        }

        /// <summary>按深度缩进</summary>
        public Thickness Indent => new(Depth * 16, 0, 0, 0);

        /// <summary>是否为媒体库根（深度 0）</summary>
        public bool IsRoot => Depth == 0;

        /// <summary>根行：浅白透明胶囊背景；子行为透明。</summary>
        public Microsoft.UI.Xaml.Media.Brush CapsuleBackground =>
            IsRoot
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 255, 255, 255))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        /// <summary>文件夹左侧朝下小箭头（表示可展开的文件夹）；文件不显示</summary>
        public string ChevronGlyph => IsFolder ? "\uE70D" : string.Empty;

        public Visibility ChevronVisibility =>
            IsFolder ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed partial class MainWindow : Window
    {

        private readonly ObservableCollection<PlaylistItem> _playlist = new();
        private ObservableCollection<PlaylistItem> _userPlaylist = new();
        private TaskbarProgressHelper? _taskbarProgress;
        private IntPtr _mainWindowHwnd;

        // 最小窗口尺寸（DIP）
        private const int MinWindowWidthDip = 1400;
        private const int MinWindowHeightDip = 800;

        // ---- WM_GETMINMAXINFO 子类化：真正锁定最小窗口 ----
        private const int WM_GETMINMAXINFO = 0x0024;
        private const long GWL_WNDPROC = -4;
        private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
        private static WndProcDelegate? _minMaxWndProc;
        private static nint _prevWndProc;
        private static double _minTrackScale = 1.0;

        // ---- 隐藏系统标题栏按钮（自绘最小化/最大化/关闭） ----
        private const long GWL_STYLE = -16;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_BORDER = 0x00800000L;

        // ---- WM_NCHITTEST：彻底无边框下的边缘拖拽调大小 ----
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_SIZE = 0x0005;
        private const int SIZE_MAXIMIZED = 2;
        private const int SIZE_RESTORED = 0;
        private const int HTCAPTION = 2;
        private const int HTCLIENT = 1;
        private const int HTNOWHERE = 0;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
        private static extern long GetWindowLongPtr64(nint hWnd, int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT_INT { public int L, T, R, B; }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref RECT_INT value, int sz);

        [DllImport("dwmapi.dll", PreserveSig = true, EntryPoint = "DwmSetWindowAttribute")]
        private static extern int DwmSetWindowAttributeInt(nint hwnd, int attr, ref int value, int sz);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_DONOTROUND = 1;


        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
        private static extern nint SetWindowLongPtr642(nint hWnd, int nIndex, long dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref RECT_INT pvParam, int fWinIni);
        private const int SPI_GETWORKAREA = 0x0030;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public int rcMonitor_L, rcMonitor_T, rcMonitor_R, rcMonitor_B;
            public int rcWork_L, rcWork_T, rcWork_R, rcWork_B;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);
        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(nint hWnd, out RECT_INT lpRect);
        private static readonly nint HWND_TOPMOST = new(-1);
        private static readonly nint HWND_NOTOPMOST = new(-2);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_ASYNCWINDOWPOS = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
        private DesktopLyricsOverlay? _desktopLyricsWindow;
        private bool _desktopLyricsEnabled;
        private MiniPlayerWindow? _miniPlayerWindow;
        private bool _miniPlayerEnabled;
        private DateTime _lastPlaybackPersistUtc = DateTime.MinValue;
        private ArtistAvatarEditorWindow? _artistAvatarEditorWindow;
        private AppTrayIcon? _trayIcon;
        private TaskbarThumbnailButtons? _taskbarButtons;
        // 任务栏缩略图按钮专用：宿主 WinUI 矢量 / FontIcon 的隐藏 Canvas（在视觉树中、Opacity=0、
        // 位置偏移到 -10000,-10000，肉眼看不见但能正常 measure/arrange/render）。
        // 用于把图标渲染成 HICON，避免手绘心形在小尺寸下模糊。
        private Canvas? _thumbIconHostCanvas;

        /// <summary>
        /// 任务栏图标渲染宿主。给 <see cref="TaskbarIconFactory"/> 用——
        /// RenderTargetBitmap 要求被渲染的元素在视觉树里、有 XamlRoot，所以必须由主窗口提供这个容器。
        /// </summary>
        internal Canvas? ThumbIconHostCanvas => _thumbIconHostCanvas;
        private bool _allowClose;
        private bool _closePromptOpen;
        private bool _applyingSettingsVolume;
        private DispatcherQueueTimer? _volumeSaveTimer;
        private double _volumeToSave;
        private DispatcherQueueTimer? _libraryWatchDebounce;
        private bool _libraryRescanInProgress;
        // 播放顺序与随机源已移到 PlaybackOrderResolver（阶段7 解耦）。
        // 读写播放顺序一律走 _orderResolver.Order，随机索引走 _orderResolver.NextRandomIndex。
        private readonly PlaybackOrderResolver _orderResolver = new();
        private string _librarySearchText = string.Empty;
        private DispatcherQueueTimer? _librarySearchDebounceTimer;
        private readonly List<string> _folderSearchMatches = new();
        private int _folderSearchIndex = -1;
        private string? _folderSearchHighlightPath;
        private MediaPlayer? _mediaPlayer;
        private DispatcherQueueTimer? _positionTimer;
        private bool _isUserSeeking;
        private bool _isUpdatingProgressUi;

        // ---------- 排序状态 ----------
        private SortField _sortField = SortField.Title;
        private bool _sortAscending = true;
        private AlbumSortMode _albumSortMode = AlbumSortMode.Title;

        /// <summary>专辑墙排序方向（升序/降序；Random 时忽略）。</summary>
        private bool _albumSortAscending = true;
        private ArtistSongSortMode _artistSongSortMode = ArtistSongSortMode.Title;
        private ArtistAlbumSortMode _artistAlbumSortMode = ArtistAlbumSortMode.Title;
        private bool _artistAlbumSortAscending = true;
        private AlbumEntry? _openedAlbum;
        private ArtistEntry? _openedArtist;
        /// <summary>当前艺术家详情是否按「专辑艺术家」匹配曲目</summary>
        private bool _artistDetailUsesAlbumArtist;
        /// <summary>专辑详情是否从艺术家详情进入（返回时回到艺术家页）</summary>
        private bool _albumOpenedFromArtist;

        // 标签排序板块状态：分类字段、当前分类值、面板视角、面板内已选(专辑/艺术家)、自定义排序配置
        private string _tagSortClassField = "Artist";
        private string _tagSortClassValue = string.Empty;   // 当进入某个分类（如某艺术家）时的值
        private string _tagSortPanelMode = "Songs";          // Songs / Albums / Artists / Sort
        private readonly ObservableCollection<PlaylistItem> _tagSortClassSongs = new(); // 当前分类下的曲目
        private string _tagSortPreset = "Artist / Album";    // 当前排序方式预设或自定义
        private bool _tagSortAscending = true;
        private List<(string field, bool asc)> _tagSortCustom = new(); // 自定义排序（最多 5 级）

        // 模块 A：曲目列表列配置（列定制）
        private List<ListColumnSpec> _tagSortColumns = new(); // 加载自 AppSettings，空=默认
        private int _tagSortColumnVersion = 1;                // 列配置变更时 ++，驱动行模板重建
        private string _tagSortPanelSongSortField = string.Empty; // 面板列表列头排序字段（空=保持进入时的原顺序）
        private bool _tagSortPanelSongSortAsc = true;         // 面板列表列头排序方向

        // 模块 B：分类字段配置
        private List<string> _tagSortCategoryFields = new();  // 加载自 AppSettings，空=默认5个

        // 分类墙加载保护：全部分组缓存 + 当前可见数（高基数字段不会一次性渲染所有卡片）
        private const int TagSortClassWallInitialMax = 200;   // 初始可见上限（再多就触发"加载更多"）
        private const int TagSortClassWallLoadMoreStep = 200; // 每次"加载更多"再加 200
        private List<TagSortCategoryEntry> _tagSortClassWallAll = new(); // 全部分组（已排序）
        private int _tagSortClassWallShown = 0;                            // 当前已显示数量

        // 模块 C：分组浏览（列表内多级分组、组头折叠）
        private List<string> _tagSortGroupFields = new() { "Artist", "Album" }; // 当前激活的分组字段序列（多级嵌套顺序）
        private List<string> _tagSortGroupCustom = new() { "Artist", "Album" }; // 自定义分组快照（下拉“自定义”项返回此配置，独立于当前激活项）
        private string? _tagSortGroupActivePreset;                             // 当前激活项标记：某预设的字段列表 Tag（如 "Artist,Album"）或 "__custom__"；空=默认
        private ObservableCollection<object> _tagSortGroupFlatRows = new();    // 扁平化：组头(TagSortGroupHeader) + 歌曲(PlaylistItem) 混排
        private List<TagSortGroupHeader> _tagSortGroupTree = new();            // 多级分组树（roots）
        private Dictionary<PlaylistItem, int> _tagSortSongIndent = new();      // 歌曲行缩进像素（按所属末级节点深度计算）

        // ---------- 左侧分类（Songs / Albums / Artists / Folders / UserPlaylist）----------
        private string _currentCategory = "Songs";

        /// <summary>中间区域浏览历史（鼠标侧键前进/后退，类似资源管理器）</summary>
        private sealed class LibraryNavState
        {
            public string Category { get; init; } = "Songs";
            public string? ArtistName { get; init; }
            public string? AlbumName { get; init; }
            public bool AlbumFromArtist { get; init; }
            public bool UsesAlbumArtist { get; init; }
        }

        private readonly List<LibraryNavState> _navBackStack = new();
        private readonly List<LibraryNavState> _navForwardStack = new();
        private LibraryNavState? _navCurrent;
        private bool _suppressNavHistory;

        // ---------- 列表列分割线拖动 ----------
        private bool _isDraggingColumnSplitter;
        private string? _columnSplitPair;
        private double _columnSplitStartX;
        private double _columnLeftStartWidth;
        private double _columnRightStartWidth;

        // ---------- 单元格悬停详情 ----------
        private DispatcherQueueTimer? _hoverTipTimer;
        private FrameworkElement? _hoverElement;
        private string? _hoverTipText;
        private ToolTip? _activeHoverTip;

        // ---------- 右侧正在播放 / 波形 / 歌词 ----------
        private DispatcherQueueTimer? _waveformTimer;
        private const int WaveBarCount = FormatHelper.WaveBarCount; // 单一来源：见 FormatHelper.WaveBarCount
        private readonly double[] _waveLevels = new double[WaveBarCount];
        private readonly double[] _wavePhases = new double[WaveBarCount];
        private readonly float[] _spectrumBands = new float[WaveBarCount]; // 真 FFT 频谱（每柱一个 0..1 电平）
        private readonly Random _waveRandom = new();
        private int _waveformIdleSettleTicks;
        private List<LyricLine> _lyricLines = new();
        private int _currentLyricIndex = -1;        private readonly List<TextBlock> _lyricTextBlocks = new();
        // 歌词手动滚动协调
        private bool _userScrollingLyrics;
        private DispatcherQueueTimer? _lyricScrollResumeTimer;
        private string? _nowPlayingPath;
        private bool _nowPlayingPaneOpen;
        // 当前播放曲目的 ReplayGain 元数据（SetReplayGain 时缓存，供 UI 显示/面板重应用）
        private (double TrackGainDb, double AlbumGainDb, double Peak)? _currentRgData;

        // 歌曲面板小封面异步加载：防止同一路径并发重复读取
        private readonly System.Collections.Generic.HashSet<string> _rowCoverLoading = new(System.StringComparer.OrdinalIgnoreCase);
        // 封面解码缓存（按路径复用已解码封面，避免滚动/重复时反复 IO+解码造成卡顿）；限流并发封面解码
        // BitmapImage 持有解码后的像素，逐曲播放会无限累积 —— 这是“播放越多内存越大”的主因，故设上限。
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _coverImageCache = new(System.StringComparer.OrdinalIgnoreCase);
        private const int RowCoverCacheMax = 1024;
        private readonly System.Threading.SemaphoreSlim _coverLoadGate = new(4);

        // 歌词平滑滚动
        private DispatcherQueueTimer? _lyricScrollTimer;
        private double _lyricScrollFrom;
        private double _lyricScrollTo;
        private long _lyricScrollStartMs;
        private const int LyricScrollDurationMs = 480;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public MainWindow()
        {
            StartupLog.Write("MainWindow ctor begin");
            Instance = this;
            // 缓存 UI 线程 DispatcherQueue 给引擎层（HiFiOutputBackend）在后台线程构造时取用，
            // 避免 DispatcherQueue.GetForCurrentThread() 返回 null 导致 NullReferenceException。
            HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue;
            InitializeComponent();
            StartupLog.Write("MainWindow InitializeComponent done");
            InitializeLevelMeter();
            InitializeCrossfadeUi();
            InitializeSrcUi();
            InitializeOutputBufferUi();
            StartAudioDeviceWatcher();
            try
            {
                _mainWindowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }
            catch
            {
                _mainWindowHwnd = IntPtr.Zero;
            }

            // 任务栏缩略图按钮（悬停任务栏图标时显示 上一首/播放暂停/下一首/收藏）
            if (_mainWindowHwnd != IntPtr.Zero)
            {
                try
                {
                    // Add() 推迟到 Loaded 后调用——窗口任务栏按钮未就绪时
                    // ThumbarAddButtons 会静默失败（按钮不会显示）。Loaded 后调用一次。
                    _taskbarButtons = new TaskbarThumbnailButtons(this, _mainWindowHwnd);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ctor.taskbar", caught);
                }

                // 任务栏缩略图按钮用的"隐藏 FontIcon 宿主 Canvas"
                // - 必须在 RootShell 视觉树中（RenderTargetBitmap 要求元素有 XamlRoot）
                // - 位置 -10000/-10000 + Opacity=0 + 永远不显示，确保肉眼看不到
                try
                {
                    _thumbIconHostCanvas = new Canvas
                    {
                        Width = 32,
                        Height = 32,
                        Opacity = 0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(_thumbIconHostCanvas, -10000);
                    Canvas.SetTop(_thumbIconHostCanvas, -10000);
                    RootShell.Children.Add(_thumbIconHostCanvas);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ctor.thumbIconHost", caught);
                }
            }

            // 系统托盘图标常驻：程序启动即显示（不随窗口隐藏/恢复变化，退出时清理）
            try
            {
                _trayIcon ??= new AppTrayIcon(this);
                _trayIcon.Show();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ctor.tray", caught);
            }

            // 默认 1400×800；Resize 按 DPI 换算为物理像素
            ResizeWindowToDips(1400, 800);
            // 真正锁定最小窗口 1400×800（WM_GETMINMAXINFO 子类化，无闪烁）
            SetupMinSizeHooks();
            // 标题栏扩展放到 Activated 之后，避免资源管理器直接启动时黑窗闪退（0xC000027B）
            Activated += MainWindow_FirstActivated;
            Closed += MainWindow_Closed;
            AppWindow.Closing += AppWindow_Closing;
            ApplyCapsuleSortButtonStyle(accent: true);
            ApplyPlaylistHeaderChipStyle();
            ApplyCapsuleToControl(
                AlbumDetailPlayButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                AlbumDetailAddToPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                SavePlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                OpenPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                ClearPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                PlayUserPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                PlayArtistWorksButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                AddArtistWorksToPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                PlayArtistSongsButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                AddArtistSongsToPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                PlayAllArtistAlbumsButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyCapsuleToControl(
                AddAllArtistAlbumsToPlaylistButton,
                32,
                new CornerRadius(16),
                ResolveCapsuleFillBrush(),
                foreground: null);
            ApplyAccentSelectionResources(PlaylistView);
            UpdateLibraryNavHighlight();
            PlaylistView.SelectionChanged += PlaylistView_SelectionChromeChanged;
            PlaylistView.ContainerContentChanging += PlaylistView_ContainerContentChanging;
            PlaylistView.ItemsSource = _playlist;
            AlbumGridView.ItemsSource = _albums;
            PlaylistWallGridView.ItemsSource = _playlistWall;
            AlbumGridView.ContainerContentChanging += AlbumGridView_ContainerContentChanging;
            ApplyAccentSelectionResources(AlbumGridView);
            AlbumTrackListView.ItemsSource = _albumTracks;
            ApplyAccentSelectionResources(AlbumTrackListView);
            AlbumTrackListView.ContainerContentChanging += AlbumTrackListView_ContainerContentChanging;
            PlaylistDetailListView.SizeChanged += SongListView_SizeChanged;
            FolderBrowserView.SizeChanged += (_, _) =>
            {
                if (FolderBrowserView.ActualWidth > 0) RefreshFolderBrowserSelectionChrome();
            };
            PlaylistDetailListView.ContainerContentChanging += PlaylistDetailListView_ContainerContentChanging;
            PlaylistDetailListView.SelectionChanged += PlaylistDetailListView_SelectionChromeChanged;
            PlaylistDetailListView.SizeChanged += SongListView_SizeChanged;
            AlbumTrackListView.SizeChanged += SongListView_SizeChanged; // 行内容宽度跟随列表宽度
            ArtistGridView.ItemsSource = _artists;
            ArtistTrackListView.ItemsSource = _artistTracks;
            ApplyAccentSelectionResources(ArtistTrackListView);
            ArtistTrackListView.ContainerContentChanging += ArtistTrackListView_ContainerContentChanging;
            ArtistTrackListView.SizeChanged += SongListView_SizeChanged; // 行内容宽度跟随列表宽度
            ArtistAlbumGridView.ItemsSource = _artistAlbums;
            ArtistAlbumGridView.ContainerContentChanging += ArtistAlbumGridView_ContainerContentChanging;
            ApplyAccentSelectionResources(ArtistAlbumGridView);
            FolderBrowserView.ItemsSource = _folderBrowserItems;
            FolderBrowserView.ContainerContentChanging += FolderBrowserView_ContainerContentChanging;
            ApplyAccentSelectionResources(FolderBrowserView);

            SyncHeaderColumnsFromState();
            _currentCategory = "Songs";
            UpdateLibraryNavHighlight();
            _navCurrent = CaptureLibraryNavState();

            // 右列播放队列：绑定到 _playlist（与 PlaylistListBorder 共享同一集合）
            QueueListView.ItemsSource = _playlist;
            _playlist.CollectionChanged += (_, _) =>
            {
                UpdateQueueEmptyHint();
                SyncQueueSelection();
            };
            UpdateQueueEmptyHint();
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                QueueListView.SelectedIndex = _currentIndex;
            }

            LibraryPaneRoot.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(LibraryPaneRoot_PointerPressed),
                handledEventsToo: true);

            for (int i = 0; i < WaveBarCount; i++)
            {
                _wavePhases[i] = _waveRandom.NextDouble() * Math.PI * 2;
            }

            if (Content is FrameworkElement root)
            {
                root.Loaded += MainWindow_Loaded;
            }

            StartupLog.Write("MainWindow ctor end");

            // 把版本号和日志路径写进窗口标题,排查时一眼能看出是不是在跑新版
            try
            {
                string ver = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?";
                string shortPath = StartupLog.CurrentFilePath;
                if (shortPath.Length > 60)
                {
                    shortPath = "..." + shortPath[(shortPath.Length - 57)..];
                }
                Title = "CelesteMusicPlayer v" + ver + "   日志: " + shortPath;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow set Title", caught);
            }
        }

        // ============================== 播放队列（右列固定） ==============================

        /// <summary>右列播放队列的 SelectionChanged：点哪首就跳到哪首开始播。</summary>
        private void QueueListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (QueueListView.SelectedItem is PlaylistItem item && item != null)
            {
                int idx = _playlist.IndexOf(item);
                if (idx >= 0 && idx != _currentIndex)
                {
                    PlayAtIndex(idx);
                }
            }
        }

        /// <summary>队列为空时显示提示，否则隐藏。</summary>
        private void UpdateQueueEmptyHint()
        {
            QueueCountText.Text = _playlist.Count + " 首";
            QueueEmptyHint.Visibility = _playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncQueueSelection();
        }

        /// <summary>把 QueueListView.SelectedIndex 同步到 _currentIndex（如果合法）。
        /// 由 _playlist.CollectionChanged 触发（删/增/重排后保持高亮跟随）。
        /// "_currentIndex 单独变化但 playlist 不变" 的情况由 1s 定时器兜底。</summary>
        private void SyncQueueSelection()
        {
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                if (QueueListView.SelectedIndex != _currentIndex)
                {
                    QueueListView.SelectedIndex = _currentIndex;
                }
            }
            else if (QueueListView.SelectedIndex != -1)
            {
                QueueListView.SelectedIndex = -1;
            }
        }

        /// <summary>
        /// 把 WinUI FontIcon（默认 Segoe Fluent Icons 字体，glyph 码如 "&#xEB51;" 心形轮廓）
        /// 渲染成 HICON，给任务栏缩略图按钮用。**与 MainWindow 内 FavoriteButton 上 FontIcon
        /// 渲染完全一致**——同一字体、同一默认字号比例，所以任务栏上的心和主界面上的心看起来一样。
        ///
        /// 实现要点：
        /// - 把 FontIcon 放进隐藏 Canvas（构造时已挂在 RootShell，Opacity=0 / Left=-10000 / Top=-10000）
        ///   这样 FontIcon 在视觉树里、有 XamlRoot，能被 RenderTargetBitmap 渲染
        /// - 调 Measure/Arrange 让 FontIcon 知道自己的尺寸（用固定 32x32，不读 DesiredSize）
        /// - RenderTargetBitmap 把 UIElement 渲到 BGRA byte[]
        /// - 写到 System.Drawing.Bitmap（Format32bppArgb 内存布局就是 BGRA）→ GetHicon()
        /// - 调用方负责 DestroyIcon 释放 HICON
        ///
        /// **重要：async 而不是 .GetAwaiter().GetResult()**：
        /// 上版用 sync 阻塞等 RenderAsync/GetPixelsAsync，在 UI thread 上多次连续调用
        /// 容易死锁（dispatcher sync context 上等 RenderAsync 完成 → RTB 内部分发回调也等 UI thread → 永久死锁）。
        /// 改成 async/await 之后，6 个 HICON 串行 await 在 UI thread 上自然让出，dwm 合成线程能正常推进。
        ///
        /// 为什么不用 GDI+ DrawString + Segoe Fluent Icons 字体？
        /// GDI+ DrawString 在 PUA 区段可能用 GDI 路径，视觉与 DirectWrite 不同；
        /// 用 WinUI FontIcon 走 DirectWrite 路径，**主界面和任务栏完全同源**。
        /// </summary>
        internal async System.Threading.Tasks.Task<IntPtr> RenderFontIconHiconAsync(string glyph, double fontSize, Windows.UI.Color color)
        {
            if (_thumbIconHostCanvas == null)
            {
                StartupLog.Write("[thumb] RenderFontIconHiconAsync 失败: _thumbIconHostCanvas 为 null（窗口未就绪）");
                return IntPtr.Zero;
            }

            var fontIcon = new FontIcon
            {
                Glyph = glyph,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(color)
                // 不指定 FontFamily → 默认 Segoe Fluent Icons (Win11) / Segoe MDL2 Assets (Win10)
                // 与主界面 FavoriteButton 上的 FontIcon 完全一致
            };

            _thumbIconHostCanvas.Children.Add(fontIcon);
            try
            {
                // 显式固定 32x32 大小——之前用 DesiredSize.Width/Height 在 Opacity=0 父 Canvas
                // 里有时为 0，导致 RTB 渲染 0x0 异步任务永不完成 → Pump 整条卡死。
                const double RenderSize = 32.0;
                fontIcon.Measure(new Windows.Foundation.Size(RenderSize, RenderSize));
                fontIcon.Arrange(new Windows.Foundation.Rect(0, 0, RenderSize, RenderSize));

                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(fontIcon);                  // 让出 UI thread 给 dwm 合成
                var pixels = await rtb.GetPixelsAsync();          // 让出 UI thread 给拷贝管线
                int w = rtb.PixelWidth;
                int h = rtb.PixelHeight;

                var bytes = new byte[pixels.Length];
                var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels);
                reader.ReadBytes(bytes);

                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bd = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bd.Scan0, bytes.Length);
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                IntPtr hIcon = bmp.GetHicon();
                StartupLog.Write("[thumb] FontIcon \"" + glyph + "\" fontSize=" + fontSize
                    + " 渲染完成: " + w + "x" + h + " hIcon=0x" + hIcon.ToString("X"));
                return hIcon;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RenderFontIconHiconAsync", caught);
                return IntPtr.Zero;
            }
            finally
            {
                _thumbIconHostCanvas.Children.Remove(fontIcon);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }
    }
    /// <summary>把 Slider 的秒数值转为 分:秒 显示。</summary>
    internal sealed class SecondsToTimeSpanConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                double seconds = value is double d ? d : System.Convert.ToDouble(value);
                if (seconds < 0)
                {
                    seconds = 0;
                }

                var ts = TimeSpan.FromSeconds(seconds);
                return ts.ToString(@"mm\:ss");
            }
            catch
            {
                return value;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
