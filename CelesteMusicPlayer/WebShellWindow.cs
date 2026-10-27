using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace CelesteMusicPlayer
{
    /// <summary>Single web view. The MainWindow owner remains the sole audio session.</summary>
    internal sealed class WebShellWindow : Window
    {
        private const string Origin = "https://celeste.internal";
        // Virtual-host mapped resources bypass WebResourceRequested. Covers use
        // a separate, unmapped host and are served only by this handler.
        private const string CoverOrigin = "https://celeste-cover.internal";
        private readonly MainWindow _owner;
        private readonly WebView2 _web = new();
        private readonly TextBlock _status = new() { Text = "正在加载网页界面…", Margin = new Thickness(30) };
        private readonly DispatcherQueueTimer _timer;
        private readonly INotifyCollectionChanged _library;
        private readonly SemaphoreSlim _coverGate = new(2);
        private readonly Dictionary<string, PlaylistItem> _tracks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlaylistItem> _tagExtraTracks = new(StringComparer.Ordinal);
        private readonly HashSet<string> _metadataSent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _queueCoverPaths = new(StringComparer.Ordinal);
        private bool _restoreMaximizedAfterFullscreen;
        private OverlappedPresenter? _windowedPresenter;
        private bool _closed, _ready, _dirty = true, _sending;
        // 网页标题栏拖动：WebView2 抢走了指针消息，宿主收不到 XAML 指针事件。
        // 优先把 WM_NCLBUTTONDOWN 交给系统跑它自己的模态移动循环（零轮询、最跟手）；
        // 探测到系统没接管时，回落到 SetWindowPos 高频轮询。
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_LBUTTONUP = 0x0202;
        private const int HTCAPTION = 0x0002;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private DispatcherQueueTimer? _dragTimer;
        private DispatcherQueueTimer? _dragProbe;
        private bool _dragging, _dragPolling;
        private int _dragOffsetX, _dragOffsetY;
        private IntPtr _dragHwnd;
        private Windows.Graphics.PointInt32 _dragOrigin;
        private NativeMethods.POINT _dragStartCursor;
        private int _revision;
        private int _themeRevision = -1;
        private CancellationTokenSource? _waveformCancellation;
        private CancellationTokenSource? _overviewCancellation;

        internal WebShellWindow(MainWindow owner)
        {
            _owner = owner;
            Title = "Celeste";
            var root = new Grid(); root.Children.Add(_web); root.Children.Add(_status); Content = root;
            // One caption only: HTML controls, native resize border and WebView2 draggable regions.
            if (AppWindow.Presenter is OverlappedPresenter chrome) chrome.SetBorderAndTitleBar(true, false);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += (_, _) => { if (_ready) { SendState(); SendThemeIfChanged(); if (_dirty) _ = SendLibraryAsync(); } };
            _library = (INotifyCollectionChanged)owner.LibraryTracks;
            _library.CollectionChanged += LibraryChanged;
            NetworkLibraryService.Changed += SourcesChanged;
            AppWindow.Changed += BackgroundWindowChanged;
            root.Loaded += async (_, _) => await InitializeAsync();
            Closed += (_, _) =>
            {
                _closed = true; _ready = false; _timer.Stop();
                StopWindowDrag(); // 拖动轮询必须随窗口一起停，否则关窗后定时器还在跑
                _library.CollectionChanged -= LibraryChanged;
                NetworkLibraryService.Changed -= SourcesChanged;
                AppWindow.Changed -= BackgroundWindowChanged;
                WebAppearance.ClearDraft();
                _waveformCancellation?.Cancel();
                _overviewCancellation?.Cancel();
                _web.Close();
            };
        }

        private void SourcesChanged() => DispatcherQueue.TryEnqueue(() => { _dirty = true; _metadataSent.Clear(); });
        internal void NotifySourceStatus(string text) => Post(new { kind = "celeste.sourceStatus", version = 1, message = text });

        private void LibraryChanged(object? sender, NotifyCollectionChangedEventArgs args) => _dirty = true;

        private void BackgroundWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            SendVisibility();
            SendWindowState();
        }

        private void SendWindowState() => Post(new { kind = "celeste.window", version = 1, fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen });

        private void SendVisibility() => Post(new
        {
            kind = "celeste.visibility", version = 1,
            hidden = !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        });

        /// <summary>播放/收藏等变化后让主页自己刷新，不重发整份曲库。</summary>
        internal void NotifyHomeChanged()
        {
            if (_closed || !_ready) return;
            Post(new { kind = "celeste.homeChanged", version = 1 });
        }

        private async Task InitializeAsync()
        {
            try
            {
                NetworkLibraryStore.MigrateWebDav();
                string folder = Path.Combine(AppContext.BaseDirectory, "WebShell");
                if (!File.Exists(Path.Combine(folder, "index.html"))) throw new FileNotFoundException("网页资源缺失，请使用完整发布目录。", folder);
                await _web.EnsureCoreWebView2Async();
                if (_closed) return;
                var core = _web.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping("celeste.internal", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                // 背景图：用户选的图片先被拷进这个受控目录，再以虚拟主机地址给网页。
                // 直接把本机路径塞进 CSS 是不行的 —— CSP 不许 file://，WebView2 也不会去读。
                core.SetVirtualHostNameToFolderMapping(MainWindow.BackgroundAssetHost, MainWindow.BackgroundFolder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsNonClientRegionSupportEnabled = true;
                core.NavigationStarting += (_, args) => { if (!IsTrusted(args.Uri)) args.Cancel = true; };
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnMessage;
                core.AddWebResourceRequestedFilter(CoverOrigin + "/cover/*", CoreWebView2WebResourceContext.Image);
                core.AddWebResourceRequestedFilter(CoverOrigin + "/playlist/*", CoreWebView2WebResourceContext.Image);
                core.WebResourceRequested += OnCoverRequest;
                core.ProcessFailed += (_, _) => { _ready = false; _status.Text = "界面进程异常，请重新启动 Celeste。"; _status.Visibility = Visibility.Visible; };
                core.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess) { _status.Text = "页面加载失败：" + args.WebErrorStatus; _status.Visibility = Visibility.Visible; }
                };
                core.Navigate(Origin + "/index.html");
                StartupLog.Write("[WebShell] initialized");
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.Initialize", ex); _status.Text = "网页界面初始化失败：" + ex.Message + "\n可关闭此窗口继续使用旧界面。"; }
        }

        private static bool IsTrusted(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Host == "celeste.internal" && uri.IsDefaultPort && uri.AbsolutePath == "/index.html";

        private void Post(object message)
        {
            if (!_closed && _ready) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }

        private void SendState()
        {
            try { Post(new { kind = "celeste.state", version = 1, state = _owner.WebPlaybackState() }); }
            catch (Exception ex) { StartupLog.WriteException("WebShell.State", ex); }
        }

        /// <summary>主题快照：ready 时立刻给一次，避免网页先白一下再跳主题；之后只在修订号变了才重发。</summary>
        private bool ApplyMaterial(Dictionary<string, string> values, string mode)
        {
            try
            {
                int style = values.TryGetValue("material-style", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 0;
                bool transparent = !values.TryGetValue("material-transparency", out var enabled) || enabled != "0";
                bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
                bool glass = style > 0 && transparent && !highContrast;
                if (Content is FrameworkElement root) root.RequestedTheme = mode == "dark" ? ElementTheme.Dark : ElementTheme.Light;
                if (glass)
                {
                    if (SystemBackdrop is not DesktopAcrylicBackdrop) SystemBackdrop = new DesktopAcrylicBackdrop();
                    _web.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                }
                else
                {
                    SystemBackdrop = null;
                    _web.DefaultBackgroundColor = mode == "dark" ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
                }
                return glass;
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.Material", ex); SystemBackdrop = null; return false; }
        }

        private void SendTheme()
        {
            try
            {
                var state = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
                string mode = WebAppearance.ResolveMode(state.mode);
                var light = WebAppearance.Resolve(state, WebAppearance.Light);
                var dark = WebAppearance.Resolve(state, WebAppearance.Dark);
                bool materialActive = ApplyMaterial(mode == "dark" ? dark : light, mode);
                _themeRevision = WebAppearance.CurrentRevision;
                StartupLog.Write($"[WebShell] theme sent preset={state.presetId} mode={mode} accent={state.accentSource} revision={_themeRevision}");
                Post(new
                {
                    kind = "celeste.theme", version = 1, revision = _themeRevision, mode,
                    materialActive, presetId = state.presetId, accentSource = state.accentSource, accentCustom = state.accentCustom,
                    // 底栏按钮的显隐/顺序搭这趟车一起送：网页那边只在修订号变化时才收到，
                    // 不需要为「收一个按钮」单开一条通道
                    playerButtons = state.playerButtons,
                    // 导航构成同理：和主题同一趟车，网页按修订号收
                    navItems = state.navItems,
                    light = new { css = WebAppearance.ToCss(light), values = light },
                    dark = new { css = WebAppearance.ToCss(dark), values = dark }
                });
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.Theme", ex); }
        }

        private void SendThemeIfChanged()
        {
            if (WebAppearance.CurrentRevision == _themeRevision) return;
            SendTheme();
        }

        private async Task SendLibraryAsync()
        {
            if (_sending || _closed || !_ready) return;
            _sending = true; _dirty = false;
            int revision = ++_revision;
            try
            {
                var snapshot = _owner.WebAllTracks();
                _tracks.Clear();
                foreach (var item in snapshot) _tracks[MainWindow.WebTrackId(item)] = item;
                var copies = _tracks.Select(pair => (Id: pair.Key, Item: pair.Value.Clone())).ToArray();
                Post(new { kind = "celeste.library", version = 1, phase = "begin", revision, total = copies.Length });
                for (int offset = 0; offset < copies.Length; offset += 100)
                {
                    var batch = copies.Skip(offset).Take(100).ToArray();
                    var rows = await Task.Run(() => batch.Select(pair => new
                    {
                        id = pair.Id, title = pair.Item.Title, artist = pair.Item.Artist, album = pair.Item.Album,
                        albumArtist = pair.Item.AlbumArtist, year = pair.Item.Year, genre = pair.Item.Genre,
                        track = pair.Item.Track, disc = pair.Item.Disc, sortPath = pair.Item.FilePath,
                        remote = pair.Item.IsRemote, sourceId = pair.Item.SourceId, sourceName = pair.Item.SourceName,
                        duration = Math.Max(0, pair.Item.Duration.TotalSeconds),
                        chips = Array.Empty<string>(),
                        coverUrl = CoverOrigin + "/cover/" + pair.Id
                    }).ToArray());
                    if (_closed || !_ready) return;
                    Post(new { kind = "celeste.library", version = 1, phase = "batch", revision, tracks = rows });
                }
                var albums = MainWindow.WebAlbums(snapshot);
                Post(new { kind = "celeste.library", version = 1, phase = "end", revision, total = copies.Length, albums });
                StartupLog.Write("[WebShell] library sent tracks=" + copies.Length);
            }
            catch (Exception ex) { Error("曲库加载失败：" + ex.Message); StartupLog.WriteException("WebShell.Library", ex); }
            finally { _sending = false; }
        }

        private async void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string? requestId = null;
            try
            {
                if (!IsTrusted(args.Source) || args.WebMessageAsJson.Length > 4_000_000) return;
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                var msg = doc.RootElement;
                if (msg.GetProperty("kind").GetString() != "celeste.command" || msg.GetProperty("version").GetInt32() != 1) return;
                string command = msg.GetProperty("command").GetString() ?? "";
                requestId = msg.TryGetProperty("requestId", out var request) ? request.GetString() : null;
                if (requestId?.Length > 100) return;
                switch (command)
                {
                    case "ready": _ready = true; _metadataSent.Clear(); _status.Visibility = Visibility.Collapsed; _dirty = true; _timer.Start(); SendTheme(); SendVisibility(); _ = SendLibraryAsync(); break;
                    case "trackInfo":
                        var metadataIds = msg.GetProperty("trackIds").EnumerateArray().Take(100).Select(value => value.GetString() ?? "").ToArray();
                        _ = SendTrackInfoAsync(metadataIds); break;
                    case "waveform":
                        _ = SendWaveformAsync(msg.GetProperty("trackId").GetString() ?? ""); break;
                    case "nowPlayingVisual":
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = _owner.WebNowPlayingVisual() });
                        break;
                    case "nowPlaying":
                        // 详情页的歌词：一次取整首，当前句由前端按播放时间算（不逐帧问宿主）
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = _owner.WebNowPlaying() });
                        break;
                    case "playTrack": _owner.WebPlayTrack(Resolve(msg.GetProperty("trackId").GetString())); break;
                    case "playAll":
                        var ids = msg.GetProperty("trackIds");
                        if (ids.GetArrayLength() > 50_000) throw new InvalidOperationException("曲目数量超出本次接口限制。");
                        _owner.WebPlayAll(ids.EnumerateArray().Select(id => Resolve(id.GetString())).Distinct().ToArray()); break;
                    case "togglePlay": _owner.TogglePlayPausePublic(); break;
                    case "play": if (!_owner.IsEnginePlayingNow) _owner.TogglePlayPausePublic(); break;
                    case "next": _owner.NextPublic(); break;
                    case "previous": _owner.PreviousPublic(); break;
                    case "seek": _owner.SeekSourceSeconds(Math.Clamp(Finite(msg, "seconds"), 0, Math.Max(0, _owner.EngineDurationValue.TotalSeconds))); break;
                    case "setVolume": _owner.WebSetVolume(Math.Clamp(Finite(msg, "volume"), 0, 100)); break;
                    case "favorite": _owner.FavoriteCurrentPublic(); break;
                    case "transport": _owner.WebTransportAction(msg.GetProperty("action").GetString() ?? ""); break;
                    case "setPlaybackOrder": _owner.WebSetPlaybackOrder(msg.GetProperty("order").GetString() ?? ""); break;
                    case "audioSettings":
                        var settings = await _owner.WebAudioSettingsAsync(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = settings }); break;
                    case "appSettings":
                        var appSettings = await _owner.WebSettingsAsync(msg, this);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = appSettings }); break;
                    case "sources":
                        var sources = await _owner.WebSourcesAsync(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = sources });
                        break;
                    case "theme":
                        var theme = await _owner.WebAppearanceAsync(msg).ConfigureAwait(true);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = theme });
                        // 外观改完立刻回推一份快照，不等 250ms 的轮询；网页拿到的永远是刚算好的那套。
                        SendTheme(); break;
                    case "collectionOverview":
                        var overview = await ReadOverviewAsync(msg.GetProperty("category").GetString() ?? "", msg.GetProperty("name").GetString() ?? "");
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = overview }); break;
                    case "cancelOverview": _overviewCancellation?.Cancel(); break;
                    case "browse":
                        var browse = await _owner.WebBrowseAsync(msg, this);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = browse }); break;
                    case "home":
                        var home = await _owner.WebHomeAsync(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = home }); break;
                    case "audioChain":
                        var chain = await _owner.WebAudioChainAsync();
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = chain }); break;
                    case "trackAction":
                        var actionResult = await _owner.WebTrackActionAsync(msg, this);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = actionResult }); break;
                    case "folders":
                        var folders = await _owner.WebFoldersAsync(msg, this);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = folders }); break;
                    case "dsp":
                        var dsp = await _owner.WebDspAsync(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = dsp }); break;
                    case "tags":
                        string tagOperation = msg.TryGetProperty("operation", out var tagOp) ? tagOp.GetString() ?? "" : "";
                        object tags;
                        if (tagOperation is "addFiles" or "addFolder" or "changeDirectory")
                        {
                            var added = await _owner.WebTagAddFilesAsync(tagOperation != "addFiles", this);
                            if (!added.Canceled && tagOperation == "changeDirectory") _tagExtraTracks.Clear();
                            foreach (var track in added.Tracks) _tagExtraTracks[MainWindow.WebTrackId(track)] = track;
                            tags = new { count = added.Tracks.Length, ids = added.Tracks.Select(MainWindow.WebTrackId).ToArray(), directory = added.Directory, canceled = added.Canceled };
                        }
                        else
                        {
                            var tagTracks = new Dictionary<string, PlaylistItem>(_tracks, StringComparer.Ordinal);
                            foreach (var pair in _tagExtraTracks) tagTracks[pair.Key] = pair.Value;
                            foreach (var track in _owner.UserPlaylist) tagTracks.TryAdd(MainWindow.WebTrackId(track), track);
                            if (msg.TryGetProperty("scopeIds", out var tagScope) && tagScope.ValueKind == JsonValueKind.Array)
                                foreach (var idElement in tagScope.EnumerateArray().Take(20000))
                                {
                                    string id = idElement.GetString() ?? "";
                                    if (tagTracks.ContainsKey(id)) continue;
                                    string? path = _owner.WebFolderCoverPath(id);
                                    if (path == null || !File.Exists(path)) continue;
                                    var track = _owner.WebTagFromPath(path); _tagExtraTracks[id] = track; tagTracks[id] = track;
                                }
                            tags = await _owner.WebTagsAsync(msg, tagTracks);
                            if (tagOperation == "rename")
                            {
                                var extras = _tagExtraTracks.Values.ToArray(); _tagExtraTracks.Clear();
                                foreach (var track in extras) _tagExtraTracks[MainWindow.WebTrackId(track)] = track;
                                var currentTracks = _tracks.Values.ToArray(); _tracks.Clear();
                                foreach (var track in currentTracks) _tracks[MainWindow.WebTrackId(track)] = track;
                            }
                        }
                        if (tagOperation is "write" or "rename" or "lyricSave" || tagOperation.StartsWith("cover", StringComparison.Ordinal))
                        { _dirty = true; _metadataSent.Clear(); }
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = tags }); break;
                    case "queue":
                        _queueCoverPaths.Clear();
                        foreach (var track in _owner.UserPlaylist) _queueCoverPaths[MainWindow.WebTrackId(track)] = track.FilePath;
                        var queue = _owner.WebQueue(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = queue }); break;
                    case "more":
                        var more = _owner.WebMore(msg);
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = more }); break;
                    case "window": WindowBridge(msg, requestId); break;
                    default: Error("此功能尚未接入当前界面。"); break;
                }
                SendState();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebShell.Command", ex);
                if (requestId != null) Post(new { kind = "celeste.reply", version = 1, requestId, ok = false, error = ex.Message });
                else Error(ex.Message);
            }
        }

        private PlaylistItem Resolve(string? id) => id != null && _tracks.TryGetValue(id, out var item)
            ? item : id != null && MainWindow.ResolveSourceTrack(id) is PlaylistItem remote ? remote : throw new InvalidOperationException("曲目已不在当前曲库中，请稍后重试。");

        private async Task<object> ReadOverviewAsync(string category, string name)
        {
            _overviewCancellation?.Cancel();
            var cancellation = new CancellationTokenSource(); _overviewCancellation = cancellation;
            try { return await _owner.WebCollectionOverviewAsync(category, name, cancellation.Token); }
            finally { if (ReferenceEquals(_overviewCancellation, cancellation)) _overviewCancellation = null; cancellation.Dispose(); }
        }

        private async Task SendWaveformAsync(string id)
        {
            _waveformCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _waveformCancellation = cancellation;
            try
            {
                var peaks = await _owner.WebWaveformAsync(id, cancellation.Token);
                if (!cancellation.IsCancellationRequested) Post(new { kind = "celeste.waveform", version = 1, trackId = id, peaks });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebShell.Waveform", ex);
                if (!cancellation.IsCancellationRequested) Post(new { kind = "celeste.waveform", version = 1, trackId = id, peaks = Array.Empty<float>() });
            }
            finally { if (ReferenceEquals(_waveformCancellation, cancellation)) _waveformCancellation = null; cancellation.Dispose(); }
        }

        private async Task SendTrackInfoAsync(string[] ids)
        {
            foreach (string id in ids)
            {
                if (_closed || !_tracks.TryGetValue(id, out var item) || !_metadataSent.Add(id)) continue;
                await _coverGate.WaitAsync();
                try
                {
                    if (_closed) return;
                    string path = item.FilePath;
                    var chips = await Task.Run(() => AudioInfoFormatter.FormatChips(path).ToArray());
                    Post(new { kind = "celeste.metadata", version = 1, trackId = id, chips });
                }
                catch (Exception ex) { StartupLog.WriteException("WebShell.Metadata", ex); }
                finally { _coverGate.Release(); }
            }
        }

        private static double Finite(JsonElement msg, string key)
        {
            double value = msg.GetProperty(key).GetDouble();
            return double.IsFinite(value) ? value : throw new ArgumentException("无效的数值。");
        }

        // 网页宿主的窗口行为：关闭/最小化/最大化沿用原生；置顶是这个窗口自己的，
        // 和 MainWindow 的「主窗口置顶」不是一个东西 —— 设置页里也是分开两个开关。
        private void WindowBridge(JsonElement msg, string? requestId)
        {
            string action = msg.GetProperty("action").GetString() ?? "";
            if (action == "state")
            {
                SendWindowState();
                bool top = AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop;
                if (requestId != null) Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = new { top, fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen } });
                return;
            }

            bool value = msg.TryGetProperty("value", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            WindowCommand(action, value);
        }

        private void WindowCommand(string? action, bool value)
        {
            // 拖动命令要走在这里前述检查之前：全屏时 presenter 不是 OverlappedPresenter，
            // 此时仍要能收到 drag-end 把轮询停掉。
            if (action == "drag-start") { DispatcherQueue.TryEnqueue(StartWindowDrag); return; }
            if (action == "drag-end") { StopWindowDrag(); return; }
            if (action == "close") { Close(); return; }
            if (action == "fullscreen")
            {
                if (value && AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                {
                    _windowedPresenter = AppWindow.Presenter as OverlappedPresenter;
                    _restoreMaximizedAfterFullscreen = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else if (!value && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                {
                    if (_windowedPresenter != null) AppWindow.SetPresenter(_windowedPresenter);
                    else AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    if (_restoreMaximizedAfterFullscreen && AppWindow.Presenter is OverlappedPresenter restored) restored.Maximize();
                }
                SendWindowState();
                return;
            }
            if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
            if (action == "top") presenter.IsAlwaysOnTop = value;
            else if (action == "minimize") presenter.Minimize();
            else if (action == "maximize") { if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore(); else presenter.Maximize(); }
        }

        /// <summary>网页标题栏空白区按下。</summary>
        private void StartWindowDrag()
        {
            // 最大化/全屏下拖动无意义，也不允许（会让窗口抖出奇怪的位置）
            if (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Maximized) return;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
            // ⚠ 关键修一次伪拖动：从网页 pointerdown 到宿主收到这次 IPC 隔了几十毫秒，
            // 物理按键很可能已经松开了（就是一次普通点击）。此时若照发 WM_NCLBUTTONDOWN，
            // 系统会以为键还按着而进入自己的移动循环 —— 表现就是「点一下，之后不按住也能拖着窗口跑」。
            // 所以一律以**当前的真实按键状态**为准，而不是以消息到达时间为准。
            if (!IsLeftButtonDown()) return;
            _dragHwnd = hwnd;
            _dragOrigin = AppWindow.Position;
            _dragStartCursor = cursor;
            _dragOffsetX = cursor.X - _dragOrigin.X;
            _dragOffsetY = cursor.Y - _dragOrigin.Y;
            _dragging = true;
            // 同步进入 Windows 移动循环，返回时拖动已结束；不再让探针/轮询争夺窗口位置。
            try { TryNativeMoveLoop(cursor); }
            finally { StopWindowDrag(); }
        }

        private static bool IsLeftButtonDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

        /// <summary>让 Windows 跑自己的模态移动循环：不轮询、UI 线程零负担，跟手程度和系统窗口一致。</summary>
        private bool TryNativeMoveLoop(NativeMethods.POINT cursor)
        {
            try
            {
                if (_dragHwnd == IntPtr.Zero) return false;
                // 再查一次：查到发这条消息之间仍有极小的时间窗，宁可不拖也不要拖成"粘住"
                if (!IsLeftButtonDown()) return false;
                // 光标可能被 WebView2 捕获着，不释放系统进不了自己的移动循环
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(_dragHwnd, WM_NCLBUTTONDOWN, HTCAPTION, (IntPtr)((cursor.Y << 16) | (cursor.X & 0xFFFF)));
                return true;
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.NativeMove", ex); return false; }
        }

        private void DragProbeTick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                if (!_dragging) { StopWindowDrag(); return; }
                if (!IsLeftButtonDown())
                {
                    // 自愈：万一上面那次检查之后还是晚了一步、系统进了一个没人按住的移动循环，
                    // 窗口会一直占着鼠标捕获不放。补一个 WM_LBUTTONUP 让它自己把循环收掉。
                    if (_dragHwnd != IntPtr.Zero && NativeMethods.GetCapture() == _dragHwnd)
                    {
                        NativeMethods.PostMessage(_dragHwnd, WM_LBUTTONUP, 0, IntPtr.Zero);
                        StartupLog.Write("[WebShell] 拖动：结束了一次没人按住的残留移动循环");
                    }
                    StopWindowDrag();
                    return;
                }
                if (_dragPolling) { StopProbe(); return; }
                if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
                // 光标还没动就判断不出谁在管窗口，继续探下一轮
                if (Math.Abs(cursor.X - _dragStartCursor.X) <= 4 && Math.Abs(cursor.Y - _dragStartCursor.Y) <= 4) return;
                Windows.Graphics.PointInt32 now = AppWindow.Position;
                StopProbe();
                // 窗口跟着动了 = 系统已接管；否则说明本机不吃这条消息，切轮询兜底
                if (now.X == _dragOrigin.X && now.Y == _dragOrigin.Y) StartDragPolling();
            }
            catch (Exception ex) { StopProbe(); StartupLog.WriteException("WebShell.DragProbe", ex); }
        }

        private void StartDragPolling()
        {
            if (_dragPolling) return;
            _dragPolling = true;
            _dragTimer ??= DispatcherQueue.CreateTimer();
            _dragTimer.Interval = TimeSpan.FromMilliseconds(8);
            _dragTimer.Tick -= DragTick;
            _dragTimer.Tick += DragTick;
            _dragTimer.Start();
        }

        private void StopProbe()
        {
            if (_dragProbe != null) { _dragProbe.Stop(); _dragProbe.Tick -= DragProbeTick; }
        }

        private void DragTick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                if (_dragHwnd == IntPtr.Zero || !IsLeftButtonDown()) { StopWindowDrag(); return; }
                if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
                // 兜底路径也必须直接 SetWindowPos：AppWindow.Move() 每次都要绕一圈
                // XAML→AppWindow→窗口服务，拖动时每帧的开销肉眼可见地拖后手。
                NativeMethods.SetWindowPos(_dragHwnd, IntPtr.Zero, cursor.X - _dragOffsetX, cursor.Y - _dragOffsetY, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                StopWindowDrag();
                StartupLog.WriteException("WebShell.Drag", ex);
            }
        }

        private void StopWindowDrag()
        {
            _dragging = false;
            _dragPolling = false;
            StopProbe();
            if (_dragTimer != null) { _dragTimer.Stop(); _dragTimer.Tick -= DragTick; }
        }

        private void Error(string message) => Post(new { kind = "celeste.error", version = 1, message });

        private async void OnCoverRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            bool entered = false;
            try
            {
                var uri = new Uri(args.Request.Uri);
                int coverSize = uri.Query == "?size=768" ? 768 : uri.Query == "?size=480" ? 480 : 160;
                bool playlistCover = uri.AbsolutePath.StartsWith("/playlist/", StringComparison.Ordinal);
                string id = uri.AbsolutePath.Substring(playlistCover ? "/playlist/".Length : "/cover/".Length);
                var current = _owner.GetCurrentPlayingItem();
                string? customCover = null;
                string? path;
                if (playlistCover)
                {
                    string name = Uri.UnescapeDataString(id);
                    if (!NamedPlaylistStore.List().Contains(name)) { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                    customCover = PlaylistLibraryService.CustomCoverPath(name);
                    path = customCover ?? PlaylistLibraryService.FirstExistingTrack(name);
                    coverSize = 480;
                }
                else path = _tracks.TryGetValue(id, out var item) ? item.FilePath
                    : current != null && MainWindow.WebTrackId(current) == id ? current.FilePath
                    : _queueCoverPaths.TryGetValue(id, out var queuePath) ? queuePath : _owner.WebFolderCoverPath(id);
                if (!playlistCover && NetworkLibraryStore.Track(id) is NetworkTrack remoteTrack && remoteTrack.SourceId.Length > 0 && (path == null || !File.Exists(path)))
                {
                    await _coverGate.WaitAsync(); entered = true;
                    var source = NetworkLibraryStore.Source(remoteTrack.SourceId);
                    string coverFile = Path.Combine(NetworkLibraryService.CacheRoot, source.Id, NetworkIdentity.Hash(remoteTrack.Entry.Key) + ".cover");
                    byte[]? coverBytes = File.Exists(coverFile) ? await File.ReadAllBytesAsync(coverFile) : null;
                    if (coverBytes == null && source.Enabled)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        coverBytes = await NetworkSourceProvider.For(source).CoverAsync(source, remoteTrack.Entry, timeout.Token);
                        if (coverBytes != null) { Directory.CreateDirectory(Path.GetDirectoryName(coverFile)!); await File.WriteAllBytesAsync(coverFile, coverBytes); }
                    }
                    args.Response = sender.Environment.CreateWebResourceResponse(coverBytes == null ? null : new MemoryStream(coverBytes).AsRandomAccessStream(), coverBytes == null ? 404 : 200, coverBytes == null ? "Not Found" : "OK", "Content-Type: image/jpeg\r\nCache-Control: private, max-age=60");
                    return;
                }
                if (path == null) { args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                await _coverGate.WaitAsync(); entered = true;
                if (_closed) return;
                byte[]? bytes = await Task.Run(() =>
                {
                    var source = customCover == null ? MainWindow.ExtractCoverBytes(path) : File.ReadAllBytes(path);
                    if (source == null || source.Length > 20_000_000) return null;
                    using var stream = new MemoryStream(source);
                    using var image = System.Drawing.Image.FromStream(stream);
                    using var thumbnail = new System.Drawing.Bitmap(image, coverSize, coverSize);
                    using var output = new MemoryStream(); thumbnail.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
                    return output.ToArray();
                });
                if (_closed) return;
                args.Response = sender.Environment.CreateWebResourceResponse(bytes == null ? null : new MemoryStream(bytes).AsRandomAccessStream(), bytes == null ? 404 : 200, bytes == null ? "Not Found" : "OK", "Content-Type: image/jpeg\r\nCache-Control: private, max-age=3600");
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.Cover", ex); if (!_closed) args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); }
            finally { if (entered) _coverGate.Release(); deferral.Complete(); }
        }

        /// <summary>拖动要用到的两个 Win32 调用。指针被 WebView2 吃掉，只能直接问系统光标位置。</summary>
        private static class NativeMethods
        {
            internal const int VK_LBUTTON = 0x01;

            [StructLayout(LayoutKind.Sequential)]
            internal struct POINT { public int X; public int Y; }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            internal static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            internal static extern IntPtr GetCapture();

            [DllImport("user32.dll", EntryPoint = "SendMessageW")]
            internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        }
    }
}
