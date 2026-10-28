using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;

namespace CelesteMusicPlayer;

/// <summary>Web presentation only; all audio commands use the existing mini bridge.</summary>
internal sealed class WebMiniPlayerWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly Grid _root = new();
    private bool _closed, _ready;
    private byte[]? _cover;
    private object? _lines = new object();
    internal WebMiniPlayerWindow()
    {
        Title = "Celeste · 迷你播放器";
        WindowIconHelper.Apply(this);
        _root.Children.Add(_web);
        Content = _root;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false; p.IsMaximizable = false;
            p.IsAlwaysOnTop = AppSettingsStore.Load().MiniPlayerAlwaysOnTop;
        }
        Resize(false);
        _root.Loaded += Initialize;
        MiniPlayerBridge.StateChanged += StateChanged;
        Closed += (_, _) => { _closed = true; MiniPlayerBridge.StateChanged -= StateChanged; _web.Close(); };
    }
    private void Resize(bool expanded)
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(400 * scale), (int)((expanded ? 620 : 188) * scale)));
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= Initialize;
        Resize(false);
        try
        {
            await _web.EnsureCoreWebView2Async();
            if (_closed) return;
            var core = _web.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("celeste-mini.internal", Path.Combine(AppContext.BaseDirectory, "WebShell"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://celeste-mini.internal/", StringComparison.Ordinal)) a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.WebMessageReceived += (_, a) =>
            {
                if (_closed || !a.Source.StartsWith("https://celeste-mini.internal/", StringComparison.Ordinal)) return;
                try
                {
                    using var doc = JsonDocument.Parse(a.WebMessageAsJson);
                    var m = doc.RootElement;
                    switch (m.GetProperty("command").GetString())
                    {
                        case "ready": _ready = true; MiniPlayerBridge.Push(); StateChanged(MiniPlayerBridge.Current); break;
                        case "play": MiniPlayerBridge.TogglePlayPause(); break;
                        case "previous": MiniPlayerBridge.Previous(); break;
                        case "next": MiniPlayerBridge.Next(); break;
                        case "seek": if (m.GetProperty("value").TryGetDouble(out var t) && double.IsFinite(t)) MiniPlayerBridge.Seek(Math.Clamp(t, 0, MiniPlayerBridge.Current.DurationSeconds)); break;
                        case "volume": if (m.GetProperty("value").TryGetDouble(out var v) && double.IsFinite(v)) MiniPlayerBridge.SetVolume(Math.Clamp(v, 0, 100)); break;
                        case "queue": MiniPlayerBridge.PlayQueueAt(m.GetProperty("value").GetInt32()); break;
                        case "expand": Resize(m.GetProperty("value").GetBoolean()); break;
                        case "main": MiniPlayerBridge.BringMainWindowToFront(); break;
                        case "close": Close(); break;
                        case "minimize": (AppWindow.Presenter as OverlappedPresenter)?.Minimize(); break;
                        case "pin":
                            if (AppWindow.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = !p.IsAlwaysOnTop; var s = AppSettingsStore.Load(); s.MiniPlayerAlwaysOnTop = p.IsAlwaysOnTop; AppSettingsStore.Save(s); Post(new { kind = "pin", value = p.IsAlwaysOnTop }); }
                            break;
                    }
                }
                catch (Exception ex) { StartupLog.WriteException("WebMiniPlayer.command", ex); }
            };
            core.Navigate("https://celeste-mini.internal/mini-player.html");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("WebMiniPlayer.initialize", ex);
            _root.Children.Clear();
            var retry = new Button { Content = "加载失败，点击重新加载", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            retry.Click += (_, _) => { _root.Children.Clear(); _root.Children.Add(_web); Initialize(_root, new RoutedEventArgs()); };
            _root.Children.Add(retry);
        }
    }
    private void Post(object value) { if (_ready && !_closed) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
    private void StateChanged(MiniPlayerSnapshot s)
    {
        if (!_ready || _closed) return;
        if (!ReferenceEquals(_cover, s.CoverBytes)) { _cover = s.CoverBytes; Post(new { kind = "cover", value = _cover is { Length: > 0 } ? "data:image/jpeg;base64," + Convert.ToBase64String(_cover) : "" }); }
        if (!ReferenceEquals(_lines, s.LyricLines)) { _lines = s.LyricLines; Post(new { kind = "lyrics", value = s.LyricLines?.Select(l => new { text = l.Text, time = l.Time.TotalSeconds }) }); }
        Post(new { kind = "state", s.Title, s.Artist, position = s.PositionSeconds, duration = s.DurationSeconds, playing = s.IsPlaying, volume = s.Volume, queue = s.QueueTitles, queueIndex = s.QueueIndex, lyricIndex = s.LyricIndex, pinned = (AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop ?? false });
    }
}
