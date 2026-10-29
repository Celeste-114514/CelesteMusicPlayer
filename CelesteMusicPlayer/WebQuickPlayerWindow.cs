using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using NAudio.CoreAudioApi;

namespace CelesteMusicPlayer;
internal sealed class WebQuickPlayerWindow : Window
{
    private readonly Grid _root = new();
    private readonly WebView2 _web = new();
    internal readonly QuickPlaybackSession Session;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _ready, _closed, _visible = true, _spectrumWanted = true;
    private long _assetGeneration;
    private byte[]? _cover;
    private string _themeKey = "";
    private readonly Windows.Media.Playback.MediaPlayer _mediaHost = new();
    private readonly Windows.Media.SystemMediaTransportControls _smtc;
    private string _lastMediaTitle = "";
    internal WebQuickPlayerWindow(Action beforePlay)
    {
        Title = "Celeste · 零散播放"; WindowIconHelper.Apply(this);
        HiFiOutputBackend.UiDispatcherQueue = DispatcherQueue;
        Session = new QuickPlaybackSession(a => DispatcherQueue.TryEnqueue(() => a()));
        Session.BeforePlay = () => { beforePlay(); _smtc.IsEnabled = true; };
        Session.Changed += Publish;
        Session.TrackChanged += LoadAssets;
        _mediaHost.CommandManager.IsEnabled = false; _smtc = _mediaHost.SystemMediaTransportControls;
        _smtc.IsPlayEnabled = true; _smtc.IsPauseEnabled = true; _smtc.IsNextEnabled = true; _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += MediaButton;
        _smtc.PlaybackPositionChangeRequested += MediaSeek;
        _root.Children.Add(_web); Content = _root;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(66);
        int ticks = 0;
        _timer.Tick += (_, _) => { if (_visible && ++ticks % 3 == 0) Publish(); if (_visible && _spectrumWanted) SendSpectrum(); };
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1000, 740));
        RestoreBounds();
        _root.Loaded += Initialize;
        Closed += (_, _) =>
        {
            StartupLog.Write("[quick.audit] close; libraryConnections=" + LibraryDb.OpenConnectionCount);
            SaveBounds(); _closed = true; ++_assetGeneration; _timer.Stop(); Session.Dispose();
            _smtc.ButtonPressed -= MediaButton; _smtc.PlaybackPositionChangeRequested -= MediaSeek;
            _smtc.IsEnabled = false; _mediaHost.Dispose(); _web.Close();
        };
        StartupLog.Write("[quick.audit] independent window created; libraryConnections=" + LibraryDb.OpenConnectionCount);
    }
    internal void PauseForMain() { if (Session.Engine.IsPlaying || Session.Busy) Session.Pause(); _smtc.IsEnabled = false; }
    private void MediaButton(Windows.Media.SystemMediaTransportControls sender, Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs e)
        => DispatcherQueue.TryEnqueue(async () => { try { switch (e.Button) { case Windows.Media.SystemMediaTransportControlsButton.Play: if (!Session.Engine.IsPlaying) await Session.ToggleAsync(); break; case Windows.Media.SystemMediaTransportControlsButton.Pause: Session.Pause(); break; case Windows.Media.SystemMediaTransportControlsButton.Next: await Session.StepAsync(1); break; case Windows.Media.SystemMediaTransportControlsButton.Previous: await Session.StepAsync(-1); break; } } catch (Exception ex) { Error(ex); } });
    private void MediaSeek(Windows.Media.SystemMediaTransportControls sender, Windows.Media.PlaybackPositionChangeRequestedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => { if (!Session.Busy) Session.Engine.Seek(e.RequestedPlaybackPosition); });
    internal async Task EnqueueAsync(string[] paths, bool replace = false, bool play = true)
    { if (paths.Length == 0) return; try { await Session.AddAsync(paths, replace, play); } catch (Exception ex) { Error(ex); } }
    private async void Initialize(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= Initialize;
        try
        {
            await _web.EnsureCoreWebView2Async(); if (_closed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("celeste-quick.internal", Path.Combine(AppContext.BaseDirectory, "WebShell"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://celeste-quick.internal/", StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("https://celeste-quick-cover.internal/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var bytes = _cover;
                e.Response = core.Environment.CreateWebResourceResponse(bytes == null ? null : new MemoryStream(bytes).AsRandomAccessStream(), bytes == null ? 404 : 200, bytes == null ? "Not Found" : "OK", (bytes != null && bytes.Length > 4 && bytes[0] == 137 && bytes[1] == 80 ? "Content-Type: image/png" : "Content-Type: image/jpeg") + "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://celeste-quick.internal");
            };
            core.WebMessageReceived += Command;
            _web.Source = new Uri("https://celeste-quick.internal/quick-player.html");
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Command(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || !e.Source.StartsWith("https://celeste-quick.internal/", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson); var m = doc.RootElement;
            double Value() => m.TryGetProperty("value", out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw new ArgumentException("无效的操作值");
            switch (m.GetProperty("command").GetString())
            {
                case "ready": _ready = true; _timer.Start(); Publish(); LoadAssets(); SendDevices(); break;
                case "visible": _visible = m.GetProperty("value").GetBoolean(); break;
                case "spectrum": _spectrumWanted = m.GetProperty("value").GetBoolean(); if (!_spectrumWanted) Post(new { kind = "spectrum", available = false, bands = Array.Empty<float>() }); break;
                case "play": await Session.ToggleAsync(); break;
                case "stop": Session.Stop(); break;
                case "previous": await Session.StepAsync(-1); break;
                case "next": await Session.StepAsync(1); break;
                case "seek": if (!Session.Busy) Session.Engine.Seek(TimeSpan.FromSeconds(Math.Clamp(Value(), 0, Session.Engine.Duration.TotalSeconds))); break;
                case "volume": Session.SetVolume(Value()); Publish(); break;
                case "queue": await Session.PlayAsync((int)Value()); break;
                case "order": Session.Order = m.GetProperty("value").GetString() is "shuffle" ? "shuffle" : m.GetProperty("value").GetString() is "single" ? "single" : m.GetProperty("value").GetString() is "sequential" ? "sequential" : "list"; Publish(); break;
                case "open": await PickFiles(false); break;
                case "append": await PickFiles(true); break;
                case "drop": await EnqueueAsync(e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToArray(), false, true); break;
                case "clear": Session.Stop(); Session.Queue.Clear(); Publish(); LoadAssets(); break;
                case "export": await ExportQueue(); break;
                case "close": Close(); break;
                case "minimize": if (AppWindow.Presenter is OverlappedPresenter min) min.Minimize(); break;
                case "pin": if (AppWindow.Presenter is OverlappedPresenter pin) { pin.IsAlwaysOnTop = !pin.IsAlwaysOnTop; Publish(); } break;
                case "device":
                    string id = m.GetProperty("id").GetString() ?? "";
                    string mode = m.GetProperty("mode").GetString() ?? "Shared";
                    Session.Stop(); Session.Engine.SetOutputMode(mode == "WasapiExclusive" ? HiFiOutputBackend.OutputMode.WasapiExclusive : HiFiOutputBackend.OutputMode.WasapiShared);
                    Session.Engine.SetOutputDevicePreference(id.Length > 0 ? id : null);
                    if (Session.Index >= 0) await Session.PlayAsync(Session.Index);
                    break;
            }
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task PickFiles(bool append)
    {
        var p = new FileOpenPicker(); foreach (var ext in QuickPlayLauncher.PlayableExtensions) p.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await p.PickMultipleFilesAsync(); if (files.Count > 0) await EnqueueAsync(files.Select(f => f.Path).ToArray(), !append, !append);
    }
    private async Task ExportQueue()
    {
        var p = new FileSavePicker(); p.FileTypeChoices.Add("临时播放队列", new[] { ".m3u8" }); p.SuggestedFileName = "Celeste-临时队列";
        WinRT.Interop.InitializeWithWindow.Initialize(p, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var f = await p.PickSaveFileAsync();
        if (f != null) await File.WriteAllLinesAsync(f.Path, new[] { "#EXTM3U" }.Concat(Session.Queue.SelectMany(q => q.IsCueSegment ? new[] { "#EXTINF:" + q.Duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + q.Title, "#CELESTE-CUE-START:" + q.StartTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), q.FilePath } : new[] { q.FilePath })));
    }
    private void SendDevices()
    {
        using var e = new MMDeviceEnumerator(); var devices = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try { Post(new { kind = "devices", devices = devices.Select(d => new { id = d.ID, name = d.FriendlyName }).ToArray() }); }
        finally { foreach (var device in devices) device.Dispose(); }
    }
    private async void LoadAssets()
    {
        var item = Session.Current; long generation = ++_assetGeneration; _cover = null;
        Post(new { kind = "assets", cover = "", lines = Array.Empty<object>(), offset = 0 });
        if (item == null || _closed) return;
        try
        {
            var data = await Task.Run(() =>
            {
                byte[]? cover = null;
                try { using var f = TagLib.File.Create(item.FilePath); cover = f.Tag.Pictures.FirstOrDefault()?.Data.Data; } catch { }
                if (cover == null)
                {
                    var dir = Path.GetDirectoryName(item.FilePath) ?? "";
                    var path = new[] { "cover.jpg", "folder.jpg", "cover.png", "folder.png" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                    if (path != null && new FileInfo(path).Length < 16 * 1024 * 1024) cover = File.ReadAllBytes(path);
                }
                return (Cover: cover, Lines: LyricsLoader.LoadForAudio(item.FilePath));
            });
            if (_closed || generation != _assetGeneration) return;
            _cover = data.Cover;
            // Sidecar CUE lyrics use source-file timestamps; UI clock is relative to this segment.
            Post(new { kind = "assets", cover = _cover == null ? "" : "https://celeste-quick-cover.internal/cover?v=" + generation,
                offset = item.StartTimeSeconds, offsetMs = AppSettingsStore.Load().LyricOffsetMs, lines = data.Lines.Select(l => new { t = l.Time.TotalSeconds, text = l.Text, tr = l.IsTranslation, romanization = l.Romanization }).ToArray() });
        }
        catch (Exception ex) { Error(ex); }
    }
    private void SendSpectrum()
    {
        var bands = new float[96]; bool available = Session.Engine.IsPlaying && Session.Engine.TryGetSpectrum(bands);
        Post(new { kind = "spectrum", available, bands = bands.Select(v => available && float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0).ToArray() });
    }
    private void Publish()
    {
        if (_closed) return;
        var e = Session.Engine; var item = Session.Current;
        _smtc.PlaybackStatus = e.IsPlaying ? Windows.Media.MediaPlaybackStatus.Playing : Windows.Media.MediaPlaybackStatus.Paused;
        if (_lastMediaTitle != item?.Title) { _lastMediaTitle = item?.Title ?? ""; var u = _smtc.DisplayUpdater; u.Type = Windows.Media.MediaPlaybackType.Music; u.MusicProperties.Title = item?.Title ?? ""; u.MusicProperties.Artist = item?.Artist ?? ""; u.Update(); }
        Post(new { kind = "state", title = item?.Title ?? "打开一首音乐", artist = item?.Artist ?? "临时播放，自在聆听", album = item?.Album ?? "", quality = item?.FormatInfoLine ?? "",
            position = e.Position.TotalSeconds, duration = e.Duration.TotalSeconds, playing = e.IsPlaying, busy = Session.Busy, error = Session.Error, order = Session.Order,
            pinned = AppWindow.Presenter is OverlappedPresenter p && p.IsAlwaysOnTop, volume = Session.Volume, volumeEnabled = !e.IsDsdDirectOut,
            output = e.ActualOutputFormat ?? "尚未输出", device = e.OutputDeviceName ?? "系统默认设备", queueIndex = Session.Index,
            queue = Session.Queue.Select(q => new { title = q.Title, artist = q.Artist, duration = q.Duration.TotalSeconds }).ToArray() });
        var appearance = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load(); string mode = WebAppearance.ResolveMode(appearance.mode);
        string key = mode + WebAppearance.CurrentRevision;
        if (key != _themeKey) { _themeKey = key; Post(new { kind = "theme", mode, palette = WebAppearance.Resolve(appearance, mode) }); }
    }
    private void Post(object message)
    { if (_closed || !_ready) return; try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); } catch (Exception ex) { StartupLog.WriteException("Quick.Post", ex); } }
    private void Error(Exception ex) { StartupLog.WriteException("Quick.Window", ex); Post(new { kind = "error", message = ex.Message }); }
    private static string BoundsPath => Path.Combine(AppSettingsStore.GetConfigDirectory(), "quick-window.json");
    private void SaveBounds()
    { try { var p = AppWindow.Position; var s = AppWindow.Size; File.WriteAllText(BoundsPath, JsonSerializer.Serialize(new { x = p.X, y = p.Y, width = s.Width, height = s.Height })); } catch (Exception ex) { StartupLog.WriteException("Quick.Bounds", ex); } }
    private void RestoreBounds()
    {
        try
        {
            if (!File.Exists(BoundsPath)) return; using var d = JsonDocument.Parse(File.ReadAllText(BoundsPath)); var r = d.RootElement;
            int x = r.GetProperty("x").GetInt32(), y = r.GetProperty("y").GetInt32();
            var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
            int w = Math.Clamp(r.GetProperty("width").GetInt32(), Math.Min(460, area.Width), area.Width), h = Math.Clamp(r.GetProperty("height").GetInt32(), Math.Min(480, area.Height), area.Height);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Math.Clamp(x, area.X, area.X + area.Width - w), Math.Clamp(y, area.Y, area.Y + area.Height - h), w, h));
        }
        catch (Exception ex) { StartupLog.WriteException("Quick.Bounds", ex); }
    }
}
