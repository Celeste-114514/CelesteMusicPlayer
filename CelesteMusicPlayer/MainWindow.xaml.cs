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
        Duration
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

        public string DurationText { get; set; } = "00:00";

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
        /// <summary>设置页等子窗口用来回写主窗口状态。</summary>
        internal static MainWindow? Instance { get; private set; }

        internal bool IsEnginePlayingNow => _audioEngine?.IsPlaying == true;

        internal bool IsEngineActiveNow => _audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused);

        internal TimeSpan EnginePositionValue => _audioEngine?.Position ?? TimeSpan.Zero;

        internal TimeSpan EngineDurationValue => _audioEngine?.Duration ?? TimeSpan.Zero;

        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac",
            ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus",
            ".dsf", ".dff",
            ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm"
        };

        private readonly ObservableCollection<PlaylistItem> _playlist = new();
        private ObservableCollection<PlaylistItem> _userPlaylist = new();
        private TaskbarProgressHelper? _taskbarProgress;
        private IntPtr _mainWindowHwnd;

        // 最小窗口尺寸（DIP）
        private const int MinWindowWidthDip = 1360;
        private const int MinWindowHeightDip = 775;

        // ---- WM_GETMINMAXINFO 子类化：真正锁定最小窗口 ----
        private const int WM_GETMINMAXINFO = 0x0024;
        private const long GWL_WNDPROC = -4;
        private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
        private static WndProcDelegate? _minMaxWndProc;
        private static nint _prevWndProc;
        private static double _minTrackScale = 1.0;

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

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
        private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        private static extern nint CallWindowProcW(nint wndProc, nint hWnd, uint msg, nint wParam, nint lParam);
        private string? _genreYearFilter;
        private readonly ObservableCollection<AlbumEntry> _albums = new();
        private readonly ObservableCollection<PlaylistCardViewModel> _playlistWall = new();
        private readonly ObservableCollection<PlaylistItem> _playlistDetailItems = new();
        private string? _currentPlaylistDetail;
        private static readonly Microsoft.UI.Xaml.Media.Brush PlaylistDetailHoverBg =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        private readonly ObservableCollection<PlaylistItem> _albumTracks = new();
        private readonly ObservableCollection<ArtistEntry> _artists = new();
        private readonly ObservableCollection<PlaylistItem> _artistTracks = new();
        private readonly ObservableCollection<AlbumEntry> _artistAlbums = new();
        private readonly ObservableCollection<FolderBrowserItem> _folderBrowserItems = new();

        /// <summary>「选择文件夹」选定的根目录；文件夹分类只展示其内容</summary>
        private string? _browseFolderPath;

        private ArtistEntry? _avatarContextArtist;
        private PlaylistItem? _contextMenuSong;
        private bool _isMultiSelectMode;
        private bool _isEnginePaused;
        private bool _usingEnginePlayback;

        private float[]? _waveformData;
        private string? _waveformPath;
        private string _progressBarStyle = "Gradient";
        // 主题波形强调色（ResolveAccentColor 的缓存，避免频繁解析）
        private static Color _waveAccentColor = Color.FromArgb(255, 0, 120, 212);
        private SystemMediaTransportControls? _engineSmtc;
        private Style? _playlistItemDefaultStyle;
        private Style? _artistTrackItemDefaultStyle;
        private Style? _albumTrackItemDefaultStyle;
        private Style? _artistAlbumItemDefaultStyle;
        private Style? _libraryAlbumItemDefaultStyle;
        private Style? _folderItemDefaultStyle;
        /// <summary>多选当前作用的歌曲列表（歌曲库 / 艺术家详情 / 专辑详情曲目）</summary>
        private ListView? _multiSelectTargetList;
        /// <summary>多选当前作用的专辑网格（音乐库专辑 / 艺术家详情专辑）</summary>
        private GridView? _multiSelectAlbumGrid;
        /// <summary>多选当前作用的文件夹浏览列表</summary>
        private ListView? _multiSelectFolderList;
        /// <summary>批量改选中时跳过 SelectionChanged 里的昂贵 UI 刷新</summary>
        private bool _suppressSelectionUiUpdates;
        private Brush? _cachedMultiSelectFrostBrush;

        private int _currentIndex = -1;
        /// <summary>当前播放在用户播放列表中的下标（播放顺序以播放列表为准）</summary>
        private int _userPlaylistIndex = -1;
        private PlaylistItem? _seamlessPreloaded; // 已预加载待无缝接续的下一首记录（供 SeamlessTrackChanged 更新 UI）
        private CurrentPlaylistWindow? _currentPlaylistWindow;
        internal PlayQueueWindow? QueueWindow { get; set; }
        private DesktopLyricsOverlay? _desktopLyricsWindow;
        private bool _desktopLyricsEnabled;
        private MiniPlayerWindow? _miniPlayerWindow;
        private bool _miniPlayerEnabled;
        private double? _pendingRestorePositionSeconds;
        private DateTime _lastPlaybackPersistUtc = DateTime.MinValue;
        private ArtistAvatarEditorWindow? _artistAvatarEditorWindow;
        private AppTrayIcon? _trayIcon;
        private bool _allowClose;
        private bool _closePromptOpen;
        private bool _applyingSettingsVolume;
        private DispatcherQueueTimer? _volumeSaveTimer;
        private double _volumeToSave;
        private DispatcherQueueTimer? _libraryWatchDebounce;
        private bool _libraryRescanInProgress;
        private PlaybackOrder _playbackOrder = PlaybackOrder.ListLoop;
        private readonly Random _playbackRandom = new();
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
        private const int WaveBarCount = 40;
        private readonly double[] _waveLevels = new double[WaveBarCount];
        private readonly double[] _wavePhases = new double[WaveBarCount];
        private readonly Random _waveRandom = new();
        private int _waveformIdleSettleTicks;
        private List<LyricLine> _lyricLines = new();
        private int _currentLyricIndex = -1;
        private readonly List<TextBlock> _lyricTextBlocks = new();
        private string? _nowPlayingPath;
        private bool _nowPlayingPaneOpen;

        // 歌曲面板小封面异步加载：防止同一路径并发重复读取
        private readonly System.Collections.Generic.HashSet<string> _rowCoverLoading = new(System.StringComparer.OrdinalIgnoreCase);

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
            InitializeComponent();
            StartupLog.Write("MainWindow InitializeComponent done");
            try
            {
                _mainWindowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }
            catch
            {
                _mainWindowHwnd = IntPtr.Zero;
            }

            // 默认 1360×775；Resize 按 DPI 换算为物理像素
            ResizeWindowToDips(1360, 775);
            // 真正锁定最小窗口 1360×775（WM_GETMINMAXINFO 子类化，无闪烁）
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
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartupLog.Write("MainWindow_Loaded begin");
            try
            {
                InitializePlayerAndTimers();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MainWindow_Loaded", ex);
            }
            finally
            {
                StartupLog.Write("MainWindow_Loaded end");
            }
        }

        private void InitializePlayerAndTimers()
        {
            if (_mediaPlayer != null)
            {
                return;
            }

            if (PlayerElement == null)
            {
                _ = ShowErrorAsync("初始化失败", "PlayerElement 未生成，请检查 MainWindow.xaml 中的 x:Name。");
                return;
            }

            _mediaPlayer = new MediaPlayer();
            PlayerElement.SetMediaPlayer(_mediaPlayer);
            ApplyAudioChannelFromSettings();
            _mediaPlayer.CommandManager.IsEnabled = false;

            _mediaPlayer.MediaOpened += Player_MediaOpened;
            _mediaPlayer.MediaEnded += Player_MediaEnded;
            _mediaPlayer.MediaFailed += Player_MediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            ApplyStartupPlaybackSettings();
            _mediaPlayer.Volume = VolumeSlider.Value / 100.0;
            UpdateVolumeIcon(VolumeSlider.Value);
            UpdateSignalChainDisplay();
            UpdateDesktopLyricsBadge();
            UpdateMiniPlayerBadge();
            InitializeMusicPlayer2Features();

            ProgressSlider.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(ProgressSlider_PointerPressed),
                handledEventsToo: true);
            ProgressSlider.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(ProgressSlider_PointerReleased),
                handledEventsToo: true);
            ProgressSlider.AddHandler(
                UIElement.PointerCaptureLostEvent,
                new PointerEventHandler(ProgressSlider_PointerCaptureLost),
                handledEventsToo: true);

            _positionTimer = DispatcherQueue.CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            // 悬停提示定时器（满 1 秒才弹出）
            _hoverTipTimer = DispatcherQueue.CreateTimer();
            _hoverTipTimer.IsRepeating = false;
            _hoverTipTimer.Interval = TimeSpan.FromMilliseconds(500);
            _hoverTipTimer.Tick += HoverTipTimer_Tick;

            _waveformTimer = DispatcherQueue.CreateTimer();
            _waveformTimer.Interval = TimeSpan.FromMilliseconds(50);
            _waveformTimer.Tick += WaveformTimer_Tick;
            // 不在启动时常开：仅播放中驱动，避免定时改视觉树导致全窗光标闪烁
            // 未播放时也填充静态频谱，保证信息卡波形始终可见
            for (int i = 0; i < WaveBarCount; i++)
            {
                _waveLevels[i] = IdleLevel(i);
            }

            ApplyPlaybackOrderToPlayer();
            UpdatePlaybackOrderButtonUi();
            ClearNowPlayingPanel();
            ApplyNowPlayingCardChrome();
            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, _) =>
                {
                    ApplyNowPlayingCardChrome();
                    ApplyArtistSongsFrostChrome();
                    UpdateLibraryNavHighlight();
                    ApplyAccentSelectionResources(PlaylistView);
                    RefreshPlaylistSelectionChrome();
                    ApplyCapsuleSortButtonStyle(accent: true);
                    ApplyPlaylistHeaderChipStyle();
                };
            }

            NowPlayingPane.SizeChanged += (_, _) => UpdateNowPlayingCardLayout();
            MainContentGrid.SizeChanged += (_, _) => FitColumnsToAvailableWidth();
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange || args.DidPresenterChange)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        EnforceMinimumWindowSize();
                        FitColumnsToAvailableWidth();
                        UpdateNowPlayingCardLayout();
                    });
                }
            };

            DispatcherQueue.TryEnqueue(() =>
            {
                FitColumnsToAvailableWidth();
                UpdateNowPlayingCardLayout();
            });

            _ = RestoreLastLibraryAsync();
        }

        /// <summary>音量、播放模式等可立即应用的启动设置。</summary>
        private void ApplyStartupPlaybackSettings()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            _applyingSettingsVolume = true;
            try
            {
                if (IsHiFiModeSelected())
                // 音量滑条在共享与 HiFi 独占下都可用：HiFi 下调 DAC 设备/驱动级主音量（不破坏 bit-perfect），
                // 用保存音量回填，避免切模式/重启后音量跳回 100%。
                VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
                _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动即同步，避免退出时以旧/0 值写盘
            }
            finally
            {
                _applyingSettingsVolume = false;
            }

            if (Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order))
            {
                _playbackOrder = order;
            }

            _ = ApplyOutputDeviceAsync(settings.OutputDeviceId);
            ApplyEngineOutputMode(settings);
        }

        /// <summary>当前设置是否处于 HiFi 独占输出模式（WASAPI 独占 / ASIO）。</summary>
        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        /// <summary>返回 DSD 容器扩展名大写（"DSF"/"DFF"），非 DSD 返回 null。</summary>
        private static string? DsdExtOf(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext switch { ".dsf" => "DSF", ".dff" => "DFF", _ => null };
        }

        private static bool IsHiFiModeSelected()
        {
            string mode = AppSettingsStore.Load().OutputMode;
            return string.Equals(mode, "WasapiExclusive", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Asio", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把设置里的输出模式映射到引擎 HiFi 后端（Shared / WasapiExclusive / Asio）。</summary>
        private void ApplyEngineOutputMode(AppSettingsState settings)
        {
            HiFiOutputBackend.OutputMode mode = string.Equals(settings.OutputMode, "WasapiExclusive", System.StringComparison.OrdinalIgnoreCase)
                ? HiFiOutputBackend.OutputMode.WasapiExclusive
                : string.Equals(settings.OutputMode, "Asio", System.StringComparison.OrdinalIgnoreCase)
                    ? HiFiOutputBackend.OutputMode.Asio
                    : HiFiOutputBackend.OutputMode.WasapiShared;
            _audioEngine?.SetOutputMode(mode);
        }

        /// <summary>应用 HiFi 输出设备：记录到引擎偏好并设置 MediaPlayer 输出设备。</summary>
        private async System.Threading.Tasks.Task ApplyOutputDeviceAsync(string deviceId)
        {
            try
            {
                _audioEngine?.SetOutputDevicePreference(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
            }
            catch
            {
            }

            try
            {
                string? devId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
                if (_mediaPlayer == null)
                {
                    return;
                }

                if (devId != null)
                {
                    try
                    {
                        var deviceInfo = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(devId);
                        _mediaPlayer.AudioDevice = deviceInfo;
                    }
                    catch
                    {
                        // 设备不存在/已移除：回退默认
                        _mediaPlayer.AudioDevice = null;
                    }
                }
                else
                {
                    _mediaPlayer.AudioDevice = null;
                }
            }
            catch
            {
            }
        }

        /// <summary>设置页变更后即时生效。</summary>
        internal void ApplySettingsLive(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool hifi = IsHiFiModeSelected();
                _applyingSettingsVolume = true;
                try
                {
                    if (hifi)
                    {
                        // HiFi 独占（bit-perfect）：数字音量恒 100%，设备主音量（DAC 级）随滑块可调。
                        // 独占：软件音量条固定 100%；实际音量由系统托盘(DAC 设备主音量)控制，bit-perfect 保真。
                        VolumeSlider.Value = VolumeSlider.Maximum;
                        _volumeToSave = VolumeSlider.Maximum;
                        MediaPlayer? hifiPlayer = GetPlayer();
                        if (hifiPlayer != null)
                        {
                            hifiPlayer.Volume = 1.0; // 引擎路径下 MediaPlayer 常停用，兜底置满
                        }

                        UpdateVolumeIcon(VolumeSlider.Value);
                    }
                    else
                    {
                        VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
                        _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动同步，避免退出误写
                        MediaPlayer? player = GetPlayer();
                        if (player != null)
                        {
                            player.Volume = VolumeSlider.Value / 100.0;
                        }

                        UpdateVolumeIcon(VolumeSlider.Value);
                    }
                }
                finally
                {
                    _applyingSettingsVolume = false;
                }

                if (Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order)
                    && order != _playbackOrder)
                {
                    SetPlaybackOrder(order, persist: false);
                }

                ApplyFrostedGlassPreference(settings.EnableFrostedGlass);
                _miniPlayerWindow?.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
                _miniPlayerWindow?.ApplyBackdropPreference(settings.EnableFrostedGlass);
                SettingsWindow.ApplyBackdropIfOpen();
                ApplyExtendedSettingsLive(settings);
            });
        }

        internal void ApplyOverlayPreferenceFromSettings(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (settings.OpenMiniPlayerOnStartup != _miniPlayerEnabled)
                {
                    SetMiniPlayerEnabled(settings.OpenMiniPlayerOnStartup, persistPreference: false);
                }

                if (settings.OpenDesktopLyricsOnStartup != _desktopLyricsEnabled)
                {
                    SetDesktopLyricsEnabled(settings.OpenDesktopLyricsOnStartup, persistPreference: false);
                }
            });
        }

        private void ApplyFrostedGlassPreference(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    FrostedGlass.ApplyWindowBackdrop(this);
                }
                else
                {
                    SystemBackdrop = null;
                }
            }
            catch
            {
            }
        }

        private void ApplyStartupOverlayWindows()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.OpenDesktopLyricsOnStartup)
            {
                SetDesktopLyricsEnabled(true, persistPreference: false);
            }

            if (settings.OpenMiniPlayerOnStartup)
            {
                SetMiniPlayerEnabled(true, persistPreference: false);
            }
        }

        /// <summary>按 DIP 调整窗口客户区大小（内部换算为物理像素）。</summary>
        private void ResizeWindowToDips(int widthDip, int heightDip)
        {
            try
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            }
            catch
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(widthDip, heightDip));
            }
        }

        /// <summary>窗口小于最小尺寸时强制涨回（1200×760，按 DPI 换算）。
        /// 实际通过 WM_GETMINMAXINFO 系统级锁定最小尺寸，拖到最小后不能再缩小。</summary>
        private void EnforceMinimumWindowSize()
        {
            if (_mainWindowHwnd != IntPtr.Zero)
            {
                SetupMinSizeHooks();
            }
        }

        /// <summary>对主窗口进行窗口过程子类化，拦截 WM_GETMINMAXINFO 设置最小尺寸。</summary>
        private void SetupMinSizeHooks()
        {
            try
            {
                if (_minMaxWndProc != null || _mainWindowHwnd == IntPtr.Zero)
                {
                    return;
                }

                uint dpi = GetDpiForWindow(_mainWindowHwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                _minTrackScale = dpi / 96.0;
                _minMaxWndProc = MainMinMaxWndProc;
                _prevWndProc = SetWindowLongPtr64(_mainWindowHwnd, (int)GWL_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_minMaxWndProc));
            }
            catch
            {
            }
        }

        private static nint MainMinMaxWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                try
                {
                    MINMAXINFO mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = (int)Math.Round(MinWindowWidthDip * _minTrackScale);
                    mmi.ptMinTrackSize.Y = (int)Math.Round(MinWindowHeightDip * _minTrackScale);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }
                catch
                {
                }
            }

            return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
        }

        private bool _isUpdatingNowPlayingLayout;

        /// <summary>
        /// 播放信息页：大封面尺寸随面板高度自适应；波形与封面同宽并居中。
        /// </summary>
        private void UpdateNowPlayingCardLayout()
        {
            if (_isUpdatingNowPlayingLayout)
            {
                return;
            }

            _isUpdatingNowPlayingLayout = true;
            try
            {
            double paneWidth = NowPlayingPane.ActualWidth;
            double paneHeight = NowPlayingPane.ActualHeight;
            if (paneWidth <= 0 || paneHeight <= 0)
            {
                return;
            }

            NowPlayingPaneContent.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, paneWidth, paneHeight)
            };

            // 大封面：按面板高度 50% 居中，上限 340、下限 240
            double coverSize = Math.Clamp(paneHeight * 0.5, 240, 340);
            NowPlayingCoverBorder.Width = coverSize;
            NowPlayingCoverBorder.Height = coverSize;
            WaveformCanvas.Width = coverSize;

            // 左半区：左栏(封面+信息)中轴在主UI左1/4处(paneWidth/4)，宽=左半区-左右各30
            double half = paneWidth / 2.0;
            double sideContentMax = Math.Max(0, half - 60);
            if (NowPlayingLeftColumn != null)
            {
                // 左栏宽固定为半区宽(左右留30)，封面/信息在其中以中轴=paneWidth/4 居中，信息区超宽换行
                NowPlayingLeftColumn.ClearValue(FrameworkElement.MaxWidthProperty);
                NowPlayingLeftColumn.ClearValue(FrameworkElement.MinWidthProperty);
                NowPlayingLeftColumn.MaxWidth = sideContentMax;
                NowPlayingLeftColumn.Width = sideContentMax;
            }
            // 左栏默认居中于 paneWidth/2，位移到 paneWidth/4 → 封面中轴=主UI左1/4
            if (NowPlayingLeftShift != null)
            {
                NowPlayingLeftShift.TranslateX = -paneWidth / 4.0;
            }
            // 信息区：左右各留30(半区宽)，定宽使内部真正换行
            if (NowPlayingArtistLinkButton != null) NowPlayingArtistLinkButton.MaxWidth = sideContentMax - 16;
            if (NowPlayingAlbumLinkButton != null) NowPlayingAlbumLinkButton.MaxWidth = sideContentMax - 16;
            if (NowPlayingMetaPanel != null)
            {
                NowPlayingMetaPanel.MaxWidth = sideContentMax;
                NowPlayingMetaPanel.Width = sideContentMax - 16;
                NowPlayingMetaPanel.Margin = new Thickness(0);
            }
            // 右半区：歌词中轴在主UI右1/4处(3/4paneWidth)，宽=右半区-左右各30，居中换行
            if (LyricsSection != null)
            {
                LyricsSection.MaxWidth = sideContentMax;
                LyricsSection.Width = sideContentMax;
            }
            if (LyricsShift != null)
            {
                LyricsShift.TranslateX = paneWidth / 4.0;
            }
            // 让每条歌词在歌词区宽内真正换行(居中区宽 - 留边)
            double lyricMax = Math.Max(0, sideContentMax - 16);
            if (LyricsPanel != null)
            {
                foreach (var child in LyricsPanel.Children)
                {
                    if (child is Microsoft.UI.Xaml.Controls.TextBlock tb)
                    {
                        tb.MaxWidth = lyricMax;
                    }
                }
            }
            }
            finally
            {
                _isUpdatingNowPlayingLayout = false;
            }
        }

        private bool _windowChromeConfigured;

        private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= MainWindow_FirstActivated;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 订阅主题色变化事件(统一刷新强调元素)
                    ThemeColorService.ThemeColorChanged -= OnThemeColorChanged;
                    ThemeColorService.ThemeColorChanged += OnThemeColorChanged;

                    // 音量条自绘:尺寸变化/初始绘制/交互
                    if (VolumeStyleCanvas != null)
                    {
                        VolumeStyleCanvas.SizeChanged -= VolumeStyleCanvas_SizeChanged;
                        VolumeStyleCanvas.SizeChanged += VolumeStyleCanvas_SizeChanged;
                        VolumeStyleCanvas.PointerPressed -= VolumeStyleCanvas_PointerPressed;
                        VolumeStyleCanvas.PointerPressed += VolumeStyleCanvas_PointerPressed;
                        VolumeStyleCanvas.PointerMoved -= VolumeStyleCanvas_PointerMoved;
                        VolumeStyleCanvas.PointerMoved += VolumeStyleCanvas_PointerMoved;
                        VolumeStyleCanvas.PointerReleased -= VolumeStyleCanvas_PointerReleased;
                        VolumeStyleCanvas.PointerReleased += VolumeStyleCanvas_PointerReleased;
                        DrawVolumeStyle();
                    }

                    // 缓存波形主题色(信息卡频谱用)
                    try
                    {
                        _waveAccentColor = ThemeColorService.CurrentAccent;
                    }
                    catch
                    {
                    }

                    // 进度条悬停提示:分:秒格式(替代默认秒数)
                    try
                    {
                        if (ProgressSlider != null)
                        {
                            ProgressSlider.ThumbToolTipValueConverter = new SecondsToTimeSpanConverter();
                        }
                    }
                    catch
                    {
                    }

                    // 自定义背景图片
                    try
                    {
                        ApplyCustomBackground(AppSettingsStore.Load().CustomBackgroundPath);
                    }
                    catch
                    {
                    }

                    // 播放列表列显隐/密度
                    try
                    {
                        ApplyPlaylistColumnSettings(AppSettingsStore.Load());
                    }
                    catch
                    {
                    }

                    // 进度条样式:启动时读取设置(否则默认显示系统进度条)
                    try
                    {
                        _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
                    }
                    catch
                    {
                    }

                    // 进度条画布尺寸变化时重绘(首次布局/窗口缩放)
                    if (ProgressStyleCanvas != null)
                    {
                        ProgressStyleCanvas.SizeChanged -= ProgressStyleCanvas_SizeChanged;
                        ProgressStyleCanvas.SizeChanged += ProgressStyleCanvas_SizeChanged;
                        RedrawProgressStyle();
                    }

                    // 启动即为波形模式:加载选中/第一首歌曲的波形预览(媒体库恢复完成后重试)
                    TryLoadWaveformPreview();
                    _ = RetryWaveformPreviewLaterAsync();
                    _playlist.CollectionChanged -= OnPlaylistForWaveformPreview;
                    _playlist.CollectionChanged += OnPlaylistForWaveformPreview;

                    // 首次激活兜底：确保信息卡波形已绘制（无论是否播放）
                    if (WaveformCanvas != null
                        && (WaveformCanvas.Children.Count == 0 || _waveLevels.All(v => v < 0.05)))
                    {
                        for (int i = 0; i < WaveBarCount; i++)
                        {
                            _waveLevels[i] = IdleLevel(i);
                        }

                        DrawWaveformBars();
                    }
                }
                catch
                {
                }
            });
            if (_windowChromeConfigured)
            {
                return;
            }

            _windowChromeConfigured = true;
            StartupLog.Write("MainWindow_FirstActivated");
            try
            {
                TryApplySystemBackdrop();
                ConfigureWindowChrome();
                StartupLog.Write("Window chrome configured");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ConfigureWindowChrome", ex);
            }

            // 运行标记：检测上次异常退出（崩溃/强杀残留 .running）
            AppSettingsStore.MarkAppStart();
            bool unclean = AppSettingsStore.WasUncleanExitLastTime;

            // 设置文件损坏恢复提示：仅本次会话提示一次
            if (AppSettingsStore.SettingsWereRecovered)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowErrorAsync("设置文件已损坏", "设置文件曾损坏，已自动备份恢复为默认设置。\n（备份文件位于设置目录的 .corrupt-* 文件）");
                    }
                    catch
                    {
                    }
                });
            }
            else if (unclean)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowErrorAsync("上次可能异常退出", "检测到上次程序未正常关闭（可能崩溃或被强制结束）。\n若反复出现，请查看设置目录下的 CelesteMusicPlayer.log 排查原因。");
                    }
                    catch
                    {
                    }
                });
            }
        }

        /// <summary>延后设置 Desktop Acrylic（壁纸色毛玻璃）；失败则回退 Mica / 纯色。</summary>
        private void TryApplySystemBackdrop()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (!settings.EnableFrostedGlass)
                {
                    SystemBackdrop = null;
                    StartupLog.Write("SystemBackdrop disabled by settings");
                    return;
                }

                FrostedGlass.ApplyWindowBackdrop(this);
                StartupLog.Write(
                    SystemBackdrop is DesktopAcrylicBackdrop
                        ? "DesktopAcrylicBackdrop applied"
                        : SystemBackdrop is MicaBackdrop
                            ? "MicaBackdrop fallback applied"
                            : "SystemBackdrop cleared");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyWindowBackdrop", ex);
            }
        }

        /// <summary>应用图标 + 标题栏与内容区合并（系统按钮浮在背景上）</summary>
        private void ConfigureWindowChrome()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ExtendsContentIntoTitleBar", ex);
            }

            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    AppWindowTitleBar titleBar = AppWindow.TitleBar;
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                    titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                    titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                    titleBar.ButtonPressedForegroundColor = Colors.White;
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TitleBar colors", ex);
            }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    AppWindow.SetIcon(iconPath);
                }
            }
            catch
            {
            }

            try
            {
                // 非打包模式(WindowsPackageType=None)下 ms-appx:/// 不可用,标题栏图标改用文件加载
                string pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (System.IO.File.Exists(pngPath) && AppTitleBarIcon != null)
                {
                    AppTitleBarIcon.Source = new BitmapImage(new Uri(pngPath));
                }
            }
            catch
            {
            }
        }

        /// <summary>信息卡：更深毛玻璃 + 阴影立体感</summary>
        private void ApplyNowPlayingCardChrome()
        {
            if (NowPlayingPane != null)
            {
                // 无外框：透明背景、无圆角（右侧面板不显示独立边框）
                NowPlayingPane.Background = null;
                NowPlayingPane.BorderBrush = null;
                NowPlayingPane.BorderThickness = new Thickness(0);
                NowPlayingPane.CornerRadius = new CornerRadius(0);
                NowPlayingPane.Padding = new Thickness(12);
            }

            ApplyArtistSongsFrostChrome();
        }

        /// <summary>
        /// 右侧信息卡 / 艺术家歌曲区共用的透明毛玻璃（纯色 Tint，无额外高光层）。
        /// </summary>
        private Brush CreateNowPlayingStyleAcrylicBrush()
            => FrostedGlass.CreatePanelBrush(ResolveUiBaseTintColor());

        /// <summary>
        /// 深色 Tint 的 Acrylic：提高霜化/模糊感，色调跟整体 UI，避免发白。
        /// 应用于播放信息页内容面板（底层），后续由封面动态背景覆盖。
        /// </summary>
        private void ApplyNowPlayingCardAcrylic()
        {
            if (NowPlayingPaneContent != null)
            {
                NowPlayingPaneContent.Background = CreateNowPlayingStyleAcrylicBrush();
            }
        }

        /// <summary>外围阴影（由动态背景/遮罩统一处理，无独立阴影层）。</summary>
        private void ApplyNowPlayingCardShadow()
        {
        }

        private Color ResolveUiBaseTintColor()
        {
            // 优先取右侧面板实际底色（用户看到的整体 UI 区域色）
            if (TryGetBrushColor(NowPlayingPane?.Background, out Color paneColor)
                && paneColor.A > 0
                && !IsNearWhite(paneColor))
            {
                return Color.FromArgb(255, paneColor.R, paneColor.G, paneColor.B);
            }

            FrameworkElement? anchor = Content as FrameworkElement ?? NowPlayingPaneContent;
            string[] keys =
            {
                "CardBackgroundFillColorDefault",
                "CardBackgroundFillColorDefaultBrush",
                "SolidBackgroundFillColorBase",
                "SolidBackgroundFillColorBaseBrush",
                "ApplicationPageBackgroundThemeBrush"
            };

            foreach (string key in keys)
            {
                if (TryGetThemeColor(anchor, key, out Color themeColor)
                    && themeColor.A > 0
                    && !IsNearWhite(themeColor))
                {
                    return Color.FromArgb(255, themeColor.R, themeColor.G, themeColor.B);
                }
            }

            // 深色 Mica / 深灰 UI 回退（勿用浅灰，否则矩形发白）
            return Color.FromArgb(255, 42, 42, 42);
        }

        private static bool IsNearWhite(Color color)
        {
            return color.R >= 220 && color.G >= 220 && color.B >= 220;
        }

        private static bool TryGetBrushColor(Brush? brush, out Color color)
        {
            if (brush is SolidColorBrush solid)
            {
                color = solid.Color;
                return true;
            }

            if (brush is AcrylicBrush acrylic)
            {
                color = acrylic.TintColor;
                return true;
            }

            color = default;
            return false;
        }

        private static bool TryGetThemeColor(FrameworkElement? element, string key, out Color color)
        {
            color = default;
            try
            {
                object? value = null;
                if (element != null && element.Resources.TryGetValue(key, out object local))
                {
                    value = local;
                }
                else if (Application.Current.Resources.TryGetValue(key, out object app))
                {
                    value = app;
                }

                if (value is Color c)
                {
                    color = c;
                    return true;
                }

                if (value is SolidColorBrush solid)
                {
                    color = solid.Color;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>启动时恢复上次打开的文件夹或音频文件列表。</summary>
        private async Task RestoreLastLibraryAsync()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (!settings.RestoreLibrary)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                LibrarySessionState? state = LibrarySessionStore.TryLoad();
                if (state == null)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                // 文件夹分类根目录：只要曾选择过文件夹就恢复
                if (!string.IsNullOrWhiteSpace(state.FolderPath) && Directory.Exists(state.FolderPath))
                {
                    _browseFolderPath = state.FolderPath;
                }

                string[] paths;
                if (string.Equals(state.Mode, "folder", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(state.FolderPath)
                    && Directory.Exists(state.FolderPath))
                {
                    string folderPath = state.FolderPath;
                    paths = await Task.Run(() => EnumerateAudioFiles(folderPath).ToArray());
                    if (paths.Length == 0)
                    {
                        await RestoreLastPlayingTrackAsync();
                        ApplyStartupOverlayWindows();
                        return;
                    }

                    LoadAndAddFiles(paths, persist: false);
                    LibrarySessionStore.SaveFolder(folderPath, paths);
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                paths = (state.FilePaths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (paths.Length == 0)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                LoadAndAddFiles(paths, persist: false);
                await RestoreLastPlayingTrackAsync();
                ApplyStartupOverlayWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复曲库失败: {ex.Message}");
                try
                {
                    ApplyStartupOverlayWindows();
                }
                catch
                {
                }
            }
        }

        /// <summary>恢复上次正在播放的歌曲到界面（暂停，不自动开播）。</summary>
        private async Task RestoreLastPlayingTrackAsync()
        {
            try
            {
                if (!AppSettingsStore.Load().RestorePlayback)
                {
                    return;
                }

                PlaybackSessionState? session = PlaybackSessionStore.TryLoad();
                if (session == null
                    || string.IsNullOrWhiteSpace(session.FilePath)
                    || !System.IO.File.Exists(session.FilePath))
                {
                    return;
                }

                string path = session.FilePath;
                PlaylistItem? item = null;
                int userIdx = FindUserPlaylistIndex(path);
                if (userIdx >= 0)
                {
                    item = _userPlaylist[userIdx];
                }
                else
                {
                    int libIdx = FindLibraryIndex(path);
                    if (libIdx >= 0)
                    {
                        item = _playlist[libIdx];
                        AddSongsToUserPlaylist(new[] { item });
                        userIdx = FindUserPlaylistIndex(path);
                    }
                    else
                    {
                        item = CreatePlaylistItemFromPath(path);
                        AddSongsToUserPlaylist(new[] { item });
                        userIdx = FindUserPlaylistIndex(path);
                    }
                }

                if (item == null || userIdx < 0)
                {
                    return;
                }

                await PrepareTrackPausedAsync(userIdx, session.PositionSeconds);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复播放曲目失败: {ex.Message}");
            }
        }

        private async Task PrepareTrackPausedAsync(int userPlaylistIndex, double positionSeconds)
        {
            if (userPlaylistIndex < 0 || userPlaylistIndex >= _userPlaylist.Count)
            {
                return;
            }

            PlaylistItem item = _userPlaylist[userPlaylistIndex];
            _userPlaylistIndex = userPlaylistIndex;
            _currentIndex = FindLibraryIndex(item.FilePath);
            _pendingRestorePositionSeconds = Math.Max(0, positionSeconds);

            NowPlayingText.Text = "已就绪：" + item.Title + " - " + item.Artist;
            await UpdateNowPlayingPanelAsync(item);

            // 扩展格式（APE/WavPack 等）：系统 Media Foundation 无法解码，启动时不预加载，
            // 避免触发 MediaFailed 弹窗；点击播放时由 FFmpeg 引擎转码播放。
            if (AudioPlaybackEngine.NeedsFfmpeg(item.FilePath))
            {
                return;
            }

            // HiFi 独占模式：不把歌曲塞进 MediaPlayer（否则会走共享混音并出现在音量合成器）。
            // 就绪状态保留，由用户点播放时经 StartPlayback 走独占（NAudio）路径。
            if (IsHiFiModeSelected())
            {
                NotifyCurrentPlaylistWindow();
                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            try
            {
                MediaSource source = MediaSource.CreateFromUri(CreateFileMediaUri(item.FilePath));
                player.Source = source;
                if (AppSettingsStore.Load().AutoPlayWhenStart)
                {
                    player.Play();
                }
                else
                {
                    player.Pause();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("恢复加载媒体失败: " + ex.Message);
            }

            NotifyCurrentPlaylistWindow();
            _miniPlayerWindow?.RefreshFromOwner();
        }

        private void PersistPlaybackSession()
        {
            try
            {
                if (!AppSettingsStore.Load().RestorePlayback)
                {
                    return;
                }

                PlaylistItem? item = GetCurrentPlayingItem();
                if (item == null)
                {
                    return;
                }

                double pos = GetPlayer()?.PlaybackSession.Position.TotalSeconds ?? 0;
                PlaybackSessionStore.Save(item.FilePath, pos);
            }
            catch
            {
            }
        }


        private void PlaybackRateButton_Click(object sender, RoutedEventArgs e)
        {
            // 引擎（FFmpeg 转码播放）暂不支持变速
            if (_audioEngine?.IsPlaying == true || _isEnginePaused)
            {
                NowPlayingText.Text = "引擎播放暂不支持变速，请使用系统原生格式";
                return;
            }

            var flyout = new MenuFlyout();
            double current = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            foreach (double rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                double r = rate;
                var item = new MenuFlyoutItem { Text = r.ToString("0.##") + "x" };
                item.Click += (_, _) => SetPlaybackRate(r);
                flyout.Items.Add(item);
            }

            flyout.ShowAt(sender as FrameworkElement);
        }

        private void SetPlaybackRate(double rate)
        {
            AppSettingsStore.Update(s => s.PlaybackRate = Math.Clamp(rate, 0.5, 2.0));
            ApplyPlaybackRateFromSettings();
            UpdatePlaybackRateButtonText();
        }

        private void UpdatePlaybackRateButtonText()
        {
            if (PlaybackRateText == null)
            {
                return;
            }

            double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            PlaybackRateText.Text = rate.ToString("0.##") + "x";
        }

        private void ApplyAudioChannelFromSettings()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            string channel = AppSettingsStore.Load().AudioChannel;
            player.AudioBalance = channel switch
            {
                "Left" => -1f,
                "Right" => 1f,
                _ => 0f
            };
        }

        private void ApplyAlwaysOnTopFromSettings()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = AppSettingsStore.Load().AlwaysOnTop;
                }
            }
            catch
            {
            }
        }
        private MediaPlayer? GetPlayer() => _mediaPlayer ?? PlayerElement?.MediaPlayer;

        /// <summary>汉堡菜单：选择文件 / 文件夹 / 重新扫描</summary>
        private void SelectLocalAudioButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.Bottom
            };

            var fileItem = new MenuFlyoutItem { Text = "选择文件…" };
            fileItem.Icon = new FontIcon { Glyph = "\uE710" }; // Add
            fileItem.Click += OpenFileButton_Click;

            var folderItem = new MenuFlyoutItem { Text = "选择文件夹…" };
            folderItem.Icon = new FontIcon { Glyph = "\uE8B7" }; // Folder
            folderItem.Click += OpenFolderButton_Click;

            var rescanItem = new MenuFlyoutItem { Text = "重新扫描本地文件" };
            rescanItem.Icon = new FontIcon { Glyph = "\uE72C" }; // Refresh
            rescanItem.Click += RescanLocalLibraryButton_Click;

            flyout.Items.Add(fileItem);
            flyout.Items.Add(folderItem);
            flyout.Items.Add(rescanItem);
            AppendHamburgerFeatureItems(flyout);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var mediaLibItem = new MenuFlyoutItem { Text = "媒体库" };
            mediaLibItem.Icon = new FontIcon { Glyph = "\uE838" }; // MusicLibrary
            mediaLibItem.Click += (_, _) => SettingsWindow.ShowMediaLibrary();
            flyout.Items.Add(mediaLibItem);

            var settingsItem = new MenuFlyoutItem { Text = "选项设置" };
            settingsItem.Icon = new FontIcon { Glyph = "\uE713" };
            settingsItem.Click += (_, _) => SettingsWindow.ShowOrActivate();
            flyout.Items.Add(settingsItem);

            flyout.ShowAt(SelectLocalAudioButton, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom
            });
        }

        // =====================================================================
        // 打开文件 / 选择文件夹
        // =====================================================================

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                picker.FileTypeFilter.Add(".wma");
                picker.FileTypeFilter.Add(".ogg");
                picker.FileTypeFilter.Add(".aac");
                picker.FileTypeFilter.Add(".ape");
                picker.FileTypeFilter.Add(".wv");
                picker.FileTypeFilter.Add(".tta");
                picker.FileTypeFilter.Add(".mpc");
                picker.FileTypeFilter.Add(".tak");
                picker.FileTypeFilter.Add(".opus");
                picker.FileTypeFilter.Add(".dsf");
                picker.FileTypeFilter.Add(".dff");

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                string[] paths = files
                    .Select(f => f.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                LoadAndAddFiles(paths, persistAsFiles: true, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开文件失败", ex.Message);
            }
        }

        /// <summary>
        /// 选择文件夹：递归扫描其中所有支持的音频，再交给 LoadAndAddFiles。
        /// </summary>
        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
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

                // 后台枚举路径，避免大文件夹卡住 UI 太久；读标签仍在 LoadAndAddFiles
                string folderPath = folder.Path;
                _browseFolderPath = folderPath;

                string[] paths = await System.Threading.Tasks.Task.Run(() =>
                    EnumerateAudioFiles(folderPath).ToArray());

                LibrarySessionStore.SaveFolder(folderPath, paths);
                if (_currentCategory == "Folders")
                {
                    RefreshFolderBrowserRoots();
                }

                if (paths.Length == 0)
                {
                    await ShowErrorAsync("未找到音频", "该文件夹（含子文件夹）中没有支持的音频文件。");
                    return;
                }

                LoadAndAddFiles(paths, persist: false, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("选择文件夹失败", ex.Message);
            }
        }

        /// <summary>
        /// 按上次会话的文件夹或文件列表重新扫描，清空并替换当前音乐库展示。
        /// </summary>
        /// <summary>按媒体库设置过滤路径：移除缺失文件 / 忽略过短文件。</summary>
        private static string[] FilterLibraryPaths(IEnumerable<string> paths)
        {
            AppSettingsState s = AppSettingsStore.Load();
            var result = new List<string>();
            foreach (string path in paths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (s.RemoveMissingOnUpdate && !System.IO.File.Exists(path))
                {
                    continue;
                }

                if (s.IgnoreTooShortOnUpdate && s.FileTooShortSec > 0 && System.IO.File.Exists(path))
                {
                    try
                    {
                        using TagLib.File tagFile = TagLib.File.Create(path);
                        if (tagFile.Properties.Duration.TotalSeconds < s.FileTooShortSec)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }

                result.Add(path);
            }

            return result.ToArray();
        }

        private async void RescanLocalLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_libraryRescanInProgress)
            {
                return;
            }

            _libraryRescanInProgress = true;
            try
            {
                try
                {
                    await RescanLocalLibraryCoreAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorAsync("重新扫描失败", ex.Message);
                }
            }
            finally
            {
                _libraryRescanInProgress = false;
            }
        }

        private async Task RescanLocalLibraryCoreAsync()
        {
            try
            {
                LibrarySessionState? state = LibrarySessionStore.TryLoad();
                string? folderPath = !string.IsNullOrWhiteSpace(state?.FolderPath)
                    ? state!.FolderPath
                    : _browseFolderPath;

                bool folderMode = state != null
                    && string.Equals(state.Mode, "folder", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(folderPath)
                    && Directory.Exists(folderPath);

                string[] paths;
                if (folderMode)
                {
                    string scanRoot = folderPath!;
                    _browseFolderPath = scanRoot;
                    paths = await Task.Run(() => FilterLibraryPaths(EnumerateAudioFiles(scanRoot)));
                    LibrarySessionStore.SaveFolder(scanRoot, paths);
                }
                else
                {
                    // 文件列表模式：重读已保存路径；若会话缺失则用当前曲库路径
                    IEnumerable<string> sourcePaths = (state?.FilePaths != null && state.FilePaths.Count > 0)
                        ? state.FilePaths
                        : _playlist.Select(p => p.FilePath);

                    paths = FilterLibraryPaths(sourcePaths);

                    if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                    {
                        _browseFolderPath = folderPath;
                    }

                    if (paths.Length > 0)
                    {
                        LibrarySessionStore.SaveFiles(paths);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_browseFolderPath))
                {
                    RefreshFolderBrowserRoots();
                }

                if (paths.Length == 0)
                {
                    await ReplaceLibraryWithPaths(Array.Empty<string>(), persist: false);
                    await ShowErrorAsync("重新扫描", "未找到可读取的本地音频。请先通过「选择文件」或「选择文件夹」导入。");
                    return;
                }

                await ReplaceLibraryWithPaths(paths, persist: false);
                NowPlayingText.Text = $"已重新扫描，共 {_playlist.Count} 首";
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("重新扫描失败", ex.Message);
            }
        }

        /// <summary>把本地路径构造成 file URI,转义 # % ? 避免被解析为 fragment/query。</summary>
        private static Uri CreateFileMediaUri(string path)
        {
            string escaped = path
                .Replace("%", "%25")
                .Replace("#", "%23")
                .Replace("?", "%3F");
            return new Uri(escaped, UriKind.Absolute);
        }

        /// <summary>清空音乐库后按路径重建元数据，并刷新当前分类界面。
        /// 元数据读取放后台分批执行，并在扫描过程中以「扫描 xxx/xxx」更新左上角状态。</summary>
        private async System.Threading.Tasks.Task ReplaceLibraryWithPaths(string[] filePaths, bool persist)
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            bool rebindPlaylistView = ReferenceEquals(PlaylistView.ItemsSource, _playlist);
            if (rebindPlaylistView)
            {
                PlaylistView.ItemsSource = null;
            }

            _playlist.Clear();
            _albums.Clear();
            _artists.Clear();
            _albumTracks.Clear();
            _artistTracks.Clear();
            _artistAlbums.Clear();
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            if (filePaths.Length > 0)
            {
                int total = filePaths.Length;
                var built = new System.Collections.Generic.List<PlaylistItem>(total);
                // 后台分批读取元数据（纯文件/TagLib，不碰 UI），每处理一批派发一次进度到左上角状态
                await Task.Run(() =>
                {
                    var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int done = 0;
                    foreach (string path in filePaths)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                        {
                            continue;
                        }

                        if (!knownPaths.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            built.Add(CreatePlaylistItemFromPath(path));
                        }
                        catch (Exception ex)
                        {
                            knownPaths.Remove(path);
                            System.Diagnostics.Debug.WriteLine($"扫描加载失败: {path} → {ex.Message}");
                        }

                        done++;
                        if ((done & 15) == 0)
                        {
                            int d = done;
                            DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"扫描 {Math.Min(d, total)}/{total}");
                        }
                    }
                });

                DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"扫描完成，共 {built.Count} 首");
                foreach (PlaylistItem item in built)
                {
                    _playlist.Add(item);
                }
            }

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _playlist;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                UpdateCurrentIndexByPath(playingPath);
            }
            else
            {
                _currentIndex = -1;
            }

            ApplyCategoryView();

            if (persist)
            {
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }

        /// <summary>递归枚举文件夹内所有音频路径</summary>
        private static IEnumerable<string> EnumerateAudioFiles(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                yield break;
            }

            // 逐目录递归:某个受保护子目录(无权限)只跳过该目录,不影响其它文件
            foreach (string path in EnumerateAudioFilesRecursive(folderPath))
            {
                yield return path;
            }
        }

        private static IEnumerable<string> EnumerateAudioFilesRecursive(string folder)
        {
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(folder);
                files = Directory.GetFiles(folder);
            }
            catch
            {
                yield break;
            }

            foreach (string path in files)
            {
                string ext = Path.GetExtension(path);
                if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }

            foreach (string sub in subDirs)
            {
                foreach (string path in EnumerateAudioFilesRecursive(sub))
                {
                    yield return path;
                }
            }
        }

        /// <summary>根据路径读元数据并加入列表，然后按当前排序规则重排。</summary>
        /// <param name="persist">为 true 时写入上次会话（默认）。</param>
        /// <param name="persistAsFiles">为 true 时按「文件列表」模式保存整份播放列表。</param>
        private void LoadAndAddFiles(string[] filePaths, bool persist = true, bool persistAsFiles = false, bool replace = false)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return;
            }

            string? playingPath = null;
            if (replace)
            {
                // 替换模式：停止当前播放并清空媒体库，再载入新内容
                MediaPlayer? player = GetPlayer();
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                }

                StopEngineIfActive();
                _playlist.Clear();
                _currentIndex = -1;
                ClearNowPlayingPanel();
            }
            else
            {
                // 记住当前正在播的文件，排序后要能找回下标
                playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    ? _playlist[_currentIndex].FilePath
                    : null;
            }

            // HashSet 去重，避免大批量导入时对 _playlist 反复线性扫描
            var knownPaths = new HashSet<string>(
                _playlist.Select(i => i.FilePath),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;

            foreach (string path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    continue;
                }

                if (!knownPaths.Add(path))
                {
                    continue;
                }

                try
                {
                    PlaylistItem item = CreatePlaylistItemFromPath(path);
                    _playlist.Add(item);
                    added++;
                }
                catch (Exception ex)
                {
                    knownPaths.Remove(path);
                    System.Diagnostics.Debug.WriteLine($"加载失败: {path} → {ex.Message}");
                }
            }

            if (added == 0)
            {
                if (replace)
                {
                    ApplyCategoryView();
                }

                return;
            }

            // 当前在「歌曲」分类时，按标题默认排序刷新列表
            if (_currentCategory == "Songs")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Albums")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Artists" || _currentCategory == "AlbumArtists")
            {
                ApplyCategoryView();
            }
            else
            {
                ApplySort(preservePlayingPath: playingPath);
            }

            // 不自动播放：用户双击或点播放后再开始
            if (added > 0)
            {
                NowPlayingText.Text = $"已添加 {added} 首，共 {_playlist.Count} 首";
            }

            if (persist && persistAsFiles)
            {
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }

        /// <summary>TagLib 读取标题 / 艺术家 / 专辑 / 音轨号 / 年份 / 时长</summary>
        private static PlaylistItem CreatePlaylistItemFromPath(string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string artist = "未知艺术家";
            string albumArtist = "未知艺术家";
            string album = "未知专辑";
            uint track = 0;
            uint disc = 0;
            uint year = 0;
            string genre = "未知流派";
            TimeSpan duration = TimeSpan.Zero;

            using (TagLib.File tagFile = TagLib.File.Create(path))
            {
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
                disc = tagFile.Tag.Disc;
                year = tagFile.Tag.Year;
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                {
                    genre = tagFile.Tag.FirstGenre.Trim();
                }

                duration = tagFile.Properties.Duration;
            }

            return new PlaylistItem
            {
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Track = track,
                Disc = disc,
                Year = year,
                Genre = genre,
                Duration = duration,
                DurationText = FormatTime(duration),
                FilePath = path
            };
        }

        // =====================================================================
        // 左侧分类导航
        // =====================================================================

        private void CategoryNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                && _openedAlbum == null
                && _openedArtist == null)
            {
                return;
            }

            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = tag;
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }

        private void UserPlaylistNavButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "PlaylistWall";
                ApplyCategoryView();
            });
        }

        private void NavTagSortButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "TagSort";
                ApplyCategoryView();
            });
        }

        // 音效处理按钮：占位入口（后续阶段接入 ECHO 音效处理页面）
        private void NavAudioFxButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AudioFX";
                ApplyCategoryView();
            });
            // 占位提示：音效处理页面后续接通
            NowPlayingText.Text = "音效处理功能即将上线（暂未接入）";
        }

        // ---------------- 标签排序板块（按曲目元数据字段逐级分组钻取） ----------------

        private void BreakoutTagSortView()
        {
            TagSortBorder.Visibility = Visibility.Visible;
            ApplyTagSortFieldButtons();
            ShowTagSortClassWall();
        }

        /// <summary>高亮当前分类字段按钮（五个维度切换）。</summary>
        private void ApplyTagSortFieldButtons()
        {
            var accent = ResolveAccentBrush();
            var fg = ResolveContrastingForeground(accent);
            var idleBg = ResolveCapsuleFillBrush();
            var border = ResolveNavCapsuleBorderBrush();
            void Update(Button b)
            {
                bool active = string.Equals(b.Tag as string, _tagSortClassField, StringComparison.Ordinal);
                if (active) { b.Background = accent; b.Foreground = fg; b.BorderThickness = new Thickness(0); }
                else { b.Background = idleBg; b.ClearValue(Control.ForegroundProperty); b.BorderThickness = new Thickness(1); b.BorderBrush = border; }
            }
            Update(TagSortFieldArtistButton);
            Update(TagSortFieldAlbumArtistButton);
            Update(TagSortFieldAlbumButton);
            Update(TagSortFieldGenreButton);
            Update(TagSortFieldYearButton);
        }

        /// <summary>按分类字段值取归一化键（Artist/AlbumArtist 等）。</summary>
        private static string TagSortFieldVal(PlaylistItem p, string field)
        {
            return field switch
            {
                "Artist" => string.IsNullOrWhiteSpace(p.Artist) ? "未知" : p.Artist.Trim(),
                "AlbumArtist" => string.IsNullOrWhiteSpace(p.AlbumArtist) ? "未知" : p.AlbumArtist.Trim(),
                "Album" => string.IsNullOrWhiteSpace(p.Album) ? "未知" : p.Album.Trim(),
                "Genre" => string.IsNullOrWhiteSpace(p.Genre) ? "未知" : p.Genre.Trim(),
                "Year" => p.Year > 0 ? p.Year.ToString() : "未知",
                _ => "未知"
            };
        }

        private void TagSortFieldBoundButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string field)
            {
                _tagSortClassField = field;
                _tagSortClassValue = string.Empty;
                ApplyTagSortFieldButtons();
                ShowTagSortClassWall();
            }
        }

        /// <summary>刷新分类墙：按 _tagSortClassField 分组 _playlist，每组分封面（首曲封面）。</summary>
        private void ShowTagSortClassWall()
        {
            TagSortPanel.Visibility = Visibility.Collapsed;
            TagSortClassScroll.Visibility = Visibility.Visible;
            var entries = _playlist
                .GroupBy(p => TagSortFieldVal(p, _tagSortClassField), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagSortCategoryEntry
                {
                    Name = g.Key,
                    Count = g.Count(),
                    FirstFilePath = g.First().FilePath
                })
                .ToList();
            TagSortClassGridView.ItemsSource = entries;
            foreach (var e in entries)
            {
                _ = LoadTagSortCategoryCoverAsync(e);
            }
        }

        // 标签排序分类封面：封面字节内存缓存 + 并发控制（缓存命中免重复读文件/解码，避免大量分类同时打满线程池与 UI 线程）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> TagSortCoverBytesCache = new();
        private static readonly System.Threading.SemaphoreSlim TagSortCoverGate = new(4);

        private async System.Threading.Tasks.Task LoadTagSortCategoryCoverAsync(TagSortCategoryEntry entry)
        {
            try
            {
                string key = entry?.FirstFilePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                byte[]? bytes = null;
                await TagSortCoverGate.WaitAsync(); // 限流并发提取，避免几十个专辑同时解码拖慢 UI
                try
                {
                    if (!TagSortCoverBytesCache.TryGetValue(key, out bytes) || bytes == null || bytes.Length == 0)
                    {
                        bytes = await Task.Run(() => ExtractCoverBytes(key));
                        if (bytes is { Length: > 0 })
                        {
                            TagSortCoverBytesCache[key] = bytes;
                        }
                    }
                }
                finally
                {
                    TagSortCoverGate.Release();
                }

                if (bytes is { Length: > 0 })
                {
                    var bmp = await CreateBitmapFromBytesAsync(bytes);
                    if (bmp != null) entry.Cover = bmp;
                }
            }
            catch
            {
            }
        }

        private void TagSortClassGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TagSortCategoryEntry entry)
            {
                _tagSortClassValue = entry.Name;
                _tagSortPanelMode = "Songs";
                ShowTagSortPanel();
            }
        }

        /// <summary>进入分类面板：过滤当前分类下的曲目，按 _tagSortPanelMode 展示。</summary>
        private void ShowTagSortPanel()
        {
            TagSortClassScroll.Visibility = Visibility.Collapsed;
            TagSortPanel.Visibility = Visibility.Visible;
            _tagSortClassSongs.Clear();
            foreach (var p in _playlist)
            {
                if (string.Equals(TagSortFieldVal(p, _tagSortClassField), _tagSortClassValue, StringComparison.Ordinal))
                {
                    _tagSortClassSongs.Add(p);
                }
            }
            TagSortPanelTitle.Text = TagSortClassFieldLabel(_tagSortClassField) + "：" + _tagSortClassValue;
            ApplyTagSortPanelMode();
        }

        private static string TagSortClassFieldLabel(string field)
        {
            return field switch
            {
                "Artist" => "艺术家", "AlbumArtist" => "专辑艺术家", "Album" => "专辑",
                "Genre" => "流派", "Year" => "年份", _ => field
            };
        }

        /// <summary>排序字段的中文标签（给排序依据状态显示用）。</summary>
        private static string TagSortFieldLabel(string field)
        {
            return field switch
            {
                "Artist" => "艺术家", "AlbumArtist" => "专辑艺术家", "Album" => "专辑",
                "Genre" => "流派", "Year" => "年份", "Title" => "标题",
                "Track" => "音轨号", "Disc" => "碟片号", _ => field
            };
        }

        /// <summary>按当前面板视角（Songs/Albums/Artists/Sort）渲染内容区。</summary>
        private void ApplyTagSortPanelMode()
        {
            TagSortPanelGridView.Visibility = Visibility.Collapsed;
            TagSortPanelSongListView.Visibility = Visibility.Collapsed;
            TagSortSortPanel.Visibility = Visibility.Collapsed;
            TagSortViewModeButton.Content = _tagSortPanelMode switch
            {
                "Albums" => "专辑", "Artists" => "艺术家", "Sort" => "排序方式", _ => "曲目"
            };

            if (_tagSortPanelMode == "Albums")
            {
                var albums = _tagSortClassSongs
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.Album) ? "未知" : p.Album, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new TagSortCategoryEntry { Name = g.Key, Count = g.Count(), FirstFilePath = g.First().FilePath, Sub = "Album" })
                    .ToList();
                TagSortPanelGridView.Visibility = Visibility.Visible;
                TagSortPanelGridView.ItemsSource = albums;
                foreach (var e in albums) _ = LoadTagSortCategoryCoverAsync(e);
            }
            else if (_tagSortPanelMode == "Artists")
            {
                var artists = _tagSortClassSongs
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.Artist) ? "未知" : p.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new TagSortCategoryEntry { Name = g.Key, Count = g.Count(), FirstFilePath = g.First().FilePath, Sub = "Artist" })
                    .ToList();
                TagSortPanelGridView.Visibility = Visibility.Visible;
                TagSortPanelGridView.ItemsSource = artists;
                foreach (var e in artists) _ = LoadTagSortCategoryCoverAsync(e);
            }
            else if (_tagSortPanelMode == "Sort")
            {
                TagSortSortPanel.Visibility = Visibility.Visible;
                WriteTagSortStatus();
            }
            else // Songs
            {
                var songs = new ObservableCollection<PlaylistItem>();
                for (int i = 0; i < _tagSortClassSongs.Count; i++)
                {
                    _tagSortClassSongs[i].Index = i + 1;
                    songs.Add(_tagSortClassSongs[i]);
                }
                TagSortPanelSongListView.ItemsSource = songs;
                TagSortPanelSongListView.Visibility = Visibility.Visible;
            }
        }

        private void TagSortPanelBackButton_Click(object sender, RoutedEventArgs e)
        {
            _tagSortClassValue = string.Empty;
            ShowTagSortClassWall();
        }

        private void TagSortViewModeItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string mode)
            {
                _tagSortPanelMode = mode;
                ApplyTagSortPanelMode();
            }
        }

        private void TagSortPanelGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            // 专辑/艺术家视角：点某项 → 钻取到该值的曲目列表（切到“曲目”视角显示该子集）
            if (e.ClickedItem is TagSortCategoryEntry cat)
            {
                string field = cat.Sub == "Artist" ? "Artist" : "Album";
                _tagSortPanelMode = "Songs";
                // 过滤当前分类下再按该子值
                var filtered = _tagSortClassSongs.Where(p => string.Equals(
                    field == "Artist" ? (p.Artist ?? "") : (p.Album ?? ""), cat.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                _tagSortClassSongs.Clear();
                foreach (var s in filtered) _tagSortClassSongs.Add(s);
                TagSortPanelTitle.Text = field == "Artist" ? "艺术家：" : "专辑：" + cat.Name;
                ApplyTagSortPanelMode();
            }
        }

        /// <summary>标签排序信息列表「播放当前列表歌曲」：按当前列表顺序替换播放队列并从第一首播放。</summary>
        private void TagSortPanelPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tagSortClassSongs.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_tagSortClassSongs.ToList());
            PlayUserPlaylistAt(0);
        }

        private void TagSortPanelSongListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                PlayPlaylistItem(song);
            }
        }

        private void TagSortPanelSongList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(TagSortPanelSongListView, container, song);
            }
        }

        private void TagSortPanelSongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshRealizedSongListSelectionChrome(TagSortPanelSongListView);
        }

        private void TagSortPanelSongListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                _multiSelectTargetList = TagSortPanelSongListView;
                var flyout = BuildPlaylistItemContextMenu(song, inUserPlaylist: false,
                    multiSelectAction: () => EnterMultiSelectModeFrom(TagSortPanelSongListView));
                flyout.ShowAt(TagSortPanelSongListView, e.GetPosition(TagSortPanelSongListView));
            }
        }

        private void TagSortClassGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TagSortCategoryEntry entry)
            {
                ShowTagSortCategoryMenu(entry, TagSortClassGridView, e.GetPosition(TagSortClassGridView));
            }
        }

        private void TagSortPanelGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TagSortCategoryEntry entry)
            {
                var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

                var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE700" } };
                multi.Click += (_, _) => EnterTagSortPanelGridMultiSelect();
                flyout.Items.Add(multi);

                var play = new MenuFlyoutItem
                {
                    Text = entry.Sub == "Artist" ? "播放该艺术家" : "播放该专辑",
                    Icon = new FontIcon { Glyph = "\uE768" }
                };
                play.Click += (_, _) =>
                {
                    var songs = CollectTagSortCategorySongs(entry);
                    if (songs.Count > 0)
                    {
                        if (entry.Sub == "Album")
                        {
                            var a = BuildTagSortAlbumEntry(entry);
                            PlayAlbum(a, replacePlaylist: true);
                        }
                        else
                        {
                            PlayPlaylistItem(songs[0]);
                        }
                    }
                };
                flyout.Items.Add(play);

                var addQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
                addQueue.Click += (_, _) => AddSongsToUserPlaylist(CollectTagSortCategorySongs(entry));
                flyout.Items.Add(addQueue);

                if (entry.Sub == "Album")
                {
                    var albumEntry = BuildTagSortAlbumEntry(entry);

                    var wallAlbum = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                    wallAlbum.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumEntry));
                    flyout.Items.Add(wallAlbum);

                    // 批量下载歌词 / 封面 / 复制专辑信息 / 打开专辑详情（复用专辑墙通用项）
                    AppendAlbumContextItems(flyout, albumEntry, fromArtist: false);
                }

                flyout.ShowAt(TagSortPanelGridView, e.GetPosition(TagSortPanelGridView));
            }
        }

        /// <summary>把标签排序面板的专辑项构造成 AlbumEntry（Artist 留空 → 按专辑名匹配曲目）供复用现有专辑操作。</summary>
        private AlbumEntry BuildTagSortAlbumEntry(TagSortCategoryEntry entry)
        {
            return new AlbumEntry
            {
                Name = entry.Name,
                Artist = string.Empty,
                CoverSourcePath = entry.FirstFilePath
            };
        }

        /// <summary>标签排序面板专辑/艺术家网格进入多选：勾选多张专辑/艺术家，主按钮添加到播放队列。</summary>
        private void EnterTagSortPanelGridMultiSelect()
        {
            if (_multiSelectTargetList != null) ExitSongMultiSelectUiOnly();
            if (_multiSelectFolderList != null) ExitFolderMultiSelectUiOnly();
            _multiSelectAlbumGrid = TagSortPanelGridView;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;
            TagSortPanelGridView.SelectionMode = ListViewSelectionMode.Multiple;
            TagSortPanelGridView.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
        }

        /// <summary>分类墙 / 面板网格项的右键菜单：播放该分类全部 / 添加到播放队列。</summary>
        private void ShowTagSortCategoryMenu(TagSortCategoryEntry entry, FrameworkElement anchor, Windows.Foundation.Point point)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            bool isAlbumField = string.Equals(_tagSortClassField, "Album", StringComparison.Ordinal);

            var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE700" } };
            multi.Click += (_, _) => EnterTagSortClassWallMultiSelect();
            flyout.Items.Add(multi);

            var play = new MenuFlyoutItem
            {
                Text = isAlbumField ? "播放该专辑" : "播放该分类",
                Icon = new FontIcon { Glyph = "\uE768" }
            };
            play.Click += (_, _) =>
            {
                var songs = CollectTagSortCategorySongs(entry);
                if (songs.Count == 0) return;
                if (isAlbumField)
                {
                    PlayAlbum(BuildTagSortAlbumEntry(entry), replacePlaylist: true);
                }
                else
                {
                    PlayPlaylistItem(songs[0]);
                }
            };
            flyout.Items.Add(play);

            var addQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
            addQueue.Click += (_, _) => AddSongsToUserPlaylist(CollectTagSortCategorySongs(entry));
            flyout.Items.Add(addQueue);

            if (isAlbumField)
            {
                var albumEntry = BuildTagSortAlbumEntry(entry);
                var wallAlbum = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                wallAlbum.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumEntry));
                flyout.Items.Add(wallAlbum);
                AppendAlbumContextItems(flyout, albumEntry, fromArtist: false); // 批量歌词/封面/复制信息/打开详情
            }

            flyout.ShowAt(anchor, point);
        }

        /// <summary>分类墙进入多选：把 TagSortClassGridView 设多选、勾选分组项；主按钮将选中分类曲目加入播放队列。</summary>
        private void EnterTagSortClassWallMultiSelect()
        {
            if (_multiSelectTargetList != null) ExitSongMultiSelectUiOnly();
            if (_multiSelectFolderList != null) ExitFolderMultiSelectUiOnly();
            _multiSelectAlbumGrid = TagSortClassGridView;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;
            TagSortClassGridView.SelectionMode = ListViewSelectionMode.Multiple;
            TagSortClassGridView.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
        }

        /// <summary>取分类墙 / 面板网格一个卡片（某字段值）对应的全部曲目。
        /// 优先用 entry.Sub 指定的字段（面板专辑/艺术家视角），否则用当前分类字段。</summary>
        private List<PlaylistItem> CollectTagSortCategorySongs(TagSortCategoryEntry entry)
        {
            string field = string.IsNullOrWhiteSpace(entry.Sub) ? _tagSortClassField : entry.Sub;
            return _playlist
                .Where(p => string.Equals(TagSortFieldVal(p, field), entry.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>从指定列表进入多选（标签排序面板曲目）。</summary>
        private void EnterMultiSelectModeFrom(ListView list)
        {
            if (list == null) return;
            _multiSelectTargetList = list;
            EnterMultiSelectMode((list.SelectedItems.FirstOrDefault() as PlaylistItem) ?? null);
        }

        // ---------------- 排序方式（排序主面板歌曲顺序） ----------------

        private void TagSortPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagSortPresetCombo.SelectedItem is ComboBoxItem cbi)
            {
                string key = cbi.Content?.ToString() ?? string.Empty;
                _tagSortCustom = PresetToFields(key);
                _tagSortPreset = key;
                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            }
        }

        private void TagSortOrderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 由用户点击触发（Click 不会因程序化改 IsChecked 而重入）。
                bool? asc = TagSortAscButton?.IsChecked == true;
                bool? desc = TagSortDescButton?.IsChecked == true;
                if ((e.OriginalSource is FrameworkElement fe && ReferenceEquals(fe, TagSortDescButton))
                    || desc == true)
                {
                    _tagSortAscending = false;
                    if (TagSortAscButton != null) TagSortAscButton.IsChecked = false;
                    if (TagSortDescButton != null) TagSortDescButton.IsChecked = true;
                }
                else if (asc == true)
                {
                    _tagSortAscending = true;
                    if (TagSortDescButton != null) TagSortDescButton.IsChecked = false;
                    if (TagSortAscButton != null) TagSortAscButton.IsChecked = true;
                }

                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TagSortOrderClick", ex);
            }
        }

        private void TagSortCustomSortButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new CustomSortOrderWindow(_tagSortCustom, _tagSortAscending);
            win.SortConfirmed += (fields, asc) =>
            {
                _tagSortCustom = fields;
                _tagSortAscending = asc;
                _tagSortPreset = "自定义";
                TagSortPresetCombo.SelectedItem = null;
                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            };
            win.Activate();
        }

        /// <summary>预设字符串 → 排序字段链。</summary>
        private static List<(string field, bool asc)> PresetToFields(string preset)
        {
            var asc = new[] {
                ("Album", "专辑"),
                ("AlbumArtist,Album", "专辑艺术家 / 专辑"),
                ("AlbumArtist,Year,Album", "专辑艺术家 / 年份 / 专辑"),
                ("Artist,Album", "艺术家 / 专辑"),
                ("Genre,Album", "流派 / 专辑"),
                ("Year,Album", "年份 / 专辑")
            };
            foreach (var (fields, label) in asc)
            {
                if (preset == label)
                {
                    return fields.Split(',').Select(f => (f, true)).ToList();
                }
            }
            return new List<(string, bool)>();
        }

        /// <summary>把当前排序方式应用到轨道库（主面板歌曲顺序）。</summary>
        private void ApplyTagSortToLibrary()
        {
            if (_tagSortCustom == null || _tagSortCustom.Count == 0)
            {
                return;
            }

            IOrderedEnumerable<PlaylistItem> sorted = _tagSortAscending
                ? _playlist.OrderBy(p => TagSortFieldVal(p, _tagSortCustom[0].field), StringComparer.CurrentCultureIgnoreCase)
                : _playlist.OrderByDescending(p => TagSortFieldVal(p, _tagSortCustom[0].field), StringComparer.CurrentCultureIgnoreCase);

            for (int i = 1; i < _tagSortCustom.Count; i++)
            {
                bool a = _tagSortCustom[i].asc;
                sorted = a
                    ? sorted.ThenBy(p => TagSortFieldVal(p, _tagSortCustom[i].field), StringComparer.CurrentCultureIgnoreCase)
                    : sorted.ThenByDescending(p => TagSortFieldVal(p, _tagSortCustom[i].field), StringComparer.CurrentCultureIgnoreCase);
            }

            var list = sorted.ToList();
            _playlist.Clear();
            foreach (var p in list) _playlist.Add(p);
            RenumberCollection(_playlist);
            if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _playlist; // 歌曲分类主列表即按此顺序
            }
        }

        private void WriteTagSortStatus()
        {
            string desc = _tagSortPreset;
            var tags = _tagSortCustom.Count == 0 ? new List<(string field, bool asc)>() : _tagSortCustom;
            if (tags.Count > 0)
            {
                desc += "（" + string.Join(" → ", tags.Select(t => TagSortFieldLabel(t.field) + (t.asc ? "↑" : "↓"))) + "）";
            }
            TagSortSortStatusText.Text = "当前排序依据：" + desc;
        }

        private void NavPlaylistWallButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "UserPlaylist";
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }

        /// <summary>填充播放列表墙（命名单封面卡片）。</summary>
        private void ApplyPlaylistWallCategory()
        {
            _playlistWall.Clear();
            PlaylistLibraryService.Refresh();
            foreach (var p in PlaylistLibraryService.Items)
            {
                // 列表墙只显示用户真实命名单：过滤内建“我喜欢的音乐”与空列表
                if (string.Equals(p.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    continue;
                }

                var vm = new PlaylistCardViewModel
                {
                    Name = p.Name,
                    SongCountText = NamedPlaylistStore.LoadSongs(p.Name).Count + " 首",
                };
                _ = LoadPlaylistWallCoverAsync(vm);
                _playlistWall.Add(vm);
            }

            // 无任何命中单时显示空状态提示
            UpdatePlaylistWallEmptyHint();
        }

        private void UpdatePlaylistWallEmptyHint()
        {
            if (PlaylistWallEmptyHint == null)
            {
                return;
            }

            PlaylistWallEmptyHint.Visibility =
                _playlistWall.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async System.Threading.Tasks.Task LoadPlaylistWallCoverAsync(PlaylistCardViewModel vm)
        {
            try
            {
                // 优先从列表歌曲取首曲封面（第一首含封面的）；歌曲全无封面时才回落用户手动设的自定义封面。
                byte[]? bytes = await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (string path in NamedPlaylistStore.LoadSongs(vm.Name))
                    {
                        if (!System.IO.File.Exists(path)) continue;
                        try
                        {
                            byte[]? b = ExtractCoverBytes(path);
                            if (b is { Length: > 0 }) return b;
                        }
                        catch
                        {
                        }
                    }
                    return (byte[]?)null;
                });
                if (bytes is not { Length: > 0 })
                {
                    // 歌曲全无封面：回落到用户手动设置的自定义封面；仍无则以 Cover=null 由 View 图标兜底。
                    string? custom = PlaylistLibraryService.CustomCoverPath(vm.Name);
                    if (!string.IsNullOrWhiteSpace(custom))
                    {
                        bytes = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllBytes(custom));
                    }
                }
                if (bytes is not { Length: > 0 })
                {
                    return;
                }
                var bmp = await CreateBitmapFromBytesAsync(bytes); // 与专辑封面同一机制
                if (bmp != null)
                {
                    vm.Cover = bmp;
                }
            }
            catch
            {
            }
        }

        private void PlaylistWallGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is PlaylistCardViewModel vm)
            {
                ShowPlaylistDetail(vm.Name);
            }
        }

        private void ShowPlaylistDetail(string name)
        {
            _currentPlaylistDetail = name;
            FillPlaylistDetailItems();
            PlaylistDetailNameText.Text = name;
            PlaylistDetailCountText.Text = _playlistDetailItems.Count + " 首";
            PlaylistDetailListView.ItemsSource = _playlistDetailItems;
            _ = LoadPlaylistDetailCoverAsync(_playlistDetailItems.FirstOrDefault()?.FilePath, name);
            ApplyCategoryView();
        }

        private void FillPlaylistDetailItems()
        {
            _playlistDetailItems.Clear();
            if (string.IsNullOrEmpty(_currentPlaylistDetail)) return;
            int ordinal = 1;
            foreach (string path in NamedPlaylistStore.LoadSongs(_currentPlaylistDetail))
            {
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    var item = CreatePlaylistItemFromPath(path);
                    item.Index = ordinal++;
                    _playlistDetailItems.Add(item);
                }
                catch
                {
                }
            }
        }

        private void PlaylistDetailListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            // 顺序已由 ObservableCollection 自动更新；保存新顺序回命名单
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                for (int i = 0; i < _playlistDetailItems.Count; i++)
                {
                    _playlistDetailItems[i].Index = i + 1; // 拖拽后重排连续序号
                }
                // Index 为 x:Bind OneTime 绑定，重设后需强制刷新才会更新序号
                PlaylistDetailListView.ItemsSource = null;
                PlaylistDetailListView.ItemsSource = _playlistDetailItems;
                NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
            }
        }

        private void PlaylistDetailSortMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string field)
            {
                SortPlaylistDetailItems(field);
            }
        }

        /// <summary>按指定字段对命名单详情排序并保存回命名单。排序后重排连续序号。</summary>
        private void SortPlaylistDetailItems(string field)
        {
            if (_playlistDetailItems.Count <= 1) return;
            try
            {
                List<PlaylistItem> sorted = field switch
                {
                    "Title" => _playlistDetailItems
                        .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.Ordinal)
                        .ToList(),
                    "Artist" => _playlistDetailItems
                        .OrderBy(p => p.Artist, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.Ordinal)
                        .ToList(),
                    "Album" => _playlistDetailItems
                        .OrderBy(p => p.Album, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Track).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Track" => _playlistDetailItems.OrderBy(p => p.Track).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Year" => _playlistDetailItems.OrderBy(p => p.Year).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Duration" => _playlistDetailItems.OrderBy(p => p.Duration).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "FilePath" => _playlistDetailItems
                        .OrderBy(p => p.FilePath, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.FilePath, StringComparer.Ordinal)
                        .ToList(),
                    _ => _playlistDetailItems.ToList()
                };

                for (int i = 0; i < sorted.Count; i++)
                {
                    sorted[i].Index = i + 1;
                }
                _playlistDetailItems.Clear();
                foreach (var p in sorted) _playlistDetailItems.Add(p);

                if (!string.IsNullOrEmpty(_currentPlaylistDetail))
                {
                    NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
                }
            }
            catch
            {
            }
        }

        /// <summary>从当前命名单详情删除勾选的歌曲，重排连续序号并保存回命名单。</summary>
        private void RemoveSongsFromCurrentPlaylistDetail(IEnumerable<PlaylistItem> selected)
        {
            try
            {
                var removes = selected.Select(s => s.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var keep = _playlistDetailItems.Where(p => !removes.Contains(p.FilePath)).ToList();
                _playlistDetailItems.Clear();
                for (int i = 0; i < keep.Count; i++)
                {
                    keep[i].Index = i + 1;
                    _playlistDetailItems.Add(keep[i]);
                }

                if (!string.IsNullOrEmpty(_currentPlaylistDetail))
                {
                    NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
                    PlaylistDetailCountText.Text = _playlistDetailItems.Count + " 首";
                }
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task LoadPlaylistDetailCoverAsync(string? firstTrack, string name)
        {
            try
            {
                byte[]? bytes = null;
                string? custom = PlaylistLibraryService.CustomCoverPath(name);
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    bytes = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllBytes(custom));
                }
                else if (!string.IsNullOrWhiteSpace(firstTrack))
                {
                    bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(firstTrack));
                }

                if (bytes is { Length: > 0 })
                {
                    var bmp = await CreateBitmapFromBytesAsync(bytes); // 与专辑封面同一机制
                    if (bmp != null)
                    {
                        PlaylistDetailCoverImage.Source = bmp;
                    }
                }
            }
            catch
            {
            }
        }

        private void PlaylistDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            _currentPlaylistDetail = null;
            ApplyCategoryView();
            ApplyPlaylistWallCategory();
        }

        private void PlaylistDetailPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                _ = LoadNamedPlaylistToQueueAndPlayAsync(_currentPlaylistDetail);
            }
        }

        private void PlaylistDetailAddQueueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                AddNamedPlaylistToQueue(_currentPlaylistDetail);
            }
        }

        private void PlaylistDetailAddWallButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPlaylistDetail)) return;
            _ = ShowNamedPlaylistPickerAsync(_playlistDetailItems.ToList());
        }

        private void SongRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || IsSongInListSelected(chrome, dc)) return;
            chrome.Background = PlaylistDetailHoverBg;
        }

        private void SongRow_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || IsSongInListSelected(chrome, dc)) return;
            chrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private ListView? FindAncestorListView(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is ListView lv) return lv;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private bool IsSongInListSelected(DependencyObject node, PlaylistItem song)
        {
            try
            {
                ListView? list = FindAncestorListView(node);
                if (list == null) return false;
                return list.SelectedItems.Contains(song);
            }
            catch
            {
                return false;
            }
        }

        private void PlaylistDetailRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || PlaylistDetailRowIsSelected(dc)) return;
            chrome.Background = PlaylistDetailHoverBg;
        }

        private void PlaylistDetailRow_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || PlaylistDetailRowIsSelected(dc)) return;
            chrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private bool PlaylistDetailRowIsSelected(PlaylistItem song)
        {
            try
            {
                return PlaylistDetailListView.SelectedItems.Contains(song);
            }
            catch
            {
                return false;
            }
        }

        private void PlaylistDetailListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(PlaylistDetailListView, container, song);
                if (!args.InRecycleQueue && args.Phase == 0)
                {
                    LoadRowCoverAsync(PlaylistDetailListView, container, song);
                }
            }
        }

        private void PlaylistDetailListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshRealizedSongListSelectionChrome(PlaylistDetailListView);
        }

        private void PlaylistDetailListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // 仅当实际点到歌曲行时才播放（空白区/容器双击不触发整列表播放）
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = PlaylistDetailListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song != null && !string.IsNullOrEmpty(_currentPlaylistDetail) && !string.IsNullOrEmpty(song.FilePath))
            {
                PlayNamedPlaylistFromTrack(_currentPlaylistDetail, song.FilePath);
            }
        }

        /// <summary>命中单详情双击/指定某首先：把命名单载入当前队列并定位播放该首。</summary>
        internal void PlayNamedPlaylistFromTrack(string name, string filePath)
        {
            try
            {
                var items = new List<PlaylistItem>();
                foreach (string path in NamedPlaylistStore.LoadSongs(name))
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try { items.Add(CreatePlaylistItemFromPath(path)); } catch { }
                }

                if (items.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(items);
                int index = FindUserPlaylistIndex(filePath);
                PlayUserPlaylistAt(index >= 0 ? index : 0);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PlayNamedPlaylistFromTrack", ex);
            }
        }

        private void PlaylistDetailListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var song = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistItem;
            if (song == null) return;
            var flyout = BuildPlaylistItemContextMenu(song, false);
            (e.OriginalSource as FrameworkElement)?.DispatcherQueue.TryEnqueue(() => flyout.ShowAt(PlaylistDetailListView, e.GetPosition(PlaylistDetailListView)));
        }

        private void PlaylistWallGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistCardViewModel? vm = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistCardViewModel;
            if (vm == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { Glyph = "\uE8AC" } };
            rename.Click += async (_, _) => await RenamePlaylistFromWallAsync(vm);
            flyout.Items.Add(rename);
            var addToQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
            addToQueue.Click += (_, _) => AddNamedPlaylistToQueue(vm.Name);
            flyout.Items.Add(addToQueue);
            var delete = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { Glyph = "\uE74D" } };
            delete.Click += (_, _) =>
            {
                if (!string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    NamedPlaylistStore.Delete(vm.Name);
                    PlaylistLibraryService.ClearCustomCover(vm.Name);
                    PlaylistLibraryService.Refresh();
                    ApplyPlaylistWallCategory();
                }
            };
            flyout.Items.Add(delete);
            var exportOne = new MenuFlyoutItem { Text = "导出（m3u8）", Icon = new FontIcon { Glyph = "\uE896" } };
            exportOne.Click += async (_, _) => await ExportOnePlaylistAsync(vm);
            flyout.Items.Add(exportOne);
            var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE8B1" } };
            multi.Click += (_, _) => EnterPlaylistWallMultiSelect(vm);
            flyout.Items.Add(multi);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var local = new MenuFlyoutItem { Text = "设置封面（本地图片…）", Icon = new FontIcon { Glyph = "\uE710" } };
            local.Click += async (_, _) => await SetPlaylistCoverFromLocalAsync(vm);
            flyout.Items.Add(local);
            var web = new MenuFlyoutItem { Text = "从网络搜索封面…", Icon = new FontIcon { Glyph = "\uE774" } };
            web.Click += async (_, _) => await SetPlaylistCoverFromWebAsync(vm);
            flyout.Items.Add(web);
            var restore = new MenuFlyoutItem { Text = "恢复默认（首曲封面）", Icon = new FontIcon { Glyph = "\uE74D" } };
            restore.Click += (_, _) =>
            {
                PlaylistLibraryService.ClearCustomCover(vm.Name);
                ApplyPlaylistWallCategory();
            };
            flyout.Items.Add(restore);

            flyout.ShowAt(PlaylistWallGridView, e.GetPosition(PlaylistWallGridView));
        }

        private async System.Threading.Tasks.Task SetPlaylistCoverFromLocalAsync(PlaylistCardViewModel vm)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                PlaylistLibraryService.WriteCustomCover(vm.Name, ms.ToArray());
                ApplyPlaylistWallCategory();
            }
            catch
            {
            }
        }

        /// <summary>给 ContentDialog 的按钮设置当前主题色（局部资源覆盖，不改全局、避免运行时覆盖 Application.Resources 崩溃）。</summary>
        private static void ApplyDialogAccent(Microsoft.UI.Xaml.Controls.ContentDialog dlg)
        {
            try
            {
                Windows.UI.Color accent = ThemeColorService.CurrentAccent;
                var accentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
                Windows.UI.Color pressed = Windows.UI.Color.FromArgb(255, (byte)(accent.R * 0.7), (byte)(accent.G * 0.7), (byte)(accent.B * 0.7));
                Windows.UI.Color disabled = Windows.UI.Color.FromArgb(255, (byte)(accent.R * 0.3), (byte)(accent.G * 0.3), (byte)(accent.B * 0.3));
                dlg.Resources["AccentButtonBackground"] = accentBrush;
                dlg.Resources["AccentButtonBackgroundPointerOver"] = accentBrush;
                dlg.Resources["AccentButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(pressed);
                dlg.Resources["AccentButtonBackgroundDisabled"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(disabled);
                dlg.Resources["AccentButtonForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task RenamePlaylistFromWallAsync(PlaylistCardViewModel vm)
        {
            if (string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
            {
                return;
            }

            var box = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "新名称", Text = vm.Name, MinWidth = 300 };
            var dlg = new ContentDialog
            {
                Title = "重命名播放列表",
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };
            ApplyDialogAccent(dlg);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            string newName = box.Text?.Trim() ?? string.Empty;
            if (newName.Length == 0 || string.Equals(newName, vm.Name, StringComparison.Ordinal)) return;
            try
            {
                NamedPlaylistStore.Rename(vm.Name, newName);
            }
            catch
            {
                return;
            }

            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }

        private async System.Threading.Tasks.Task ExportOnePlaylistAsync(PlaylistCardViewModel vm)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (string s in NamedPlaylistStore.LoadSongs(vm.Name))
            {
                sb.AppendLine("#EXTINF:-1," + System.IO.Path.GetFileName(s));
                sb.AppendLine(s);
            }
            string safe = vm.Name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            StorageFile m3uFile = await folder.CreateFileAsync(safe + ".m3u8", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(m3uFile, sb.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
        }

        private void EnterPlaylistWallMultiSelect(PlaylistCardViewModel anchor)
        {
            PlaylistWallGridView.SelectionMode = ListViewSelectionMode.Multiple;
            PlaylistWallGridView.IsItemClickEnabled = false;
            PlaylistWallMultiBar.Visibility = Visibility.Visible;
        }

        private void ExitPlaylistWallMultiSelect()
        {
            PlaylistWallGridView.SelectionMode = ListViewSelectionMode.None;
            PlaylistWallGridView.IsItemClickEnabled = true;
            PlaylistWallMultiBar.Visibility = Visibility.Collapsed;
            PlaylistWallGridView.SelectedItems.Clear();
        }

        private void PlaylistWallMultiExitButton_Click(object sender, RoutedEventArgs e) => ExitPlaylistWallMultiSelect();

        private void PlaylistWallMultiAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            var selected = PlaylistWallGridView.SelectedItems.OfType<PlaylistCardViewModel>().ToList();
            foreach (var vm in selected)
            {
                AddNamedPlaylistToQueue(vm.Name);
            }
            ExitPlaylistWallMultiSelect();
        }

        private void PlaylistWallMultiDelete_Click(object sender, RoutedEventArgs e)
        {
            var selected = PlaylistWallGridView.SelectedItems.OfType<PlaylistCardViewModel>().ToList();
            foreach (var vm in selected)
            {
                if (!string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    NamedPlaylistStore.Delete(vm.Name);
                    PlaylistLibraryService.ClearCustomCover(vm.Name);
                }
            }
            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
            ExitPlaylistWallMultiSelect();
        }

        private async System.Threading.Tasks.Task SetPlaylistCoverFromWebAsync(PlaylistCardViewModel vm)
        {
            try
            {
                NowPlayingText.Text = "正在从网络搜索封面…";
                string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(vm.Name);
                if (string.IsNullOrWhiteSpace(url))
                {
                    NowPlayingText.Text = "未找到封面";
                    return;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                byte[] bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length > 0)
                {
                    PlaylistLibraryService.WriteCustomCover(vm.Name, bytes);
                }

                NowPlayingText.Text = string.Empty;
                ApplyPlaylistWallCategory();
            }
            catch
            {
                NowPlayingText.Text = "获取封面失败";
            }
        }

        private async void CreatePlaylistWallButton_Click(object sender, RoutedEventArgs e)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "播放列表名称", MinWidth = 300 };
            var dlg = new ContentDialog
            {
                Title = "新建播放列表",
                Content = box,
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };
            ApplyDialogAccent(dlg);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            string name = box.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) return;
            try
            {
                NamedPlaylistStore.Create(name);
            }
            catch
            {
                return;
            }

            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }

        /// <summary>导出全部播放列表为 m3u8：每命中单写一个 .m3u8 到用户选择目录。</summary>
        private async void ExportPlaylistsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                int count = 0;
                foreach (string name in NamedPlaylistStore.List())
                {
                    if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                    {
                        continue; // 内建“我喜欢的音乐”不导出为独立 m3u8（属收藏数据）
                    }

                    List<string> songs = NamedPlaylistStore.LoadSongs(name);
                    if (songs.Count == 0) continue;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("#EXTM3U");
                    foreach (string s in songs)
                    {
                        sb.AppendLine("#EXTINF:-1," + System.IO.Path.GetFileName(s));
                        sb.AppendLine(s);
                    }
                    string safe = name;
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                    StorageFile m3uFile = await folder.CreateFileAsync(safe + ".m3u8", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                    await Windows.Storage.FileIO.WriteTextAsync(m3uFile, sb.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
                    count++;
                }

                StartupLog.Write("导出播放列表完成: " + count + " 个 .m3u8 → " + folder.Path);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ExportPlaylists", ex);
            }
        }

        private async void SaveQueueToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveUserPlaylistAsync(Content.XamlRoot);
            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }

        private void LibraryPaneRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            PointerUpdateKind kind = e.GetCurrentPoint(LibraryPaneRoot).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed)
            {
                NavigateLibraryHistoryBack();
                e.Handled = true;
            }
            else if (kind == PointerUpdateKind.XButton2Pressed)
            {
                NavigateLibraryHistoryForward();
                e.Handled = true;
            }
        }

        private LibraryNavState CaptureLibraryNavState()
            => new()
            {
                Category = _currentCategory,
                ArtistName = _openedArtist?.Name,
                AlbumName = _openedAlbum?.Name,
                AlbumFromArtist = _albumOpenedFromArtist,
                UsesAlbumArtist = _artistDetailUsesAlbumArtist
            };

        private static bool LibraryNavStatesEqual(LibraryNavState a, LibraryNavState b)
            => string.Equals(a.Category, b.Category, StringComparison.Ordinal)
               && string.Equals(a.ArtistName, b.ArtistName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AlbumName, b.AlbumName, StringComparison.OrdinalIgnoreCase)
               && a.AlbumFromArtist == b.AlbumFromArtist
               && a.UsesAlbumArtist == b.UsesAlbumArtist;

        /// <summary>执行会改变中间界面的导航，并写入后退栈（清空前进栈）。</summary>
        private void CommitLibraryNavigation(Action navigate)
        {
            if (_suppressNavHistory)
            {
                navigate();
                _navCurrent = CaptureLibraryNavState();
                return;
            }

            LibraryNavState before = _navCurrent ?? CaptureLibraryNavState();
            navigate();
            LibraryNavState after = CaptureLibraryNavState();
            if (!LibraryNavStatesEqual(before, after))
            {
                _navBackStack.Add(before);
                _navForwardStack.Clear();
            }

            _navCurrent = after;
        }

        private void NavigateLibraryHistoryBack()
        {
            if (_navBackStack.Count == 0)
            {
                return;
            }

            LibraryNavState current = CaptureLibraryNavState();
            LibraryNavState target = _navBackStack[^1];
            _navBackStack.RemoveAt(_navBackStack.Count - 1);
            _navForwardStack.Add(current);
            RestoreLibraryNavState(target);
        }

        private void NavigateLibraryHistoryForward()
        {
            // 从未前进过 / 前进栈为空：保持当前界面
            if (_navForwardStack.Count == 0)
            {
                return;
            }

            LibraryNavState current = CaptureLibraryNavState();
            LibraryNavState target = _navForwardStack[^1];
            _navForwardStack.RemoveAt(_navForwardStack.Count - 1);
            _navBackStack.Add(current);
            RestoreLibraryNavState(target);
        }

        private void RestoreLibraryNavState(LibraryNavState state)
        {
            _suppressNavHistory = true;
            try
            {
                ExitMultiSelectMode();
                _currentCategory = state.Category;
                ApplyCategoryView();

                if (!string.IsNullOrWhiteSpace(state.ArtistName)
                    && (string.Equals(state.Category, "Artists", StringComparison.Ordinal)
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal)))
                {
                    _artistDetailUsesAlbumArtist = state.UsesAlbumArtist
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal);
                    ArtistEntry? artist = _artists.FirstOrDefault(a =>
                        string.Equals(a.Name, state.ArtistName, StringComparison.CurrentCultureIgnoreCase));
                    if (artist == null)
                    {
                        artist = new ArtistEntry { Name = state.ArtistName };
                        _artists.Add(artist);
                    }

                    OpenArtistDetailCore(artist);
                }

                if (!string.IsNullOrWhiteSpace(state.AlbumName))
                {
                    AlbumEntry? album = FindAlbumEntryByName(state.AlbumName);
                    if (album != null)
                    {
                        OpenAlbumDetailCore(album, state.AlbumFromArtist);
                    }
                }

                _navCurrent = CaptureLibraryNavState();
                UpdateLibraryNavHighlight();
            }
            finally
            {
                _suppressNavHistory = false;
            }
        }

        private AlbumEntry? FindAlbumEntryByName(string albumName)
        {
            AlbumEntry? fromWall = _albums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromWall != null)
            {
                return fromWall;
            }

            AlbumEntry? fromArtist = _artistAlbums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromArtist != null)
            {
                return fromArtist;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => string.Equals(t.Album, albumName, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            return tracks.Count == 0 ? null : BuildAlbumEntriesFromTracks(tracks).FirstOrDefault();
        }

        /// <summary>切换播放列表时：若设置不允许继续播放，则暂停当前播放。</summary>
        private void ApplySwitchPlaylistPausePreference()
        {
            if (AppSettingsStore.Load().ContinueWhenSwitchPlaylist)
            {
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source != null && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                try
                {
                    player.Pause();
                }
                catch
                {
                }
            }
        }

        /// <summary>根据当前左侧分类刷新中间区域</summary>
        private void ApplyCategoryView()
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            UpdateLibraryNavHighlight();

            // 列表墙/命中单详情默认隐藏：仅其它分类内按需显 Visible，避免切到其它页面残留盖层
            PlaylistWallBorder.Visibility = Visibility.Collapsed;
            PlaylistDetailBorder.Visibility = Visibility.Collapsed;
            TagSortBorder.Visibility = Visibility.Collapsed;

            switch (_currentCategory)
            {
                case "Songs":
                    LibraryPaneTitle.Text = "歌曲";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _playlist;

                    _sortField = SortField.Title;
                    _sortAscending = true;
                    SortFieldButton.Content = "排序：标题";
                    SortOrderButton.Content = "升序";
                    ApplySort(playingPath);
                    break;

                case "UserPlaylist":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: true);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _userPlaylist;
                    // 播放列表默认不排序：保持添加顺序（后添加批次在前，批内相对顺序不变）
                    RenumberCollection(_userPlaylist);
                    break;

                case "Albums":
                    LibraryPaneTitle.Text = "专辑";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Visible;
                    UpdateAlbumSortButtonsUi();
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Visible;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshAlbumViewAsync();
                    break;

                case "Artists":
                case "AlbumArtists":
                    LibraryPaneTitle.Text = _currentCategory == "AlbumArtists" ? "专辑艺术家" : "艺术家";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshArtistViewAsync();
                    break;

                case "Folders":
                    LibraryPaneTitle.Text = "文件夹";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshFolderBrowserRoots();
                    break;

                case "PlaylistWall":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    if (string.IsNullOrEmpty(_currentPlaylistDetail))
                    {
                        PlaylistWallBorder.Visibility = Visibility.Visible;
                        PlaylistDetailBorder.Visibility = Visibility.Collapsed;
                        ApplyPlaylistWallCategory();
                    }
                    else
                    {
                        PlaylistWallBorder.Visibility = Visibility.Collapsed;
                        PlaylistDetailBorder.Visibility = Visibility.Visible;
                    }

                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    break;

                case "Favorites":
                case "Recent":
                    ApplyFavoritesOrRecentCategory();
                    break;

                case "MostPlayed":
                    ApplyMostPlayedCategory();
                    break;

                case "TagSort":
                    LibraryPaneTitle.Text = "标签排序";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    BreakoutTagSortView();
                    break;

                case "Genres":
                case "Years":
                    LibraryPaneTitle.Text = _currentCategory == "Genres" ? "流派" : "年份";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ArtistGridView.Visibility = Visibility.Visible;
                    ArtistDetailPanel.Visibility = Visibility.Collapsed;
                    _ = RefreshGenreYearViewAsync();
                    break;

                case "GenreSongs":
                case "YearSongs":
                    LibraryPaneTitle.Text = (_currentCategory == "GenreSongs" ? "流派：" : "年份：") + (_genreYearFilter ?? "");
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    var groupedSongs = _currentCategory == "GenreSongs"
                        ? _playlist
                            .Where(t => string.Equals(t.Genre, _genreYearFilter, StringComparison.OrdinalIgnoreCase)
                                        || (string.IsNullOrWhiteSpace(t.Genre) && _genreYearFilter == "未知流派"))
                            .ToList()
                        : _playlist
                            .Where(t => (t.Year > 0 ? t.Year.ToString() : "未知年份") == _genreYearFilter)
                            .ToList();
                    var groupedCollection = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(groupedSongs);
                    RenumberCollection(groupedCollection);
                    PlaylistView.ItemsSource = groupedCollection;
                    break;

                case "AudioFX":
                    // 音效处理入口占位：后续阶段在此接入 ECHO 音效处理页面
                    LibraryPaneTitle.Text = "音效处理";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    var fxEmpty = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
                    RenumberCollection(fxEmpty);
                    PlaylistView.ItemsSource = fxEmpty;
                    break;
            }

            // 仅「播放队列」分类支持拖拽重排（其它列表保持排序语义不变）
            PlaylistView.CanReorderItems = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist);
            PlaylistView.CanDragItems = PlaylistView.CanReorderItems;

            UpdateUserPlaylistActionBarVisibility();
            UpdateLibrarySearchUi();
        }

        private async Task RefreshGenreYearViewAsync()
        {
            List<ArtistEntry> entries = await Task.Run(() =>
            {
                if (_currentCategory == "Genres")
                {
                    return _playlist
                        .GroupBy(t => string.IsNullOrWhiteSpace(t.Genre) || t.Genre == "未知流派" ? "未知流派" : t.Genre)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                        .ToList();
                }

                return _playlist
                    .GroupBy(t => t.Year > 0 ? t.Year.ToString() : "未知年份")
                    .OrderByDescending(g => g.Key == "未知年份"
                        ? -1
                        : int.TryParse(g.Key, out int year) ? year : -1)
                    .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                    .ToList();
            });

            if (_currentCategory is "Genres" or "Years")
            {
                ArtistGridView.ItemsSource = entries;
                RefreshPlaylistSelectionChrome();
            }
        }

        private void OpenGenreYearSongs(string groupName)
        {
            _genreYearFilter = groupName;
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = _currentCategory == "Genres" ? "GenreSongs" : "YearSongs";
                ApplyCategoryView();
            });
        }

        // =====================================================================
        // 库搜索（歌曲 / 专辑 / 文件夹）
        // =====================================================================

        private void UpdateLibrarySearchUi()
        {
            if (LibrarySearchPanel == null)
            {
                return;
            }

            bool show = !_isMultiSelectMode
                && _openedAlbum == null
                && _openedArtist == null
                && (_currentCategory is "Songs" or "Albums" or "Artists" or "AlbumArtists" or "Folders" or "UserPlaylist" or "Favorites" or "Recent");

            LibrarySearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                if (FolderSearchNavPanel != null)
                {
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                }

                if (_currentCategory != "Folders")
                {
                    ClearFolderSearchHighlightOnly();
                }

                return;
            }

            LibrarySearchBox.PlaceholderText = _currentCategory switch
            {
                "Songs" => "搜索标题、专辑、艺术家",
                "Albums" => "搜索专辑、艺术家",
                "Artists" => "搜索艺术家",
                "AlbumArtists" => "搜索专辑艺术家",
                "Folders" => "搜索文件名",
                "UserPlaylist" => "搜索标题、艺术家、专辑、年份",
                _ => "搜索"
            };

            ApplyLibrarySearchNow();
        }

        private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _librarySearchText = LibrarySearchBox.Text ?? string.Empty;

            if (_librarySearchDebounceTimer == null)
            {
                _librarySearchDebounceTimer = DispatcherQueue.CreateTimer();
                _librarySearchDebounceTimer.IsRepeating = false;
                _librarySearchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
                _librarySearchDebounceTimer.Tick += (_, _) => ApplyLibrarySearchNow();
            }

            _librarySearchDebounceTimer.Stop();
            _librarySearchDebounceTimer.Start();
        }

        private void ApplyLibrarySearchNow()
        {
            switch (_currentCategory)
            {
                case "Songs":
                    ApplySongsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Albums":
                    ApplyAlbumsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Artists":
                case "AlbumArtists":
                    ApplyArtistsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Folders":
                    ApplyFolderSearch();
                    break;
                case "UserPlaylist":
                    ApplyUserPlaylistSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                default:
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private static bool ContainsIgnoreCase(string? source, string query)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query))
            {
                return false;
            }

            return source.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        private void ApplySongsSearchFilter()
        {
            if (_currentCategory != "Songs" || PlaylistView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(PlaylistView.ItemsSource, _playlist))
                {
                    PlaylistView.ItemsSource = _playlist;
                }

                RefreshPlaylistSelectionChrome();
                return;
            }

            List<PlaylistItem> filtered = _playlist
                .Where(p =>
                    ContainsIgnoreCase(p.Title, q)
                    || ContainsIgnoreCase(p.Album, q)
                    || ContainsIgnoreCase(p.Artist, q))
                .ToList();

            PlaylistView.ItemsSource = filtered;
            RefreshPlaylistSelectionChrome();
        }

        /// <summary>播放列表搜索：标题 / 艺术家 / 专辑 / 年份。</summary>
        private void ApplyUserPlaylistSearchFilter()
        {
            if (_currentCategory != "UserPlaylist" || PlaylistView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist))
                {
                    PlaylistView.ItemsSource = _userPlaylist;
                }

                RefreshPlaylistSelectionChrome();
                return;
            }

            List<PlaylistItem> filtered = _userPlaylist
                .Where(p => MatchesPlaylistSearch(p, q))
                .ToList();

            PlaylistView.ItemsSource = filtered;
            RefreshPlaylistSelectionChrome();
        }

        internal static bool MatchesPlaylistSearch(PlaylistItem item, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string q = query.Trim();
            return ContainsIgnoreCase(item.Title, q)
                || ContainsIgnoreCase(item.Artist, q)
                || ContainsIgnoreCase(item.Album, q)
                || ContainsIgnoreCase(item.YearText, q)
                || (item.Year > 0 && ContainsIgnoreCase(item.Year.ToString(), q));
        }

        private void ApplyAlbumsSearchFilter()
        {
            if (_currentCategory != "Albums" || AlbumGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(AlbumGridView.ItemsSource, _albums))
                {
                    AlbumGridView.ItemsSource = _albums;
                }

                RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
                return;
            }

            List<AlbumEntry> filtered = _albums
                .Where(a =>
                    ContainsIgnoreCase(a.Name, q)
                    || ContainsIgnoreCase(a.Artist, q))
                .ToList();

            AlbumGridView.ItemsSource = filtered;
            RefreshAlbumWallSelectionChrome(AlbumGridView, filtered);
        }

        private void ApplyArtistsSearchFilter()
        {
            if ((_currentCategory != "Artists" && _currentCategory != "AlbumArtists") || ArtistGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(ArtistGridView.ItemsSource, _artists))
                {
                    ArtistGridView.ItemsSource = _artists;
                }

                return;
            }

            List<ArtistEntry> filtered = _artists
                .Where(a => ContainsIgnoreCase(a.Name, q))
                .ToList();

            ArtistGridView.ItemsSource = filtered;
        }

        private void FolderSearchPrevButton_Click(object sender, RoutedEventArgs e)
            => NavigateFolderSearchMatch(-1);

        private void FolderSearchNextButton_Click(object sender, RoutedEventArgs e)
            => NavigateFolderSearchMatch(+1);

        private void ApplyFolderSearch()
        {
            string q = _librarySearchText.Trim();
            _folderSearchMatches.Clear();
            _folderSearchIndex = -1;
            _folderSearchHighlightPath = null;

            if (string.IsNullOrEmpty(q)
                || string.IsNullOrWhiteSpace(_browseFolderPath)
                || !Directory.Exists(_browseFolderPath))
            {
                UpdateFolderSearchNavUi();
                RefreshFolderBrowserSelectionChrome();
                return;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             _browseFolderPath,
                             "*.*",
                             SearchOption.AllDirectories))
                {
                    if (!IsSupportedAudioFile(file))
                    {
                        continue;
                    }

                    string name = Path.GetFileNameWithoutExtension(file);
                    string fullName = Path.GetFileName(file);
                    if (ContainsIgnoreCase(name, q) || ContainsIgnoreCase(fullName, q))
                    {
                        _folderSearchMatches.Add(file);
                    }
                }

                _folderSearchMatches.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("文件夹搜索失败: " + ex.Message);
            }

            UpdateFolderSearchNavUi();

            if (_folderSearchMatches.Count > 0)
            {
                NavigateFolderSearchMatchToIndex(0);
            }
            else
            {
                RefreshFolderBrowserRoots();
                RefreshFolderBrowserSelectionChrome();
            }
        }

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
                    FolderBrowserView.SelectedItem = item;
                    FolderBrowserView.ScrollIntoView(item);
                }
                catch
                {
                }
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

            FolderSearchNavPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                FolderSearchCountText.Text = "0/0";
                return;
            }

            FolderSearchCountText.Text = $"{_folderSearchIndex + 1}/{_folderSearchMatches.Count}";
        }

        private void ClearFolderSearchHighlightOnly()
        {
            if (_folderSearchHighlightPath == null && _folderSearchMatches.Count == 0)
            {
                return;
            }

            _folderSearchHighlightPath = null;
            _folderSearchMatches.Clear();
            _folderSearchIndex = -1;
            if (FolderBrowserView != null && _currentCategory == "Folders")
            {
                RefreshFolderBrowserSelectionChrome();
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
        /// <summary>刷新媒体库：重新枚举根列表，并重载当前选中项的详情。</summary>
        private void RefreshMediaFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategory != "Folders")
            {
                return;
            }

            RefreshFolderBrowserRoots();
            if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
            {
                LoadMediaFolderSongs(f);
            }
        }

        /// <summary>「添加文件夹」：选择文件夹加入媒体库(LibraryWatchFolders)并刷新根列表。</summary>
        private async void AddMediaFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return;
                }

                AppSettingsState settings = AppSettingsStore.Load();
                if (settings.LibraryWatchFolders == null)
                {
                    settings.LibraryWatchFolders = new List<string>();
                }

                string fp = folder.Path;
                if (!settings.LibraryWatchFolders.Contains(fp, StringComparer.OrdinalIgnoreCase))
                {
                    settings.LibraryWatchFolders.Add(fp);
                }

                AppSettingsStore.Save(settings);

                // 把该文件夹的音频加入主库
                string[] paths = await System.Threading.Tasks.Task.Run(() => EnumerateAudioFiles(fp).ToArray());
                LoadAndAddFiles(paths, persist: false);

                if (_currentCategory == "Folders")
                {
                    RefreshFolderBrowserRoots();
                }
            }
            catch
            {
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
                    FolderBrowserView.Visibility = Visibility.Collapsed;
                    FolderBrowserEmptyHint.Visibility = Visibility.Visible;
                    return;
                }

                FolderBrowserEmptyHint.Visibility = Visibility.Collapsed;
                FolderBrowserView.Visibility = Visibility.Visible;
                foreach (FolderBrowserItem child in EnumerateFolderChildren(_browseFolderPath, depth: 0))
                {
                    _folderBrowserItems.Add(child);
                }

                return;
            }

            // 多根媒体库：每个根显示完整路径，点击展开其下树
            FolderBrowserEmptyHint.Visibility = Visibility.Collapsed;
            FolderBrowserView.Visibility = Visibility.Visible;
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

        private void FolderBrowserItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 多选时：整行留给 ListView 选中；展开只走左侧箭头
            if (_isMultiSelectMode && _multiSelectFolderList != null)
            {
                return;
            }

            if (sender is not FrameworkElement { DataContext: FolderBrowserItem item })
            {
                return;
            }

            FolderBrowserView.SelectedItem = item;

            if (item.IsFolder)
            {
                ToggleFolderExpand(item);
                e.Handled = true;
            }
        }

        /// <summary>左侧朝下箭头：展开/折叠；多选时不改变选中状态。</summary>
        private void FolderBrowserChevron_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
            {
                return;
            }

            FolderBrowserItem? item = fe.DataContext as FolderBrowserItem
                ?? FindFolderBrowserItem(fe);
            if (item == null || !item.IsFolder)
            {
                return;
            }

            ToggleFolderExpand(item);
            // 点箭头：同时加载该文件夹歌曲到右侧详情区
            LoadMediaFolderSongs(item);
            e.Handled = true;
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

        private void CollapseFolderAt(int index)
        {
            int depth = _folderBrowserItems[index].Depth;
            int removeAt = index + 1;
            while (removeAt < _folderBrowserItems.Count && _folderBrowserItems[removeAt].Depth > depth)
            {
                _folderBrowserItems.RemoveAt(removeAt);
            }
        }

        /// <summary>枚举一层：子文件夹 + 支持的音频文件（不递归）</summary>
        private static List<FolderBrowserItem> EnumerateFolderChildren(string folderPath, int depth)
        {
            var result = new List<FolderBrowserItem>();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return result;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    result.Add(new FolderBrowserItem
                    {
                        DisplayName = name,
                        FullPath = dir,
                        IsFolder = true,
                        Depth = depth
                    });
                }
            }
            catch
            {
                // 无权限等：跳过子目录
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new FolderBrowserItem
                    {
                        DisplayName = Path.GetFileName(file),
                        FullPath = file,
                        IsFolder = false,
                        Depth = depth
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private void FolderBrowserView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            var fe = e.OriginalSource as FrameworkElement;
            var item = fe?.DataContext as FolderBrowserItem ?? FolderBrowserView.SelectedItem as FolderBrowserItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                // 双击文件夹：加载其内歌曲到右侧详情区
                LoadMediaFolderSongs(item);
                return;
            }

            PlaylistItem? track = EnsureTrackInLibrary(item.FullPath);
            if (track != null)
            {
                PlayPlaylistItem(track);
            }
        }

        private void FolderBrowserView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshFolderBrowserSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
            UpdateMediaDetails(FolderBrowserView.SelectedItem as FolderBrowserItem);
        }

        /// <summary>单击选中：只更新台头，不立即枚举歌曲（大文件夹避免卡顿）。歌曲在双击/箭头时加载。</summary>
        private void UpdateMediaDetails(FolderBrowserItem? item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            if (item == null)
            {
                MediaDetailsHeader.Text = "选择文件夹查看歌曲";
                MediaDetailsList.ItemsSource = null;
                MediaDetailsEmptyHint.Visibility = MediaDetailsList.Visibility = Visibility.Collapsed;
                return;
            }

            MediaDetailsHeader.Text = item.FullPath;
            MediaDetailsList.ItemsSource = null;
            MediaDetailsEmptyHint.Visibility = Visibility.Visible;
            MediaDetailsList.Visibility = Visibility.Visible;
            MediaDetailsEmptyHint.Text = item.IsFolder ? "双击文件夹查看歌曲" : "双击查看";
        }

        /// <summary>双击文件夹或点箭头：枚举该文件夹内歌曲并填充右侧详情区。</summary>
        private void LoadMediaFolderSongs(FolderBrowserItem item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            MediaDetailsEmptyHint.Visibility = Visibility.Collapsed;
            MediaDetailsList.Visibility = Visibility.Visible;
            MediaDetailsHeader.Text = item.FullPath;

            // 后台线程枚举+读取标签，避免大文件夹卡 UI
            MediaDetailsList.ItemsSource = null;
            MediaDetailsHeader.Text = item.FullPath + "（加载中…）";

            string loadedPath = item.FullPath;
            bool isFolder = item.IsFolder;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                string[] paths = isFolder
                    ? EnumerateAudioFiles(loadedPath).ToArray()
                    : (System.IO.File.Exists(loadedPath) ? new[] { loadedPath } : Array.Empty<string>());

                var songs = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        PlaylistItem p = CreatePlaylistItemFromPath(path);
                        p.DurationText = FormatTime(p.Duration);
                        songs.Add(p);
                    }
                    catch
                    {
                    }
                }

                for (int i = 0; i < songs.Count; i++)
                {
                    songs[i].Index = i + 1;
                }

                return songs;
            }).ContinueWith(t =>
            {
                try
                {
                    var songs = t.Result;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (MediaDetailsList == null || MediaDetailsHeader == null)
                        {
                            return;
                        }

                        MediaDetailsList.ItemsSource = songs;
                        MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                        if (MediaOptionsButton != null)
                        {
                            MediaOptionsButton.Visibility = Visibility.Collapsed;
                        }

                        MediaDetailsHeader.Text = item.FullPath;
                        MediaDetailsEmptyHint.Text = "无歌曲";
                        MediaDetailsEmptyHint.Visibility =
                            songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }
                catch
                {
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>右键详情区歌曲：非多选态弹歌曲菜单；多选态进入多选。</summary>
        private void MediaDetailsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            var song = (fe?.DataContext ?? (sender as ListView)?.SelectedItem) as PlaylistItem;
            if (song == null)
            {
                return;
            }

            if (MediaDetailsList?.SelectionMode == ListViewSelectionMode.Multiple)
            {
                // 已进入多选：右键加入选中
                if (!MediaDetailsList.SelectedItems.Contains(song))
                {
                    MediaDetailsList.SelectedItems.Add(song);
                }

                return;
            }

            // 右键：弹出该歌曲的选项菜单（多选作为其中一项，点多选才进入多选态）
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            PlaylistItem songRef = song;

            var play = new MenuFlyoutItem { Text = "播放" };
            play.Icon = new FontIcon { Glyph = "\uE768" };
            play.Click += (_, _) =>
            {
                PlaylistItem? track = EnsureTrackInLibrary(songRef.FilePath);
                if (track != null)
                {
                    PlayPlaylistItem(track);
                }
            };
            flyout.Items.Add(play);

            var add = new MenuFlyoutItem { Text = "加入播放队列" };
            add.Icon = new FontIcon { Glyph = "\uE710" };
            add.Click += (_, _) => AddToUserPlaylistBack(songRef);
            flyout.Items.Add(add);

            var edit = new MenuFlyoutItem { Text = "编辑标签" };
            edit.Icon = new FontIcon { Glyph = "\uE8D2" };
            edit.Click += (_, _) => TagEditorWindow.ShowBatch(new[] { songRef.FilePath });
            flyout.Items.Add(edit);

            var del = new MenuFlyoutItem { Text = "从媒体库中删除" };
            del.Icon = new FontIcon { Glyph = "\uE74D" };
            del.Click += (_, _) => _ = DeleteMediaSongWithConfirmAsync(songRef);
            flyout.Items.Add(del);

            var multi = new MenuFlyoutItem { Text = "多选" };
            multi.Icon = new FontIcon { Glyph = "\uE700" };
            multi.Click += (_, _) =>
            {
                if (MediaDetailsList != null)
                {
                    MediaDetailsList.SelectionMode = ListViewSelectionMode.Multiple;
                    MediaDetailsList.SelectedItems.Clear();
                    MediaDetailsList.SelectedItems.Add(songRef);
                    RefreshMediaSongSelectionChrome();
                }

                if (MediaOptionsButton != null)
                {
                    MediaOptionsButton.Visibility = Visibility.Visible;
                }

                if (MediaExitMultiSelectButton != null)
                {
                    MediaExitMultiSelectButton.Visibility = Visibility.Visible;
                }
            };
            flyout.Items.Add(multi);

            flyout.ShowAt(MediaDetailsList ?? (FrameworkElement)sender, e.GetPosition(MediaDetailsList ?? (FrameworkElement)sender));
            e.Handled = true;
        }

        /// <summary>询问是否把歌曲移入回收站并从磁盘删除。</summary>
        private async System.Threading.Tasks.Task DeleteMediaSongWithConfirmAsync(PlaylistItem song)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "从媒体库中删除",
                    Content = $"确定要把该歌曲移动到回收站并从磁盘删除吗？\n\n{song.FilePath}",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                ApplyDialogAccent(dialog);
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await DeleteSongFromDiskAsync(song);
                    // 重新加载当前详情列表（后台）
                    if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
                    {
                        LoadMediaFolderSongs(f);
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>详情区歌曲行选中 chrome：主题色圆角背景（去掉多选框）。</summary>
        private void MediaDetailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplyMediaSongSelectionChrome(sender as ListView, container, song);
            }
        }

        private void MediaDetailsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshMediaSongSelectionChrome();
        }

        private void RefreshMediaSongSelectionChrome()
        {
            if (MediaDetailsList == null)
            {
                return;
            }

            var realized = FindRealizedListViewContainers(MediaDetailsList);
            foreach ((ListViewItem c, PlaylistItem s) in realized)
            {
                ApplyMediaSongSelectionChrome(MediaDetailsList, c, s);
            }
        }

        private static List<(ListViewItem, PlaylistItem)> FindRealizedListViewContainers(ListView list)
        {
            var result = new List<(ListViewItem, PlaylistItem)>();
            try
            {
                var presenter = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(list, 0);
                void Walk(Microsoft.UI.Xaml.DependencyObject node, int depth)
                {
                    if (depth > 12)
                    {
                        return;
                    }

                    int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < n; i++)
                    {
                        var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
                        if (child is ListViewItem lvi)
                        {
                            if (lvi.Content is PlaylistItem p)
                            {
                                result.Add((lvi, p));
                            }
                        }
                        else
                        {
                            Walk(child, depth + 1);
                        }
                    }
                }

                Walk(presenter, 0);
            }
            catch
            {
            }

            return result;
        }

        private void ApplyMediaSongSelectionChrome(ListView? list, ListViewItem container, PlaylistItem song)
        {
            if (list == null)
            {
                return;
            }

            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool selected = list.SelectionMode == ListViewSelectionMode.Multiple
                ? list.SelectedItems.Contains(song)
                : ReferenceEquals(list.SelectedItem, song);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            Border? chrome = FindTaggedBorder(container, "SongRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 40;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                if (list.ActualWidth > 0)
                {
                    chrome.Width = list.ActualWidth;
                }

                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = new SolidColorBrush(Colors.Transparent);
                    ClearForegroundOnDescendants(chrome);
                }
            }
        }

        /// <summary>「选项」按钮：多选态的多选操作。</summary>
        private void MediaOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> sel = MediaDetailsList?.SelectedItems.OfType<PlaylistItem>().ToList() ?? new List<PlaylistItem>();
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var add = new MenuFlyoutItem { Text = $"加入播放队列（{sel.Count}）" };
            add.Icon = new FontIcon { Glyph = "\uE710" };
            add.Click += (_, _) =>
            {
                foreach (PlaylistItem s in sel)
                {
                    AddToUserPlaylistBack(s);
                }
            };
            flyout.Items.Add(add);

            var edit = new MenuFlyoutItem { Text = $"编辑标签（{sel.Count}）" };
            edit.Icon = new FontIcon { Glyph = "\uE8D2" };
            edit.Click += (_, _) => TagEditorWindow.ShowBatch(sel.Select(i => i.FilePath).ToList());
            flyout.Items.Add(edit);

            var del = new MenuFlyoutItem { Text = $"从媒体库中删除（{sel.Count}）" };
            del.Icon = new FontIcon { Glyph = "\uE74D" };
            del.Click += (_, _) => _ = DeleteMediaSongsConfirmAsync(sel);
            flyout.Items.Add(del);

            var exit = new MenuFlyoutItem { Text = "退出多选" };
            exit.Icon = new FontIcon { Glyph = "\uE711" };
            exit.Click += (_, _) => ExitMediaMultiSelect();
            flyout.Items.Add(exit);

            flyout.ShowAt(MediaOptionsButton, new Windows.Foundation.Point(0, 0));
        }

        /// <summary>退出媒体库多选：恢复单选并隐藏选项按钮。</summary>
        private void ExitMediaMultiSelect()
        {
            if (MediaDetailsList != null)
            {
                MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                MediaDetailsList.SelectedItems.Clear();
            }

            if (MediaOptionsButton != null)
            {
                MediaOptionsButton.Visibility = Visibility.Collapsed;
            }

            if (MediaExitMultiSelectButton != null)
            {
                MediaExitMultiSelectButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>显式退出多选按钮。</summary>
        private void MediaExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMediaMultiSelect();

        /// <summary>更新右侧详情列表：中途加载完成/重载时退出多选。</summary>
        private void ResetMediaSelectionUi()
        {
            ExitMediaMultiSelect();
        }

        /// <summary>多选删除：询问后把选中的歌曲移到回收站并从磁盘删除。</summary>
        private async System.Threading.Tasks.Task DeleteMediaSongsConfirmAsync(IReadOnlyList<PlaylistItem> songs)
        {
            try
            {
                if (songs.Count == 0)
                {
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "从媒体库中删除",
                    Content = $"确定要把选中的 {songs.Count} 个文件移动到回收站并从磁盘删除吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                ApplyDialogAccent(dialog);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                foreach (PlaylistItem s in songs)
                {
                    await DeleteSongFromDiskAsync(s);
                }

                // 重新加载当前详情列表
                if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
                {
                    LoadMediaFolderSongs(f);
                }
            }
            catch
            {
            }
        }

        /// <summary>把单曲加入播放队列末尾。</summary>
        private System.Threading.Tasks.Task AddToUserPlaylistBack(PlaylistItem song)
        {
            try
            {
                _userPlaylist.Add(song);
                RenumberCollection(_userPlaylist);
                if (_currentCategory == "UserPlaylist")
                {
                    PlaylistView.ItemsSource = null;
                    PlaylistView.ItemsSource = _userPlaylist;
                }
            }
            catch
            {
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }

        private void FolderBrowserView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is FolderBrowserItem item && args.ItemContainer is ListViewItem container)
            {
                ApplyFolderBrowserItemSelectionChrome(container, item);
            }
        }

        private void FolderBrowserView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem? item = FindFolderBrowserItem(e.OriginalSource as DependencyObject);
            if (item == null)
            {
                return;
            }

            if (!_isMultiSelectMode)
            {
                FolderBrowserView.SelectedItem = item;
            }

            if (item.IsFolder)
            {
                ShowFolderItemContextMenu(item, e);
            }
            else
            {
                ShowFolderAudioContextMenu(item, e);
            }

            e.Handled = true;
        }

        private FolderBrowserItem? FindFolderBrowserItem(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is FrameworkElement { DataContext: FolderBrowserItem fromContext })
                {
                    return fromContext;
                }

                if (current is ListViewItem container)
                {
                    return FolderBrowserView.ItemFromContainer(container) as FolderBrowserItem;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void ShowFolderItemContextMenu(FolderBrowserItem folder, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem folderRef = folder;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该文件夹内的音频" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayFolderAudio(folderRef.FullPath, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterFolderMultiSelectMode(folderRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => PlayFolderAudio(folderRef.FullPath, replacePlaylist: false);

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);

            // 从媒体库删除（仅对配置的媒体库根文件夹可用）
            AppSettingsState settings = AppSettingsStore.Load();
            bool isMediaRoot = settings.LibraryWatchFolders?.Contains(folderRef.FullPath, StringComparer.OrdinalIgnoreCase) == true;
            if (isMediaRoot)
            {
                var removeItem = new MenuFlyoutItem { Text = "从媒体库中删除" };
                removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
                removeItem.Click += async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "从媒体库中删除",
                        Content = $"将该文件夹移出媒体库？\n\n{folderRef.FullPath}\n\n是否同时把该文件夹内的音频移到回收站？",
                        PrimaryButtonText = "删除（移到回收站）",
                        SecondaryButtonText = "仅移出媒体库",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot
                    };
                    ApplyDialogAccent(dialog);

                    ContentDialogResult r;
                    try
                    {
                        r = await dialog.ShowAsync();
                    }
                    catch
                    {
                        r = ContentDialogResult.None;
                    }

                    if (r == ContentDialogResult.None)
                    {
                        return;
                    }

                    if (r == ContentDialogResult.Primary)
                    {
                        // 删除源文件（移到回收站）
                        foreach (string p in EnumerateAudioFiles(folderRef.FullPath))
                        {
                            if (System.IO.File.Exists(p))
                            {
                                try
                                {
                                    MoveToRecycleBin(p);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }

                    settings.LibraryWatchFolders?.RemoveAll(q =>
                        string.Equals(q, folderRef.FullPath, StringComparison.OrdinalIgnoreCase));
                    AppSettingsStore.Save(settings);
                    RefreshFolderBrowserRoots();
                };
                flyout.Items.Add(removeItem);
            }

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }

        private void ShowFolderAudioContextMenu(FolderBrowserItem fileItem, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? track = EnsureTrackInLibrary(fileItem.FullPath);
            if (track == null)
            {
                return;
            }

            _contextMenuSong = track;
            var flyout = BuildPlaylistItemContextMenu(track, false, () => EnterFolderMultiSelectMode(fileItem));

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }

        /// <summary>深度优先：子文件夹顺序与界面一致，文件夹内音频按文件名排序。</summary>
        private static List<string> EnumerateAudioFilesRecursiveOrdered(string folderPath)
        {
            var result = new List<string>();
            CollectAudioFilesRecursiveOrdered(folderPath, result);
            return result;
        }

        private static void CollectAudioFilesRecursiveOrdered(string folderPath, List<string> sink)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    CollectAudioFilesRecursiveOrdered(dir, sink);
                }
            }
            catch
            {
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        sink.Add(file);
                    }
                }
            }
            catch
            {
            }
        }

        private PlaylistItem? EnsureTrackInLibrary(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return null;
            }

            PlaylistItem? existing = _playlist.FirstOrDefault(p =>
                string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            LoadAndAddFiles(new[] { filePath }, persistAsFiles: true);
            return _playlist.FirstOrDefault(p =>
                string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }

        private List<PlaylistItem> GetOrImportTracksByPaths(IReadOnlyList<string> paths)
        {
            var uniquePaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (uniquePaths.Count == 0)
            {
                return new List<PlaylistItem>();
            }

            var libraryMap = new Dictionary<string, PlaylistItem>(StringComparer.OrdinalIgnoreCase);
            foreach (PlaylistItem item in _playlist)
            {
                libraryMap.TryAdd(item.FilePath, item);
            }

            var missing = uniquePaths.Where(p => !libraryMap.ContainsKey(p) && System.IO.File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                LoadAndAddFiles(missing.ToArray(), persistAsFiles: true);
                libraryMap.Clear();
                foreach (PlaylistItem item in _playlist)
                {
                    libraryMap.TryAdd(item.FilePath, item);
                }
            }

            var result = new List<PlaylistItem>(uniquePaths.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in uniquePaths)
            {
                if (!seen.Add(path))
                {
                    continue;
                }

                if (libraryMap.TryGetValue(path, out PlaylistItem? track))
                {
                    result.Add(track);
                }
            }

            return result;
        }

        private void PlayFolderAudio(string folderPath, bool replacePlaylist)
        {
            List<string> paths = EnumerateAudioFilesRecursiveOrdered(folderPath);
            List<PlaylistItem> tracks = GetOrImportTracksByPaths(paths);
            if (tracks.Count == 0)
            {
                return;
            }

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                PlayUserPlaylistAt(0);
            }
            else
            {
                AddSongsToUserPlaylist(tracks);
            }
        }

        private List<PlaylistItem> CollectTracksFromSelectedFolderItems(IEnumerable<FolderBrowserItem> items)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FolderBrowserItem item in items)
            {
                if (item.IsFolder)
                {
                    foreach (string path in EnumerateAudioFilesRecursiveOrdered(item.FullPath))
                    {
                        if (seen.Add(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
                else if (seen.Add(item.FullPath))
                {
                    paths.Add(item.FullPath);
                }
            }

            return GetOrImportTracksByPaths(paths);
        }

        private void EnterFolderMultiSelectMode(FolderBrowserItem? preselect)
        {
            if (_multiSelectTargetList != null)
            {
                ExitSongMultiSelectUiOnly();
            }

            if (_multiSelectAlbumGrid != null)
            {
                ExitAlbumMultiSelectUiOnly();
            }

            _folderItemDefaultStyle ??= FolderBrowserView.ItemContainerStyle;
            _multiSelectFolderList = FolderBrowserView;
            _multiSelectTargetList = null;
            _multiSelectAlbumGrid = null;
            _isMultiSelectMode = true;

            SetListSelectionMode(FolderBrowserView, ListViewSelectionMode.Multiple);

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(FolderBrowserView);
            ApplyMultiSelectFolderItemStyle();
            UpdateLibrarySearchUi();

            if (preselect != null)
            {
                try
                {
                    FolderBrowserView.SelectedItems.Add(preselect);
                }
                catch
                {
                    FolderBrowserView.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
            });
        }

        private void ApplyMultiSelectFolderItemStyle()
        {
            ApplyAccentSelectionResources(FolderBrowserView);
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 36.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            FolderBrowserView.ItemContainerStyle = style;
            RefreshFolderBrowserSelectionChrome();
        }

        private void ApplyFolderBrowserItemSelectionChrome(
            ListViewItem container,
            FolderBrowserItem item,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool multiOnThisList = _isMultiSelectMode && _multiSelectFolderList == FolderBrowserView;
            Brush unselectedBg = multiOnThisList
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisList
                ? IsItemSelected(FolderBrowserView, item, selectedSet)
                : ReferenceEquals(FolderBrowserView.SelectedItem, item);

            bool searchHit = !multiOnThisList
                && !string.IsNullOrEmpty(_folderSearchHighlightPath)
                && string.Equals(item.FullPath, _folderSearchHighlightPath, StringComparison.OrdinalIgnoreCase);

            Border? chrome = FindTaggedBorder(container, "FolderRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 36;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                if (FolderBrowserView != null && FolderBrowserView.ActualWidth > 0)
                {
                    chrome.Width = FolderBrowserView.ActualWidth; // 行内容铺满整行，选中矩形右侧铺满
                }
                if (selected || searchHit)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected || searchHit)
            {
                container.Background = accent;
                container.Foreground = selectedFg;
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
            }
        }

        /// <summary>汇总唯一艺术家 / 专辑艺术家，按名字升序；加载已保存的自定义头像</summary>
        private async Task RefreshArtistViewAsync()
        {
            _artists.Clear();
            bool albumArtistMode = string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);

            if (_playlist.Count == 0)
            {
                return;
            }

            var groups = _playlist
                .GroupBy(
                    p => albumArtistMode ? p.AlbumArtist : p.Artist,
                    StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var group in groups)
            {
                var entry = new ArtistEntry
                {
                    Name = albumArtistMode ? group.First().AlbumArtist : group.First().Artist,
                    TrackCount = group.Count()
                };
                _artists.Add(entry);
            }

            ApplyArtistsSearchFilter();

            foreach (ArtistEntry entry in _artists.ToList())
            {
                if (_currentCategory != "Artists" && _currentCategory != "AlbumArtists")
                {
                    return;
                }

                entry.AvatarImage = await ResolveArtistAvatarAsync(entry.Name, albumArtistMode);
            }
        }

        private static string ArtistAvatarStoreKey(string artistName, bool albumArtistMode)
            => albumArtistMode ? "aa|" + artistName.Trim() : artistName.Trim();

        /// <summary>
        /// 自定义头像优先；否则取该艺术家年份最晚专辑中音轨 1 的封面。
        /// </summary>
        private async Task<BitmapImage?> ResolveArtistAvatarAsync(string artistName, bool? albumArtistMode = null)
        {
            bool useAlbumArtist = albumArtistMode
                ?? (_artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal));
            string storeKey = ArtistAvatarStoreKey(artistName, useAlbumArtist);
            BitmapImage? custom = await ArtistAvatarStore.TryLoadAsync(storeKey);
            if (custom != null)
            {
                return custom;
            }

            return await ResolveArtistDefaultAvatarAsync(artistName, useAlbumArtist);
        }

        private async Task<BitmapImage?> ResolveArtistDefaultAvatarAsync(string artistName, bool useAlbumArtist)
        {
            // 默认优先使用网络头像（网易云），失败再回退本地专辑封面
            BitmapImage? web = await TryLoadWebArtistAvatarAsync(artistName);
            if (web != null)
            {
                return web;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, useAlbumArtist))
                .ToList();
            if (tracks.Count == 0)
            {
                return null;
            }

            AlbumEntry? latestAlbum = BuildAlbumEntriesFromTracks(tracks)
                .OrderByDescending(a => a.Year)
                .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (latestAlbum == null || string.IsNullOrWhiteSpace(latestAlbum.CoverSourcePath))
            {
                return null;
            }

            byte[]? bytes = await Task.Run(() => ExtractCoverBytes(latestAlbum.CoverSourcePath));
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return await CreateBitmapFromBytesAsync(bytes);
        }

        private static bool TrackMatchesArtistName(PlaylistItem track, string artistName, bool useAlbumArtist)
        {
            string key = useAlbumArtist ? track.AlbumArtist : track.Artist;
            return string.Equals(key, artistName, StringComparison.CurrentCultureIgnoreCase);
        }


        private static readonly Dictionary<string, BitmapImage?> WebArtistAvatarCache = new();
        private static readonly SemaphoreSlim WebArtistAvatarGate = new(1, 1);

        /// <summary>按歌手名从网络加载头像（缓存，失败返回 null）。</summary>
        private static async Task<BitmapImage?> TryLoadWebArtistAvatarAsync(string artistName)
        {
            string key = artistName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (WebArtistAvatarCache.TryGetValue(key, out BitmapImage? cached))
            {
                return cached;
            }

            await WebArtistAvatarGate.WaitAsync();
            try
            {
                if (WebArtistAvatarCache.TryGetValue(key, out cached))
                {
                    return cached;
                }

                // 磁盘缓存命中（重启后免联网搜索）：加载后回填内存缓存
                BitmapImage? fromDisk = await ArtistAvatarStore.TryLoadWebAsync(key);
                if (fromDisk != null)
                {
                    WebArtistAvatarCache[key] = fromDisk;
                    return fromDisk;
                }

                BitmapImage? result = null;
                try
                {
                    string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(key);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                        byte[] bytes = await http.GetByteArrayAsync(url);
                        if (bytes.Length > 0)
                        {
                            result = await CreateBitmapFromBytesAsync(bytes);
                            // 持久化到磁盘缓存，下次打开（含重启后）不再联网搜索
                            await ArtistAvatarStore.SaveWebAsync(key, bytes);
                        }
                    }
                }
                catch
                {
                }

                if (WebArtistAvatarCache.Count > 500)
                {
                    WebArtistAvatarCache.Clear();
                }

                WebArtistAvatarCache[key] = result;
                return result;
            }
            finally
            {
                WebArtistAvatarGate.Release();
            }
        }
        /// <summary>圆形头像上右键：选择本地头像 / 恢复默认</summary>
        private void ArtistAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ArtistEntry? artist = FindArtistEntry(sender as DependencyObject);
            if (artist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(artist, sender as FrameworkElement, e);
        }

        private void ArtistDetailAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_openedArtist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(_openedArtist, sender as FrameworkElement, e);
        }

        private void ShowArtistAvatarFlyout(ArtistEntry artist, FrameworkElement? element, RightTappedRoutedEventArgs e)
        {
            _avatarContextArtist = artist;

            var flyout = new MenuFlyout();

            var playWorks = new MenuFlyoutItem { Text = "播放该艺术家的作品" };
            playWorks.Icon = new FontIcon { Glyph = "\uE768" };
            playWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    PlayArtistWorks(_avatarContextArtist.Name, replacePlaylist: true);
                }
            };
            flyout.Items.Add(playWorks);

            var addWorks = new MenuFlyoutItem { Text = "添加该艺术家的作品至播放列表" };
            addWorks.Icon = new FontIcon { Glyph = "\uE710" };
            addWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    _ = ShowNamedPlaylistPickerAsync(GetTracksForArtist(_avatarContextArtist.Name, useCurrentSongSort: true));
                }
            };
            flyout.Items.Add(addWorks);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var webAvatarItem = new MenuFlyoutItem { Text = "从网络获取头像…" };
            webAvatarItem.Icon = new FontIcon { Glyph = "\uE774" };
            webAvatarItem.Click += async (_, _) => await DownloadArtistAvatarFromWebAsync(_avatarContextArtist);
            flyout.Items.Add(webAvatarItem);

            var selectItem = new MenuFlyoutItem { Text = "从本地选择艺术家头像" };
            selectItem.Click += SelectArtistAvatarMenu_Click;
            flyout.Items.Add(selectItem);

            var restoreItem = new MenuFlyoutItem { Text = "恢复默认头像" };
            restoreItem.Click += RestoreArtistAvatarMenu_Click;
            flyout.Items.Add(restoreItem);

            if (element != null)
            {
                flyout.ShowAt(element, e.GetPosition(element));
            }

            e.Handled = true;
        }

        private static ArtistEntry? FindArtistEntry(DependencyObject? start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (fe.DataContext is ArtistEntry fromContext)
                    {
                        return fromContext;
                    }

                    if (fe is GridViewItem { Content: ArtistEntry fromContent })
                    {
                        return fromContent;
                    }
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private async Task DownloadArtistAvatarFromWebAsync(ArtistEntry? artist)
        {
            if (artist == null)
            {
                return;
            }

            try
            {
                NowPlayingText.Text = "正在从网络获取头像…";
                string? imageUrl = await OnlineMusicApi.SearchArtistAvatarUrlAsync(artist.Name);
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    NowPlayingText.Text = "未找到该艺术家的头像";
                    return;
                }

                string tmp = Path.Combine(Path.GetTempPath(), "celeste-avatar-" + Guid.NewGuid().ToString("N") + ".jpg");
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                    byte[] bytes = await http.GetByteArrayAsync(imageUrl);
                    await System.IO.File.WriteAllBytesAsync(tmp, bytes);
                }

                bool albumArtistMode = _artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
                var editor = new ArtistAvatarEditorWindow(ArtistAvatarStoreKey(artist.Name, albumArtistMode), tmp);
                _artistAvatarEditorWindow = editor;
                editor.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_artistAvatarEditorWindow, editor))
                    {
                        _artistAvatarEditorWindow = null;
                    }

                    NowPlayingText.Text = string.Empty;
                };
                editor.AvatarConfirmed += image =>
                {
                    artist.AvatarImage = image;
                    ApplyArtistAvatarToDetailIfOpen(artist, image);
                };
                editor.Activate();
            }
            catch
            {
                NowPlayingText.Text = "获取头像失败";
            }
        }

        private async void SelectArtistAvatarMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_avatarContextArtist == null)
            {
                return;
            }

            ArtistEntry artist = _avatarContextArtist;

            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                {
                    return;
                }

                var editor = new ArtistAvatarEditorWindow(
                    ArtistAvatarStoreKey(artist.Name, _artistDetailUsesAlbumArtist
                        || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal)),
                    file.Path);
                _artistAvatarEditorWindow = editor;
                editor.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_artistAvatarEditorWindow, editor))
                    {
                        _artistAvatarEditorWindow = null;
                    }
                };
                editor.AvatarConfirmed += image =>
                {
                    artist.AvatarImage = image;
                    ApplyArtistAvatarToDetailIfOpen(artist, image);
                };
                editor.Activate();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("选择头像失败", ex.Message);
            }
        }

        private async void RestoreArtistAvatarMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_avatarContextArtist == null)
            {
                return;
            }

            ArtistEntry artist = _avatarContextArtist;
            try
            {
                bool albumArtistMode = _artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
                ArtistAvatarStore.DeleteCustomAvatar(ArtistAvatarStoreKey(artist.Name, albumArtistMode));
                BitmapImage? image = await ResolveArtistDefaultAvatarAsync(artist.Name, albumArtistMode);
                artist.AvatarImage = image;
                ApplyArtistAvatarToDetailIfOpen(artist, image);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("恢复默认头像失败", ex.Message);
            }
        }

        private void ApplyArtistAvatarToDetailIfOpen(ArtistEntry artist, BitmapImage? image)
        {
            if (_openedArtist == null
                || !string.Equals(_openedArtist.Name, artist.Name, StringComparison.CurrentCultureIgnoreCase))
            {
                return;
            }

            ArtistDetailAvatarBrush.ImageSource = image;
            ArtistDetailAvatarPlaceholder.Visibility =
                image == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ArtistEntry artist)
            {
                if (_currentCategory is "Genres" or "Years")
                {
                    OpenGenreYearSongs(artist.Name);
                }
                else
                {
                    OpenArtistDetail(artist);
                }
            }
        }

        private void OpenArtistDetail(ArtistEntry artist)
            => CommitLibraryNavigation(() => OpenArtistDetailCore(artist));

        private void OpenArtistDetailCore(ArtistEntry artist)
        {
            _openedArtist = artist;
            _artistDetailUsesAlbumArtist = string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
            _artistSongSortMode = ArtistSongSortMode.Title;
            _artistAlbumSortMode = ArtistAlbumSortMode.Title;
            _artistAlbumSortAscending = true;
            ArtistSongSortButton.Content = "排序";
            ArtistAlbumSortFieldButton.Content = "按标题排序";
            ArtistAlbumSortOrderButton.Content = "升序";

            ArtistGridView.Visibility = Visibility.Collapsed;
            ArtistDetailPanel.Visibility = Visibility.Visible;
            LibraryPaneTitle.Text = artist.Name;

            ArtistDetailNameText.Text = artist.Name;
            ArtistDetailAvatarBrush.ImageSource = artist.AvatarImage;
            ArtistDetailAvatarPlaceholder.Visibility =
                artist.AvatarImage == null ? Visibility.Visible : Visibility.Collapsed;

            // 若头像未加载(如从超链接/播放面板进入用的是缓存或新建条目)，异步补齐后再应用，确保头像显示
            if (artist.AvatarImage == null)
            {
                _ = LoadArtistDetailAvatarAsync(artist);
            }

            RebuildArtistTracks();
            _ = RebuildArtistAlbumsAsync();
            ApplyArtistSongsFrostChrome();
            UpdateLibrarySearchUi();
        }

        /// <summary>异步加载艺术家头像并应用到详情页（超链接/播放面板进入时头像未填充的情况）。</summary>
        private async System.Threading.Tasks.Task LoadArtistDetailAvatarAsync(ArtistEntry artist)
        {
            try
            {
                BitmapImage? image = await ResolveArtistAvatarAsync(artist.Name, _artistDetailUsesAlbumArtist);
                if (image == null || !ReferenceEquals(_openedArtist, artist))
                {
                    return; // 已切到其它艺术家或加载失败
                }

                artist.AvatarImage = image;
                ArtistDetailAvatarBrush.ImageSource = image;
                ArtistDetailAvatarPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
        }

        private void ApplyArtistSongsFrostChrome()        {
            if (ArtistSongsFrostPanel == null)
            {
                return;
            }

            // 歌曲列表区无边框（与专辑详情页歌曲列表一致）
            ArtistSongsFrostPanel.Background = new SolidColorBrush(Colors.Transparent);
            ArtistSongsFrostPanel.BorderThickness = new Thickness(0);
            ArtistSongsFrostPanel.CornerRadius = new CornerRadius(0);
            ArtistSongsFrostPanel.Background = null;
            ArtistSongsFrostPanel.ClearValue(Border.CornerRadiusProperty);

            if (ArtistTrackListView != null)
            {
                ArtistTrackListView.Background = new SolidColorBrush(Colors.Transparent);
                ArtistTrackListView.BorderThickness = new Thickness(0);
                ApplyAccentSelectionResources(ArtistTrackListView);
            }
        }

        private List<PlaylistItem> GetTracksForArtist(string artistName, bool useCurrentSongSort)
        {
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            if (useCurrentSongSort
                && _openedArtist != null
                && string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
            {
                return ApplyArtistSongSort(tracks);
            }

            return tracks
                .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// replace=true：清空播放列表后写入并从头播放；
        /// replace=false：按「添加至播放队列」规则插到最前。
        /// </summary>
        private void PlayArtistWorks(string artistName, bool replacePlaylist)
        {
            List<PlaylistItem> tracks = GetTracksForArtist(artistName, useCurrentSongSort: true);
            if (tracks.Count == 0)
            {
                return;
            }

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                PlayUserPlaylistAt(0);
            }
            else
            {
                AddSongsToUserPlaylist(tracks);
            }
        }

        private void PlayArtistWorksButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedArtist != null)
            {
                PlayArtistWorks(_openedArtist.Name, replacePlaylist: true);
            }
        }

        private void AddArtistWorksToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedArtist != null)
            {
                AddSongsToUserPlaylist(GetTracksForArtist(_openedArtist.Name, useCurrentSongSort: true));
            }
        }

        private void PlayArtistSongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_artistTracks.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_artistTracks.ToList());
            PlayUserPlaylistAt(0);
        }

        private void AddArtistSongsToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_artistTracks.Count == 0)
            {
                return;
            }

            AddSongsToUserPlaylist(_artistTracks.ToList());
        }

        private void PlayAllArtistAlbumsButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> tracks = CollectArtistAlbumTracksInOrder();
            if (tracks.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(tracks);
            PlayUserPlaylistAt(0);
        }

        /// <summary>歌曲面板「播放所有歌曲」：把当前歌曲列表全部加入播放队列并按当前排序播放。</summary>
        private void PlayAllLibrarySongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_playlist.ToList());
            PlayUserPlaylistAt(0);
        }

        private void AddAllArtistAlbumsToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> tracks = CollectArtistAlbumTracksInOrder();
            if (tracks.Count == 0)
            {
                return;
            }

            AddSongsToUserPlaylist(tracks);
        }

        /// <summary>
        /// 按当前界面中的专辑顺序，各专辑内按音轨号收集曲目（去重）。
        /// 例：Album1(曲1,曲2) → Album2(曲a,曲b) ⇒ 曲1,曲2,曲a,曲b。
        /// </summary>
        private List<PlaylistItem> CollectArtistAlbumTracksInOrder()
        {
            return CollectTracksFromAlbumsInDisplayOrder(_artistAlbums);
        }

        /// <summary>按传入专辑集合顺序，各专辑内按音轨号收集曲目。</summary>
        private List<PlaylistItem> CollectTracksFromAlbumsInDisplayOrder(IEnumerable<AlbumEntry> albums)
        {
            var tracks = new List<PlaylistItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AlbumEntry album in albums)
            {
                foreach (PlaylistItem track in GetTracksForAlbum(album))
                {
                    if (seen.Add(track.FilePath))
                    {
                        tracks.Add(track);
                    }
                }
            }

            return tracks;
        }

        private void ArtistAlbumGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            AlbumEntry? album = null;
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is FrameworkElement { DataContext: AlbumEntry fromContext })
                    {
                        album = fromContext;
                        break;
                    }

                    if (current is GridViewItem container)
                    {
                        album = ArtistAlbumGridView.ItemFromContainer(container) as AlbumEntry;
                        break;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (album == null)
            {
                return;
            }

            AlbumEntry albumRef = album;
            if (!_isMultiSelectMode)
            {
                ArtistAlbumGridView.SelectedItem = albumRef;
            }

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该专辑" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayAlbum(albumRef, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterAlbumWallMultiSelectMode(ArtistAlbumGridView, albumRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => AddSongsToUserPlaylist(GetTracksForAlbum(albumRef));

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);
            var wallAlbumItem = new MenuFlyoutItem { Text = "添加到播放列表" };
            wallAlbumItem.Icon = new FontIcon { Glyph = "" };
            wallAlbumItem.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumRef));
            flyout.Items.Add(wallAlbumItem);

            AppendAlbumContextItems(flyout, albumRef, fromArtist: true);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(ArtistAlbumGridView, e.GetPosition(ArtistAlbumGridView));
            }

            e.Handled = true;
        }

        private void CloseArtistDetailUi()
        {
            ExitMultiSelectMode();
            _openedArtist = null;
            _artistDetailUsesAlbumArtist = false;
            _artistTracks.Clear();
            _artistAlbums.Clear();
            ArtistDetailPanel.Visibility = Visibility.Collapsed;
            ArtistGridView.Visibility = Visibility.Visible;
            ArtistDetailAvatarBrush.ImageSource = null;
            ArtistDetailAvatarPlaceholder.Visibility = Visibility.Visible;
            _albumOpenedFromArtist = false;
        }

        private void ArtistDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            CommitLibraryNavigation(() =>
            {
                CloseArtistDetailUi();
                if (_currentCategory == "Artists")
                {
                    LibraryPaneTitle.Text = "艺术家";
                }
                else if (_currentCategory == "AlbumArtists")
                {
                    LibraryPaneTitle.Text = "专辑艺术家";
                }

                UpdateLibrarySearchUi();
            });
        }

        private void RebuildArtistTracks()
        {
            _artistTracks.Clear();
            if (_openedArtist == null)
            {
                return;
            }

            string artistName = _openedArtist.Name;
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            foreach (PlaylistItem track in ApplyArtistSongSort(tracks))
            {
                _artistTracks.Add(track);
            }
        }

        private List<PlaylistItem> ApplyArtistSongSort(List<PlaylistItem> tracks)
        {
            Dictionary<string, uint> albumYears = tracks
                .GroupBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Where(t => t.Year > 0).Select(t => t.Year).DefaultIfEmpty(0u).Max(),
                    StringComparer.CurrentCultureIgnoreCase);

            return _artistSongSortMode switch
            {
                ArtistSongSortMode.AlbumTitleThenTrack => tracks
                    .OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                ArtistSongSortMode.AlbumYearThenTrack => tracks
                    .OrderBy(t =>
                    {
                        albumYears.TryGetValue(t.Album, out uint y);
                        return y == 0 ? uint.MaxValue : y;
                    })
                    .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                _ => tracks
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }

        private void ArtistSongSortMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _artistSongSortMode = tag switch
            {
                "AlbumTitle" => ArtistSongSortMode.AlbumTitleThenTrack,
                "AlbumYear" => ArtistSongSortMode.AlbumYearThenTrack,
                _ => ArtistSongSortMode.Title
            };

            ArtistSongSortButton.Content = tag switch
            {
                "AlbumTitle" => "排序：专辑（标题）",
                "AlbumYear" => "排序：专辑（时间）",
                _ => "排序：标题"
            };

            RebuildArtistTracks();
        }

        private async Task RebuildArtistAlbumsAsync()
        {
            _artistAlbums.Clear();
            if (_openedArtist == null)
            {
                return;
            }

            string artistName = _openedArtist.Name;
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            List<AlbumEntry> entries = BuildAlbumEntriesFromTracks(tracks);
            entries = ApplyArtistAlbumSort(entries);

            foreach (AlbumEntry entry in entries)
            {
                _artistAlbums.Add(entry);
            }

            foreach (AlbumEntry entry in entries)
            {
                if (_openedArtist == null ||
                    !string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return;
                }

                byte[]? bytes = await Task.Run(() => ExtractCoverBytes(entry.CoverSourcePath));
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                BitmapImage? image = await CreateBitmapFromBytesAsync(bytes);
                if (image != null)
                {
                    entry.CoverImage = image;
                    if (_openedAlbum != null &&
                        string.Equals(_openedAlbum.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AlbumDetailCoverImage.Source = image;
                    }
                }
            }
        }

        private List<AlbumEntry> ApplyArtistAlbumSort(List<AlbumEntry> source)
        {
            bool asc = _artistAlbumSortAscending;
            return _artistAlbumSortMode switch
            {
                ArtistAlbumSortMode.Year => asc
                    ? source
                        .OrderBy(a => a.Year == 0 ? uint.MaxValue : a.Year)
                        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
                    : source
                        .OrderByDescending(a => a.Year)
                        .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList(),
                _ => asc
                    ? source
                        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
                    : source
                        .OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
            };
        }

        private void ArtistAlbumSortFieldButton_Click(object sender, RoutedEventArgs e)
        {
            _artistAlbumSortMode = _artistAlbumSortMode == ArtistAlbumSortMode.Title
                ? ArtistAlbumSortMode.Year
                : ArtistAlbumSortMode.Title;
            ArtistAlbumSortFieldButton.Content = _artistAlbumSortMode == ArtistAlbumSortMode.Year
                ? "按年份排序"
                : "按标题排序";
            RefreshArtistAlbumListOrder();
        }

        private void ArtistAlbumSortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _artistAlbumSortAscending = !_artistAlbumSortAscending;
            ArtistAlbumSortOrderButton.Content = _artistAlbumSortAscending ? "升序" : "降序";
            RefreshArtistAlbumListOrder();
        }

        private void RefreshArtistAlbumListOrder()
        {
            if (_artistAlbums.Count <= 1)
            {
                return;
            }

            List<AlbumEntry> sorted = ApplyArtistAlbumSort(_artistAlbums.ToList());
            _artistAlbums.Clear();
            foreach (AlbumEntry entry in sorted)
            {
                _artistAlbums.Add(entry);
            }
        }

        private void ArtistTrackListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (ArtistTrackListView.SelectedItem is PlaylistItem track)
            {
                PlayPlaylistItem(track);
            }
        }

        private void ArtistTrackListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshArtistTrackSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
        }

        private void ArtistTrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(ArtistTrackListView, container, song);
            }
        }

        private void ArtistTrackListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = ArtistTrackListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            // 右键也先选中，显示主题色圆角
            if (!_isMultiSelectMode)
            {
                ArtistTrackListView.SelectedItem = song;
            }

            _contextMenuSong = song;
            var flyout = BuildPlaylistItemContextMenu(song, false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(ArtistTrackListView, e.GetPosition(ArtistTrackListView));
            }

            e.Handled = true;
        }

        private void ArtistAlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, ArtistAlbumGridView))
            {
                return;
            }

            if (e.ClickedItem is AlbumEntry album)
            {
                OpenAlbumDetail(album, fromArtist: true);
            }
        }

        private void ArtistAlbumGridView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);
            UpdateSelectAllMultiSelectButtonState();
        }

        private void ArtistAlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AlbumEntry album && args.ItemContainer is GridViewItem container)
            {
                ApplyAlbumGridItemSelectionChrome(ArtistAlbumGridView, container, album);
            }
        }

        /// <summary>从曲目集合汇总专辑条目（封面优先音轨 1）</summary>
        private static List<AlbumEntry> BuildAlbumEntriesFromTracks(IEnumerable<PlaylistItem> sourceTracks)
        {
            var groups = sourceTracks
                .GroupBy(p => p.Album, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var entries = new List<AlbumEntry>();
            int sortIndex = 0;
            foreach (var group in groups)
            {
                PlaylistItem coverTrack =
                    group.FirstOrDefault(t => (t.Disc == 0 || t.Disc == 1) && t.Track == 1)
                    ?? group.OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                        .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                        .ThenBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
                        .First();

                uint year = group.Where(t => t.Year > 0).Select(t => t.Year).DefaultIfEmpty(0u).Max();
                TimeSpan total = TimeSpan.FromTicks(group.Sum(t => t.Duration.Ticks));

                string artist = group
                    .GroupBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.First().Artist)
                    .FirstOrDefault() ?? "未知艺术家";

                bool allDsd = group.All(t => IsDsdFile(t.FilePath));
                string? dsdExt = allDsd ? DsdExtOf(group.First().FilePath) : null;

                entries.Add(new AlbumEntry
                {
                    Name = group.First().Album,
                    Artist = artist,
                    Year = year,
                    TrackCount = group.Count(),
                    TotalDuration = total,
                    TotalDurationText = FormatTime(total),
                    CoverSourcePath = coverTrack.FilePath,
                    SortIndex = sortIndex++,
                    IsDsd = allDsd
                });
                if (allDsd && entries.Count > 0)
                {
                    entries[^1].SetDsdContainer(dsdExt);
                }
            }

            return entries;
        }

        /// <summary>
        /// 播放单曲：先加入（或提前到）播放列表最前，再按播放列表播放。
        /// </summary>
        private void PlayPlaylistItem(PlaylistItem track)
        {
            // 若已在该播放队列中：保持原顺序，仅定位到它播放（不再把它移到最前）。
            int existing = FindUserPlaylistIndex(track.FilePath);
            if (existing >= 0)
            {
                PlayUserPlaylistAt(existing);
                return;
            }

            // 不在播放队列：加入队列（按现有 InsertPlaylistAtBegin 设置决定位置，通常放最前）再播放。
            AddSongsToUserPlaylist(new[] { track });
            int userIndex = FindUserPlaylistIndex(track.FilePath);
            if (userIndex >= 0)
            {
                PlayUserPlaylistAt(userIndex);
                return;
            }

            int libraryIndex = FindLibraryIndex(track.FilePath);
            if (libraryIndex >= 0)
            {
                PlayLibraryItemAt(libraryIndex, syncUserPlaylistIndex: false);
            }
            else
            {
                StartPlayback(track);
                _userPlaylistIndex = -1;
            }
        }

        private int FindUserPlaylistIndex(string filePath)
        {
            for (int i = 0; i < _userPlaylist.Count; i++)
            {
                if (string.Equals(_userPlaylist[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindLibraryIndex(string filePath)
        {
            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 从曲库汇总唯一专辑；封面优先取该专辑音轨号为 1 的文件。
        /// </summary>
        private async Task RefreshAlbumViewAsync()
        {
            _albums.Clear();

            if (_playlist.Count == 0)
            {
                return;
            }

            List<AlbumEntry> entries = BuildAlbumEntriesFromTracks(_playlist);
            entries = ApplyAlbumSortToList(entries);

            foreach (AlbumEntry entry in entries)
            {
                _albums.Add(entry);
            }

            ApplyAlbumsSearchFilter();

            foreach (AlbumEntry entry in entries)
            {
                if (_currentCategory != "Albums")
                {
                    return;
                }

                byte[]? bytes = await Task.Run(() => ExtractCoverBytes(entry.CoverSourcePath));
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                BitmapImage? image = await CreateBitmapFromBytesAsync(bytes);
                if (image != null)
                {
                    entry.CoverImage = image;
                    if (_openedAlbum != null &&
                        string.Equals(_openedAlbum.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AlbumDetailCoverImage.Source = image;
                    }
                }
            }
        }

        private void AlbumSortMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _albumSortMode = tag switch
            {
                "Artist" => AlbumSortMode.Artist,
                "Year" => AlbumSortMode.Year,
                "Added" => AlbumSortMode.Added,
                "Random" => AlbumSortMode.Random,
                "TrackCount" => AlbumSortMode.TrackCount,
                "TotalDuration" => AlbumSortMode.TotalDuration,
                _ => AlbumSortMode.Title
            };

            UpdateAlbumSortButtonsUi();
            ResortAlbumsInPlace();
        }

        /// <summary>升序/降序切换。</summary>
        private void AlbumSortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            // Random 排序无方向概念，切换无意义；仍允许翻转但不影响结果
            _albumSortAscending = !_albumSortAscending;
            UpdateAlbumSortButtonsUi();
            ResortAlbumsInPlace();
        }

        private void UpdateAlbumSortButtonsUi()
        {
            AlbumSortButton.Content = GetAlbumSortFieldName(_albumSortMode);
            if (AlbumSortOrderButton != null)
            {
                AlbumSortOrderButton.Content = _albumSortAscending ? "升序" : "降序";
            }
        }

        private static string GetAlbumSortFieldName(AlbumSortMode mode) => mode switch
        {
            AlbumSortMode.Artist => "按艺术家",
            AlbumSortMode.Year => "按发行年份",
            AlbumSortMode.Added => "按添加时间",
            AlbumSortMode.Random => "随机",
            AlbumSortMode.TrackCount => "按专辑曲目数",
            AlbumSortMode.TotalDuration => "按专辑总时长",
            _ => "按专辑名称"
        };

        private void ResortAlbumsInPlace()
        {
            if (_albums.Count <= 1)
            {
                ApplyAlbumsSearchFilter();
                return;
            }

            List<AlbumEntry> sorted = ApplyAlbumSortToList(_albums.ToList());
            _albums.Clear();
            foreach (AlbumEntry entry in sorted)
            {
                _albums.Add(entry);
            }

            ApplyAlbumsSearchFilter();
        }

        private List<AlbumEntry> ApplyAlbumSortToList(List<AlbumEntry> source)
        {
            bool asc = _albumSortAscending;
            IOrderedEnumerable<AlbumEntry> ordered = _albumSortMode switch
            {
                AlbumSortMode.Artist =>
                    asc ? source.OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.Artist, StringComparer.CurrentCultureIgnoreCase),
                AlbumSortMode.Year =>
                    asc ? source.OrderBy(a => a.Year == 0 ? uint.MaxValue : a.Year)
                          : source.OrderByDescending(a => a.Year),
                AlbumSortMode.Added =>
                    asc ? source.OrderBy(a => a.SortIndex)
                          : source.OrderByDescending(a => a.SortIndex),
                AlbumSortMode.Random =>
                    source.OrderBy(_ => System.Guid.NewGuid()),
                AlbumSortMode.TrackCount =>
                    asc ? source.OrderBy(a => a.TrackCount).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.TrackCount).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
                AlbumSortMode.TotalDuration =>
                    asc ? source.OrderBy(a => a.TotalDuration.Ticks).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.TotalDuration.Ticks).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
                _ =>
                    asc ? source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            };

            return ordered.ToList();
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, AlbumGridView))
            {
                return;
            }

            if (e.ClickedItem is AlbumEntry album)
            {
                OpenAlbumDetail(album, fromArtist: false);
            }
        }

        private void AlbumGridView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
            UpdateSelectAllMultiSelectButtonState();
        }

        private void AlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AlbumEntry album && args.ItemContainer is GridViewItem container)
            {
                ApplyAlbumGridItemSelectionChrome(AlbumGridView, container, album);
            }
        }


        /// <summary>专辑墙右键附加操作（音乐库专辑墙 / 艺术家详情专辑共用）。</summary>
        private void AppendAlbumContextItems(MenuFlyout flyout, AlbumEntry album, bool fromArtist)
        {
            var dlLyric = new MenuFlyoutItem { Text = "批量下载歌词" };
            dlLyric.Icon = new FontIcon { Glyph = "" };
            dlLyric.Click += async (_, _) =>
            {
                var tracks = GetTracksForAlbum(album);
                int ok = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    PlaylistItem song = tracks[i];
                    NowPlayingText.Text = $"正在批量下载歌词 ({i + 1}/{tracks.Count})…";
                    string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                    if (path != null)
                    {
                        ok++;
                    }
                }

                NowPlayingText.Text = $"歌词下载完成：{ok}/{tracks.Count}";
            };
            flyout.Items.Add(dlLyric);

            var dlCover = new MenuFlyoutItem { Text = "批量下载封面" };
            dlCover.Icon = new FontIcon { Glyph = "" };
            dlCover.Click += async (_, _) =>
            {
                var tracks = GetTracksForAlbum(album);
                int ok = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    PlaylistItem song = tracks[i];
                    NowPlayingText.Text = $"正在批量下载封面 ({i + 1}/{tracks.Count})…";
                    if (await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath))
                    {
                        InvalidateCoverCache(song.FilePath);
                        ok++;
                    }
                }

                NowPlayingText.Text = $"封面下载完成：{ok}/{tracks.Count}";
            };
            flyout.Items.Add(dlCover);

            var copyInfo = new MenuFlyoutItem { Text = "复制专辑信息" };
            copyInfo.Icon = new FontIcon { Glyph = "" };
            copyInfo.Click += (_, _) =>
            {
                var data = new DataPackage();
                data.SetText(string.IsNullOrWhiteSpace(album.Artist) ? album.Name : album.Name + " - " + album.Artist);
                Clipboard.SetContent(data);
                NowPlayingText.Text = "已复制专辑信息";
            };
            flyout.Items.Add(copyInfo);

            var openDetail = new MenuFlyoutItem { Text = "打开专辑详情" };
            openDetail.Icon = new FontIcon { Glyph = "" };
            openDetail.Click += (_, _) => OpenAlbumDetail(album, fromArtist);
            flyout.Items.Add(openDetail);
        }
        private void AlbumGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            AlbumEntry? album = null;
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is FrameworkElement { DataContext: AlbumEntry fromContext })
                    {
                        album = fromContext;
                        break;
                    }

                    if (current is GridViewItem container)
                    {
                        album = AlbumGridView.ItemFromContainer(container) as AlbumEntry;
                        break;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (album == null)
            {
                return;
            }

            AlbumEntry albumRef = album;
            if (!_isMultiSelectMode)
            {
                AlbumGridView.SelectedItem = albumRef;
            }

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该专辑" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayAlbum(albumRef, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterAlbumWallMultiSelectMode(AlbumGridView, albumRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => AddSongsToUserPlaylist(GetTracksForAlbum(albumRef));

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);
            var wallAlbumItem = new MenuFlyoutItem { Text = "添加到播放列表" };
            wallAlbumItem.Icon = new FontIcon { Glyph = "" };
            wallAlbumItem.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumRef));
            flyout.Items.Add(wallAlbumItem);

            AppendAlbumContextItems(flyout, albumRef, fromArtist: false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(AlbumGridView, e.GetPosition(AlbumGridView));
            }

            e.Handled = true;
        }

        private List<PlaylistItem> GetTracksForAlbum(AlbumEntry album)
        {
            return _playlist
                .Where(t =>
                    string.Equals(t.Album, album.Name, StringComparison.CurrentCultureIgnoreCase)
                    && (string.IsNullOrWhiteSpace(album.Artist)
                        || string.Equals(t.Artist, album.Artist, StringComparison.CurrentCultureIgnoreCase)))
                .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 播放专辑：可替换用户播放列表，并从音轨 1（若无则第一首）开始播放。
        /// </summary>
        private void PlayAlbum(AlbumEntry album, bool replacePlaylist)
        {
            List<PlaylistItem> tracks = GetTracksForAlbum(album);
            if (tracks.Count == 0)
            {
                return;
            }

            PlaylistItem first =
                tracks.FirstOrDefault(t => (t.Disc == 0 || t.Disc == 1) && t.Track == 1) ?? tracks[0];

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                int index = FindUserPlaylistIndex(first.FilePath);
                if (index >= 0)
                {
                    PlayUserPlaylistAt(index);
                }

                return;
            }

            PlayPlaylistItem(first);
        }

        private void AlbumDetailPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedAlbum != null)
            {
                PlayAlbum(_openedAlbum, replacePlaylist: true);
            }
        }

        private void AlbumDetailAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedAlbum != null)
            {
                AddSongsToUserPlaylist(GetTracksForAlbum(_openedAlbum));
            }
        }

        private void OpenAlbumDetail(AlbumEntry album, bool fromArtist)
            => CommitLibraryNavigation(() => OpenAlbumDetailCore(album, fromArtist));

        private void OpenAlbumDetailCore(AlbumEntry album, bool fromArtist)
        {
            _openedAlbum = album;
            _albumOpenedFromArtist = fromArtist;

            if (fromArtist)
            {
                ArtistListBorder.Visibility = Visibility.Collapsed;
                AlbumListBorder.Visibility = Visibility.Visible;
                AlbumDetailBackButton.Content = "← 返回艺术家";
            }
            else
            {
                AlbumDetailBackButton.Content = "← 返回专辑";
            }

            AlbumGridView.Visibility = Visibility.Collapsed;
            AlbumDetailPanel.Visibility = Visibility.Visible;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            LibraryPaneTitle.Text = album.Name;
            UpdateLibrarySearchUi();

            AlbumDetailCoverImage.Source = album.CoverImage;
            AlbumDetailNameText.Text = album.Name;
            AlbumDetailArtistText.Text = album.Artist;
            if (AlbumDetailSubInfoText != null)
            {
                // 行2：艺术家(超链接) | 发行时间 | 曲目数
                AlbumDetailSubInfoText.Text =
                    " | " + album.YearText + " | " + album.TrackCount + " 首";
            }

            _albumTracks.Clear();
            List<PlaylistItem> tracks = _playlist
                .Where(t => string.Equals(t.Album, album.Name, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (PlaylistItem track in tracks)
            {
                _albumTracks.Add(track);
            }

            // DSD 专辑提示：专辑内全部曲目为 DSF/DFF 时显示
            if (AlbumDetailDsdHint != null)
            {
                bool allDsd = tracks.Count > 0 && tracks.All(t => IsDsdFile(t.FilePath));
                AlbumDetailDsdHint.Visibility = allDsd ? Visibility.Visible : Visibility.Collapsed;
            }

            // 行3：编码器 | 位深/采样率 | 总时长（在后台线程算质量行，避免 CD 大专辑打开卡顿）
            if (AlbumDetailTechText != null)
            {
                AlbumDetailTechText.Text = string.Empty;
                _ = FillAlbumTechTextAsync(album, tracks);
            }

            // Apple Music 风格：按碟片分组显示（每组以 CD{n} 标题行开头）
            AlbumTrackListView.ItemsSource = BuildAlbumGroupedView(_albumTracks);
        }

        /// <summary>汇总专辑内歌曲质量行（编码器+位深/采样率，取出现最多的组合），如 "FLAC · 16bit/44kHz"。
        /// 只采样前若干首聚合，避免 CD 大专辑逐首解析(含 ffmpeg)导致打开卡顿。</summary>
        private static string BuildAlbumQualityLine(IEnumerable<PlaylistItem> tracks)
        {
            string? quality = tracks
                .Take(15)
                .Select(t => AudioInfoFormatter.FormatQualityLine(t.FilePath))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(quality) ? string.Empty : quality;
        }

        /// <summary>后台聚合专辑质量行并回填行3 TextBlock（避免 CD 大专辑逐首解析卡 UI）。</summary>
        private async System.Threading.Tasks.Task FillAlbumTechTextAsync(AlbumEntry album, IReadOnlyList<PlaylistItem> tracks)
        {
            try
            {
                string quality = await System.Threading.Tasks.Task.Run(() => BuildAlbumQualityLine(tracks));
                string duration = album.TotalDurationText;
                var segs = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(quality))
                {
                    segs.Add(quality.Replace(" · ", " | "));
                }

                if (!string.IsNullOrWhiteSpace(duration))
                {
                    segs.Add(duration);
                }

                string text = string.Join(" | ", segs);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AlbumDetailTechText != null)
                    {
                        AlbumDetailTechText.Text = text;
                    }
                });
            }
            catch
            {
            }
        }

        /// <summary>把专辑内歌曲按播放顺序（碟号→音轨）添加到某个播放列表。</summary>
        private void AlbumDetailAddToNamedListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_albumTracks.Count == 0)
            {
                return;
            }

            _ = ShowNamedPlaylistPickerAsync(_albumTracks.ToList());
        }

        // ---- 专辑艺术家超链接：悬停显示下划线，点击进入对应专辑艺术家详情页 ----
        private void AlbumDetailArtistLink_PointerEntered(object sender, PointerRoutedEventArgs e)        {
            if (AlbumDetailArtistText != null)
            {
                AlbumDetailArtistText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            }
        }

        private void AlbumDetailArtistLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (AlbumDetailArtistText != null)
            {
                AlbumDetailArtistText.TextDecorations = Windows.UI.Text.TextDecorations.None;
            }
        }

        private void AlbumDetailArtistLink_Click(object sender, RoutedEventArgs e)
        {
            string? artistName = string.IsNullOrWhiteSpace(_openedAlbum?.Artist) ? null : _openedAlbum.Artist;
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return;
            }

            // 进入“专辑艺术家”详情页：先切分类，再打开对应艺术家的详情
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AlbumArtists";
                ApplyCategoryView(); // 真正显示专辑艺术家分类的视图根，否则右侧面板不切换(仅左侧高亮)
                ArtistEntry? artist = _artists.FirstOrDefault(a =>
                    string.Equals(a.Name, artistName, StringComparison.CurrentCultureIgnoreCase));
                if (artist == null)
                {
                    artist = new ArtistEntry { Name = artistName };
                    _artists.Add(artist);
                }

                OpenArtistDetailCore(artist);
                UpdateLibraryNavHighlight();
            });
        }

        /// <summary>构建按碟片分组的视图（Apple Music 风格 CD 标题行）。</summary>
        private System.Collections.IEnumerable BuildAlbumGroupedView(IReadOnlyList<PlaylistItem> tracks)
        {
            bool hasDisc = tracks.Any(t => t.Disc > 0);
            var groups = new List<AlbumDiscGroup>();

            if (!hasDisc)
            {
                // 无碟号：不作分组，纯列表
                var g = new AlbumDiscGroup { Key = string.Empty };
                foreach (PlaylistItem t in tracks)
                {
                    g.Add(t);
                }

                groups.Add(g);
            }
            else
            {
                foreach (var grp in tracks
                    .GroupBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .OrderBy(g => g.Key))
                {
                    var g = new AlbumDiscGroup
                    {
                        Key = grp.Key == uint.MaxValue ? "CD?" : "CD" + grp.Key
                    };
                    foreach (PlaylistItem t in grp.OrderBy(x => x.Track == 0 ? uint.MaxValue : x.Track))
                    {
                        g.Add(t);
                    }

                    groups.Add(g);
                }
            }

            var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource
            {
                IsSourceGrouped = true
            };
            cvs.Source = groups;
            return cvs.View;
        }

        private void AlbumDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            CommitLibraryNavigation(() =>
            {
                if (_albumOpenedFromArtist && _openedArtist != null)
                {
                    CloseAlbumDetailUi();
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    ArtistGridView.Visibility = Visibility.Collapsed;
                    ArtistDetailPanel.Visibility = Visibility.Visible;
                    LibraryPaneTitle.Text = _openedArtist.Name;
                    UpdateLibrarySearchUi();
                    return;
                }

                CloseAlbumDetailUi();
                if (_currentCategory == "Albums")
                {
                    LibraryPaneTitle.Text = "专辑";
                    AlbumSortPanel.Visibility = Visibility.Visible;
                }

                UpdateLibrarySearchUi();
            });
        }

        private void CloseAlbumDetailUi()
        {
            _openedAlbum = null;
            _albumOpenedFromArtist = false;
            _albumTracks.Clear();
            AlbumDetailPanel.Visibility = Visibility.Collapsed;
            AlbumGridView.Visibility = Visibility.Visible;
            AlbumDetailCoverImage.Source = null;
            AlbumDetailBackButton.Content = "← 返回专辑";
        }

        private void AlbumTrackListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (AlbumTrackListView.SelectedItem is PlaylistItem track)
            {
                PlayPlaylistItem(track);
            }
        }

        private void AlbumTrackListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumTrackSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
        }

        private void AlbumTrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(AlbumTrackListView, container, song);
            }
        }

        private void AlbumTrackListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = AlbumTrackListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            if (!_isMultiSelectMode)
            {
                AlbumTrackListView.SelectedItem = song;
            }

            _contextMenuSong = song;
            var flyout = BuildPlaylistItemContextMenu(song, false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(AlbumTrackListView, e.GetPosition(AlbumTrackListView));
            }

            e.Handled = true;
        }

        /// <summary>
        /// 提取封面字节（可在后台线程调用）。按 UseInnerCoverFirst 设置决定
        /// 内嵌标签封面与外置封面（folder.jpg / cover.jpg / 同名图）的优先级。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> CoverBytesCache = new();
        private const int CoverBytesCacheMax = 200;

        internal static byte[]? ExtractCoverBytes(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            if (CoverBytesCache.TryGetValue(audioPath, out byte[]? cached))
            {
                return cached;
            }

            byte[]? inner = TryLoadInnerCover(audioPath);
            byte[]? outer = TryLoadOuterCover(audioPath);
            byte[]? result = AppSettingsStore.Load().UseInnerCoverFirst ? inner ?? outer : outer ?? inner;

            if (CoverBytesCache.Count >= CoverBytesCacheMax)
            {
                CoverBytesCache.Clear();
            }

            CoverBytesCache[audioPath] = result;
            return result;
        }

        /// <summary>封面写入磁盘后使缓存失效（下载封面后调用）。</summary>
        internal static void InvalidateCoverCache(string audioPath)
        {
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                CoverBytesCache.TryRemove(audioPath, out _);
            }
        }

        private static byte[]? TryLoadInnerCover(string audioPath)
        {
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(audioPath);
                IPicture[]? pictures = tagFile.Tag.Pictures;
                if (pictures == null || pictures.Length == 0)
                {
                    return null;
                }

                IPicture pic = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover)
                               ?? pictures[0];
                byte[] data = pic.Data.Data;
                return data is { Length: > 0 } ? data : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"提取封面失败 [{audioPath}]: {ex.Message}");
                return null;
            }
        }

        private static byte[]? TryLoadOuterCover(string audioPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(audioPath);
                string[] candidates =
                {
                    Path.Combine(dir, "folder.jpg"),
                    Path.Combine(dir, "folder.png"),
                    Path.Combine(dir, "cover.jpg"),
                    Path.Combine(dir, "cover.png"),
                    Path.Combine(dir, name + ".jpg"),
                    Path.Combine(dir, name + ".png")
                };

                foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        return System.IO.File.ReadAllBytes(candidate);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>字节 → BitmapImage（须在 UI 线程）</summary>
        private static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[] bytes)
        {
            try
            {
                var image = new BitmapImage();
                using InMemoryRandomAccessStream stream = new();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("封面解码失败: " + ex.Message);
                return null;
            }
        }

        // =====================================================================
        // 排序
        // =====================================================================

        private void SetSongSortUiForCategory(bool isUserPlaylist)
        {
            Visibility librarySort = isUserPlaylist ? Visibility.Collapsed : Visibility.Visible;
            Visibility playlistSort = isUserPlaylist ? Visibility.Visible : Visibility.Collapsed;
            SortFieldButton.Visibility = librarySort;
            SortOrderButton.Visibility = librarySort;
            ChangeSortButton.Visibility = playlistSort;
        }

        private void SortFieldMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _sortField = tag switch
            {
                "Artist" => SortField.Artist,
                "Album" => SortField.Album,
                "Year" => SortField.Year,
                "Duration" => SortField.Duration,
                _ => SortField.Title
            };

            SortFieldButton.Content = "排序：" + GetSortFieldDisplayName(_sortField);

            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;
            ApplySort(playingPath);
        }

        private void SortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _sortAscending = !_sortAscending;
            SortOrderButton.Content = _sortAscending ? "升序" : "降序";

            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;
            ApplySort(playingPath);
        }

        private async void ChangeSortButton_Click(object sender, RoutedEventArgs e)
            => await ChangeUserPlaylistSortAsync(Content.XamlRoot);

        /// <summary>播放列表「更改排序」对话框（主界面与当前播放列表窗口共用）。</summary>
        internal async Task ChangeUserPlaylistSortAsync(XamlRoot xamlRoot)
        {
            var fieldBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = _sortField switch
                {
                    SortField.Album => 1,
                    SortField.Artist => 2,
                    SortField.Year => 3,
                    SortField.Duration => 4,
                    _ => 0
                }
            };
            fieldBox.Items.Add("标题");
            fieldBox.Items.Add("专辑");
            fieldBox.Items.Add("艺术家");
            fieldBox.Items.Add("年份");
            fieldBox.Items.Add("时长");

            var orderBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = _sortAscending ? 0 : 1
            };
            orderBox.Items.Add("升序");
            orderBox.Items.Add("降序");

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "若更改排序，原有的顺序则会被打乱",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.85
            });
            panel.Children.Add(new TextBlock { Text = "排序字段", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(fieldBox);
            panel.Children.Add(new TextBlock { Text = "排序方向", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(orderBox);

            ContentDialog dialog = new()
            {
                Title = "更改排序",
                Content = panel,
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _sortField = fieldBox.SelectedIndex switch
            {
                1 => SortField.Album,
                2 => SortField.Artist,
                3 => SortField.Year,
                4 => SortField.Duration,
                _ => SortField.Title
            };
            _sortAscending = orderBox.SelectedIndex == 0;

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            SortCollection(_userPlaylist, playingPath);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        private static string GetSortFieldDisplayName(SortField field) => field switch
        {
            SortField.Artist => "艺术家",
            SortField.Album => "专辑",
            SortField.Year => "年份",
            SortField.Duration => "时长",
            _ => "标题"
        };

        private ObservableCollection<PlaylistItem> GetActiveSongCollection()
            => string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                ? _userPlaylist
                : _playlist;

        /// <summary>
        /// 按当前字段与升/降序重排当前中间列表，并刷新序号与当前播放下标。
        /// </summary>
        private void ApplySort(string? preservePlayingPath)
        {
            ObservableCollection<PlaylistItem> target = GetActiveSongCollection();
            SortCollection(target, preservePlayingPath);
            if (_currentCategory == "Songs")
            {
                ApplySongsSearchFilter();
            }
        }

        private void SortCollection(ObservableCollection<PlaylistItem> target, string? preservePlayingPath)
        {
            if (target.Count <= 1)
            {
                RenumberCollection(target);
                SyncIndicesAfterSort(target, preservePlayingPath);
                return;
            }

            IOrderedEnumerable<PlaylistItem> ordered = _sortField switch
            {
                SortField.Artist => target.OrderBy(i => i.Artist, StringComparer.CurrentCultureIgnoreCase),
                SortField.Album => target.OrderBy(i => i.Album, StringComparer.CurrentCultureIgnoreCase),
                SortField.Year => target.OrderBy(i => i.Year),
                SortField.Duration => target.OrderBy(i => i.Duration),
                _ => target.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            };

            // 次要关键字：标题，同字段时顺序更稳定
            ordered = ordered.ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase);

            List<PlaylistItem> sorted = (_sortAscending
                ? ordered.AsEnumerable()
                : ordered.Reverse()).ToList();

            target.Clear();
            foreach (PlaylistItem item in sorted)
            {
                target.Add(item);
            }

            RenumberCollection(target);
            SyncIndicesAfterSort(target, preservePlayingPath);
        }

        private void SyncIndicesAfterSort(ObservableCollection<PlaylistItem> target, string? preservePlayingPath)
        {
            if (ReferenceEquals(target, _playlist))
            {
                UpdateCurrentIndexByPath(preservePlayingPath);
                if (_currentIndex >= 0 && _currentIndex < _playlist.Count
                    && string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
                {
                    PlaylistView.SelectedIndex = _currentIndex;
                }
            }
            else if (ReferenceEquals(target, _userPlaylist) && !string.IsNullOrEmpty(preservePlayingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(preservePlayingPath);
                if (_userPlaylistIndex >= 0
                    && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
                {
                    PlaylistView.SelectedIndex = _userPlaylistIndex;
                }
            }
        }

        private void RenumberIndices() => RenumberCollection(_playlist);

        private static void RenumberCollection(ObservableCollection<PlaylistItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Index = i + 1;
            }
        }

        private void UpdateCurrentIndexByPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentIndex = i;
                    return;
                }
            }
        }

        // =====================================================================
        // 播放列表交互 / 右键菜单 / 多选
        // =====================================================================

        /// <summary>主内容区空白点击（非歌曲项）取消当前选中，从主题色背景恢复常态。</summary>
        private void MainContentGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Microsoft.UI.Xaml.DependencyObject? origin = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
            while (origin != null)
            {
                if (origin is ListViewItem or GridViewItem)
                {
                    return;
                }

                origin = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(origin);
            }

            // 点空白：取消各歌曲列表的选中
            if (!_isMultiSelectMode)
            {
                PlaylistView.SelectedItem = null;
                if (MediaDetailsList != null && MediaDetailsList.SelectionMode != ListViewSelectionMode.Multiple)
                {
                    MediaDetailsList.SelectedItem = null;
                }
            }
        }

        /// <summary>点击播放列表空白区取消当前选中（从主题色背景恢复常态）。</summary>
        private void PlaylistList_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            // 若点击落在某个列表项内，保留选中
            Microsoft.UI.Xaml.DependencyObject? origin = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
            while (origin != null)
            {
                if (origin is ListViewItem)
                {
                    return;
                }

                origin = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(origin);
            }

            // 点空白：取消选中
            if (PlaylistView.SelectedItem != null)
            {
                PlaylistView.SelectedItem = null;
            }
        }
        /// <summary>拖拽重排后更新歌曲序号（针对当前显示的集合），并强制刷新使序号立即生效。</summary>
        private void PlaylistView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            try
            {
                if (ReferenceEquals(sender.ItemsSource, _userPlaylist))
                {
                    RenumberCollection(_userPlaylist);
                }
                else if (ReferenceEquals(sender.ItemsSource, _playlist))
                {
                    RenumberCollection(_playlist);
                }
                else if (sender.ItemsSource is System.Collections.ObjectModel.ObservableCollection<PlaylistItem> col)
                {
                    RenumberCollection(col);
                }

                // x:Bind Index 为 OneTime：重设 ItemsSource 强制整体刷新，使拖拽后的序号立即更新
                if (sender is ListView lv)
                {
                    object? src = lv.ItemsSource;
                    lv.ItemsSource = null;
                    lv.ItemsSource = src;
                }
            }
            catch
            {
            }
        }

        private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (PlaylistView.SelectedItem is not PlaylistItem item)
            {
                return;
            }

            // 播放列表界面：按当前顺序播放，不把歌曲提前到最前
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                int index = FindUserPlaylistIndex(item.FilePath);
                if (index >= 0)
                {
                    PlayUserPlaylistAt(index);
                }

                return;
            }

            PlayPlaylistItem(item);
        }

        private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PlaylistListBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = PlaylistView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            _contextMenuSong = song;

            // 右键也先选中，主题色圆角与左键一致
            if (!_isMultiSelectMode)
            {
                PlaylistView.SelectedItem = song;
            }

            var flyout = BuildPlaylistItemContextMenu(song, string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal));

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(PlaylistView, e.GetPosition(PlaylistView));
            }

            e.Handled = true;
        }

        /// <summary>构建歌曲右键菜单（所有歌曲列表视图共用）。inUserPlaylist 为 true 时含用户播放列表专属项。</summary>
        private MenuFlyout BuildPlaylistItemContextMenu(PlaylistItem song, bool inUserPlaylist, Action? multiSelectAction = null)
        {
            _contextMenuSong = song;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                if (_contextMenuSong == null)
                {
                    return;
                }

                ExitMultiSelectMode();
                // 播放列表内：仅播放，不提前到最前；其它界面仍走加入并播放
                if (inUserPlaylist)
                {
                    int index = FindUserPlaylistIndex(_contextMenuSong.FilePath);
                    if (index >= 0)
                    {
                        PlayUserPlaylistAt(index);
                    }
                }
                else
                {
                    PlayPlaylistItem(_contextMenuSong);
                }
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) =>
            {
                if (multiSelectAction != null)
                {
                    multiSelectAction();
                }
                else
                {
                    EnterMultiSelectMode(_contextMenuSong);
                }
            };

            flyout.Items.Add(playItem);

            if (inUserPlaylist)
            {
                var pinItem = new MenuFlyoutItem { Text = "置顶" };
                // Upload：上箭头 + 顶栏横线，近似「横线下朝上箭头」
                pinItem.Icon = new FontIcon { Glyph = "\uE898" };
                pinItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        PinSongToUserPlaylistTop(_contextMenuSong);
                    }
                };
                flyout.Items.Add(pinItem);
            }

            flyout.Items.Add(multiItem);

            if (inUserPlaylist)
            {
                var removeItem = new MenuFlyoutItem { Text = "从播放队列中删除" };
                removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
                removeItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        RemoveSongsFromUserPlaylist(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(removeItem);
            }
            else
            {
                var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
                addItem.Icon = new FontIcon { Glyph = "\uE710" };
                addItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        AddSongsToUserPlaylist(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(addItem);

                var wallItem = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                wallItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        _ = ShowNamedPlaylistPickerAsync(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(wallItem);
            }

            AppendPlaylistContextFeatureItems(flyout, song, inUserPlaylist);

            var deleteDiskItem = new MenuFlyoutItem { Text = "从磁盘删除（回收站）" };
            deleteDiskItem.Icon = new FontIcon { Glyph = "\uE74D" };
            bool disableDelete = AppSettingsStore.Load().DisableDeleteFromDisk;
            if (disableDelete)
            {
                deleteDiskItem.Text = "从磁盘删除（已在设置中禁用）";
                deleteDiskItem.IsEnabled = false;
            }
            else
            {
                deleteDiskItem.Click += async (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        await DeleteSongFromDiskAsync(_contextMenuSong);
                    }
                };
            }

            flyout.Items.Add(deleteDiskItem);

            flyout.Items.Add(CreateOpenFileLocationMenuItem());
            return flyout;
        }

        /// <summary>将歌曲移到用户播放列表最前（已在最前则不变）。</summary>
        internal void PinSongToUserPlaylistTop(PlaylistItem song)
        {
            int index = FindUserPlaylistIndex(song.FilePath);
            if (index <= 0)
            {
                return;
            }

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            PlaylistItem item = _userPlaylist[index];
            _userPlaylist.RemoveAt(index);
            _userPlaylist.Insert(0, item);
            RenumberCollection(_userPlaylist);

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        private void ShowCurrentPlaylistButton_Click(object sender, RoutedEventArgs e)
            => ShowCurrentPlaylistWindow();

        internal void ShowCurrentPlaylistWindow()
        {
            if (_currentPlaylistWindow != null)
            {
                _currentPlaylistWindow.Activate();
                return;
            }

            _currentPlaylistWindow = new CurrentPlaylistWindow(this);
            _currentPlaylistWindow.Closed += (_, _) => _currentPlaylistWindow = null;
            _currentPlaylistWindow.Activate();
        }

        /// <summary>打开播放队列窗口（复用主播放列表，聚焦接下来的待播曲目）。</summary>
        internal void ShowPlayQueueWindow()
        {
            if (QueueWindow != null)
            {
                QueueWindow.Activate();
                return;
            }

            try
            {
                var w = new PlayQueueWindow(this);
                w.Activate();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ShowPlayQueueWindow", ex);
            }
        }

        /// <summary>从播放队列窗口清空整个播放列表（不弹确认）。</summary>
        internal void ClearUserPlaylistPublicFromQueue()
        {
            try
            {
                _userPlaylist.Clear();
                _userPlaylistIndex = -1;
                if (PlaylistView.ItemsSource == _userPlaylist || PlaylistView.ItemsSource != null)
                {
                    ApplyCategoryView();
                }

                StopEngineIfActive();
                MediaPlayer? p = GetPlayer();
                if (p != null)
                {
                    try
                    {
                        p.Source = null;
                    }
                    catch
                    {
                    }
                }

                NowPlayingTitleText.Text = "未在播放";
                ResetNowPlayingArtistAlbumLinks();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ClearQueue", ex);
            }
        }

        internal void NotifyCurrentPlaylistWindow()
            => _currentPlaylistWindow?.RefreshFromOwner();

        internal ObservableCollection<PlaylistItem> UserPlaylist => _userPlaylist;

        /// <summary>媒体库全部歌曲（供重复文件检测等窗口使用）。</summary>
        internal IReadOnlyList<PlaylistItem> LibraryTracks => _playlist;

        internal int UserPlaylistPlayingIndex => _userPlaylistIndex;

        internal Brush GetAccentBrush() => ResolveAccentBrush();

        internal Brush GetAccentForegroundBrush() => ResolveAccentForegroundBrush();

        internal Brush GetCapsuleFillBrush() => ResolveCapsuleFillBrush();

        internal Brush GetMultiSelectFrostBrush() => CreateMultiSelectFrostBrush();

        internal int FindUserPlaylistIndexPublic(string filePath) => FindUserPlaylistIndex(filePath);

        internal void PlayUserPlaylistAtPublic(int index) => PlayUserPlaylistAt(index);

        internal void RemoveSongsFromUserPlaylistPublic(IEnumerable<PlaylistItem> songs)
            => RemoveSongsFromUserPlaylist(songs);

        internal static void OpenFileLocationInExplorerPublic(string? filePath)
            => OpenFileLocationInExplorer(filePath);

        internal void PlayUserPlaylistFromStart()
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            ExitMultiSelectMode();
            PlayUserPlaylistAt(0);
            NotifyCurrentPlaylistWindow();
        }

        private void EnterMultiSelectMode(PlaylistItem? preselect)
        {
            // 退出专辑 / 文件夹多选，避免两套多选并存
            if (_multiSelectAlbumGrid != null)
            {
                ExitAlbumMultiSelectUiOnly();
            }

            if (_multiSelectFolderList != null)
            {
                ExitFolderMultiSelectUiOnly();
            }

            ListView? target = ResolveMultiSelectTargetList();
            if (target == null)
            {
                return;
            }

            _multiSelectTargetList = target;
            _multiSelectAlbumGrid = null;
            if (target == PlaylistView)
            {
                _playlistItemDefaultStyle ??= PlaylistView.ItemContainerStyle;
            }
            else if (target == ArtistTrackListView)
            {
                _artistTrackItemDefaultStyle ??= ArtistTrackListView.ItemContainerStyle;
            }
            else if (target == AlbumTrackListView)
            {
                _albumTrackItemDefaultStyle ??= AlbumTrackListView.ItemContainerStyle;
            }

            _isMultiSelectMode = true;
            SetListSelectionMode(target, ListViewSelectionMode.Multiple);

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择歌曲";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(target);
            ApplyMultiSelectItemStyle(target);
            UpdateLibrarySearchUi();
            if (target == PlaylistView)
            {
                ApplyCapsuleSortButtonStyle(accent: true);
            }

            if (preselect != null)
            {
                try
                {
                    target.SelectedItems.Add(preselect);
                }
                catch
                {
                    target.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(RefreshMultiSelectItemBackgrounds);
        }

        /// <summary>专辑墙多选（音乐库专辑 / 艺术家详情专辑）。</summary>
        private void EnterAlbumWallMultiSelectMode(GridView grid, AlbumEntry? preselect)
        {
            if (_multiSelectTargetList != null)
            {
                ExitSongMultiSelectUiOnly();
            }

            if (_multiSelectFolderList != null)
            {
                ExitFolderMultiSelectUiOnly();
            }

            if (ReferenceEquals(grid, ArtistAlbumGridView))
            {
                _artistAlbumItemDefaultStyle ??= ArtistAlbumGridView.ItemContainerStyle;
            }
            else
            {
                _libraryAlbumItemDefaultStyle ??= AlbumGridView.ItemContainerStyle;
            }

            _multiSelectAlbumGrid = grid;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;

            SetGridSelectionMode(grid, ListViewSelectionMode.Multiple);
            grid.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择专辑";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(grid);
            ApplyMultiSelectAlbumItemStyle(grid);
            UpdateLibrarySearchUi();

            if (preselect != null)
            {
                try
                {
                    grid.SelectedItems.Add(preselect);
                }
                catch
                {
                    grid.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshAlbumWallSelectionChrome(grid, GetAlbumCollectionForGrid(grid));
                UpdateSelectAllMultiSelectButtonState();
            });
        }

        private ObservableCollection<AlbumEntry> GetAlbumCollectionForGrid(GridView grid)
            => ReferenceEquals(grid, AlbumGridView) ? _albums : _artistAlbums;

        private ListView? ResolveMultiSelectTargetList()
        {
            if (PlaylistDetailBorder.Visibility == Visibility.Visible
                && PlaylistDetailListView != null)
            {
                return PlaylistDetailListView;
            }

            if (AlbumDetailPanel.Visibility == Visibility.Visible
                && AlbumTrackListView != null)
            {
                return AlbumTrackListView;
            }

            if (ArtistDetailPanel.Visibility == Visibility.Visible
                && ArtistTrackListView != null)
            {
                return ArtistTrackListView;
            }

            if (PlaylistListBorder.Visibility == Visibility.Visible)
            {
                return PlaylistView;
            }

            // 标签排序板块：面板曲目视角（Songs）时多选针对该列表
            if (string.Equals(_currentCategory, "TagSort", StringComparison.Ordinal)
                && _tagSortPanelMode == "Songs"
                && TagSortPanelSongListView != null
                && TagSortPanelSongListView.Visibility == Visibility.Visible)
            {
                return TagSortPanelSongListView;
            }

            return null;
        }

        private void ConfigureMultiSelectPrimaryAction()
        {
            bool isUserPlaylist = _multiSelectTargetList == PlaylistView
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal);
            bool isNamedPlaylistDetail = _multiSelectTargetList == PlaylistDetailListView;
            bool isTagSortSongs = _multiSelectTargetList == TagSortPanelSongListView;
            bool isDetailSongList = _multiSelectTargetList == AlbumTrackListView
                || _multiSelectTargetList == ArtistTrackListView;
            if (isUserPlaylist)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE74D"; // Delete
                MultiSelectPrimaryActionText.Text = "从播放队列中删除";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从播放列表移除");
            }
            else if (isNamedPlaylistDetail)
            {
                // 命中单详情页多选 → 从当前命名单删除勾选的歌
                MultiSelectPrimaryActionIcon.Glyph = "\uE74D"; // Delete
                MultiSelectPrimaryActionText.Text = "从播放队列中删除";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从当前命名单移除");
            }
            else if (isTagSortSongs)
            {
                // 标签排序面板曲目多选 → 添加到播放队列
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲按顺序加入播放队列");
            }
            else if (isDetailSongList)
            {
                // 专辑/艺术家/专辑艺术家详情页多选歌曲 → 添加到播放列表（列表墙/命名单）
                MultiSelectPrimaryActionIcon.Glyph = "\uE8B7";
                MultiSelectPrimaryActionText.Text = "添加到播放列表";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "把选中的歌曲添加到播放列表（列表墙）");
            }
            else if (_multiSelectAlbumGrid != null)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "按当前专辑顺序、音轨号将选中专辑加入播放队列");
            }
            else if (_multiSelectFolderList != null)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中文件夹/音频按顺序加入播放队列");
            }
            else
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710"; // Add
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲添加至播放队列");
            }

            // “添加到播放列表”按钮：多选界面右下角，与“添加至播放队列”并列显示（支持歌曲/专辑/文件夹统一添加）
            if (MultiSelectAddToPlaylistButton != null)
            {
                MultiSelectAddToPlaylistButton.Visibility = Visibility.Visible;
            }

            if (MultiSelectDeleteMenuItem != null)
            {
                MultiSelectDeleteMenuItem.IsEnabled = !AppSettingsStore.Load().DisableDeleteFromDisk;
            }
        }

        private void SelectAllMultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode)
            {
                return;
            }

            if (_multiSelectAlbumGrid != null)
            {
                GridView grid = _multiSelectAlbumGrid;
                ObservableCollection<AlbumEntry> allAlbums = GetAlbumCollectionForGrid(grid);
                bool allSelected = allAlbums.Count > 0
                    && grid.SelectedItems.Count >= allAlbums.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(grid);
                }
                else
                {
                    SelectAllInListViewBase(grid, allAlbums.Count);
                }

                RefreshAlbumWallSelectionChrome(grid, allAlbums);
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            if (_multiSelectFolderList != null)
            {
                bool allSelected = _folderBrowserItems.Count > 0
                    && FolderBrowserView.SelectedItems.Count >= _folderBrowserItems.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(FolderBrowserView);
                }
                else
                {
                    SelectAllInListViewBase(FolderBrowserView, _folderBrowserItems.Count);
                }

                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            ListView? target = _multiSelectTargetList;
            if (target == null)
            {
                return;
            }

            IReadOnlyList<PlaylistItem> allSongs = GetMultiSelectSongSource(target);
            bool songsAllSelected = target.SelectedItems.Count >= allSongs.Count && allSongs.Count > 0;
            if (songsAllSelected)
            {
                ClearListViewBaseSelection(target);
            }
            else
            {
                SelectAllInListViewBase(target, allSongs.Count);
            }

            RefreshSongListSelectionChrome(target);
            UpdateSelectAllMultiSelectButtonState();
        }

        /// <summary>用 SelectRange 一次选中全部，避免逐项 Add 触发数千次 SelectionChanged。</summary>
        private void SelectAllInListViewBase(ListViewBase list, int count)
        {
            if (count <= 0)
            {
                return;
            }

            _suppressSelectionUiUpdates = true;
            try
            {
                list.SelectRange(new ItemIndexRange(0, (uint)count));
            }
            catch
            {
                // 少数控件/状态不支持 SelectRange 时回退逐项添加
                for (int i = 0; i < count; i++)
                {
                    object? item = list.Items[i];
                    if (item == null || list.SelectedItems.Contains(item))
                    {
                        continue;
                    }

                    try
                    {
                        list.SelectedItems.Add(item);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }

        private void ClearListViewBaseSelection(ListViewBase list)
        {
            _suppressSelectionUiUpdates = true;
            try
            {
                if (list is ListView lv)
                {
                    SetListSelectionMode(lv, ListViewSelectionMode.Multiple);
                }
                else if (list is GridView gv)
                {
                    SetGridSelectionMode(gv, ListViewSelectionMode.Multiple);
                }
                else
                {
                    list.SelectedIndex = -1;
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }

        private IReadOnlyList<PlaylistItem> GetMultiSelectSongSource(ListView target)
        {
            if (target == ArtistTrackListView)
            {
                return _artistTracks;
            }

            if (target == AlbumTrackListView)
            {
                return _albumTracks;
            }

            return GetActiveSongCollection();
        }

        private void UpdateSelectAllMultiSelectButtonState()
        {
            if (SelectAllMultiSelectIcon == null || !_isMultiSelectMode)
            {
                return;
            }

            bool allSelected;
            if (_multiSelectAlbumGrid != null)
            {
                ObservableCollection<AlbumEntry> all = GetAlbumCollectionForGrid(_multiSelectAlbumGrid);
                allSelected = all.Count > 0
                    && _multiSelectAlbumGrid.SelectedItems.Count >= all.Count;
            }
            else if (_multiSelectFolderList != null)
            {
                allSelected = _folderBrowserItems.Count > 0
                    && FolderBrowserView.SelectedItems.Count >= _folderBrowserItems.Count;
            }
            else if (_multiSelectTargetList != null)
            {
                IReadOnlyList<PlaylistItem> all = GetMultiSelectSongSource(_multiSelectTargetList);
                allSelected = all.Count > 0
                    && _multiSelectTargetList.SelectedItems.Count >= all.Count;
            }
            else
            {
                allSelected = false;
            }

            // E73A CheckboxCompositeChecked / E739 CheckboxComposite
            SelectAllMultiSelectIcon.Glyph = allSelected ? "\uE73A" : "\uE739";
        }

        private void UpdateUserPlaylistActionBarVisibility()
        {
            bool show = !_isMultiSelectMode
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                && PlaylistListBorder.Visibility == Visibility.Visible;
            UserPlaylistActionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMultiSelectMode();

        private void ExitMultiSelectMode()
        {
            if (!_isMultiSelectMode)
            {
                MultiSelectActionBar.Visibility = Visibility.Collapsed;
                MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                if (LibraryPaneTitle != null
                    && (_currentCategory == "Songs"
                        || _currentCategory == "UserPlaylist"
                        || _currentCategory == "Artists"
                        || _currentCategory == "AlbumArtists"
                        || _currentCategory == "Albums"))
                {
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                }

                return;
            }

            ExitSongMultiSelectUiOnly();
            ExitAlbumMultiSelectUiOnly();
            ExitFolderMultiSelectUiOnly();

            _isMultiSelectMode = false;
            ApplyCapsuleSortButtonStyle(accent: true);
            MultiSelectActionBar.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            if (_currentCategory == "Songs" || _currentCategory == "UserPlaylist")
            {
                SongSortPanel.Visibility = Visibility.Visible;
                SetSongSortUiForCategory(isUserPlaylist: string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal));
            }
            else if (_currentCategory == "Albums")
            {
                AlbumSortPanel.Visibility = Visibility.Visible;
            }
            else
            {
                SongSortPanel.Visibility = Visibility.Collapsed;
                AlbumSortPanel.Visibility = Visibility.Collapsed;
            }

            UpdateUserPlaylistActionBarVisibility();
            DispatcherQueue.TryEnqueue(RefreshAllSongListSelectionChrome);
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);
                RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
                RefreshFolderBrowserSelectionChrome();
            });
            UpdateLibrarySearchUi();
        }

        private void ExitSongMultiSelectUiOnly()
        {
            if (_multiSelectTargetList == null)
            {
                return;
            }

            ListView target = _multiSelectTargetList;
            SetListSelectionMode(target, ListViewSelectionMode.Single);

            if (target == PlaylistView && _playlistItemDefaultStyle != null)
            {
                PlaylistView.ItemContainerStyle = _playlistItemDefaultStyle;
            }
            else if (target == ArtistTrackListView && _artistTrackItemDefaultStyle != null)
            {
                ArtistTrackListView.ItemContainerStyle = _artistTrackItemDefaultStyle;
            }
            else if (target == AlbumTrackListView && _albumTrackItemDefaultStyle != null)
            {
                AlbumTrackListView.ItemContainerStyle = _albumTrackItemDefaultStyle;
            }

            ApplyAccentSelectionResources(target);
            _multiSelectTargetList = null;
        }

        private void ExitAlbumMultiSelectUiOnly()
        {
            if (_multiSelectAlbumGrid == null)
            {
                return;
            }

            GridView grid = _multiSelectAlbumGrid;
            SetGridSelectionMode(grid, ListViewSelectionMode.Single);
            grid.IsItemClickEnabled = true;
            if (ReferenceEquals(grid, ArtistAlbumGridView) && _artistAlbumItemDefaultStyle != null)
            {
                ArtistAlbumGridView.ItemContainerStyle = _artistAlbumItemDefaultStyle;
            }
            else if (ReferenceEquals(grid, AlbumGridView) && _libraryAlbumItemDefaultStyle != null)
            {
                AlbumGridView.ItemContainerStyle = _libraryAlbumItemDefaultStyle;
            }

            ApplyAccentSelectionResources(grid);
            _multiSelectAlbumGrid = null;
        }

        private void ExitFolderMultiSelectUiOnly()
        {
            if (_multiSelectFolderList == null)
            {
                return;
            }

            SetListSelectionMode(FolderBrowserView, ListViewSelectionMode.Single);
            if (_folderItemDefaultStyle != null)
            {
                FolderBrowserView.ItemContainerStyle = _folderItemDefaultStyle;
            }

            ApplyAccentSelectionResources(FolderBrowserView);
            _multiSelectFolderList = null;
        }

        /// <summary>
        /// 通过 None 中转切换选择模式，安全清空选中项，避免 SelectedItems.Clear 崩溃。
        /// </summary>
        private void SetListSelectionMode(ListView list, ListViewSelectionMode mode)
        {
            try
            {
                list.SelectionMode = ListViewSelectionMode.None;
            }
            catch
            {
            }

            try
            {
                list.SelectionMode = mode;
            }
            catch
            {
            }

            // 本应用用主题色圆角底表示选中，关闭 Multiple 模式左侧系统复选框（否则会显示小黑块）
            list.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                list.SelectedItem = null;
            }
            catch
            {
            }
        }

        private void SetGridSelectionMode(GridView grid, ListViewSelectionMode mode)
        {
            try
            {
                grid.SelectionMode = ListViewSelectionMode.None;
            }
            catch
            {
            }

            try
            {
                grid.SelectionMode = mode;
            }
            catch
            {
            }

            grid.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                grid.SelectedItem = null;
            }
            catch
            {
            }
        }

        private void SetPlaylistSelectionMode(ListViewSelectionMode mode)
            => SetListSelectionMode(PlaylistView, mode);

        private void MultiSelectPrimaryActionButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            bool isUserPlaylist = _multiSelectTargetList == PlaylistView
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal);
            bool isNamedPlaylistDetail = _multiSelectTargetList == PlaylistDetailListView;
            bool isTagSortSongs = _multiSelectTargetList == TagSortPanelSongListView;
            bool isDetailSongList = _multiSelectTargetList == AlbumTrackListView
                || _multiSelectTargetList == ArtistTrackListView;
            if (isUserPlaylist)
            {
                RemoveSongsFromUserPlaylist(selected);
            }
            else if (isNamedPlaylistDetail)
            {
                // 命中单详情页多选 → 从当前命名单删除勾选的歌
                RemoveSongsFromCurrentPlaylistDetail(selected);
            }
            else if (isDetailSongList)
            {
                // 专辑/艺术家/专辑艺术家详情页多选 → 添加到播放列表（列表墙/命名单）
                _ = ShowNamedPlaylistPickerAsync(selected);
            }
            else
            {
                // 含标签排序面板曲目：添加到播放队列
                AddSongsToUserPlaylist(selected);
            }

            ExitMultiSelectMode();
        }

        private void MultiSelectAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            _ = ShowNamedPlaylistPickerAsync(selected); // 多选专辑 → 添加到播放列表（列表墙/命名单）
            ExitMultiSelectMode();
        }

        /// <summary>获取当前多选模式下选中的歌曲（专辑墙 / 文件夹 / 歌曲列表统一出口）。</summary>
        private List<PlaylistItem> GetSelectedMultiSelectSongs()
        {
            if (_multiSelectAlbumGrid != null)
            {
                GridView grid = _multiSelectAlbumGrid;
                // 标签排序分类墙/面板专辑/艺术家网格：选中项是 TagSortCategoryEntry，按各自字段聚合其曲目
                if (ReferenceEquals(grid, TagSortPanelGridView) || ReferenceEquals(grid, TagSortClassGridView))
                {
                    var cat = grid.SelectedItems.OfType<TagSortCategoryEntry>().ToList();
                    var songs = new List<PlaylistItem>();
                    foreach (var c in cat) songs.AddRange(CollectTagSortCategorySongs(c));
                    return songs;
                }

                var selectedAlbums = grid.SelectedItems.OfType<AlbumEntry>().ToList();
                List<AlbumEntry> ordered = GetAlbumCollectionForGrid(grid)
                    .Where(a => selectedAlbums.Contains(a))
                    .ToList();
                return CollectTracksFromAlbumsInDisplayOrder(ordered);
            }

            if (_multiSelectFolderList != null)
            {
                var selectedItems = FolderBrowserView.SelectedItems.OfType<FolderBrowserItem>().ToList();
                List<FolderBrowserItem> ordered = _folderBrowserItems
                    .Where(i => selectedItems.Contains(i))
                    .ToList();
                return CollectTracksFromSelectedFolderItems(ordered);
            }

            ListView target = _multiSelectTargetList ?? PlaylistView;
            return target.SelectedItems.OfType<PlaylistItem>().ToList();
        }

        private void MultiSelectFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (PlaylistItem song in selected)
            {
                TrackStatsStore.SetFavorite(song.FilePath, true);
            }

            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            NowPlayingText.Text = $"已收藏 {selected.Count} 首歌曲";
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            ExitMultiSelectMode();
        }

        private async void MultiSelectDownloadLyricButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                PlaylistItem song = selected[i];
                NowPlayingText.Text = $"正在下载歌词 ({i + 1}/{selected.Count})…";
                string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                if (path != null)
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"歌词下载完成：{ok}/{selected.Count}";
        
            }
            catch
            {
            }}

        private async void MultiSelectDownloadCoverButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                PlaylistItem song = selected[i];
                NowPlayingText.Text = $"正在下载封面 ({i + 1}/{selected.Count})…";
                if (await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath))
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"封面下载完成：{ok}/{selected.Count}";
        
            }
            catch
            {
            }}

        private void MultiSelectEditTagsButton_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedMultiSelectSongs();
            if (items.Count == 0)
            {
                return;
            }

            TagEditorWindow.ShowBatch(items.Select(i => i.FilePath).ToList());
        }

        private void MultiSelectCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (PlaylistItem song in selected)
            {
                sb.AppendLine(song.FilePath);
            }

            var data = new DataPackage();
            data.SetText(sb.ToString().TrimEnd());
            Clipboard.SetContent(data);
            NowPlayingText.Text = $"已复制 {selected.Count} 个文件位置";
        }

        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            if (AppSettingsStore.Load().DisableDeleteFromDisk)
            {
                NowPlayingText.Text = "已在设置中禁用从磁盘删除";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "删除歌曲",
                Content = $"确定要将选中的 {selected.Count} 首歌曲移动到回收站吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            foreach (PlaylistItem song in selected)
            {
                await DeleteSongFromDiskAsync(song);
            }

            NowPlayingText.Text = $"已将 {selected.Count} 首歌曲移入回收站";
            ExitMultiSelectMode();
        
            }
            catch
            {
            }}

        private void RemoveSongsFromUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            var paths = new HashSet<string>(
                songs.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            for (int i = _userPlaylist.Count - 1; i >= 0; i--)
            {
                if (paths.Contains(_userPlaylist[i].FilePath))
                {
                    _userPlaylist.RemoveAt(i);
                    if (i < _userPlaylistIndex)
                    {
                        _userPlaylistIndex--;
                    }
                }
            }

            if (_userPlaylistIndex >= _userPlaylist.Count)
            {
                _userPlaylistIndex = _userPlaylist.Count - 1;
            }

            RenumberCollection(_userPlaylist);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        private async void SavePlaylistButton_Click(object sender, RoutedEventArgs e)
            => await SaveUserPlaylistAsync(Content.XamlRoot);

        internal async Task SaveUserPlaylistAsync(XamlRoot xamlRoot)
        {
            try
            {
                if (_userPlaylist.Count == 0)
                {
                    await ShowErrorAsync("保存播放列表", "当前播放列表为空。", xamlRoot);
                    return;
                }

                string folder = UserPlaylistFileStore.EnsurePlayListFolder();
                string? name = await AskPlaylistFileNameAsync(xamlRoot);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                string safeName = UserPlaylistFileStore.SanitizeFileName(name);
                string path = Path.Combine(folder, safeName + UserPlaylistFileStore.FileExtension);

                var dto = new UserPlaylistFileDto
                {
                    Name = safeName,
                    SavedAt = DateTimeOffset.Now,
                    Songs = _userPlaylist.Select(s => new UserPlaylistSongDto
                    {
                        FilePath = s.FilePath,
                        Title = s.Title,
                        Artist = s.Artist,
                        Album = s.Album,
                        Year = s.Year,
                        DurationSeconds = s.Duration.TotalSeconds
                    }).ToList()
                };

                UserPlaylistFileStore.SaveToPath(path, dto);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("保存播放列表失败", ex.Message, xamlRoot);
            }
        }

        private async void OpenPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            // “添加到播放列表”：把当前队列歌曲追加到所选/新建命名单（列表墙）
            await ShowNamedPlaylistPickerAsync(_userPlaylist.ToList());
        }

        internal async Task OpenUserPlaylistAsync(XamlRoot xamlRoot, bool navigateToPlaylist)
        {
            try
            {
                string folder = UserPlaylistFileStore.EnsurePlayListFolder();
                string? path = await PickPlaylistPathFromFolderAsync(folder, xamlRoot);
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                UserPlaylistFileDto? dto = UserPlaylistFileStore.LoadFromPath(path);
                if (dto?.Songs == null)
                {
                    await ShowErrorAsync("打开播放列表失败", "文件格式无效。", xamlRoot);
                    return;
                }

                ApplyLoadedUserPlaylist(dto, navigateToPlaylist);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开播放列表失败", ex.Message, xamlRoot);
            }
        }

        private async void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
            => await ClearUserPlaylistAsync(Content.XamlRoot);

        internal async Task ClearUserPlaylistAsync(XamlRoot xamlRoot)
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                Title = "清空播放列表",
                Content = "确定清空当前播放列表中的全部歌曲吗？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ExitMultiSelectMode();
            _userPlaylist.Clear();
            _userPlaylistIndex = -1;
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        private void PlayUserPlaylistButton_Click(object sender, RoutedEventArgs e)
            => PlayUserPlaylistFromStart();

        private async Task<string?> AskPlaylistFileNameAsync(XamlRoot xamlRoot)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = "我的播放列表",
                PlaceholderText = "输入播放列表名称"
            };

            ContentDialog dialog = new()
            {
                Title = "保存播放列表",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Text?.Trim() : null;
        }

        private async Task<string?> PickPlaylistPathFromFolderAsync(string folderPath, XamlRoot xamlRoot)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folderPath, "*.json")
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                    .ToArray();
            }
            catch
            {
                files = Array.Empty<string>();
            }

            if (files.Length == 0)
            {
                await ShowErrorAsync("打开播放列表", "PlayList 文件夹中还没有已保存的播放列表。", xamlRoot);
                return null;
            }

            var list = new ListView
            {
                ItemsSource = files.Select(Path.GetFileName).ToList(),
                SelectionMode = ListViewSelectionMode.Single,
                Height = 220
            };
            list.SelectedIndex = 0;

            ContentDialog dialog = new()
            {
                Title = "选择播放列表",
                Content = list,
                PrimaryButtonText = "打开",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || list.SelectedIndex < 0)
            {
                return null;
            }

            return files[list.SelectedIndex];
        }

        private void ApplyLoadedUserPlaylist(UserPlaylistFileDto dto, bool navigateToPlaylist = true)
        {
            // 按文件顺序追加，不走「插到最前」逻辑，避免顺序颠倒
            _userPlaylist.Clear();
            foreach (UserPlaylistSongDto song in dto.Songs)
            {
                if (string.IsNullOrWhiteSpace(song.FilePath) || !System.IO.File.Exists(song.FilePath))
                {
                    continue;
                }

                if (_userPlaylist.Any(p =>
                        string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                PlaylistItem? fromLibrary = _playlist.FirstOrDefault(p =>
                    string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase));

                if (fromLibrary != null)
                {
                    _userPlaylist.Add(ClonePlaylistItem(fromLibrary));
                    continue;
                }

                TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, song.DurationSeconds));
                _userPlaylist.Add(new PlaylistItem
                {
                    Title = string.IsNullOrWhiteSpace(song.Title)
                        ? Path.GetFileNameWithoutExtension(song.FilePath)
                        : song.Title,
                    Artist = string.IsNullOrWhiteSpace(song.Artist) ? "未知艺术家" : song.Artist,
                    AlbumArtist = string.IsNullOrWhiteSpace(song.Artist) ? "未知艺术家" : song.Artist,
                    Album = string.IsNullOrWhiteSpace(song.Album) ? "未知专辑" : song.Album,
                    Year = song.Year,
                    Duration = duration,
                    DurationText = FormatTime(duration),
                    FilePath = song.FilePath
                });
            }

            RenumberCollection(_userPlaylist);
            NotifyCurrentPlaylistWindow();

            if (!navigateToPlaylist)
            {
                return;
            }

            CommitLibraryNavigation(() =>
            {
                _currentCategory = "UserPlaylist";
                ApplyCategoryView();
            });
        }

        /// <summary>
        /// 将歌曲插入播放列表最前（保持传入顺序）。
        /// 若已存在则先移除再提前，避免重复。大批量时整表重建，避免反复 Insert(0) 导致卡死/崩溃。
        /// </summary>
        /// <summary>弹“添加到播放列表”选择器（Poweramp 逻辑）：选命名单或新建，把歌曲追加进去。</summary>
        private async System.Threading.Tasks.Task ShowNamedPlaylistPickerAsync(IReadOnlyList<PlaylistItem> songs)
        {
            if (songs == null || songs.Count == 0) return;

            var paths = songs
                .Select(s => s.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) return;

            var prompt = new TextBlock
            {
                Text = "选择要添加到的播放列表：",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(prompt);

            var names = NamedPlaylistStore.List();
            bool first = true;
            foreach (string name in names)
            {
                var rb = new RadioButton { Content = name, GroupName = "__addtopl" };
                rb.Tag = name;
                rb.IsChecked = first;
                first = false;
                panel.Children.Add(rb);
            }

            var newNameBox = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "或输入新播放列表名称（回车即新建添加）" };
            panel.Children.Add(newNameBox);

            var dialog = new ContentDialog
            {
                Title = "添加到播放列表",
                Content = new ScrollViewer { Content = panel },
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            string? chosen = (panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string)?.Trim();
            string? typed = newNameBox.Text?.Trim();
            string? target = string.IsNullOrEmpty(typed) ? chosen : typed;
            if (string.IsNullOrEmpty(target)) return;

            var merged = NamedPlaylistStore
                .LoadSongs(target)
                .Concat(paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            NamedPlaylistStore.SaveSongs(target, merged);
            PlaylistLibraryService.Refresh();
            StartupLog.Write("已添加到播放列表: " + target + " (+" + paths.Count + " 首)");
        }

        private void AddSongsToUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            List<PlaylistItem> incoming = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
                .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (incoming.Count == 0)
            {
                return;
            }

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            var incomingPaths = new HashSet<string>(
                incoming.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            // 保留未出现在本批次中的旧项（相对顺序不变）
            var kept = new List<PlaylistItem>(_userPlaylist.Count);
            foreach (PlaylistItem existing in _userPlaylist)
            {
                if (!incomingPaths.Contains(existing.FilePath))
                {
                    kept.Add(existing);
                }
            }

            var rebuilt = new List<PlaylistItem>(incoming.Count + kept.Count);
            bool insertBegin = AppSettingsStore.Load().InsertPlaylistAtBegin;
            if (insertBegin)
            {
                foreach (PlaylistItem song in incoming)
                {
                    rebuilt.Add(ClonePlaylistItem(song));
                }

                rebuilt.AddRange(kept);
            }
            else
            {
                rebuilt.AddRange(kept);
                foreach (PlaylistItem song in incoming)
                {
                    rebuilt.Add(ClonePlaylistItem(song));
                }
            }
            for (int i = 0; i < rebuilt.Count; i++)
            {
                rebuilt[i].Index = i + 1;
            }

            // 整表替换，避免 Clear + N 次 Add 触发数千次 UI/集合通知导致卡死崩溃
            bool rebindPlaylistView = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist);
            if (rebindPlaylistView)
            {
                PlaylistView.ItemsSource = null;
            }

            _userPlaylist = new ObservableCollection<PlaylistItem>(rebuilt);

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _userPlaylist;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        /// <summary>在资源管理器中选中并显示该文件。</summary>
        private static void OpenFileLocationInExplorer(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("打开文件位置失败: " + ex.Message);
            }
        }

        /// <summary>将歌曲文件移入回收站，并从各列表移除。</summary>
        private async Task DeleteSongFromDiskAsync(PlaylistItem item)
        {
            if (AppSettingsStore.Load().DisableDeleteFromDisk)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                Title = "从磁盘删除",
                Content = $"确定将以下文件移到回收站吗？\n\n{item.FilePath}",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                if (!MoveToRecycleBin(item.FilePath))
                {
                    await ShowErrorAsync("删除失败", "无法将文件移到回收站。");
                    return;
                }

                RemoveSongFromAllCollections(item);
                if (string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _nowPlayingPath = null;
                    _currentIndex = -1;
                    try
                    {
                        MediaPlayer? player = GetPlayer();
                        player?.Pause();
                        if (player != null)
                        {
                            player.PlaybackSession.Position = TimeSpan.Zero;
                            player.Source = null;
                        }
                    }
                    catch
                    {
                    }
                }

                NotifyCurrentPlaylistWindow();
                NowPlayingText.Text = "已删除：" + item.Title;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("删除失败", ex.Message);
            }
        }

        private static bool MoveToRecycleBin(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x40 | 0x10 | 0x04 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                return SHFileOperation(ref op) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>从当前曲库、用户播放列表与统计记录中移除该歌曲。</summary>
        private void RemoveSongFromAllCollections(PlaylistItem item)
        {
            bool curWasDeleted = _currentIndex >= 0 && _currentIndex < _playlist.Count
                && string.Equals(_playlist[_currentIndex].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);
            bool userWasDeleted = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                && string.Equals(_userPlaylist[_userPlaylistIndex].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);

            for (int i = _playlist.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_playlist[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _playlist.RemoveAt(i);
                    if (i < _currentIndex)
                    {
                        _currentIndex--;
                    }
                }
            }

            for (int i = _userPlaylist.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_userPlaylist[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _userPlaylist.RemoveAt(i);
                    if (i < _userPlaylistIndex)
                    {
                        _userPlaylistIndex--;
                    }
                }
            }

            if (curWasDeleted)
            {
                _currentIndex = -1;
            }

            if (userWasDeleted)
            {
                _userPlaylistIndex = -1;
            }

            RenumberCollection(_playlist);
            RenumberCollection(_userPlaylist);
            _ = RefreshAlbumViewAsync();
            _ = RefreshArtistViewAsync();
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

        private MenuFlyoutItem CreateOpenFileLocationMenuItem()
        {
            var item = new MenuFlyoutItem { Text = "打开文件位置" };
            item.Icon = new FontIcon { Glyph = "\uE8DA" };
            item.Click += (_, _) =>
            {
                if (_contextMenuSong != null)
                {
                    OpenFileLocationInExplorer(_contextMenuSong.FilePath);
                }
            };
            return item;
        }

        private static PlaylistItem ClonePlaylistItem(PlaylistItem song)
            => new()
            {
                Title = song.Title,
                Artist = song.Artist,
                AlbumArtist = song.AlbumArtist,
                Album = song.Album,
                Track = song.Track,
                Year = song.Year,
                Genre = song.Genre,
                Duration = song.Duration,
                DurationText = song.DurationText,
                FilePath = song.FilePath,
                StartTimeSeconds = song.StartTimeSeconds
            };

        /// <summary>
        /// 左侧分类：歌曲/专辑/艺术家/文件夹为圆角选中；
        /// 播放列表为胶囊框，选中时填主题色、文字对比色。
        /// </summary>
        private void UpdateLibraryNavHighlight()
        {
            Brush accent = ResolveAccentBrush();
            Brush fg = ResolveContrastingForeground(accent);
            var transparent = new SolidColorBrush(Colors.Transparent);
            Brush capsuleIdle = ResolveCapsuleFillBrush();
            Brush capsuleBorder = ResolveNavCapsuleBorderBrush();

            Button[] libraryButtons =
            {
                NavSongsButton,
                NavAlbumsButton,
                NavArtistsButton,
                NavAlbumArtistsButton,
                NavFoldersButton,
                NavFavoritesButton,
                NavRecentButton,
                NavPlaylistWallButton,
                NavGenreButton,
                NavYearButton
            };

            foreach (Button button in libraryButtons)
            {
                button.CornerRadius = new CornerRadius(8);
                button.BorderThickness = new Thickness(0);
                string tag = button.Tag as string ?? string.Empty;
                bool active = string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                    || (tag == "Genres" && _currentCategory is "Genres" or "GenreSongs")
                    || (tag == "Years" && _currentCategory is "Years" or "YearSongs");
                if (active)
                {
                    button.Background = accent;
                    button.Foreground = fg;
                }
                else
                {
                    button.Background = transparent;
                    button.ClearValue(Control.ForegroundProperty);
                }
            }

            const double playlistCapsuleHeight = 40;
            UserPlaylistNavButton.Height = playlistCapsuleHeight;
            UserPlaylistNavButton.MinHeight = playlistCapsuleHeight;
            UserPlaylistNavButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
            UserPlaylistNavButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            bool playlistActive = string.Equals(_currentCategory, "PlaylistWall", StringComparison.Ordinal);
            if (playlistActive)
            {
                UserPlaylistNavButton.Background = accent;
                UserPlaylistNavButton.Foreground = fg;
                UserPlaylistNavButton.BorderThickness = new Thickness(0);
                UserPlaylistNavButton.ClearValue(Control.BorderBrushProperty);
            }
            else
            {
                UserPlaylistNavButton.Background = capsuleIdle;
                UserPlaylistNavButton.BorderThickness = new Thickness(1);
                UserPlaylistNavButton.BorderBrush = capsuleBorder;
                UserPlaylistNavButton.ClearValue(Control.ForegroundProperty);
            }

            // 标签排序胶囊按钮（与播放列表同款）
            NavTagSortButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
            NavTagSortButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            bool tagSortActive = string.Equals(_currentCategory, "TagSort", StringComparison.Ordinal);
            if (tagSortActive)
            {
                NavTagSortButton.Background = accent;
                NavTagSortButton.Foreground = fg;
                NavTagSortButton.BorderThickness = new Thickness(0);
                NavTagSortButton.ClearValue(Control.BorderBrushProperty);
            }
            else
            {
                NavTagSortButton.Background = capsuleIdle;
                NavTagSortButton.BorderThickness = new Thickness(1);
                NavTagSortButton.BorderBrush = capsuleBorder;
                NavTagSortButton.ClearValue(Control.ForegroundProperty);
            }

            // 音效处理胶囊按钮（占位，后续接入 ECHO 音效页面）
            if (NavAudioFxButton != null)
            {
                NavAudioFxButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
                NavAudioFxButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                bool fxActive = string.Equals(_currentCategory, "AudioFX", StringComparison.Ordinal);
                if (fxActive)
                {
                    NavAudioFxButton.Background = accent;
                    NavAudioFxButton.Foreground = fg;
                    NavAudioFxButton.BorderThickness = new Thickness(0);
                    NavAudioFxButton.ClearValue(Control.BorderBrushProperty);
                }
                else
                {
                    NavAudioFxButton.Background = capsuleIdle;
                    NavAudioFxButton.BorderThickness = new Thickness(1);
                    NavAudioFxButton.BorderBrush = capsuleBorder;
                    NavAudioFxButton.ClearValue(Control.ForegroundProperty);
                }
            }
        }

        private Brush ResolveNavCapsuleBorderBrush()
        {
            if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? card)
                && card is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        }

        /// <summary>
        /// 隐藏系统默认选中底（方角），选中态改由模板内 SongRowChrome / AlbumRowChrome 圆角 Border 绘制。
        /// </summary>
        private void ApplyAccentSelectionResources(FrameworkElement host)
        {
            Brush transparent = new SolidColorBrush(Colors.Transparent);
            Brush accent = ResolveAccentBrush();
            Brush fg = ResolveContrastingForeground(accent);

            string[] backgroundKeys =
            {
                "ListViewItemBackgroundSelected",
                "ListViewItemBackgroundSelectedPointerOver",
                "ListViewItemBackgroundSelectedPressed",
                "ListViewItemBackgroundSelectedDisabled",
                "GridViewItemBackgroundSelected",
                "GridViewItemBackgroundSelectedPointerOver",
                "GridViewItemBackgroundSelectedPressed",
                "GridViewItemBackgroundSelectedDisabled"
            };

            string[] foregroundKeys =
            {
                "ListViewItemForegroundSelected",
                "ListViewItemForegroundSelectedPointerOver",
                "ListViewItemForegroundSelectedPressed",
                "GridViewItemForegroundSelected",
                "GridViewItemForegroundSelectedPointerOver",
                "GridViewItemForegroundSelectedPressed"
            };

            foreach (string key in backgroundKeys)
            {
                host.Resources[key] = transparent;
            }

            foreach (string key in foregroundKeys)
            {
                host.Resources[key] = fg;
            }

            // 关闭系统选中勾（多选时默认会出现在词条左侧）
            host.Resources["ListViewItemSelectionCheckMarkVisualEnabled"] = false;
            host.Resources["GridViewItemSelectionCheckMarkVisualEnabled"] = false;
        }

        /// <summary>
        /// 排序按钮统一为操场形胶囊（高 32、圆角 16 = 两头半圆），底色为系统主题色。
        /// </summary>
        private void ApplyCapsuleSortButtonStyle(bool accent)
        {
            const double height = 32;
            var capsule = new CornerRadius(height / 2.0); // 半高等于半径 → 两头圆、中间直

            // 排序相关按钮始终使用主题色；accent 参数保留以兼容旧调用
            Brush background = ResolveAccentBrush();
            Brush foreground = ResolveAccentForegroundBrush();

            ApplyCapsuleToControl(SortFieldButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(SortOrderButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(ChangeSortButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(AlbumSortButton, height, capsule, background, foreground);
            if (ArtistSongSortButton != null)
            {
                ApplyCapsuleToControl(ArtistSongSortButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortFieldButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortFieldButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortOrderButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortOrderButton, height, capsule, background, foreground);
            }

            if (SelectAllMultiSelectButton != null)
            {
                Brush selectAllBg = accent ? background : ResolveCapsuleFillBrush();
                Brush? selectAllFg = accent ? foreground : null;
                ApplyCapsuleToControl(
                    SelectAllMultiSelectButton,
                    height,
                    new CornerRadius(8),
                    selectAllBg,
                    selectAllFg);
            }
        }

        private static void ApplyCapsuleToControl(
            Control control,
            double height,
            CornerRadius capsule,
            Brush background,
            Brush? foreground)
        {
            control.Height = height;
            control.MinHeight = height;
            control.CornerRadius = capsule;
            control.Background = background;
            control.BorderThickness = new Thickness(0);
            control.Padding = new Thickness(14, 0, 14, 0);
            // 防止按钮被父容器纵向拉伸导致其高度大于胶囊(32)使文字中心落到胶囊下半 → 视觉偏下；
            // 强制垂直居中对齐 + 相对父容器居中，使文字相对主题胶囊真正垂直居中。
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalContentAlignment = HorizontalAlignment.Center;
            control.VerticalContentAlignment = VerticalAlignment.Center;

            if (foreground != null)
            {
                control.Foreground = foreground;
            }
            else
            {
                control.ClearValue(Control.ForegroundProperty);
            }
        }

        private Brush ResolveCapsuleFillBrush()
        {
            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? controlBrush)
                && controlBrush is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }

        /// <summary>
        /// 歌曲列表表头五词条：与「播放所有专辑」同色半透明底，操场形圆角（高 32 / 半径 16）。
        /// </summary>
        private void ApplyPlaylistHeaderChipStyle()
        {
            Brush fill = ResolveCapsuleFillBrush();
            foreach (Border? chip in new[]
                     {
                         HeaderTitleChip,
                         HeaderArtistChip,
                         HeaderAlbumChip,
                         HeaderYearChip,
                         HeaderDurationChip
                     })
            {
                if (chip == null)
                {
                    continue;
                }

                chip.Height = 32;
                chip.MinHeight = 32;
                chip.CornerRadius = new CornerRadius(16);
                chip.Background = fill;
                chip.BorderThickness = new Thickness(0);
            }
        }

        private Brush CreateMultiSelectFrostBrush()
        {
            if (_cachedMultiSelectFrostBrush != null)
            {
                return _cachedMultiSelectFrostBrush;
            }

            // 使用半透明纯色，避免 Acrylic 采样悬停高亮后越来越白、移开也不恢复
            Color baseTint = ResolveUiBaseTintColor();
            _cachedMultiSelectFrostBrush = new SolidColorBrush(
                Color.FromArgb(70, baseTint.R, baseTint.G, baseTint.B));
            return _cachedMultiSelectFrostBrush;
        }

        /// <summary>
        /// 关闭系统默认选中底（方角），选中态改由模板内 SongRowChrome / AlbumRowChrome 圆角 Border 绘制。
        /// 多选状态下内容铺满由 ListViewItem 覆盖模板的 ContentPresenter=Stretch 保证。
        /// </summary>
        private void ApplyMultiSelectItemStyle(ListView target)
        {
            ApplyAccentSelectionResources(target);

            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 40.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            target.ItemContainerStyle = style;
            RefreshAllSongListSelectionChrome();
        }

        private void ApplyMultiSelectAlbumItemStyle(GridView grid)
        {
            ApplyAccentSelectionResources(grid);

            double margin = ReferenceEquals(grid, AlbumGridView) ? 8 : 6;
            var style = new Style(typeof(GridViewItem));
            style.Setters.Add(new Setter(GridViewItem.MarginProperty, new Thickness(margin)));
            style.Setters.Add(new Setter(GridViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(GridViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(GridViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(GridViewItem.BorderThicknessProperty, new Thickness(0)));
            grid.ItemContainerStyle = style;
            RefreshAlbumWallSelectionChrome(grid, GetAlbumCollectionForGrid(grid));
        }

        private void PlaylistView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshPlaylistSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();

            // 未播放时:预览选中歌曲的波形(波形进度条模式)
            if (string.IsNullOrEmpty(_nowPlayingPath)
                && _progressBarStyle == "Waveform"
                && e.AddedItems.Count > 0
                && e.AddedItems[0] is PlaylistItem selItem
                && !string.Equals(_waveformPath, selItem.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadWaveformForCurrentAsync(selItem.FilePath);
            }
        }

        private void PlaylistView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(PlaylistView, container, song);
                if (args.InRecycleQueue)
                {
                    return;
                }

                // 行模板尚未实现时（Phase 0）触发小封面异步加载
                if (args.Phase == 0)
                {
                    LoadRowCoverAsync(PlaylistView, container, song);
                }
            }
        }

        /// <summary>异步读取歌曲小封面并填到行模板内的 RowCoverImage；去重 + 行已回收则跳过。</summary>
        private async void LoadRowCoverAsync(ListView owner, ListViewItem container, PlaylistItem song)
        {
            if (string.IsNullOrWhiteSpace(song.FilePath))
            {
                return;
            }

            if (!_rowCoverLoading.Add(song.FilePath))
            {
                return;
            }

            try
            {
                byte[]? bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(song.FilePath));
                if (bytes is not { Length: > 0 })
                {
                    return;
                }

                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    ms.Position = 0;
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                }

                // 仅在行仍被实现（未回收）时更新，避免写错容器
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (owner.ContainerFromItem(song) == container)
                    {
                        var img = container.ContentTemplateRoot as FrameworkElement;
                        var coverImg = img?.FindName("RowCoverImage") as Microsoft.UI.Xaml.Controls.Image;
                        if (coverImg != null)
                        {
                            coverImg.Source = bmp;
                        }
                    }
                });
            }
            catch
            {
            }
            finally
            {
                _rowCoverLoading.Remove(song.FilePath);
            }
        }

        private void RefreshMultiSelectItemBackgrounds() => RefreshAllSongListSelectionChrome();

        private void RefreshAllSongListSelectionChrome()
        {
            RefreshPlaylistSelectionChrome();
            RefreshArtistTrackSelectionChrome();
            RefreshAlbumTrackSelectionChrome();
        }

        private void RefreshSongListSelectionChrome(ListView list)
        {
            if (ReferenceEquals(list, ArtistTrackListView))
            {
                RefreshArtistTrackSelectionChrome();
            }
            else if (ReferenceEquals(list, AlbumTrackListView))
            {
                RefreshAlbumTrackSelectionChrome();
            }
            else
            {
                RefreshPlaylistSelectionChrome();
            }
        }

        private void RefreshAlbumTrackSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(AlbumTrackListView);

        /// <summary>
        /// 歌曲列表 / 多选：选中为圆角主题色矩形（画在 SongRowChrome 上）；
        /// 多选未选中为浅霜色底；再次点击取消选择时恢复常态。
        /// 仅刷新已实现容器，避免对全库做 ContainerFromItem。
        /// </summary>
        private void RefreshPlaylistSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(PlaylistView);

        private void RefreshArtistTrackSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(ArtistTrackListView);

        /// <summary>
        /// 列表宽度变化时，重刷已实现行的宽度，使行内容始终铺满（选中矩形铺满整行、字段对齐）。
        /// </summary>
        private void SongListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListView list && list.ActualWidth > 0)
            {
                RefreshRealizedSongListSelectionChrome(list);
            }
        }

        private void RefreshRealizedSongListSelectionChrome(ListView list)
        {
            HashSet<object>? selectedSet = BuildSelectedItemsLookup(list);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(list))
            {
                if (list.ItemFromContainer(container) is PlaylistItem song)
                {
                    ApplySongListItemSelectionChrome(list, container, song, selectedSet);
                }
            }
        }

        private void RefreshFolderBrowserSelectionChrome()
        {
            HashSet<object>? selectedSet = BuildSelectedItemsLookup(FolderBrowserView);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(FolderBrowserView))
            {
                if (FolderBrowserView.ItemFromContainer(container) is FolderBrowserItem item)
                {
                    ApplyFolderBrowserItemSelectionChrome(container, item, selectedSet);
                }
            }
        }

        private void RefreshArtistAlbumSelectionChrome()
            => RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);

        private void RefreshAlbumWallSelectionChrome(GridView grid, IEnumerable<AlbumEntry> albums)
        {
            HashSet<object>? selectedSet = BuildSelectedItemsLookup(grid);
            bool anyRealized = false;
            foreach (GridViewItem container in EnumerateRealizedGridViewItems(grid))
            {
                anyRealized = true;
                if (grid.ItemFromContainer(container) is AlbumEntry album)
                {
                    ApplyAlbumGridItemSelectionChrome(grid, container, album, selectedSet);
                }
            }

            // 面板尚未生成时回退（极少见）
            if (!anyRealized)
            {
                foreach (AlbumEntry album in albums)
                {
                    if (grid.ContainerFromItem(album) is GridViewItem container)
                    {
                        ApplyAlbumGridItemSelectionChrome(grid, container, album, selectedSet);
                    }
                }
            }
        }

        private static HashSet<object>? BuildSelectedItemsLookup(ListViewBase list)
        {
            int count = list.SelectedItems.Count;
            if (count <= 64)
            {
                return null;
            }

            var set = new HashSet<object>();
            foreach (object item in list.SelectedItems)
            {
                set.Add(item);
            }

            return set;
        }

        private static bool IsItemSelected(ListViewBase list, object item, HashSet<object>? selectedSet)
        {
            if (selectedSet != null)
            {
                return selectedSet.Contains(item);
            }

            return list.SelectedItems.Contains(item);
        }

        private static IEnumerable<ListViewItem> EnumerateRealizedListViewItems(ListView list)
        {
            Panel? panel = FindItemsPanel(list);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is ListViewItem item)
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<GridViewItem> EnumerateRealizedGridViewItems(GridView grid)
        {
            Panel? panel = FindItemsPanel(grid);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is GridViewItem item)
                {
                    yield return item;
                }
            }
        }

        private static Panel? FindItemsPanel(DependencyObject root)
        {
            if (root is ItemsStackPanel stack)
            {
                return stack;
            }

            if (root is ItemsWrapGrid wrap)
            {
                return wrap;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                Panel? found = FindItemsPanel(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void ApplySongListItemSelectionChrome(
            ListView list,
            ListViewItem container,
            PlaylistItem song,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool multiOnThisList = _isMultiSelectMode && ReferenceEquals(_multiSelectTargetList, list);
            Brush unselectedBg = multiOnThisList
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            // 容器本身保持透明，避免 Presenter 方角选中层
            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisList
                ? IsItemSelected(list, song, selectedSet)
                : ReferenceEquals(list.SelectedItem, song);

            Border? chrome = FindTaggedBorder(container, "SongRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 40;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                // 让行内容横向铺满列表宽度（选中矩形也因此铺满整行、字段对齐表头）。
                // 用显式宽度而非仅靠 HorizontalContentAlignment，确保在 ScrollViewer 布局下也生效。
                if (list.ActualWidth > 0)
                {
                    chrome.Width = list.ActualWidth;
                }
                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                // 兜底：无模板 Border 时仍尽量圆角
                container.Background = accent;
                container.Foreground = selectedFg;
                ApplyForegroundToDescendants(container, selectedFg);
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
                ClearForegroundOnDescendants(container);
            }
        }

        private void ApplyAlbumGridItemSelectionChrome(
            GridView grid,
            GridViewItem container,
            AlbumEntry album,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool multiOnThisGrid = _isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, grid);
            Brush unselectedBg = multiOnThisGrid
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisGrid
                ? IsItemSelected(grid, album, selectedSet)
                : ReferenceEquals(grid.SelectedItem, album);

            Border? chrome = FindTaggedBorder(container, "AlbumRowChrome");
            if (chrome != null)
            {
                chrome.CornerRadius = new CornerRadius(8);
                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                container.Background = accent;
                container.Foreground = selectedFg;
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
            }
        }

        private void ApplyPlaylistItemSelectionChrome(ListViewItem container, PlaylistItem song)
            => ApplySongListItemSelectionChrome(PlaylistView, container, song);

        private static Border? FindTaggedBorder(DependencyObject root, string tag)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border
                    && border.Tag is string t
                    && string.Equals(t, tag, StringComparison.Ordinal))
                {
                    return border;
                }

                Border? nested = FindTaggedBorder(child, tag);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// <summary>关闭勾选标记，并尽量给 Presenter 圆角，避免系统方角选中层。</summary>
        private static void SoftenItemPresenterCorners(DependencyObject root)
        {
            if (root is ListViewItemPresenter listPresenter)
            {
                listPresenter.CornerRadius = new CornerRadius(8);
                listPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    listPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch
                {
                }

                return;
            }

            if (root is GridViewItemPresenter gridPresenter)
            {
                gridPresenter.CornerRadius = new CornerRadius(8);
                gridPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    gridPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch
                {
                }

                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                SoftenItemPresenterCorners(VisualTreeHelper.GetChild(root, i));
            }
        }

        /// <summary>在容器已加载后关闭系统选中勾（避免与词条文字重叠）。</summary>
        private void DisableContainerSelectionCheckMark(Control container)
        {
            SoftenItemPresenterCorners(container);
            if (!container.IsLoaded)
            {
                void OnLoaded(object sender, RoutedEventArgs e)
                {
                    container.Loaded -= OnLoaded;
                    SoftenItemPresenterCorners(container);
                }

                container.Loaded += OnLoaded;
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => SoftenItemPresenterCorners(container));
            }
        }

        private static void ApplyForegroundToDescendants(DependencyObject root, Brush foreground)
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

                ApplyForegroundToDescendants(child, foreground);
            }
        }

        private static void ClearForegroundOnDescendants(DependencyObject root)
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

                ClearForegroundOnDescendants(child);
            }
        }

                private Brush ResolveAccentBrush()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.AccentSource == "Custom")
            {
                return new SolidColorBrush(ParseHexColor(settings.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212));
            }

            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj)
                && colorObj is Color color)
            {
                return new SolidColorBrush(color);
            }

            return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        }

        private Brush ResolveAccentForegroundBrush()
            => ResolveContrastingForeground(ResolveAccentBrush());

        /// <summary>解析 "#RRGGBB" 十六进制颜色。</summary>
        private static Color? ParseHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int value))
            {
                return null;
            }

            return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        /// <summary>主题色偏深用白字，偏浅用黑字。</summary>
        private static Brush ResolveContrastingForeground(Brush background)
        {
            Color color = Colors.DodgerBlue;
            if (background is SolidColorBrush solid)
            {
                color = solid.Color;
            }
            else if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj)
                && colorObj is Color accent)
            {
                color = accent;
            }

            double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
            return new SolidColorBrush(luminance < 140 ? Colors.White : Colors.Black);
        }

        private static ListViewItem? FindAncestorListViewItem(DependencyObject start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is ListViewItem item)
                {
                    return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        // =====================================================================
        // 底部控制按钮
        // =====================================================================

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEngine?.IsPlaying == true)
            {
                _audioEngine.Pause();
                _isEnginePaused = true;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Paused);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE768";
                }

                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            if (_isEnginePaused && _audioEngine != null)
            {
                _audioEngine.Resume();
                // 暂停-恢复本质是一次 seek 重建会话，无缝源 _next 已被清空；
                // 重新预加载下一首，避免恢复后播到尾因无续接而误切歌。
                if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                {
                    _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                }

                _isEnginePaused = false;
                UpdateWaveformTimerForPlaybackState(true);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Playing);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE769";
                }

                // 独占下音量完全由 Windows 托盘/DAC 物理键控制，程序不写设备主音量(避免多次 select/暂停音量跳变到 0/100)；
                // 仅共享模式在恢复时做软件增益淡入。
                if (!IsHiFiModeSelected())
                {
                    double resumeTarget = VolumeSlider.Value / 100.0;
                    _ = FadeInEngineAfterResumeAsync(resumeTarget);
                }

                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            if (player.Source == null)
            {
                if (_userPlaylist.Count > 0)
                {
                    PlayUserPlaylistAt(0);
                }
                else if (_playlist.Count > 0)
                {
                    PlayAtIndex(0);
                }

                return;
            }

            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                player.Pause();
            }
            else
            {
                player.Play();
            }
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
            => PlayPrevious();

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => PlayNext();

        // =====================================================================
        // 进度条 / 音量
        // =====================================================================

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isUserSeeking = true;
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            SeekToSliderValue();
            _isUserSeeking = false;
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            SeekToSliderValue();
            _isUserSeeking = false;
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 波形进度条必须随播放位置实时重绘,不能被 UI 更新标志拦截(否则播放中波形停住/错误)
            if (_progressBarStyle == "Waveform")
            {
                RedrawProgressStyle();
            }

            if (_isUpdatingProgressUi)
            {
                return;
            }

            if (_isUserSeeking)
            {
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            }
        }

        private void SeekToSliderValue()
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                try
                {
                    _audioEngine.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
                    // seek 会丢弃无缝源里已预加载的下一首（位置已变，续接会错位）。
                    // 重挂下一首，避免 seek 后播到尾时因 _next 为空而无法无缝续接。
                    if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                    {
                        _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                    }
                }
                catch
                {
                }

                // 用户点击进度条定位后保持暂停，方便精确定位/等待（与"点进度条→暂停"一致）。
                if (_audioEngine.IsPlaying)
                {
                    _audioEngine.Pause();
                    _isEnginePaused = true;
                    UpdateWaveformTimerForPlaybackState(false);
                    UpdateEngineSmtcStatus(MediaPlaybackStatus.Paused);
                    if (PlayPauseIcon != null)
                    {
                        PlayPauseIcon.Glyph = "\uE768";
                    }

                    _miniPlayerWindow?.RefreshFromOwner();
                }

                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            }
            catch
            {
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateVolumeIcon(e.NewValue);

            // 共享模式：MediaPlayer 数字音量（跟随用户）。
            MediaPlayer? player = GetPlayer();
            if (player != null)
            {
                player.Volume = e.NewValue / 100.0;
            }

            // 设备/引擎音量：独占下软件音量条固定 100%，且程序不设设备主音量（bit-perfect 直通，实际音量由系统托盘控制）；
            // 共享沿用原机制（数字增益随滑块）。
            if (!IsHiFiModeSelected())
            {
                _audioEngine?.SetVolume(e.NewValue / 100.0);
            }

            if (!_applyingSettingsVolume)
            {
                ScheduleVolumeSave(e.NewValue);
            }

            DrawVolumeStyle();
        }

        /// <summary>音量写盘去抖:停止拖动 300ms 后才保存一次,避免每 tick 全量写盘。</summary>
        private void ScheduleVolumeSave(double value)
        {
            _volumeToSave = value;
            _volumeSaveTimer ??= DispatcherQueue.CreateTimer();
            _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _volumeSaveTimer.IsRepeating = false;
            _volumeSaveTimer.Tick -= OnVolumeSaveTick;
            _volumeSaveTimer.Tick += OnVolumeSaveTick;
            _volumeSaveTimer.Start();
        }

        private void OnVolumeSaveTick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
            }
            catch
            {
            }
        }

        private void UpdateVolumeIcon(double volumePercent)
        {
            if (VolumeIcon == null)
            {
                return;
            }

            // E74F mute / E992 low / E993 mid / E767 high
            if (volumePercent <= 0.5)
            {
                VolumeIcon.Glyph = "\uE74F";
            }
            else if (volumePercent < 34)
            {
                VolumeIcon.Glyph = "\uE992";
            }
            else if (volumePercent < 67)
            {
                VolumeIcon.Glyph = "\uE993";
            }
            else
            {
                VolumeIcon.Glyph = "\uE767";
            }
        }

        private void DesktopLyricsButton_Click(object sender, RoutedEventArgs e)
            => SetDesktopLyricsEnabled(!_desktopLyricsEnabled);

        private void MiniPlayerButton_Click(object sender, RoutedEventArgs e)
            => SetMiniPlayerEnabled(!_miniPlayerEnabled);

        private void SetMiniPlayerEnabled(bool enabled, bool persistPreference = true)
        {
            _miniPlayerEnabled = enabled;
            if (_miniPlayerEnabled)
            {
                EnsureMiniPlayerWindow();
                AppSettingsState settings = AppSettingsStore.Load();
                _miniPlayerWindow!.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
                _miniPlayerWindow.ApplyBackdropPreference(settings.EnableFrostedGlass);
                _miniPlayerWindow.RefreshFromOwner();
                _miniPlayerWindow.Activate();
            }
            else if (_miniPlayerWindow != null)
            {
                MiniPlayerWindow closing = _miniPlayerWindow;
                _miniPlayerWindow = null;
                closing.ClosedByUser -= OnMiniPlayerClosedByUser;
                closing.Close();
            }

            if (persistPreference)
            {
                AppSettingsStore.Update(s => s.OpenMiniPlayerOnStartup = _miniPlayerEnabled);
            }

            UpdateMiniPlayerBadge();
        }

        private void EnsureMiniPlayerWindow()
        {
            if (_miniPlayerWindow != null)
            {
                return;
            }

            _miniPlayerWindow = new MiniPlayerWindow(this);
            AppSettingsState settings = AppSettingsStore.Load();
            _miniPlayerWindow.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
            _miniPlayerWindow.ApplyBackdropPreference(settings.EnableFrostedGlass);
            _miniPlayerWindow.ClosedByUser += OnMiniPlayerClosedByUser;
        }

        private void OnMiniPlayerClosedByUser()
        {
            _miniPlayerWindow = null;
            _miniPlayerEnabled = false;
            AppSettingsStore.Update(s => s.OpenMiniPlayerOnStartup = false);
            DispatcherQueue.TryEnqueue(UpdateMiniPlayerBadge);
        }

        private void UpdateMiniPlayerBadge()
        {
            if (MiniPlayerStateBadge == null)
            {
                return;
            }

            bool on = _miniPlayerEnabled && _miniPlayerWindow != null;
            MiniPlayerStateBadge.Text = on ? "on" : "off";
            MiniPlayerStateBadge.Foreground = on
                ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
                : new SolidColorBrush(Color.FromArgb(255, 176, 176, 176));
        }

        // ---- Mini player / desktop lyrics helpers ----

        internal PlaylistItem? GetCurrentPlayingItem()
        {
            if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
            {
                return _userPlaylist[_userPlaylistIndex];
            }

            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                return _playlist[_currentIndex];
            }

            return null;
        }

        internal ImageSource? GetCurrentCoverImage() => TransportCoverImage?.Source;

        internal MediaPlayer? GetMediaPlayerPublic() => GetPlayer();

        internal string GetPlaybackOrderGlyphPublic()
            => _playbackOrder switch
            {
                PlaybackOrder.Sequential => "\uE8FD",
                PlaybackOrder.Random => "\uE8B1",
                PlaybackOrder.ListLoop => "\uE8EE",
                PlaybackOrder.TrackLoop => "\uE8ED",
                PlaybackOrder.TrackOnce => "\uE72A",
                _ => "\uE8EE"
            };

        internal string GetCurrentLyricTextPublic()
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricLines.Count)
            {
                return _lyricLines[_currentLyricIndex].Text;
            }

            return string.Empty;
        }

        internal void CyclePlaybackOrderPublic() => PlaybackOrderButton_Click(PlaybackOrderButton!, new RoutedEventArgs());

        internal void PreviousPublic() => PlayPrevious();

        internal void NextPublic() => PlayNext();

        internal void TogglePlayPausePublic() => PlayPauseButton_Click(PlayPauseButton!, new RoutedEventArgs());

        internal void SeekPublic(TimeSpan position)
        {
            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                player.PlaybackSession.Position = position;
            }
            catch
            {
            }
        }

        private void DesktopLyricsButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_desktopLyricsWindow?.IsLocked == true)
            {
                _desktopLyricsWindow.SetLocked(false);
                e.Handled = true;
            }
        }

        private void SetDesktopLyricsEnabled(bool enabled, bool persistPreference = true)
        {
            _desktopLyricsEnabled = enabled;
            if (_desktopLyricsEnabled)
            {
                EnsureDesktopLyricsWindow();
                _desktopLyricsWindow!.SetLyrics(_lyricLines);
                MediaPlayer? player = GetPlayer();
                if (player?.Source != null)
                {
                    _desktopLyricsWindow.Sync(player.PlaybackSession.Position);
                }

                _desktopLyricsWindow.Show();
            }
            else
            {
                if (_desktopLyricsWindow != null)
                {
                    DesktopLyricsOverlay closing = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    closing.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    closing.Close();
                    closing.Dispose();
                }
            }

            if (persistPreference)
            {
                AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = _desktopLyricsEnabled);
            }

            UpdateDesktopLyricsBadge();
        }

        private void EnsureDesktopLyricsWindow()
        {
            if (_desktopLyricsWindow != null)
            {
                return;
            }

            _desktopLyricsWindow = new DesktopLyricsOverlay
            {
                PositionProvider = () => GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero
            };
            _desktopLyricsWindow.ClosedByUser += OnDesktopLyricsClosedByUser;
            _desktopLyricsWindow.ApplySettings(AppSettingsStore.Load());
        }

        private void OnDesktopLyricsClosedByUser()
        {
            // 可能从桌面歌词关闭按钮触发；确保主界面 badge 回到 off
            if (_desktopLyricsWindow != null)
            {
                _desktopLyricsWindow.ClosedByUser -= OnDesktopLyricsClosedByUser;
                _desktopLyricsWindow.PositionProvider = null;
                _desktopLyricsWindow.Dispose();
            }

            _desktopLyricsWindow = null;
            _desktopLyricsEnabled = false;
            AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = false);
            DispatcherQueue.TryEnqueue(UpdateDesktopLyricsBadge);
        }

        private void UpdateDesktopLyricsBadge()
        {
            if (DesktopLyricsStateBadge == null)
            {
                return;
            }

            bool on = _desktopLyricsEnabled && _desktopLyricsWindow != null;
            DesktopLyricsStateBadge.Text = on ? "on" : "off";
            DesktopLyricsStateBadge.Foreground = on
                ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
                : new SolidColorBrush(Color.FromArgb(255, 176, 176, 176));
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _taskbarProgress?.Dispose();
            _taskbarProgress = null;
            try
            {
                TrackStatsStore.Flush();
            }
            catch
            {
            }

            try
            {
                _volumeSaveTimer?.Stop();
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
            }
            catch
            {
            }

            try
            {
                _audioEngine?.Dispose();
                _audioEngine = null;
            }
            catch
            {
            }

            PersistPlaybackSession();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch
            {
            }

            CloseAllChildWindows();
            DisposeMusicPlayer2Features();
        }

        /// <summary>保存全部状态后重启播放器(用于主题色等需重启生效的更改)。</summary>
        internal void RestartApp()
        {
            try
            {
                TrackStatsStore.Flush();
                _volumeSaveTimer?.Stop();
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
                PersistPlaybackSession();

                string? exe = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(System.IO.Path.GetDirectoryName(exe))
                            ? string.Empty
                            : System.IO.Path.GetDirectoryName(exe)!
                    });
                }
            }
            catch
            {
            }

            // 直接退出当前进程(由新进程接管,绕过托盘/关闭提示逻辑)
            Environment.Exit(0);
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (_closePromptOpen)
            {
                return;
            }

            _ = HandleCloseRequestAsync();
        }

        private async Task HandleCloseRequestAsync()
        {
            if (_closePromptOpen)
            {
                return;
            }

            _closePromptOpen = true;
            try
            {
                AppClosePreferencesState prefs = AppClosePreferences.Load();
                CloseWindowAction action = AppClosePreferences.ResolveAction(prefs);
                if (action == CloseWindowAction.Ask)
                {
                    action = await ShowCloseChoiceDialogAsync();
                }

                switch (action)
                {
                    case CloseWindowAction.MinimizeToTray:
                        MinimizeToTray();
                        break;
                    case CloseWindowAction.Exit:
                        ExitApplication();
                        break;
                }
            }
            finally
            {
                _closePromptOpen = false;
            }
        }

        private async Task<CloseWindowAction> ShowCloseChoiceDialogAsync()
        {
            if (Content?.XamlRoot == null)
            {
                return CloseWindowAction.Exit;
            }

            var dontAsk = new CheckBox
            {
                Content = "下次不再询问",
                Margin = new Thickness(0, 8, 0, 0)
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "关闭主界面时，要将播放器缩小到系统托盘继续在后台运行，还是退出播放器？",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(dontAsk);

            var dialog = new ContentDialog
            {
                Title = "关闭 CelesteMusicPlayer",
                Content = panel,
                PrimaryButtonText = "缩小到托盘",
                SecondaryButtonText = "退出播放器",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            // 关闭对话框按钮用当前主题色(ContentDialog 局部资源)
            try
            {
                Windows.UI.Color closeAccent = ThemeColorService.CurrentAccent;
                var closeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(closeAccent);
                dialog.Resources["AccentButtonBackground"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPointerOver"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPressed"] = closeBrush;
            }
            catch
            {
            }

            ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return CloseWindowAction.Ask; // 取消：不做事
            }

            CloseWindowAction chosen = result == ContentDialogResult.Primary
                ? CloseWindowAction.MinimizeToTray
                : CloseWindowAction.Exit;

            if (dontAsk.IsChecked == true)
            {
                AppClosePreferences.Save(new AppClosePreferencesState
                {
                    DontAskAgain = true,
                    PreferredAction = chosen == CloseWindowAction.Exit
                        ? nameof(CloseWindowAction.Exit)
                        : nameof(CloseWindowAction.MinimizeToTray)
                });
            }

            return chosen;
        }

        private void MinimizeToTray()
        {
            try
            {
                _trayIcon ??= new AppTrayIcon(this);
                _trayIcon.Show();
                StartupLog.Write("托盘: MinimizeToTray 完成");
                AppWindow.Hide();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MinimizeToTray", ex);
                // 托盘失败则直接退出，避免关不掉
                ExitApplication();
            }
        }

        internal void RestoreFromTray()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    StartupLog.Write("托盘: RestoreFromTray 收到点击");
                    AppWindow.Show();
                    Activate();
                    _trayIcon?.Hide();
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("RestoreFromTray", ex);
                }
            });
        }

        internal void ExitFromTray()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StartupLog.Write("托盘: ExitFromTray 收到点击");
                ExitApplication();
            });
        }

        private void ExitApplication()
        {
            AppSettingsStore.MarkAppCleanExit();
            PersistPlaybackSession();
            _allowClose = true;
            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch
            {
            }

            Close();
            // 托盘/子窗口等引用可能阻止进程退出,必须显式结束进程
            Application.Current.Exit();
        }

        private void CloseAllChildWindows()
        {
            try
            {
                if (_currentPlaylistWindow != null)
                {
                    CurrentPlaylistWindow playlist = _currentPlaylistWindow;
                    _currentPlaylistWindow = null;
                    playlist.Close();
                }
            }
            catch
            {
            }

            try
            {
                if (_desktopLyricsWindow != null)
                {
                    DesktopLyricsOverlay lyrics = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    _desktopLyricsEnabled = false;
                    lyrics.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    lyrics.Close();
                    lyrics.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                SettingsWindow.CloseIfOpen();
            }
            catch
            {
            }

            try
            {
                if (_miniPlayerWindow != null)
                {
                    MiniPlayerWindow mini = _miniPlayerWindow;
                    _miniPlayerWindow = null;
                    _miniPlayerEnabled = false;
                    mini.ClosedByUser -= OnMiniPlayerClosedByUser;
                    mini.Close();
                }
            }
            catch
            {
            }

            try
            {
                if (_artistAvatarEditorWindow != null)
                {
                    ArtistAvatarEditorWindow editor = _artistAvatarEditorWindow;
                    _artistAvatarEditorWindow = null;
                    editor.Close();
                }
            }
            catch
            {
            }
        }

        // =====================================================================
        // MediaPlayer 事件
        // =====================================================================

        private void Player_MediaOpened(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TimeSpan duration = sender.PlaybackSession.NaturalDuration;

                if ((duration.TotalSeconds <= 0 || double.IsNaN(duration.TotalSeconds))
                    && _currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    duration = _playlist[_currentIndex].Duration;
                }

                _isUpdatingProgressUi = true;
                try
                {
                    double totalSeconds = duration.TotalSeconds;
                    if (totalSeconds <= 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
                    {
                        ProgressSlider.Maximum = 100;
                        TotalTimeText.Text = "00:00";
                    }
                    else
                    {
                        ProgressSlider.Maximum = totalSeconds;
                        TotalTimeText.Text = FormatTime(duration);
                    }

                    double start = 0;
                    if (_pendingRestorePositionSeconds is double pending && pending > 0.5)
                    {
                        start = Math.Min(pending, Math.Max(0, totalSeconds - 0.5));
                        _pendingRestorePositionSeconds = null;
                        try
                        {
                            sender.PlaybackSession.Position = TimeSpan.FromSeconds(start);
                        }
                        catch
                        {
                        }
                    }

                    ProgressSlider.Value = start;
                    CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(start));
                }
                finally
                {
                    _isUpdatingProgressUi = false;
                }
            });
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // E768=播放，E769=暂停
                bool playing = sender.PlaybackState == MediaPlaybackState.Playing;
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
                UpdateWaveformTimerForPlaybackState(playing);
                _desktopLyricsWindow?.SetPlaybackPaused(!playing && sender.PlaybackState != MediaPlaybackState.Opening);
            });
        }

        private void UpdateWaveformTimerForPlaybackState(bool playing)
        {
            if (_waveformTimer == null)
            {
                return;
            }

            if (playing)
            {
                _waveformIdleSettleTicks = 0;
                if (!_waveformTimer.IsRunning)
                {
                    _waveformTimer.Start();
                }
            }
            else if (!_waveformTimer.IsRunning)
            {
                DrawWaveformBars();
            }
            // 暂停/停止：若定时器仍在跑，由 Tick 做回落动画后自行 Stop
        }

        private void Player_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(HandleMediaEnded);
        }

        private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                NowPlayingText.Text = "播放失败";
                if (AppSettingsStore.Load().StopWhenError)
                {
                    try
                    {
                        sender.Pause();
                    }
                    catch
                    {
                    }
                }

                await ShowErrorAsync("无法播放", args.ErrorMessage);
            });
        }

        private void UpdateTaskbarProgress(TimeSpan position)
        {
            if (!AppSettingsStore.Load().ShowTaskbarProgress)
            {
                _taskbarProgress?.Clear();
                return;
            }

            if (_mainWindowHwnd == IntPtr.Zero)
            {
                return;
            }

            _taskbarProgress ??= new TaskbarProgressHelper(_mainWindowHwnd);
            MediaPlayer? player = GetPlayer();
            bool paused = player?.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
            _taskbarProgress.SetProgress(position.TotalSeconds, ProgressSlider.Maximum, paused);
        }

        private void PositionTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_usingEnginePlayback)
            {
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null || _isUserSeeking)
            {
                return;
            }

            TimeSpan position = player.PlaybackSession.Position;
            UpdateTaskbarProgress(position);

            _isUpdatingProgressUi = true;
            try
            {
                double seconds = position.TotalSeconds;
                if (seconds <= ProgressSlider.Maximum
                    && Math.Abs(ProgressSlider.Value - seconds) >= 0.05)
                {
                    ProgressSlider.Value = seconds;
                }

                string timeText = FormatTime(position);
                if (!string.Equals(CurrentTimeText.Text, timeText, StringComparison.Ordinal))
                {
                    CurrentTimeText.Text = timeText;
                }
            }
            finally
            {
                _isUpdatingProgressUi = false;
            }

            SyncLyricsToPosition(position);
            _desktopLyricsWindow?.Sync(position);
            _miniPlayerWindow?.SyncPosition(position, player.PlaybackSession.NaturalDuration);
            TickFeaturePlaybackExtras(position);

            if ((DateTime.UtcNow - _lastPlaybackPersistUtc).TotalSeconds >= 4)
            {
                _lastPlaybackPersistUtc = DateTime.UtcNow;
                PersistPlaybackSession();
            }
        }

        // =====================================================================
        // 右侧：正在播放信息 / 波形 / 歌词
        // =====================================================================

                private void ProgressStyleCanvas_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            RedrawProgressStyle();
        }

        /// <summary>按设置的样式重绘进度条(4 种可切换)。</summary>
        private void RedrawProgressStyle()
        {
            if (ProgressStyleCanvas == null || ProgressSlider == null)
            {
                return;
            }

            bool waveform = _progressBarStyle == "Waveform";
            if (!waveform)
            {
                // 默认样式:恢复系统进度条(主题色跟随主题设置)
                ProgressSlider.Opacity = 1;
                ProgressStyleCanvas.Visibility = Visibility.Collapsed;
                ProgressStyleCanvas.Children.Clear();
                return;
            }

            // 波形进度条:底层 Slider 透明(交互保留),自绘画布显示波形
            ProgressSlider.Opacity = 0;
            ProgressStyleCanvas.Visibility = Visibility.Visible;

            double max = ProgressSlider.Maximum;
            double ratio = max > 0 ? ProgressSlider.Value / max : 0;
            ratio = Math.Clamp(ratio, 0, 1);

            var canvas = ProgressStyleCanvas;
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            Color accent = ResolveAccentColor();
            DrawWaveformStyle(canvas, w, h, ratio, accent);
        }

        private Color ResolveAccentColor()
        {
            if (ResolveAccentBrush() is SolidColorBrush scb && scb.Color.A > 0)
            {
                return scb.Color;
            }

            return Color.FromArgb(255, 0, 120, 212);
        }

        private static Color Lighten(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

        private static Color Darken(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

        /// <summary>渐变光晕：渐变填充 + 圆角轨道 + 光晕滑块。</summary>
        private void DrawGradientStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            Color light = Lighten(accent, 0.55);
            Color dark = Darken(accent, 0.35);

            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 4) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle { Width = Math.Max(2, w * ratio), Height = 4, RadiusX = 2, RadiusY = 2 };
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                grad.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = light, Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 8, Math.Max(8, w - 8));
                var glow = new Shapes.Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(glow, cx - 8);
                Canvas.SetTop(glow, (h - 16) / 2);
                canvas.Children.Add(glow);

                var dot = new Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Stroke = new SolidColorBrush(dark),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 5);
                Canvas.SetTop(dot, (h - 10) / 2);
                canvas.Children.Add(dot);
            }
        }

        /// <summary>波形(Poweramp)：波形条已播主题色/未播灰色，当前位置竖线。</summary>
        private void DrawWaveformStyle(Canvas canvas, double w, double h, double ratio, Color accent)
        {
            if (_waveformData == null || _waveformData.Length == 0)
            {
                // 波形未就绪:只画一条中性细线(加载完成后再显示真实波形,不闪占位)
                var idleLine = new Shapes.Rectangle
                {
                    Width = w,
                    Height = 2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128))
                };
                Canvas.SetTop(idleLine, (h - 2) / 2);
                canvas.Children.Add(idleLine);
                return;
            }

            int n = _waveformData.Length;
            double barW = w / n;
            var unplayedBrush = new SolidColorBrush(Color.FromArgb(70, 150, 150, 150));
            double playedEdge = w * ratio;
            Color light = Lighten(accent, 0.55);

            for (int i = 0; i < n; i++)
            {
                double bh = Math.Max(2, _waveformData[i] * h * 0.95);
                var rect = new Shapes.Rectangle
                {
                    Width = Math.Max(1, barW - 1),
                    Height = bh,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(rect, i * barW);
                Canvas.SetTop(rect, (h - bh) / 2);

                double centerX = (i + 0.5) * barW;
                if (centerX <= playedEdge)
                {
                    // 已播部分:主题色(两端浅色渐变)
                    double t = centerX / Math.Max(1, playedEdge);
                    rect.Fill = new SolidColorBrush(Color.FromArgb(
                        255,
                        (byte)(accent.R + (light.R - accent.R) * t),
                        (byte)(accent.G + (light.G - accent.G) * t),
                        (byte)(accent.B + (light.B - accent.B) * t)));
                }
                else
                {
                    rect.Fill = unplayedBrush;
                }

                canvas.Children.Add(rect);
            }

            // 当前位置细线(echo next 风格:波形上一条细竖线)
            var line = new Shapes.Rectangle
            {
                Width = 2,
                Height = h * 0.96,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(Colors.White)
            };
            Canvas.SetLeft(line, Math.Clamp(playedEdge - 1, 0, Math.Max(0, w - 2)));
            Canvas.SetTop(line, (h - h * 0.96) / 2);
            canvas.Children.Add(line);
        }

        /// <summary>波形未就绪时的渐变兜底(仅波形模式内部使用)。</summary>
        private void DrawGradientFallback(Canvas canvas, double w, double h, double ratio, Color accent)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 4) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle { Width = Math.Max(2, w * ratio), Height = 4, RadiusX = 2, RadiusY = 2 };
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                grad.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = Lighten(accent, 0.55), Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }
        }

        /// <summary>Spotify 圆环：细轨道 + 白色圆环滑块。</summary>
        private void DrawSpotifyStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 3) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 3,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 3) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 6, Math.Max(6, w - 6));
                var ring = new Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(ring, cx - 6);
                Canvas.SetTop(ring, (h - 12) / 2);
                canvas.Children.Add(ring);
            }
        }

        /// <summary>Apple 细线：2px 细线 + 圆点滑块。</summary>
        private void DrawAppleLineStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 2) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 2) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 4, Math.Max(4, w - 4));
                var dot = new Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 4);
                Canvas.SetTop(dot, (h - 8) / 2);
                canvas.Children.Add(dot);
            }
        }

        /// <summary>播放列表内容变化时,若未播放则尝试预览波形(列表恢复完成后自动触发)。</summary>
        private void OnPlaylistForWaveformPreview(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            TryLoadWaveformPreview();
        }

        /// <summary>未播放时加载列表选中(或第一首)歌曲的波形预览。</summary>
        private void TryLoadWaveformPreview()
        {
            if (_progressBarStyle != "Waveform" || !string.IsNullOrEmpty(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = PlaylistView.SelectedItem as PlaylistItem;
            if (item == null && _playlist.Count > 0)
            {
                item = _playlist[0];
            }

            if (item != null
                && !string.Equals(_waveformPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadWaveformForCurrentAsync(item.FilePath);
            }
        }

        /// <summary>延迟重试:等媒体库异步恢复完成后再尝试加载预览波形。</summary>
        private async System.Threading.Tasks.Task RetryWaveformPreviewLaterAsync()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1200);
                TryLoadWaveformPreview();
            }
            catch
            {
            }
        }

        /// <summary>异步加载当前歌曲波形(供波形进度条)，完成后重绘。</summary>
        private async void LoadWaveformForCurrentAsync(string path)
        {
            try
            {

            _waveformPath = path;
            float[] wave = await WaveformDataProvider.GetWaveformAsync(path, 180);
            if (!string.Equals(_waveformPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return; // 已切歌
            }

            _waveformData = wave;
            StartupLog.Write("波形回调: " + (wave?.Length > 0 ? "有数据" : "空") + " style=" + _progressBarStyle);
            if (_progressBarStyle == "Waveform")
            {
                RedrawProgressStyle();
            }
        
            }
            catch
            {
            }}

                /// <summary>应用自定义背景图片(设置里选择);无路径时恢复封面背景。</summary>
        private void ApplyCustomBackground(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    return;
                }

                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 1920;
                using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                {
                    bmp.SetSource(fs.AsRandomAccessStream());
                }

                CustomBackgroundImage.Source = bmp;
                CustomBackgroundImage.Visibility = Visibility.Visible;
            }
            catch
            {
            }
        }

                /// <summary>应用播放列表列显隐与密度。</summary>
        private void ApplyPlaylistColumnSettings(AppSettingsState settings)
        {
            try
            {
                var cols = PlaylistColumnWidths.Instance;
                cols.Title = settings.ShowPlaylistTitle ? 140 : 0;
                cols.Artist = settings.ShowPlaylistArtist ? 110 : 0;
                cols.Album = settings.ShowPlaylistAlbum ? 110 : 0;
                cols.Year = settings.ShowPlaylistYear ? 52 : 0;
                cols.Duration = settings.ShowPlaylistDuration ? 60 : 0;
            }
            catch
            {
            }

            try
            {
                // 密度:切换 ListView 行高(资源键通过 RootShell.Resources 查找)
                Style? style = null;
                if (RootShell != null && RootShell.Resources != null)
                {
                    string key = settings.PlaylistDensity == "Compact" ? "CompactListItemStyle" : "ComfortableListItemStyle";
                    if (RootShell.Resources.TryGetValue(key, out object? res) && res is Style s)
                    {
                        style = s;
                    }
                }

                foreach (ListView list in new[] { PlaylistView, AlbumTrackListView, ArtistTrackListView, FolderBrowserView })
                {
                    list.ItemContainerStyle = style;
                }
            }
            catch
            {
            }
        }

        /// <summary>主题色变化事件处理:统一刷新信息卡波形/歌词/进度条/迷你播放器/桌面歌词。</summary>
        private void OnThemeColorChanged(Windows.UI.Color accent)
        {
            try
            {
                _waveAccentColor = accent;
                DrawWaveformBars();
            }
            catch
            {
            }

            // 音量条(自绘)/进度条
            try
            {
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent);
            }
            catch
            {
            }

            try
            {
                TimeSpan pos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(pos, force: true);
            }
            catch
            {
            }

            try
            {
                _miniPlayerWindow?.RefreshAccentFromOwner();
            }
            catch
            {
            }

            try
            {
                _desktopLyricsWindow?.ApplySettings(AppSettingsStore.Load());
            }
            catch
            {
            }
        }

        /// <summary>将当前播放队列保存为命名播放列表（Poweramp 式持久化，不影响当前队列）。</summary>
        internal void SaveCurrentQueueAsPlaylist(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                List<string> paths = _userPlaylist
                    .Select(p => p.FilePath)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                NamedPlaylistStore.SaveSongs(name.Trim(), paths);
                StartupLog.Write("队列已保存为播放列表: " + name.Trim() + " (" + paths.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SaveCurrentQueueAsPlaylist", ex);
            }
        }

        /// <summary>把命名单歌曲追加到当前播放队列（不播放、不替换）。</summary>
        internal void AddNamedPlaylistToQueue(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try { items.Add(CreatePlaylistItemFromPath(path)); } catch { }
                }
                if (items.Count == 0) return;
                AddSongsToUserPlaylist(items);
                StartupLog.Write("已把播放列表加入队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("AddNamedPlaylistToQueue", ex);
            }
        }

        /// <summary>把命名播放列表载入当前队列并开始播放（队列替换为列表内容）。</summary>
        internal async System.Threading.Tasks.Task LoadNamedPlaylistToQueueAndPlayAsync(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                if (paths.Count == 0)
                {
                    return;
                }

                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        items.Add(await System.Threading.Tasks.Task.Run(() => CreatePlaylistItemFromPath(path)));
                    }
                    catch
                    {
                    }
                }

                if (items.Count == 0)
                {
                    return;
                }

                _userPlaylist.Clear();
                AddSongsToUserPlaylist(items);
                PlayUserPlaylistAt(0);
                StartupLog.Write("已载入播放列表到队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LoadNamedPlaylistToQueueAndPlay", ex);
            }
        }

        /// <summary>已保存的命名播放列表名（供 UI 列示，含“我喜欢的音乐”）。</summary>
        internal IReadOnlyList<string> ListNamedPlaylists() => NamedPlaylistStore.List();

        /// <summary>当前播放列表拖拽重排后的回调(UserPlaylist 与主窗口共享,集合顺序已自动更新)。</summary>
        internal void RefreshFromPlaylistReorder()
        {
            // 用户播放列表顺序变化由共享 ObservableCollection 自动反映到主窗口;
            // 这里只需按当前播放曲目重新定位 _userPlaylistIndex,保证下一首播放方向正确。
            if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                for (int i = 0; i < _userPlaylist.Count; i++)
                {
                    if (string.Equals(_userPlaylist[i].FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _userPlaylistIndex = i;
                        return;
                    }
                }
            }

            if (_userPlaylistIndex >= _userPlaylist.Count)
            {
                _userPlaylistIndex = _userPlaylist.Count - 1;
            }
        }

                /// <summary>自绘音量条:恒定波形竖线样式(已填充主题色/未填充灰色),无重影。</summary>
        private void DrawVolumeStyle()
        {
            if (VolumeStyleCanvas == null || VolumeSlider == null)
            {
                return;
            }

            var canvas = VolumeStyleCanvas;
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 1 || h <= 1)
            {
                return;
            }

            Color accent = ThemeColorService.CurrentAccent;
            double ratio = VolumeSlider.Maximum > 0 ? VolumeSlider.Value / VolumeSlider.Maximum : 0;
            ratio = Math.Clamp(ratio, 0, 1);

            const int n = 28;
            double barW = w / n;
            var filledBrush = new SolidColorBrush(accent);
            var emptyBrush = new SolidColorBrush(Color.FromArgb(90, 150, 150, 150));
            double filledEdge = w * ratio;

            for (int i = 0; i < n; i++)
            {
                // 恒定高度竖线(无起伏)
                double bh = Math.Max(3, h * 0.85);
                var rect = new Shapes.Rectangle
                {
                    Width = Math.Max(1, barW - 1),
                    Height = bh,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = (i + 0.5) * barW <= filledEdge ? filledBrush : emptyBrush
                };
                Canvas.SetLeft(rect, i * barW);
                Canvas.SetTop(rect, (h - bh) / 2);
                canvas.Children.Add(rect);
            }
        }

        /// <summary>音量条点击/拖动定位。</summary>
        private void VolumeStyleCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            SetVolumeFromPointer(e);
            try
            {
                VolumeStyleCanvas.CapturePointer(e.Pointer);
            }
            catch
            {
            }
        }

        private void VolumeStyleCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (VolumeStyleCanvas.PointerCaptures != null && VolumeStyleCanvas.PointerCaptures.Count > 0)
            {
                SetVolumeFromPointer(e);
            }
        }

        private void VolumeStyleCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                VolumeStyleCanvas.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }
        }

        private void SetVolumeFromPointer(PointerRoutedEventArgs e)
        {
            try
            {
                // HiFi 独占：音量条固定 100% 不动，调音量请用系统托盘（DAC 设备主音量，bit-perfect 保真）。
                if (IsHiFiModeSelected())
                {
                    VolumeSlider.Value = VolumeSlider.Maximum;
                    NowPlayingText.Text = "请在系统托盘音量条内修改音量";
                    return;
                }

                double px = e.GetCurrentPoint(VolumeStyleCanvas).Position.X;
                double ratio = Math.Clamp(px / Math.Max(1, VolumeStyleCanvas.ActualWidth), 0, 1);
                VolumeSlider.Value = ratio * VolumeSlider.Maximum; // 触发 ValueChanged -> 音量
            }
            catch
            {
            }
        }

        private void VolumeStyleCanvas_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            DrawVolumeStyle();
        }

        private void ClearNowPlayingPanel()
        {
            // 保留旧波形:停止/切歌时进度条静态显示上次波形,不闪占位
            _waveformPath = null;
            RedrawProgressStyle();
            _nowPlayingPath = null;
            NowPlayingTitleText.Text = "未在播放";
            ResetNowPlayingArtistAlbumLinks();
            NowPlayingCoverImage.Source = null;
            ApplyNowPlayingPaneTransparent();
            UpdateTransportNowPlaying(null, null);
            ClearLyricsUi("开始播放后显示歌词");
            // 未播放时也填充静态频谱，保证信息卡波形始终可见
            for (int i = 0; i < WaveBarCount; i++)
            {
                _waveLevels[i] = IdleLevel(i);
            }

            DrawWaveformBars();
            ClearAlbumArtBackground();
        }

        private void UpdateTransportNowPlaying(PlaylistItem? item, ImageSource? cover)
        {
            if (item == null)
            {
                TransportTitleText.Text = "目前未播放音乐";
                TransportArtistText.Text = string.Empty;
                TransportArtistText.Visibility = Visibility.Collapsed;
                TransportFormatText.Text = string.Empty;
                TransportFormatText.Visibility = Visibility.Collapsed;
                TransportCoverImage.Source = null;
                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            TransportTitleText.Text = item.Title;
            TransportArtistText.Text = item.Artist;
            TransportArtistText.Visibility = Visibility.Visible;
            TransportFormatText.Text = item.FormatInfoLine;
            TransportFormatText.Visibility =
                string.IsNullOrWhiteSpace(item.FormatInfoLine)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            TransportCoverImage.Source = cover;
            _miniPlayerWindow?.RefreshFromOwner();
        }

        /// <summary>独占/HiFi 输出时显示实际输出格式（WASAPI 设备端采样率/位深）；无则清空该行。</summary>
        private void UpdateNowPlayingOutputFormat()
        {
            try
            {
                string? outFmt = _audioEngine?.ActualOutputFormat;
                if (NowPlayingAudioInfoText != null)
                {
                    NowPlayingAudioInfoText.Text = string.IsNullOrEmpty(outFmt) ? string.Empty : "实际输出：" + outFmt;
                }
            }
            catch
            {
            }
        }

        /// <summary>信号链调试面板：实时显示 源格式→输出格式→是否独占→是否经过 DSP（对标 foobar 排障"假 bit-perfect"）。</summary>
        internal void UpdateSignalChainDisplay()
        {
            if (SignalChainInfoText == null)
            {
                return;
            }

            try
            {
                bool hifi = _audioEngine?.IsHiFiMode == true || IsHiFiModeSelected();
                string? srcFmt = _audioEngine?.SourceFormatDescription;
                string? outFmt = _audioEngine?.ActualOutputFormat;

                // 源格式：HiFi 直通取 WAV 源；否则为系统 MediaPlayer 解码路径
                string src = string.IsNullOrWhiteSpace(srcFmt)
                    ? (hifi ? "（未知/解析中）" : "MediaPlayer（系统解码）")
                    : srcFmt;

                // 输出格式 / 设备
                string outp = string.IsNullOrWhiteSpace(outFmt)
                    ? (hifi ? "（未知/解析中）" : "系统混音器（Shared）")
                    : outFmt + (hifi ? "" : "（Shared）");

                string exclusivo = hifi ? "独占" : "共享";

                // DSP 摘要：EQ 仅在 AudioGraph（非 HiFi 独占）下有效；不显示音量（用户不关心它在此链路里）。
                string dsp;
                if (hifi)
                {
                    dsp = "无（bit-perfect 直通）";
                }
                else
                {
                    double[] eq = EqualizerStore.Load().BandGains;
                    bool eqFlat = eq == null || Array.TrueForAll(eq, g => Math.Abs(g) < 0.5);
                    dsp = eqFlat ? "EQ=off" : "EQ=on";
                }

                SignalChainInfoText.Text =
                    "信号链：源[" + src + "] → 输出[" + outp + "] | " +
                    "模式=" + exclusivo + (hifi ? "" : "（系统混音）") +
                    " | DSP: " + dsp;
            }
            catch
            {
                SignalChainInfoText.Text = "信号链：—";
            }
        }

        /// <summary>读取并显示当前曲目的音频格式信息（采样率/位深/码率/声道）。</summary>
        private async System.Threading.Tasks.Task UpdateAudioInfoTextAsync(string path)
        {
            try
            {
                string? info = await System.Threading.Tasks.Task.Run(() => AudioInfoFormatter.Format(path));
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (NowPlayingAudioInfoText != null && string.Equals(_nowPlayingPath, path, System.StringComparison.OrdinalIgnoreCase))
                    {
                        NowPlayingAudioInfoText.Text = info ?? string.Empty;
                    }
                });
            }
            catch
            {
            }
        }

        private async Task UpdateNowPlayingPanelAsync(PlaylistItem item)
        {
            _nowPlayingPath = item.FilePath;
            NowPlayingTitleText.Text = item.Title;
            UpdateNowPlayingArtistAlbumText(item);
            UpdateTransportNowPlaying(item, null);
            _ = UpdateAudioInfoTextAsync(item.FilePath);

            byte[]? coverBytes = await Task.Run(() => ExtractCoverBytes(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            BitmapImage? coverImage = null;
            if (coverBytes != null && coverBytes.Length > 0)
            {
                coverImage = await CreateBitmapFromBytesAsync(coverBytes);
            }

            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            NowPlayingCoverImage.Source = coverImage;
            UpdateTransportNowPlaying(item, coverImage);
            _ = ApplyAlbumArtBackgroundAsync(coverBytes, item.FilePath);
            ApplyNowPlayingPaneTransparent();

            List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            BuildLyricsUi(lyrics);
            _ = MaybeAutoDownloadExtrasAsync(item, lyrics, coverBytes);
        }

        /// <summary>悬停超链接：显示下划线（无主题色，正常文字色 + 悬停下划线标识可点击）。</summary>
        private void NowPlayingArtistLink_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingArtistText != null) NowPlayingArtistText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }

        private void NowPlayingArtistLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingArtistText != null) NowPlayingArtistText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        }

        private void NowPlayingAlbumLink_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingAlbumText != null) NowPlayingAlbumText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }

        private void NowPlayingAlbumLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingAlbumText != null) NowPlayingAlbumText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        }

        /// <summary>重置艺术家/专辑超链接为空占位（未播放时禁用）。</summary>
        private void ResetNowPlayingArtistAlbumLinks()
        {
            try
            {
                if (NowPlayingArtistText != null) NowPlayingArtistText.Text = "未知艺术家";
                if (NowPlayingArtistLinkButton != null) NowPlayingArtistLinkButton.IsEnabled = false;
                if (NowPlayingAlbumText != null) NowPlayingAlbumText.Text = "未知专辑";
                if (NowPlayingAlbumLinkButton != null) NowPlayingAlbumLinkButton.IsEnabled = false;
                if (NowPlayingArtistAlbumSeparator != null) NowPlayingArtistAlbumSeparator.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
        }

        /// <summary>点击艺术家超链接：收起播放面板并跳转到对应艺术家详情页。</summary>
        private void NowPlayingArtistLinkButton_Click(object sender, RoutedEventArgs e)
        {
            string artistName = NowPlayingArtistText?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return;
            }

            ArtistEntry? entry = _artists.FirstOrDefault(
                a => string.Equals(a.Name, artistName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new ArtistEntry { Name = artistName };
            }

            SetNowPlayingPaneVisible(false);
            // 从播放面板跳转：先切到「艺术家」分类并真正显示其视图根，再打开详情，保证左侧分类与右侧面板同步
            _currentCategory = "Artists";
            ApplyCategoryView();
            DispatcherQueue.TryEnqueue(() => OpenArtistDetail(entry!));
        }

        /// <summary>点击专辑超链接：收起播放面板并跳转到对应专辑详情页。</summary>
        private void NowPlayingAlbumLinkButton_Click(object sender, RoutedEventArgs e)
        {
            string albumName = NowPlayingAlbumText?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(albumName))
            {
                return;
            }

            string artistName = NowPlayingArtistText?.Text?.Trim() ?? "";
            AlbumEntry? entry = _albums.FirstOrDefault(
                a => string.Equals(a.Name, albumName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = BuildAlbumEntriesFromTracks(_playlist).FirstOrDefault(
                    a => string.Equals(a.Name, albumName, StringComparison.OrdinalIgnoreCase));
            }
            if (entry == null)
            {
                entry = new AlbumEntry { Name = albumName, Artist = artistName };
            }

            SetNowPlayingPaneVisible(false);
            // 从播放面板跳转：先切到「专辑」分类并真正显示视图根，再打开详情，左侧分类与右侧面板同步
            _currentCategory = "Albums";
            ApplyCategoryView();
            DispatcherQueue.TryEnqueue(() => OpenAlbumDetail(entry!, fromArtist: false));
        }

        private void UpdateNowPlayingArtistAlbumText(PlaylistItem item)
        {
            bool hasArtist = !string.IsNullOrWhiteSpace(item.Artist) && item.Artist != "未知艺术家";
            bool hasAlbum = !string.IsNullOrWhiteSpace(item.Album) && item.Album != "未知专辑";

            NowPlayingArtistText.Text = hasArtist ? item.Artist.Trim() : "未知艺术家";
            NowPlayingArtistLinkButton.IsEnabled = hasArtist;

            NowPlayingAlbumText.Text = hasAlbum ? item.Album.Trim() : "未知专辑";
            NowPlayingAlbumLinkButton.IsEnabled = hasAlbum;

            // 分隔符仅在艺术家与专辑都存在时显示
            if (NowPlayingArtistAlbumSeparator != null)
            {
                NowPlayingArtistAlbumSeparator.Visibility = (hasArtist && hasAlbum)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>播放面板背景保持透明，与专辑详情页一致（露出主程序背景，非浮层）。</summary>
        private void ApplyNowPlayingPaneTransparent()
        {
            try
            {
                if (NowPlayingPaneContent != null)
                {
                    NowPlayingPaneContent.Background = null;
                }
            }
            catch
            {
            }
        }

        private async Task ApplyAlbumArtBackgroundAsync(byte[]? coverBytes, string forPath)
        {
            if (AlbumArtBackgroundImage == null)
            {
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                ClearAlbumArtBackground();
                return;
            }

            if (coverBytes == null || coverBytes.Length == 0)
            {
                ClearAlbumArtBackground();
                return;
            }

            int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;
            byte[]? blurred = await Task.Run(() =>
                blurRadius > 0
                    ? AlbumArtBackground.CreateHeavilyBlurredPng(coverBytes, blurRadius: blurRadius)
                    : coverBytes);
            if (_nowPlayingPath != forPath || blurred == null || blurred.Length == 0)
            {
                return;
            }

            BitmapImage? image = await CreateBitmapFromBytesAsync(blurred);
            if (_nowPlayingPath != forPath)
            {
                return;
            }

            AlbumArtBackgroundImage.Source = image;
            if (AlbumArtBackgroundScrim != null)
            {
                AlbumArtBackgroundScrim.Opacity = 1;
            }
        }

        private void ClearAlbumArtBackground()
        {
            if (AlbumArtBackgroundImage != null)
            {
                AlbumArtBackgroundImage.Source = null;
            }
        }

        private void BuildLyricsUi(List<LyricLine> lyrics)
        {
            _lyricLines = lyrics;
            _currentLyricIndex = -1;
            _lyricTextBlocks.Clear();
            LyricsPanel.Children.Clear();

            if (lyrics.Count == 0)
            {
                AppSettingsState lyricSettings = AppSettingsStore.Load();
                string hint = "暂无歌词";
                if (lyricSettings.ShowSongInfoIfNoLyric)
                {
                    string title = NowPlayingTitleText?.Text ?? "未知曲目";
                    string artistAlbum = string.Join(" · ", new[] { NowPlayingArtistText?.Text, NowPlayingAlbumText?.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    hint = title + "\n" + artistAlbum;
                }

                ClearLyricsUi(hint);
                return;
            }

            LyricsEmptyHint.Visibility = Visibility.Collapsed;
            LyricsScrollViewer.Visibility = Visibility.Visible;

            // 上下垫高，便于首尾句也能滚到中间
            double pad = Math.Max(80, LyricsScrollViewer.ActualHeight / 2);
            LyricsPanel.Padding = new Thickness(8, pad, 8, pad);

            AppSettingsState uiSettings = AppSettingsStore.Load();
            TextAlignment align = uiSettings.LyricAlign switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                _ => TextAlignment.Center
            };
            LyricsPanel.Spacing = uiSettings.LyricLineSpacing;

            foreach (LyricLine line in lyrics)
            {
                var tb = new TextBlock
                {
                    TextAlignment = align,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154)),
                    Opacity = 0.55,
                    Tag = line
                };
                if (line.CharTimes != null && line.CharTimes.Count == line.Text.Length)
                {
                    // 逐字歌词：每字一个 Run，便于按字高亮
                    tb.Text = null;
                    var unplayedBrush = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                    foreach (char c in line.Text)
                    {
                        tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = c.ToString(),
                            Foreground = unplayedBrush
                        });
                    }
                }
                else
                {
                    tb.Text = line.Text;
                }

                _lyricTextBlocks.Add(tb);
                LyricsPanel.Children.Add(tb);
            }

            SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
            _miniPlayerWindow?.RefreshFromOwner();
        }

        private void ClearLyricsUi(string hint)
        {
            _lyricLines = new List<LyricLine>();
            _currentLyricIndex = -1;
            _lyricTextBlocks.Clear();
            LyricsPanel.Children.Clear();
            LyricsPanel.Padding = new Thickness(0);
            LyricsScrollViewer.Visibility = Visibility.Collapsed;
            LyricsEmptyHint.Text = hint;
            LyricsEmptyHint.Visibility = Visibility.Visible;
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
        }

        private void SyncLyricsToPosition(TimeSpan position, bool force = false)
        {
            if (_lyricLines.Count == 0 || _lyricTextBlocks.Count == 0)
            {
                return;
            }

            int index = 0;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].Time <= position)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            if (!force && index == _currentLyricIndex)
            {
                // 当前行未变：保持整行主题色，不把 Run 染成灰白
                if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                {
                    Brush curAccent = ResolveAccentBrush();
                    TextBlock row = _lyricTextBlocks[_currentLyricIndex];
                    row.Foreground = curAccent;
                    row.Opacity = 1.0;
                    if (curAccent is SolidColorBrush scbCur2)
                    {
                        ResetRowRunColors(row, scbCur2.Color.R, scbCur2.Color.G, scbCur2.Color.B);
                    }
                }

                return;
            }

            _currentLyricIndex = index;
            if (force)
            {
                StartupLog.Write("歌词强制重渲染 index=" + index + " 行数=" + _lyricTextBlocks.Count);
            }

            // 方案A：当前句主题色强调 + 相邻句微亮（纯属性调整，不改行结构、不用 Inlines）
            Brush accent = ResolveAccentBrush();
            for (int i = 0; i < _lyricTextBlocks.Count; i++)
            {
                TextBlock row = _lyricTextBlocks[i];
                int dist = Math.Abs(i - index);
                if (dist == 0)
                {
                    row.FontSize = 19;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    row.Foreground = accent;
                    row.Opacity = 1.0;
                    // 当前行整行保持主题色（不把 Run 染成灰白，避免播放中被掩盖成灰色）
                    if (accent is SolidColorBrush scbCur)
                    {
                        ResetRowRunColors(row, scbCur.Color.R, scbCur.Color.G, scbCur.Color.B);
                    }
                    else
                    {
                        ResetRowRunColors(row, 255, 255, 255);
                    }
                }
                else if (dist == 1)
                {
                    row.FontSize = 15;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = new SolidColorBrush(Color.FromArgb(255, 205, 205, 205));
                    row.Opacity = 0.85;
                    ResetRowRunColors(row, 205, 205, 205);
                }
                else
                {
                    row.FontSize = 14;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                    row.Opacity = 0.55;
                    ResetRowRunColors(row, 154, 154, 154);
                }
            }

            ScrollLyricToCenter(_lyricTextBlocks[index]);
        }

        /// <summary>当前行的逐字高亮刷新（无逐字数据时无操作）。</summary>
        private void UpdateCurrentLineCharHighlight(TimeSpan position)
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
            {
                UpdateCharHighlight(_lyricTextBlocks[_currentLyricIndex], position);
            }
        }

        /// <summary>逐字着色：已唱字符白色，未唱灰色。仅对带 CharTimes 的行生效。</summary>
        private void UpdateCharHighlight(TextBlock row, TimeSpan position)
        {
            if (row.Tag is not LyricLine line
                || line.CharTimes == null
                || line.CharTimes.Count != line.Text.Length
                || row.Inlines.Count == 0)
            {
                return;
            }

            int n = 0;
            for (int i = 0; i < line.CharTimes.Count; i++)
            {
                if (line.CharTimes[i] <= position)
                {
                    n = i + 1;
                }
                else
                {
                    break;
                }
            }

            var played = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            var unplayed = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
            for (int i = 0; i < row.Inlines.Count; i++)
            {
                if (row.Inlines[i] is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = i < n ? played : unplayed;
                }
            }
        }

        /// <summary>将行的逐字 Run 统一重置为指定颜色（用于非当前行）。</summary>
        private void ResetRowRunColors(TextBlock row, byte r, byte g, byte b)
        {
            if (row.Inlines.Count == 0)
            {
                return;
            }

            var brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
            foreach (Microsoft.UI.Xaml.Documents.Inline inline in row.Inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = brush;
                }
            }
        }

        private void ScrollLyricToCenter(FrameworkElement line)
        {
            LyricsScrollViewer.UpdateLayout();
            line.UpdateLayout();

            double viewport = LyricsScrollViewer.ViewportHeight;
            if (viewport <= 0)
            {
                return;
            }

            GeneralTransform transform = line.TransformToVisual(LyricsPanel);
            Windows.Foundation.Point topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double lineCenter = topLeft.Y + line.ActualHeight / 2;
            double target = lineCenter - viewport / 2;
            target = Math.Max(0, Math.Min(target, LyricsScrollViewer.ScrollableHeight));

            _lyricScrollFrom = LyricsScrollViewer.VerticalOffset;
            _lyricScrollTo = target;
            if (Math.Abs(_lyricScrollTo - _lyricScrollFrom) < 0.5)
            {
                return;
            }

            _lyricScrollStartMs = Environment.TickCount64;
            if (_lyricScrollTimer == null)
            {
                _lyricScrollTimer = DispatcherQueue.CreateTimer();
                _lyricScrollTimer.Interval = TimeSpan.FromMilliseconds(8);
                _lyricScrollTimer.Tick += LyricScrollTimer_Tick;
            }

            _lyricScrollTimer.Start();
        }

        private void LyricScrollTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            double elapsed = Environment.TickCount64 - _lyricScrollStartMs;
            double t = Math.Clamp(elapsed / LyricScrollDurationMs, 0, 1);
            // ease-in-out cubic，比系统默认滚动更柔和
            double eased = t < 0.5
                ? 4 * t * t * t
                : 1 - Math.Pow(-2 * t + 2, 3) / 2;

            double y = _lyricScrollFrom + (_lyricScrollTo - _lyricScrollFrom) * eased;
            LyricsScrollViewer.ChangeView(null, y, null, disableAnimation: true);

            if (t >= 1)
            {
                _lyricScrollTimer?.Stop();
                LyricsScrollViewer.ChangeView(null, _lyricScrollTo, null, disableAnimation: true);
            }
        }

        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawWaveformBars();
        }

        private void WaveformTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            MediaPlayer? player = GetPlayer();
            bool enginePlaying = _audioEngine?.IsPlaying == true;
            bool playing = (player?.Source != null
                    && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                || enginePlaying;

            double t = Environment.TickCount64 / 1000.0;
            double volume = enginePlaying ? VolumeSlider.Value / 100.0 : (player?.Volume ?? 0.8);
            bool changed = false;

            for (int i = 0; i < WaveBarCount; i++)
            {
                double target;
                if (playing)
                {
                    // 对称呼吸式频谱：中间高两边低、每柱各自节奏，无横向滚动
                    double rhythm = 0.5 + 0.5 * Math.Sin(t * 1.8 + _wavePhases[i]);
                    double halfSpan = Math.Max(1.0, (WaveBarCount - 1) / 2.0);
                    double pos = (i - (WaveBarCount - 1) / 2.0) / halfSpan;
                    double symmetry = 0.5 + 0.5 * (1.0 - Math.Min(1.0, Math.Abs(pos)));
                    double n = rhythm * symmetry;
                    target = Math.Clamp(n * (0.55 + 0.45 * volume), 0.1, 1.0);
                }
                else
                {
                    target = IdleLevel(i);
                }

                double next = _waveLevels[i] + (target - _waveLevels[i]) * (playing ? 0.35 : 0.18);
                if (Math.Abs(next - _waveLevels[i]) > 0.002)
                {
                    changed = true;
                }

                _waveLevels[i] = next;
            }

            if (changed || playing)
            {
                DrawWaveformBars();
            }

            if (!playing)
            {
                _waveformIdleSettleTicks++;
                if (_waveformIdleSettleTicks >= 12 || !changed)
                {
                    _waveformTimer?.Stop();
                    _waveformIdleSettleTicks = 0;
                }
            }
            else
            {
                _waveformIdleSettleTicks = 0;
            }
        }

        private static Color WaveColorFor(int index) => _waveAccentColor;

        /// <summary>频谱包络：中间高、两边低。</summary>
        private static double SpectrumEnvelope(int index)
        {
            double center = (WaveBarCount - 1) / 2.0;
            double envelope = 1.0 - 0.55 * Math.Abs(index - center) / Math.Max(1.0, center);
            return Math.Max(0.2, envelope);
        }

        /// <summary>未播放时的静态频谱形状（始终可见，中间高两边低）。</summary>
        private static double IdleLevel(int index)
        {
            double shape = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(index * 1.7 + 1.3));
            return Math.Max(0.18, shape * SpectrumEnvelope(index));
        }

        private void DrawWaveformBars()
        {
            if (WaveformCanvas == null)
            {
                return;
            }

            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            // 兜底：_waveLevels 全为 0 时填充静态频谱
            bool allFlat = true;
            for (int i = 0; i < WaveBarCount; i++)
            {
                if (_waveLevels[i] > 0.05)
                {
                    allFlat = false;
                    break;
                }
            }

            if (allFlat)
            {
                for (int i = 0; i < WaveBarCount; i++)
                {
                    _waveLevels[i] = IdleLevel(i);
                }
            }

            double gap = 2;
            double barWidth = Math.Max(2, (width - gap * (WaveBarCount - 1)) / WaveBarCount);

            while (WaveformCanvas.Children.Count < WaveBarCount)
            {
                int idx = WaveformCanvas.Children.Count;
                WaveformCanvas.Children.Add(new Border
                {
                    Background = new SolidColorBrush(WaveColorFor(idx)),
                    CornerRadius = new CornerRadius(2.5),
                    IsHitTestVisible = false
                });
            }

            while (WaveformCanvas.Children.Count > WaveBarCount)
            {
                WaveformCanvas.Children.RemoveAt(WaveformCanvas.Children.Count - 1);
            }

            for (int i = 0; i < WaveBarCount; i++)
            {
                if (WaveformCanvas.Children[i] is not Border bar)
                {
                    continue;
                }

                // 每次重绘都更新颜色(主题色变化后生效,不能只在新柱子创建时设置)
                bar.Background = new SolidColorBrush(WaveColorFor(i));

                double level = Math.Clamp(_waveLevels[i], 0.12, 1.0);
                double barHeight = Math.Max(10, Math.Min(height, height * level * 1.15));
                double left = i * (barWidth + gap);
                double top = (height - barHeight) / 2;

                // 直接赋值（Border 未设置时 Width/Height 为 NaN，比较判断恒 false 会导致柱子 0×0 不可见）
                bar.Width = barWidth;
                bar.Height = barHeight;
                Canvas.SetLeft(bar, left);
                Canvas.SetTop(bar, top);
            }
        }

        // =====================================================================
        // 播放核心
        // =====================================================================

        private void PlayAtIndex(int index)
            => PlayLibraryItemAt(index, syncUserPlaylistIndex: true);

        private void PlayLibraryItemAt(int index, bool syncUserPlaylistIndex)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                return;
            }

            PlaylistItem item = _playlist[index];
            _currentIndex = index;
            if (syncUserPlaylistIndex)
            {
                _userPlaylistIndex = FindUserPlaylistIndex(item.FilePath);
            }

            if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && index >= 0
                && PlaylistView.ItemsSource is System.Collections.IList songsList
                && index < songsList.Count)
            {
                PlaylistView.SelectedIndex = index;
                PlaylistView.ScrollIntoView(item);
            }

            StartPlayback(item);
        }

        private void PlayUserPlaylistAt(int index)
        {
            if (index < 0 || index >= _userPlaylist.Count)
            {
                return;
            }

            PlaylistItem item = _userPlaylist[index];
            _userPlaylistIndex = index;
            _currentIndex = FindLibraryIndex(item.FilePath);

            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                && !_isMultiSelectMode)
            {
                PlaylistView.SelectedIndex = index;
                PlaylistView.ScrollIntoView(item);
            }
            else if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && _currentIndex >= 0
                && PlaylistView.ItemsSource is System.Collections.IList songsList2
                && _currentIndex < songsList2.Count)
            {
                PlaylistView.SelectedIndex = _currentIndex;
                PlaylistView.ScrollIntoView(_playlist[_currentIndex]);
            }

            StartPlayback(item);
            PersistPlaybackSession();
            NotifyCurrentPlaylistWindow();
        }

        private void StartPlayback(PlaylistItem item)
        {
            ScrobblePreviousIfAny();

            // 进度条样式(读设置缓存) + 异步加载波形(波形样式用)
            _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
            // 保留旧波形直到新波形解码完成(避免加载过程闪占位)
            _waveformPath = null;
            StartupLog.Write("波形加载开始: " + item.FilePath + " style=" + _progressBarStyle);
            LoadWaveformForCurrentAsync(item.FilePath);

            // DSD(DSF/DFF) 在非 WASAPI 独占模式下自动转码为 PCM 输出（保留可听性，非 bit-perfect），
            // 独占模式下走 DoP 原生直出。提示在 PlayExtendedWithEngineAsync 成功后给出。
            StartupLog.Write("StartPlayback: " + item.FilePath + " mode=" + (AppSettingsStore.Load().OutputMode));

            // HiFi 独占模式（WASAPI 独占 / ASIO）：所有曲目统一走 FFmpeg 引擎 + NAudio 输出。
            // 直接按设置判断（而非 _audioEngine.IsHiFiMode），避免 engine 尚未创建/未设 mode 时的首次播放漏走独占。
            bool hifiMode = IsHiFiModeSelected();
            if (hifiMode || AudioPlaybackEngine.NeedsFfmpeg(item.FilePath))
            {
                // 用源实际时长作为进度/播完上限（规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界）。
                // DSD 源用 ffmpeg 探测真实音轨时长（TagLib 读 DSD 源时长可能不准）；其它用元数据时长，异步不阻塞开播。
                _ = ApplyEngineSourceDurationAsync(item);
                // 停掉 MediaPlayer，避免与引擎同时出声
                MediaPlayer? curPlayer = GetPlayer();
                if (curPlayer != null && curPlayer.Source != null)
                {
                    try
                    {
                        curPlayer.Pause();
                    }
                    catch
                    {
                    }
                }

                _ = PlayExtendedWithEngineAsync(item);
                return;
            }

            // 普通格式：停掉引擎，避免残留引擎声音
            StopEngineIfActive();

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            _pendingRestorePositionSeconds = null;
            NowPlayingText.Text = "正在播放：" + item.Title + " - " + item.Artist;
            _ = UpdateNowPlayingPanelAsync(item);
            RecordPlaybackStatsOnStart(item);

            AppSettingsState settings = AppSettingsStore.Load();
            double targetVolume = VolumeSlider.Value / 100.0;

            void BeginSource()
            {
                try
                {
                    MediaSource source = MediaSource.CreateFromUri(CreateFileMediaUri(item.FilePath));
                    player.Source = source;
                    if (item.StartTimeSeconds > 0)
                    {
                        _pendingRestorePositionSeconds = item.StartTimeSeconds;
                    }

                    ApplyPlaybackRateFromSettings();
                    player.Play();
                    if (settings.EnableFade && _fadeController != null)
                    {
                        player.Volume = 0;
                        _fadeController.FadeIn(player, targetVolume, settings.FadeMilliseconds);
                    }
                    else
                    {
                        player.Volume = targetVolume;
                    }

                    PlaybackSessionStore.Save(item.FilePath, 0);
                }
                catch (Exception ex)
                {
                    NowPlayingText.Text = "播放失败：" + item.Title;
                    _ = ShowErrorAsync("播放失败", ex.Message);
                }
            }

            if (settings.EnableFade && _fadeController != null && player.Source != null
                && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                _fadeController.FadeOutThen(player, settings.FadeMilliseconds, BeginSource);
            }
            else
            {
                BeginSource();
            }
        }

        /// <summary>引擎播放前异步设置源实际时长（DSD 用 ffmpeg 探测真实音轨时长，其它用元数据时长），
        /// 作为进度/播完上限规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界。</summary>
        private async System.Threading.Tasks.Task ApplyEngineSourceDurationAsync(PlaylistItem item)
        {
            try
            {
                if (_audioEngine == null || item == null) return;
                string srcExt = System.IO.Path.GetExtension(item.FilePath).ToLowerInvariant();
                TimeSpan src = item.Duration;
                if (srcExt is ".dsf" or ".dff")
                {
                    var probed = await _audioEngine.ProbeSourceDurationAsync(item.FilePath);
                    if (probed > TimeSpan.Zero) src = probed;
                }

                _audioEngine.SetSourceDuration(src);
            }
            catch
            {
            }
        }


        /// <summary>用 FFmpeg 引擎播放扩展格式（APE/WavPack 等）。</summary>
        private async Task PlayExtendedWithEngineAsync(PlaylistItem item)
        {
            NowPlayingText.Text = "正在转码：" + item.Title;
            _audioEngine ??= new AudioPlaybackEngine();
            AppSettingsState hifiSettings = AppSettingsStore.Load();
            ApplyEngineOutputMode(hifiSettings);
            _audioEngine.SetOutputDevicePreference(string.IsNullOrWhiteSpace(hifiSettings.OutputDeviceId) ? null : hifiSettings.OutputDeviceId);
            _audioEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _audioEngine.PlaybackEnded += OnEnginePlaybackEnded;
            _audioEngine.SeamlessTrackChanged -= OnSeamlessTrackChanged;
            _audioEngine.SeamlessTrackChanged += OnSeamlessTrackChanged;

            try
            {
                _audioEngine.SetEqualizer(EqualizerStore.Load().BandGains);
            }
            catch
            {
            }

            bool ok = await _audioEngine.PlayFileWithFfmpegAsync(item.FilePath, s =>
                DispatcherQueue.TryEnqueue(() => { NowPlayingText.Text = s; }));
            if (ok)
            {
                _isEnginePaused = false;
                _usingEnginePlayback = true;
                NowPlayingText.Text = "正在播放（引擎）：" + item.Title + " - " + item.Artist;
                // DSD 在非 WASAPI 独占模式：ffmpeg 转码为 PCM 输出（非 bit-perfect），左上角提示转码结果。
                if (IsDsdFile(item.FilePath)
                    && !string.Equals(AppSettingsStore.Load().OutputMode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase))
                {
                    string pcmDesc = string.IsNullOrWhiteSpace(_audioEngine?.SourceFormatDescription)
                        ? (_audioEngine?.ActualOutputFormat ?? "PCM") : _audioEngine!.SourceFormatDescription!;
                    NowPlayingText.Text = "已转码为 " + pcmDesc + " 输出";
                    StartupLog.Write("DSD 已转码 PCM 输出: " + item.FilePath + " → " + (_audioEngine?.SourceFormatDescription ?? "?"));
                }
                // 设备主音量（DAC 驱动级）只由用户拖动音量条时设置，切歌不重置，保持用户设定的响度。
                // （播放器数字音量恒 100% 直通，bit-perfect；无实体音量键的小尾巴可拖动音量条调轻）
                _ = UpdateNowPlayingPanelAsync(item);
                UpdateNowPlayingOutputFormat();
                UpdateSignalChainDisplay();
                RecordPlaybackStatsOnStart(item);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE769";
                }

                // 引擎桥接：时长 / 进度条 / 波形
                ProgressSlider.Maximum = Math.Max(1, _audioEngine.Duration.TotalSeconds);
                ProgressSlider.Value = 0;
                TotalTimeText.Text = FormatTime(_audioEngine.Duration);
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.PositionChanged += EnginePositionChanged;
                UpdateWaveformTimerForPlaybackState(true);
                _ = FadeInEngineAsync();
                _miniPlayerWindow?.RefreshFromOwner();
                ConfigureEngineSmtc(item, playing: true);
                _ = PreloadSeamlessNextAsync(item);
            }
            else
            {
                string? reason = _audioEngine?.LastError;
                NowPlayingText.Text = string.IsNullOrWhiteSpace(reason)
                    ? "播放失败（FFmpeg 转码或打开出错）"
                    : "播放失败：" + reason;

                if (reason != null && reason.Contains("未找到内置 ffmpeg.exe"))
                {
                    _ = ShowErrorAsync(
                        "无法播放该格式",
                        "内置 FFmpeg 解码器未找到。\n\n这通常是被杀毒软件（如火绒/360）拦截删除。\n请将程序目录下 Assets\\ffmpeg 文件夹加入杀毒软件信任区，然后重新打开程序。");
                }
            }
        }


        private async Task FadeInEngineAfterResumeAsync(double target)
        {
            // 防御：若读到的暂停音量异常(<=1%≈静音误判)，回退全音量，避免恢复后设备音量停在约 0 造成"系统托盘音量归0"。
            if (target <= 0.01 || double.IsNaN(target))
            {
                target = 1.0;
            }

            target = Math.Clamp(target, 0.0, 1.0);
            try
            {
                // 暂停恢复后独占会话重建，瞬时全音量可达造成爆音/音量暴增：
                // 从较低音量极短渐变到目标，缓解瞬态。（仅暂停恢复路径调用，不影响常规切歌的 bit-perfect 直通。）
                _audioEngine?.SetVolume(target * 0.18);
                const int steps = 5;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * (0.18 + 0.82 * i / (double)steps));
                    await Task.Delay(30);
                }
            }
            catch
            {
            }
            finally
            {
                // 无论 Fade 是否中断/异常，最终都恢复目标设备音量，避免停在 0.02 静音值
                try { _audioEngine?.SetVolume(target); } catch { }
            }
        }

        /// <summary>引擎开播淡入（约 320ms 渐入到当前音量）。</summary>
        private async Task FadeInEngineAsync()
        {
            try
            {
                double target = VolumeSlider.Value / 100.0;
                // HiFi 独占（bit-perfect）下数字音量恒 100%；设备主音量由用户拖动音量条设置，切歌不重置。
                if (IsHiFiModeSelected())
                {
                    return; // 不在此重设设备音量，避免切歌把音量拉回 100%
                }

                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * i / steps);
                    await Task.Delay(40);
                }

                _audioEngine?.SetVolume(target);
            }
            catch
            {
            }
        }

        private void OnEnginePlaybackEnded()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Stopped);
                _miniPlayerWindow?.RefreshFromOwner();
                HandleMediaEnded();
            });
        }

        /// <summary>引擎开播后预加载下一首到无缝源（共享/ASIO、顺序播放）。同格式可无缝续接，否则由上层重建。</summary>
        private async System.Threading.Tasks.Task PreloadSeamlessNextAsync(PlaylistItem current)
        {
            if (_audioEngine == null || current == null)
            {
                return;
            }

            // DSD 播完自动切歌优先：DSD 源不参与无缝预加载（走原 Stop→PlayNext，避免无缝与 DSD 时长修正冲突）
            string curExt = System.IO.Path.GetExtension(current.FilePath).ToLowerInvariant();
            if (curExt is ".dsf" or ".dff")
            {
                _seamlessPreloaded = null;
                return;
            }

            try
            {
                PlaylistItem? next = ResolveSequentialNextItem(current);
                if (next == null || string.IsNullOrWhiteSpace(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                {
                    StartupLog.Write("预加载: 无下一首 or 文件不存在 next=" + (next?.Title ?? "<null>"));
                    _seamlessPreloaded = null;
                    return;
                }

                string? wav = await _audioEngine.EnsureCachedWavAsync(next.FilePath);
                if (string.IsNullOrWhiteSpace(wav))
                {
                    StartupLog.Write("预加载: 转码失败 无WAV next=" + next.Title);
                    _seamlessPreloaded = null;
                    return;
                }

                bool ok = await _audioEngine.PrepareNextSeamless(wav);
                StartupLog.Write("预加载: \"" + current.Title + "\" → \"" + next.Title + "\" wav=" + System.IO.Path.GetFileName(wav) + " 采纳无缝=" + ok);
                if (ok)
                {
                    _seamlessPreloaded = next;
                }
                else
                {
                    _seamlessPreloaded = null; // 格式不同等：无缝不启用，后续走重建
                }
            }
            catch (Exception ex)
            {
                StartupLog.Write("预加载: 异常 " + ex.Message);
                _seamlessPreloaded = null;
            }
        }

        /// <summary>顺序播放时确定"当前曲目之后"的一首（播放队列优先，否则媒体库列表）。</summary>
        private PlaylistItem? ResolveSequentialNextItem(PlaylistItem current)
        {
            if (_userPlaylist.Count > 0)
            {
                int idx = FindUserPlaylistIndex(current.FilePath);
                if (idx >= 0 && idx + 1 < _userPlaylist.Count)
                {
                    return _userPlaylist[idx + 1];
                }
                return null;
            }

            if (_playlist.Count > 0)
            {
                int idx = _playlist.ToList().FindIndex(p => string.Equals(p.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx + 1 < _playlist.Count)
                {
                    return _playlist[idx + 1];
                }
            }
            return null;
        }

        /// <summary>无缝切到预加载的下一首：更新正在播放信息与索引，并继续预加载下下首。</summary>
        private void OnSeamlessTrackChanged()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                PlaylistItem? next = _seamlessPreloaded;
                _seamlessPreloaded = null;
                if (next != null)
                {
                    _isEnginePaused = false;
                    _usingEnginePlayback = true;
                    NowPlayingText.Text = "正在播放（引擎）：" + next.Title + " - " + next.Artist;
                    _ = UpdateNowPlayingPanelAsync(next);
                    // 无缝续接后重新加载下一首的波形/进度条样式（否则会残留上一首的波形与时长）
                    _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
                    _waveformPath = null;
                    StartupLog.Write("无缝切歌 波形加载开始: " + next.FilePath + " style=" + _progressBarStyle);
                    LoadWaveformForCurrentAsync(next.FilePath);
                    UpdateNowPlayingOutputFormat();
                    RecordPlaybackStatsOnStart(next);
                    if (_audioEngine != null)
                    {
                        ProgressSlider.Maximum = Math.Max(1, _audioEngine.Duration.TotalSeconds);
                        ProgressSlider.Value = 0;
                        TotalTimeText.Text = FormatTime(_audioEngine.Duration);
                    }
                    ConfigureEngineSmtc(next, playing: true);
                    AdvanceUserPlaylistIndexTo(next);
                    _miniPlayerWindow?.RefreshFromOwner();
                    _ = PreloadSeamlessNextAsync(next);
                }
            });
        }

        /// <summary>无缝切歌后同步用户播放队列当前索引（命中则设为该项）。</summary>
        private void AdvanceUserPlaylistIndexTo(PlaylistItem item)
        {
            if (_userPlaylist.Count == 0) return;
            int idx = FindUserPlaylistIndex(item.FilePath);
            if (idx >= 0)
            {
                _userPlaylistIndex = idx;
                PlaylistView.SelectedIndex = idx;
            }
        }

        /// <summary>配置引擎播放的系统媒体控件（SMTC）。</summary>
        private void ConfigureEngineSmtc(PlaylistItem item, bool playing)
        {
            try
            {
                if (!AppSettingsStore.Load().EnableSmtc)
                {
                    return;
                }

                _engineSmtc ??= SystemMediaTransportControls.GetForCurrentView();
                SystemMediaTransportControls smtc = _engineSmtc;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.IsStopEnabled = true;
                smtc.ButtonPressed -= Smtc_ButtonPressed;
                smtc.ButtonPressed += Smtc_ButtonPressed;
                smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                SystemMediaTransportControlsDisplayUpdater updater = smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = item.Title;
                updater.MusicProperties.Artist = item.Artist;
                updater.MusicProperties.AlbumTitle = item.Album;
                updater.Thumbnail = null;
                updater.Update();

                // 异步补封面缩略图（deskbox/系统媒体浮层可显示专辑封面）
                _ = LoadAndSetSmtcThumbnailAsync(updater, item);
            }
            catch
            {
            }
        }

        /// <summary>异步读取曲目封面并设置到 SMTC 缩略图（失败静默）。</summary>
        private async System.Threading.Tasks.Task LoadAndSetSmtcThumbnailAsync(SystemMediaTransportControlsDisplayUpdater updater, PlaylistItem item)
        {
            try
            {
                if (updater == null || item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
                byte[]? bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(item.FilePath));
                if (bytes is not { Length: > 0 }) return;
                using var ms = new System.IO.MemoryStream(bytes);
                ms.Position = 0;
                var stream = ms.AsRandomAccessStream();
                var reference = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream);
                updater.Thumbnail = reference;
                updater.Update();
            }
            catch
            {
            }
        }

        /// <summary>更新引擎 SMTC 播放状态（暂停/恢复/结束）。</summary>
        private void UpdateEngineSmtcStatus(MediaPlaybackStatus status)
        {
            if (_engineSmtc == null)
            {
                return;
            }

            try
            {
                _engineSmtc.PlaybackStatus = status;
                if (status == MediaPlaybackStatus.Stopped)
                {
                    _engineSmtc.IsEnabled = false;
                }
            }
            catch
            {
            }
        }

        /// <summary>引擎播放位置 → 进度条 / 时间 / 任务栏进度。</summary>
        private void EnginePositionChanged(TimeSpan position)
        {
            try
            {
                if (!_usingEnginePlayback)
                {
                    return;
                }

                // 用户正在拖动/点击进度条时不覆盖，避免跳动
                if (_isUserSeeking)
                {
                    return;
                }

                // 时长变化时同步进度条上限
                double duration = _audioEngine?.Duration.TotalSeconds ?? 0;
                if (duration > 1 && Math.Abs(ProgressSlider.Maximum - duration) > 1)
                {
                    ProgressSlider.Maximum = duration;
                    TotalTimeText.Text = FormatTime(_audioEngine!.Duration);
                }

                double seconds = position.TotalSeconds;
                if (seconds >= 0
                    && seconds <= ProgressSlider.Maximum
                    && Math.Abs(ProgressSlider.Value - seconds) >= 0.05)
                {
                    _isUpdatingProgressUi = true;
                    try
                    {
                        ProgressSlider.Value = seconds;
                    }
                    finally
                    {
                        _isUpdatingProgressUi = false;
                    }
                }

                string timeText = FormatTime(position);
                if (CurrentTimeText != null
                    && !string.Equals(CurrentTimeText.Text, timeText, StringComparison.Ordinal))
                {
                    CurrentTimeText.Text = timeText;
                }

                _desktopLyricsWindow?.Sync(position);
                _miniPlayerWindow?.SyncPosition(position, _audioEngine?.Duration ?? TimeSpan.Zero);
                _taskbarProgress?.SetProgress(position.TotalSeconds, ProgressSlider.Maximum, paused: false);

                // 引擎（HiFi 独占/ASIO）路径也要推进当前歌词行与滚动，
                // 否则歌词不随播放滚动（普通 MediaPlayer 路径由 PositionTimer_Tick 调用）。
                SyncLyricsToPosition(position);
            }
            catch
            {
            }
        }

        /// <summary>停止引擎播放并复位相关 UI 状态（切到普通格式时调用）。</summary>
        private void StopEngineIfActive()
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.Stop();
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Stopped);
                _miniPlayerWindow?.RefreshFromOwner();
            }
        }

        private void HandleMediaEnded()
        {
            if (ConsumeSleepStopIfDue())
            {
                return;
            }

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackLoop:
                    // IsLooping 为 true 时通常不会触发；兜底再播当前曲
                    if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                    {
                        PlayUserPlaylistAt(_userPlaylistIndex);
                    }
                    break;

                case PlaybackOrder.TrackOnce:
                    break;

                case PlaybackOrder.Sequential:
                    PlayNext(autoAdvance: true);
                    break;

                default:
                    PlayNext(autoAdvance: true);
                    break;
            }
        }

        private void PlayNext()
            => PlayNext(autoAdvance: false);

        private void PlayNext(bool autoAdvance)
        {
            // 优先按播放队列续播；若队列为空（例如直接从媒体库/文件夹双击播放），
            // 则按媒体库当前列表 (_playlist) 顺序推进，保证播完能自动连续下一首。
            if (_userPlaylist.Count > 0)
            {
                int? nextIndex = ResolveNextIndex(autoAdvance);
                if (nextIndex != null)
                {
                    PlayUserPlaylistAt(nextIndex.Value);
                }

                return;
            }

            AdvanceInLibraryPlaylist(autoAdvance);
        }

        /// <summary>队列为空时，从媒体库当前列表推进到下一首（按播放顺序）。</summary>
        private void AdvanceInLibraryPlaylist(bool autoAdvance)
        {
            if (_playlist.Count == 0 || _currentIndex < 0)
            {
                return;
            }

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackOnce:
                    return; // 单曲播放模式：不自动续播

                case PlaybackOrder.TrackLoop:
                    PlayLibraryItemAt(_currentIndex, syncUserPlaylistIndex: false);
                    return;

                case PlaybackOrder.Sequential:
                    if (_currentIndex + 1 < _playlist.Count)
                    {
                        PlayLibraryItemAt(_currentIndex + 1, syncUserPlaylistIndex: false);
                    }

                    return;

                case PlaybackOrder.Random:
                    if (_playlist.Count > 1)
                    {
                        int r = _playbackRandom.Next(_playlist.Count);
                        if (r == _currentIndex)
                        {
                            r = (r + 1) % _playlist.Count;
                        }

                        PlayLibraryItemAt(r, syncUserPlaylistIndex: false);
                    }
                    else if (_playlist.Count == 1)
                    {
                        PlayLibraryItemAt(_currentIndex, syncUserPlaylistIndex: false);
                    }

                    return;

                default: // ListLoop 等：循环到列表尾后回到开头
                    PlayLibraryItemAt((_currentIndex + 1) % _playlist.Count, syncUserPlaylistIndex: false);
                    return;
            }
        }

        private void PlayPrevious()
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            int? prevIndex = ResolvePreviousIndex();
            if (prevIndex == null)
            {
                return;
            }

            PlayUserPlaylistAt(prevIndex.Value);
        }

        private int? ResolveNextIndex(bool autoAdvance)
        {
            int count = _userPlaylist.Count;
            if (count == 0)
            {
                return null;
            }

            int baseIndex = _userPlaylistIndex >= 0 ? _userPlaylistIndex : 0;

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackOnce:
                    if (autoAdvance)
                    {
                        return null;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : null;

                case PlaybackOrder.TrackLoop:
                    if (autoAdvance)
                    {
                        return baseIndex;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : 0;

                case PlaybackOrder.Sequential:
                {
                    int next = baseIndex + 1;
                    return next >= count ? null : next;
                }

                case PlaybackOrder.ListLoop:
                    return (baseIndex + 1) % count;

                case PlaybackOrder.Random:
                {
                    if (count == 1)
                    {
                        return 0;
                    }

                    int next = _playbackRandom.Next(count);
                    if (next == baseIndex)
                    {
                        next = (next + 1) % count;
                    }
                    return next;
                }

                default:
                    return (baseIndex + 1) % count;
            }
        }

        private int? ResolvePreviousIndex()
        {
            int count = _userPlaylist.Count;
            if (count == 0)
            {
                return null;
            }

            int baseIndex = _userPlaylistIndex >= 0 ? _userPlaylistIndex : 0;

            switch (_playbackOrder)
            {
                case PlaybackOrder.Sequential:
                case PlaybackOrder.TrackOnce:
                    return baseIndex > 0 ? baseIndex - 1 : null;

                case PlaybackOrder.Random:
                case PlaybackOrder.ListLoop:
                case PlaybackOrder.TrackLoop:
                default:
                    return baseIndex <= 0 ? count - 1 : baseIndex - 1;
            }
        }

        // =====================================================================
        // 播放顺序按钮
        // =====================================================================

        private void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
        {
            PlaybackOrder[] order =
            {
                PlaybackOrder.Sequential,
                PlaybackOrder.Random,
                PlaybackOrder.ListLoop,
                PlaybackOrder.TrackLoop,
                PlaybackOrder.TrackOnce
            };

            int index = Array.IndexOf(order, _playbackOrder);
            int next = index < 0 ? 0 : (index + 1) % order.Length;
            SetPlaybackOrder(order[next]);
        }

        private void PlaybackOrderButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowPlaybackOrderMenu(PlaybackOrderButton, e.GetPosition(PlaybackOrderButton));
        }

        private void ShowPlaybackOrderMenu(FrameworkElement target, Windows.Foundation.Point position)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Sequential, "\uE8FD", "顺序播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Random, "\uE8B1", "随机播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.ListLoop, "\uE8EE", "列表循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackLoop, "\uE8ED", "单曲循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackOnce, "\uE72A", "单曲播放");

            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = position,
                Placement = FlyoutPlacementMode.Top
            });
        }

        private void AddPlaybackOrderMenuItem(MenuFlyout flyout, PlaybackOrder order, string glyph, string label)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
                IsChecked = _playbackOrder == order,
                Tag = order
            };
            item.Click += PlaybackOrderMenuItem_Click;
            flyout.Items.Add(item);
        }

        private void PlaybackOrderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: PlaybackOrder order })
            {
                SetPlaybackOrder(order);
            }
        }

        private void SetPlaybackOrder(PlaybackOrder order, bool persist = true)
        {
            _playbackOrder = order;
            ApplyPlaybackOrderToPlayer();
            UpdatePlaybackOrderButtonUi();
            _miniPlayerWindow?.RefreshFromOwner();
            if (persist)
            {
                AppSettingsStore.Update(s => s.PlaybackOrder = order.ToString());
            }
        }

        private void ApplyPlaybackOrderToPlayer()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            player.IsLoopingEnabled = _playbackOrder == PlaybackOrder.TrackLoop;
        }

        private void UpdatePlaybackOrderButtonUi()
        {
            bool trackOnce = _playbackOrder == PlaybackOrder.TrackOnce;
            PlaybackOrderIcon.Visibility = trackOnce ? Visibility.Collapsed : Visibility.Visible;
            PlaybackOrderTrackOnceGlyph.Visibility = trackOnce ? Visibility.Visible : Visibility.Collapsed;

            (string glyph, string name) = _playbackOrder switch
            {
                PlaybackOrder.Sequential => ("\uE8FD", "顺序播放"),
                PlaybackOrder.Random => ("\uE8B1", "随机播放"),
                PlaybackOrder.ListLoop => ("\uE8EE", "列表循环"),
                PlaybackOrder.TrackLoop => ("\uE8ED", "单曲循环"),
                PlaybackOrder.TrackOnce => ("\uE72A", "单曲播放"),
                _ => ("\uE8EE", "列表循环")
            };

            if (!trackOnce)
            {
                PlaybackOrderIcon.Glyph = glyph;
            }

            ToolTipService.SetToolTip(PlaybackOrderButton, name + "（左键切换，右键选择）");
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            if (time.TotalHours >= 1)
            {
                return time.ToString(@"h\:mm\:ss");
            }

            return time.ToString(@"mm\:ss");
        }

        private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null)
        {
            try
            {
                ContentDialog dialog = new()
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = xamlRoot ?? Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch
            {
            }
        }

        // =====================================================================
        // 主区域：播放列表 / 右侧 可拖分割线
        // =====================================================================

        /// <summary>播放列表区域尺寸变化：保证各列完整可见且不过度拉宽。</summary>
        private void PlaylistListBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            FitColumnsToAvailableWidth();
        }

        /// <summary>
        /// 列宽适配中间区域：按首选比例缩放，使标题～时长始终铺满中间可视宽度。
        /// </summary>
        private void FitColumnsToAvailableWidth()
        {
            if (_isDraggingColumnSplitter)
            {
                return;
            }

            double available = GetPlaylistColumnsViewportWidth();
            if (available <= 0)
            {
                return;
            }

            // 4 根列间间隙 + 时长右侧留白（避免胶囊右圆角被裁切）
            const double splitterTotal = HorizontalResizeSplitter.HitWidth * 4;
            const double endPad = 12;
            double usable = Math.Max(0, available - splitterTotal - endPad);

            // 首选比例（与默认密度一致）
            const double prefTitle = 140;
            const double prefArtist = 110;
            const double prefAlbum = 110;
            const double prefYear = 52;
            const double prefDuration = 60;
            const double preferredSum = prefTitle + prefArtist + prefAlbum + prefYear + prefDuration;

            double scale = usable / preferredSum;
            double title = Math.Max(48, prefTitle * scale);
            double artist = Math.Max(48, prefArtist * scale);
            double album = Math.Max(48, prefAlbum * scale);
            double year = Math.Max(40, prefYear * scale);
            double duration = Math.Max(40, prefDuration * scale);

            // Min 截断后校正总和，确保刚好铺满
            double again = title + artist + album + year + duration;
            if (again > usable)
            {
                double deficit = again - usable;
                double take = Math.Min(deficit, Math.Max(0, title - 48));
                title -= take;
                deficit -= take;
                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, artist - 48));
                    artist -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, album - 48));
                    album -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, year - 40));
                    year -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    duration = Math.Max(40, duration - deficit);
                }
            }
            else if (usable - again > 0.5)
            {
                // 剩余补给标题列，保证铺满中间区域
                title += usable - again;
            }

            var w = PlaylistColumnWidths.Instance;
            w.Title = title;
            w.Artist = artist;
            w.Album = album;
            w.Year = year;
            w.Duration = duration;
            SyncHeaderColumnsFromState();
            if (HeaderEndPadCol != null
                && (!HeaderEndPadCol.Width.IsAbsolute
                    || Math.Abs(HeaderEndPadCol.Width.Value - endPad) >= 0.5))
            {
                HeaderEndPadCol.Width = new GridLength(endPad);
            }
        }

        private double GetPlaylistColumnsViewportWidth()
        {
            double border = PlaylistListBorder.ActualWidth;
            if (border > 0)
            {
                // Header Margin="4,4,4,4"
                return Math.Max(0, border - 8);
            }

            if (PlaylistHeaderGrid.ActualWidth > 0)
            {
                return PlaylistHeaderGrid.ActualWidth;
            }

            return PlaylistColumn.ActualWidth > 0 ? Math.Max(0, PlaylistColumn.ActualWidth - 24) : 0;
        }

        // =====================================================================
        // 播放列表列：可拖分割线
        // =====================================================================

        private void SyncHeaderColumnsFromState()
        {
            var w = PlaylistColumnWidths.Instance;
            SetColumnWidthIfChanged(HeaderTitleCol, w.Title);
            SetColumnWidthIfChanged(HeaderArtistCol, w.Artist);
            SetColumnWidthIfChanged(HeaderAlbumCol, w.Album);
            SetColumnWidthIfChanged(HeaderYearCol, w.Year);
            SetColumnWidthIfChanged(HeaderDurationCol, w.Duration);
        }

        private static void SetColumnWidthIfChanged(ColumnDefinition column, double width)
        {
            if (column.Width.IsAbsolute && Math.Abs(column.Width.Value - width) < 0.5)
            {
                return;
            }

            column.Width = new GridLength(width);
        }

        private void ColumnSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement splitter || splitter.Tag is not string pair)
            {
                return;
            }

            _isDraggingColumnSplitter = true;
            _columnSplitPair = pair;
            _columnSplitStartX = e.GetCurrentPoint(PlaylistHeaderGrid).Position.X;

            GetColumnWidths(pair, out _columnLeftStartWidth, out _columnRightStartWidth);
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void ColumnSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingColumnSplitter || _columnSplitPair == null)
            {
                return;
            }

            double delta = e.GetCurrentPoint(PlaylistHeaderGrid).Position.X - _columnSplitStartX;

            const double minLeft = 48;
            const double minRight = 40;

            double left = Math.Max(minLeft, _columnLeftStartWidth + delta);
            double right = Math.Max(minRight, _columnRightStartWidth - delta);
            double total = _columnLeftStartWidth + _columnRightStartWidth;

            if (left + right > total)
            {
                right = total - left;
            }

            if (right < minRight)
            {
                right = minRight;
                left = total - right;
            }

            if (left < minLeft)
            {
                left = minLeft;
                right = total - left;
            }

            SetColumnWidths(_columnSplitPair, left, right);
            e.Handled = true;
        }

        private void ColumnSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingColumnSplitter = false;
            _columnSplitPair = null;
            if (sender is UIElement el)
            {
                try
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                catch
                {
                }
            }

            e.Handled = true;
        }

        private void ColumnSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingColumnSplitter = false;
            _columnSplitPair = null;
        }

        private static void GetColumnWidths(string pair, out double left, out double right)
        {
            var w = PlaylistColumnWidths.Instance;
            switch (pair)
            {
                case "Title|Artist":
                    left = w.Title;
                    right = w.Artist;
                    break;
                case "Artist|Album":
                    left = w.Artist;
                    right = w.Album;
                    break;
                case "Album|Year":
                    left = w.Album;
                    right = w.Year;
                    break;
                case "Year|Duration":
                    left = w.Year;
                    right = w.Duration;
                    break;
                default:
                    left = w.Year;
                    right = w.Duration;
                    break;
            }
        }

        private void SetColumnWidths(string pair, double left, double right)
        {
            var w = PlaylistColumnWidths.Instance;
            switch (pair)
            {
                case "Title|Artist":
                    w.Title = left;
                    w.Artist = right;
                    HeaderTitleCol.Width = w.TitleLength;
                    HeaderArtistCol.Width = w.ArtistLength;
                    break;
                case "Artist|Album":
                    w.Artist = left;
                    w.Album = right;
                    HeaderArtistCol.Width = w.ArtistLength;
                    HeaderAlbumCol.Width = w.AlbumLength;
                    break;
                case "Album|Year":
                    w.Album = left;
                    w.Year = right;
                    HeaderAlbumCol.Width = w.AlbumLength;
                    HeaderYearCol.Width = w.YearLength;
                    break;
                case "Year|Duration":
                    w.Year = left;
                    w.Duration = right;
                    HeaderYearCol.Width = w.YearLength;
                    HeaderDurationCol.Width = w.DurationLength;
                    break;
            }
        }

        // =====================================================================
        // 单元格悬停 1 秒后显示该字段完整信息
        // =====================================================================

        private void PlaylistCell_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            HideHoverTip();
            _hoverElement = element;
            _hoverTipText = ResolveCellDetailText(element);

            if (string.IsNullOrWhiteSpace(_hoverTipText) || _hoverTipTimer == null)
            {
                return;
            }

            _hoverTipTimer.Stop();
            _hoverTipTimer.Start();
        }

        private void PlaylistCell_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // 保留事件：移动时不重置 1 秒计时，避免轻微抖动导致提示永远不出
        }

        private void PlaylistCell_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverTipTimer?.Stop();
            HideHoverTip();
            _hoverElement = null;
            _hoverTipText = null;
        }

        private void HoverTipTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (_hoverElement == null || string.IsNullOrWhiteSpace(_hoverTipText))
            {
                return;
            }

            HideHoverTip();

            _activeHoverTip = new ToolTip
            {
                Content = _hoverTipText,
                Placement = PlacementMode.Mouse
            };
            ToolTipService.SetToolTip(_hoverElement, _activeHoverTip);
            _activeHoverTip.IsOpen = true;
        }

        private void HideHoverTip()
        {
            if (_activeHoverTip != null)
            {
                _activeHoverTip.IsOpen = false;
                _activeHoverTip = null;
            }

            if (_hoverElement != null)
            {
                ToolTipService.SetToolTip(_hoverElement, null);
            }
        }

        /// <summary>根据 TextBlock.Tag 取出对应字段的完整文案</summary>
        private static string? ResolveCellDetailText(FrameworkElement element)
        {
            PlaylistItem? item = FindPlaylistItem(element);
            if (item == null || element.Tag is not string field)
            {
                return null;
            }

            string label;
            string value;
            switch (field)
            {
                case "Title":
                    label = "标题";
                    value = item.Title;
                    break;
                case "Artist":
                    label = "艺术家";
                    value = item.Artist;
                    break;
                case "Album":
                    label = "专辑";
                    value = item.Album;
                    break;
                case "Year":
                    label = "年份";
                    value = item.YearText;
                    break;
                case "Duration":
                    label = "时长";
                    value = item.DurationText;
                    break;
                default:
                    return null;
            }

            return $"{label}：{value}\n文件：{Path.GetFileName(item.FilePath)}";
        }

        /// <summary>从单元格向上找所属的 PlaylistItem（兼容 x:Bind 时 DataContext 为空的情况）</summary>
        private static PlaylistItem? FindPlaylistItem(DependencyObject start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (fe.DataContext is PlaylistItem fromContext)
                    {
                        return fromContext;
                    }

                    if (fe is ListViewItem { Content: PlaylistItem fromContent })
                    {
                        return fromContent;
                    }
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
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
