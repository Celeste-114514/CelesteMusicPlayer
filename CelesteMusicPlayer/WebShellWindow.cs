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
        private readonly TextBlock _status = new()
        {
            Text = "正在加载网页界面…",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560
        };
        // 界面起不来时的唯一出路：不重启程序，把网页重新载一轮。
        // 原生兜底之所以还需要按钮 —— 白屏的时候网页自己的恢复页根本画不出来。
        private readonly Button _reload = new()
        {
            Content = "重新载入界面",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        private readonly StackPanel _statusLayer = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
            MaxWidth = 600,
            Margin = new Thickness(30)
        };
        private readonly DispatcherQueueTimer _timer;
        private readonly INotifyCollectionChanged _library;
        private readonly SemaphoreSlim _coverGate = new(2);
        private readonly Dictionary<string, PlaylistItem> _tracks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlaylistItem> _tagExtraTracks = new(StringComparer.Ordinal);
        private readonly HashSet<string> _metadataSent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _queueCoverPaths = new(StringComparer.Ordinal);
        private bool _restoreMaximizedAfterFullscreen;
        /// <summary>网页有没有订阅立体声采样（还要叠加"窗口看得见"才真的开）。</summary>
        private bool _stereoSamplingWanted;
        private OverlappedPresenter? _windowedPresenter;
        private bool _closed, _ready, _dirty = true, _sending;
        /// <summary>本次运行页面崩溃过几次（崩溃卡上要报"这是第几次"，也是自动重载的节流依据）。</summary>
        private int _pageCrashCount;
        /// <summary>崩过一次并已自动重载：等下一次 ready 补一张说明卡。</summary>
        private bool _pendingCrashNotice;
        /// <summary>上次自动重载的时间，用来防止"崩→重载→立刻又崩"变成死循环刷屏。</summary>
        private DateTime _lastCrashReloadUtc = DateTime.MinValue;
        /// <summary>启动加载走完（曲库恢复到可以用了）：这时候才该告诉用户"上次没正常退出"。</summary>
        private bool _startupSettled;
        /// <summary>那条提示已经决定推过（推或判定不该推），别因为重载/re-ready 再推一次。</summary>
        private bool _startupNoticeQueued;
        /// <summary>自测开关只触发一次。</summary>
        private bool _crashDemoFired;
        // Drag sampling must not share the UI dispatcher with library/theme refreshes.
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private int _dragGeneration;
        private int _revision;
        private int _themeRevision = -1;
        private CancellationTokenSource? _waveformCancellation;
        private CancellationTokenSource? _overviewCancellation;

        internal WebShellWindow(MainWindow owner)
        {
            _owner = owner;
            Title = "Celeste";
            _statusLayer.Children.Add(_status);
            _statusLayer.Children.Add(_reload);
            _reload.Click += (_, _) => ReloadShell();
            var root = new Grid(); root.Children.Add(_web); root.Children.Add(_statusLayer); Content = root;
            // One caption only: HTML controls, native resize border and WebView2 draggable regions.
            if (AppWindow.Presenter is OverlappedPresenter chrome) chrome.SetBorderAndTitleBar(true, true);
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
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
                // 关窗必须把立体声采样退订掉，否则引擎那边还留着采集抽头
                _owner.SetStereoSampling("web.nowPlaying", false);
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

        /// <summary>更新进度/结论。网页的关于卡片靠这条消息画进度条，不靠轮询。</summary>
        internal void NotifyUpdateStatus(object status) => Post(new { kind = "celeste.update", version = 1, status });

        private void LibraryChanged(object? sender, NotifyCollectionChangedEventArgs args) => _dirty = true;

        private void BackgroundWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            // Position changes during a drag do not alter visibility or fullscreen state.
            // Avoid two WebView messages and DOM updates for every native move.
            if (args.DidVisibilityChange || args.DidPresenterChange) { SendVisibility(); SyncStereoSampling(); }
            if (args.DidPresenterChange) SendWindowState();
        }

        private void SendWindowState() => Post(new { kind = "celeste.window", version = 1, fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen });

        private void SendVisibility() => Post(new
        {
            kind = "celeste.visibility", version = 1,
            hidden = !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        });

        /// <summary>窗口看不见时（隐藏/最小化）采样没有意义，也不该继续让引擎留着采集。</summary>
        private bool HiddenForSampling
            => !AppWindow.IsVisible || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

        private void SyncStereoSampling()
            => _owner.SetStereoSampling("web.nowPlaying", _stereoSamplingWanted && !HiddenForSampling);

        /// <summary>播放/收藏等变化后让主页自己刷新，不重发整份曲库。</summary>
        internal void NotifyHomeChanged()
        {
            if (_closed || !_ready) return;
            Post(new { kind = "celeste.homeChanged", version = 1 });
        }

        private string? _pendingPage;

        /// <summary>
        /// 本窗口 WebView2 的环境句柄。托盘菜单复用它，就不必再起一整套浏览器进程
        /// （独立环境要额外几百 MB 和好几秒），右键菜单才可能做到"秒开"。
        /// </summary>
        internal CoreWebView2Environment? WebViewEnvironment { get; private set; }

        /// <summary>
        /// 从宿主侧要求网页跳页（托盘菜单的「设置」「发现新版本」用）。
        /// 网页还没 ready 时先记下来，等 ready 那一刻再补发 —— 否则启动初期点托盘会静默无效。
        /// </summary>
        internal void NavigateTo(string page)
        {
            if (string.IsNullOrEmpty(page) || _closed) return;
            if (!_ready) { _pendingPage = page; return; }
            Post(new { kind = "celeste.navigate", version = 1, page });
        }

        private void FlushPendingPage()
        {
            if (_pendingPage is not { Length: > 0 } page) return;
            _pendingPage = null;
            Post(new { kind = "celeste.navigate", version = 1, page });
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
                try { WebViewEnvironment = core.Environment; } catch (Exception ex) { StartupLog.WriteException("WebShell.Environment", ex); }
                core.SetVirtualHostNameToFolderMapping("celeste.internal", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                // 背景图：用户选的图片先被拷进这个受控目录，再以虚拟主机地址给网页。
                // 直接把本机路径塞进 CSS 是不行的 —— CSP 不许 file://，WebView2 也不会去读。
                core.SetVirtualHostNameToFolderMapping(MainWindow.BackgroundAssetHost, MainWindow.BackgroundFolder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsNonClientRegionSupportEnabled = true;
                core.NavigationStarting += (_, args) => { if (!IsTrusted(args.Uri)) args.Cancel = true; };
                // 网页里的外链一律由宿主代开：只放行 https 官方站点（ExternalLink 里判），
                // 剩下的一律吞掉，避免导航把整个外壳页面顶掉。
                core.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    try { ExternalLink.Open(args.Uri); }
                    catch (Exception ex) { StartupLog.Write("[WebShell] 拒绝打开外部链接 " + args.Uri + "：" + ex.Message); }
                };
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnMessage;
                core.AddWebResourceRequestedFilter(CoverOrigin + "/cover/*", CoreWebView2WebResourceContext.Image);
                core.AddWebResourceRequestedFilter(CoverOrigin + "/playlist/*", CoreWebView2WebResourceContext.Image);
                core.WebResourceRequested += OnCoverRequest;
                core.ProcessFailed += OnWebProcessFailed;
                core.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess) { _status.Text = "页面加载失败：" + args.WebErrorStatus; _status.Visibility = Visibility.Visible; _reload.Visibility = Visibility.Visible; }
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
                    case "ready": _ready = true; _metadataSent.Clear(); _status.Visibility = Visibility.Collapsed; _reload.Visibility = Visibility.Collapsed; _dirty = true; _timer.Start(); SendTheme(); SendVisibility(); _ = SendLibraryAsync(); FlushPendingPage();
                        // 刚才是崩了才重载的话，界面回来了再补一张卡片说明发生了什么。
                        // 注意：这不是「上次异常退出」的提示 —— 那条要等启动加载走完才推（NotifyStartupSettled）。
                        if (_pendingCrashNotice) { _pendingCrashNotice = false; _ = PushCrashNoticeAsync(); }
                        else _ = TryPushStartupNoticeAsync();
                        // 自测开关（见 RunCrashDemoAsync）：只认环境变量，默认完全无影响。
                        if (!_crashDemoFired && Environment.GetEnvironmentVariable("CELESTE_CRASH_DEMO") == "1")
                        {
                            _crashDemoFired = true;
                            _ = RunCrashDemoAsync();
                        }
                        break;
                    case "trackInfo":
                        var metadataIds = msg.GetProperty("trackIds").EnumerateArray().Take(100).Select(value => value.GetString() ?? "").ToArray();
                        _ = SendTrackInfoAsync(metadataIds); break;
                    case "waveform":
                        _ = SendWaveformAsync(msg.GetProperty("trackId").GetString() ?? ""); break;
                    case "nowPlayingVisual":
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = _owner.WebNowPlayingVisual() });
                        break;
                    case "stereoSampling":
                        // 网页订阅/退订立体声采样。详情页打开来要、关掉就还回去，
                        // 宿主这边是引用计数，别的消费者还在看不会被一起关掉。
                        bool wanted = msg.TryGetProperty("enabled", out JsonElement enabledFlag)
                            && enabledFlag.ValueKind == JsonValueKind.True;
                        _stereoSamplingWanted = wanted;
                        _owner.SetStereoSampling("web.nowPlaying", wanted && !HiddenForSampling);
                        if (requestId != null) Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = new { enabled = wanted } });
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
                    case "audioReadout":
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = _owner.WebAudioReadout() }); break;
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
                    case "openExternal":
                        string externalUrl = msg.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
                        Uri openedUri = ExternalLink.Open(externalUrl);
                        if (requestId != null)
                        {
                            Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = new { url = openedUri.AbsoluteUri } });
                        }
                        break;
                    case "diagnostics":
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = await _owner.WebDiagnosticsAsync(msg) }); break;
                    case "recovery":
                        Post(new { kind = "celeste.reply", version = 1, requestId, ok = true, data = await _owner.WebRecoveryAsync(msg) }); break;
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

            if (action == "caption-regions")
            {
                var regions = new List<Windows.Graphics.RectInt32>();
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                    foreach (var rect in msg.GetProperty("rects").EnumerateArray().Take(64))
                        regions.Add(new Windows.Graphics.RectInt32(
                            (int)Finite(rect, "x"), (int)Finite(rect, "y"),
                            Math.Max(0, (int)Finite(rect, "width")), Math.Max(0, (int)Finite(rect, "height"))));
                Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                    .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, regions.ToArray());
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

        /// <summary>Track the physical cursor independently of WebView2/UI dispatcher traffic.</summary>
        private void StartWindowDrag()
        {
            if (_closed || AppWindow.Presenter is not OverlappedPresenter presenter ||
                presenter.State == OverlappedPresenterState.Maximized || !IsLeftButtonDown()) return;
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !NativeMethods.GetCursorPos(out var cursor) ||
                !NativeMethods.GetWindowRect(hwnd, out var bounds)) return;
            int offsetX = cursor.X - bounds.Left, offsetY = cursor.Y - bounds.Top;
            int ticket = Interlocked.Increment(ref _dragGeneration);
            new Thread(() =>
            {
                // Scoped timer resolution: no permanent system/process power setting changes.
                bool preciseTimer = NativeMethods.timeBeginPeriod(1) == 0;
                try
                {
                    int lastX = bounds.Left, lastY = bounds.Top;
                    while (Volatile.Read(ref _dragGeneration) == ticket && IsLeftButtonDown())
                    {
                        if (!NativeMethods.GetCursorPos(out var point)) break;
                        int x = point.X - offsetX, y = point.Y - offsetY;
                        if (x != lastX || y != lastY)
                        {
                            // Synchronous call applies backpressure rather than queueing stale moves.
                            if (!NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE)) break;
                            lastX = x; lastY = y;
                        }
                        Thread.Sleep(4);
                    }
                }
                finally { if (preciseTimer) NativeMethods.timeEndPeriod(1); }
            }) { IsBackground = true, Name = "Celeste window drag" }.Start();
        }

        private static bool IsLeftButtonDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        private void StopWindowDrag() => Interlocked.Increment(ref _dragGeneration);
        private void Error(string message) => Post(new { kind = "celeste.error", version = 1, message });

        /// <summary>
        /// 重新加载网页界面。给"重载界面"按钮（网页恢复页与原生兜底都会用到）和出错后的自救。
        /// 不会重启程序本体 —— 只让 WebView2 把 index.html 重新载一轮。
        /// </summary>
        internal void ReloadShell()
        {
            try
            {
                _ready = false;
                _status.Text = "正在重新加载界面…";
                _status.Visibility = Visibility.Visible;
                _reload.Visibility = Visibility.Collapsed;
                _web.Reload();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebShell.Reload", ex);
                _status.Text = "重新加载失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 网页进程崩溃（用户在用的时候界面突然没了）。能自动救的先救一次，
        /// 救不回来才退回原生文字层 —— 渲染进程都死了，网页自己也画不出恢复页。
        /// </summary>
        private void OnWebProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            string kind = args.ProcessFailedKind.ToString();
            _pageCrashCount++;
            _ready = false;
            StartupLog.Write("[crash] 网页进程异常 kind=" + kind + " 第 " + _pageCrashCount + " 次");
            MainWindow.RecordPageCrash(kind, _pageCrashCount);

            // 浏览器进程整个没了：重载也没意义，只能靠原生文字层。
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _status.Text = "界面进程退出了（可能是内存不足或显卡驱动异常）。\n点下面重新载入，或关掉窗口重新打开。";
                _status.Visibility = Visibility.Visible;
                _reload.Visibility = Visibility.Visible;
                return;
            }

            // 渲染进程崩了但外壳还活着：自动重载一次，绝大多数情况就回来了。
            // 20 秒内刚重载过又崩，说明不是偶发 —— 别再自动重载，免得"崩→载→崩"刷屏。
            if ((DateTime.UtcNow - _lastCrashReloadUtc).TotalSeconds < 20)
            {
                StartupLog.Write("[crash] 20 秒内重复崩溃，停止自动重载");
                _status.Text = "界面反复崩溃（20 秒内两次）。可以点下面重新载入，或用安全模式再试。";
                _status.Visibility = Visibility.Visible;
                _reload.Visibility = Visibility.Visible;
                return;
            }

            _lastCrashReloadUtc = DateTime.UtcNow;
            _pendingCrashNotice = true;
            ReloadShell();
        }

        /// <summary>重载回来后补一张卡：说清刚才崩了、已经替你重载，并给后续选项。</summary>
        private async Task PushCrashNoticeAsync()
        {
            try
            {
                object? payload = MainWindow.BuildCrashPush(_pageCrashCount);
                if (payload == null) return;
                // 等首屏铺完再压卡片，否则会看到"卡片先到、界面后到"的闪一下。
                await Task.Delay(700).ConfigureAwait(true);
                if (_closed || !_ready) return;
                StartupLog.Write("[crash] 已推送崩溃卡 #" + _pageCrashCount);
                Post(new { kind = "celeste.recovery", version = 1, data = payload });
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.PushCrashNotice", ex); }
        }

        /// <summary>
        /// 自测用：设了环境变量 CELESTE_CRASH_DEMO=1，就在进主界面 5 秒后把
        /// "崩溃 → 自动重载 → 弹崩溃卡"这条自救路径走一遍（不真的弄崩进程），
        /// 方便肉眼验收卡片长什么样。不设变量时这个方法根本不会被调到。
        /// </summary>
        private async Task RunCrashDemoAsync()
        {
            await Task.Delay(5000).ConfigureAwait(true);
            if (_closed) return;
            _pageCrashCount++;
            _pendingCrashNotice = true;
            StartupLog.Write("[crash] 自测触发（CELESTE_CRASH_DEMO=1）：模拟一次页面崩溃");
            ReloadShell();
        }

        /// <summary>
        /// 启动加载走完（曲库恢复到能用了）。"上次没正常退出"的提示攒到这里才推 ——
        /// 用户要的是"加载动画走完、进了主界面"之后再看到它，而不是刚进来就被拦住。
        /// </summary>
        internal void NotifyStartupSettled()
        {
            _startupSettled = true;
            _ = TryPushStartupNoticeAsync();
        }

        private async Task TryPushStartupNoticeAsync()
        {
            if (_startupNoticeQueued || !_ready || !_startupSettled || _closed) return;
            _startupNoticeQueued = true;
            try
            {
                object? payload = MainWindow.BuildUncleanPush();
                if (payload == null) return;
                await Task.Delay(500).ConfigureAwait(true);
                if (_closed || !_ready) return;
                StartupLog.Write("[recovery] 已推送「上次没正常退出」提示");
                Post(new { kind = "celeste.recovery", version = 1, data = payload });
            }
            catch (Exception ex) { StartupLog.WriteException("WebShell.PushStartupNotice", ex); }
        }

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
            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT { public int Left, Top, Right, Bottom; }
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
            [DllImport("winmm.dll")]
            internal static extern uint timeBeginPeriod(uint period);
            [DllImport("winmm.dll")]
            internal static extern uint timeEndPeriod(uint period);

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
