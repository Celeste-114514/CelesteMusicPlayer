// 皮肤系统第 3 步：专辑详情页的网页试点（Apple 风格）
//
// 分工铁律：**网页只负责长什么样，一件事都不做**——点播放、拖进度、切歌全都上报给 C#，
// 由 C# 调现有的播放方法。这样音频链路（独占 / bit-perfect / DSD / DSP）一行不用碰。
//
// 试点层盖住主内容区和底部播放条，但**保留上面的原生标题栏**：
// 窗口拖动、最小化、最大化、关闭仍然归系统管，网页不接管窗口（省掉一整套 P/Invoke）。
// 平时这一层是 Collapsed、WebView2 也压根没创建，对现有界面零影响；
// 关掉试点只是 Visible→Collapsed，原样回到原生界面。
//
// 消息协议（与 WebUI/album.html 里的 post() 一一对应，改一边必须改另一边）：
//   C# → 网页：data（专辑+曲目）、now（播放状态，每秒）、theme（深浅色）
//   网页 → C#：ready、play、pause、resume、next、prev、seek、volume、love、exit

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webPilotOpen;
        private DispatcherTimer? _webPilotTimer;

        /// <summary>专辑封面的虚拟域名地址，打开试点时算一次，推送时直接复用。</summary>
        private string _webCoverUrl = "";

        /// <summary>网页资源目录。虚拟域名 celeste.local 就指向这里。</summary>
        private static string WebAssetRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "CelesteMusicPlayer", "WebAssets");

        /// <summary>
        /// 把随程序发布的网页文件铺到用户目录，同名覆盖——改了 HTML 重新打开就是新的，
        /// 不用重新发布程序。
        /// </summary>
        private static void DeployWebAsset(string fileName)
        {
            try
            {
                string src = Path.Combine(AppContext.BaseDirectory, "WebUI", fileName);
                if (!File.Exists(src))
                {
                    StartupLog.Write($"[Web试点] 网页文件不在：{src}");
                    return;
                }
                Directory.CreateDirectory(WebAssetRoot);
                File.Copy(src, Path.Combine(WebAssetRoot, fileName), true);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("DeployWebAsset", ex);
            }
        }

        /// <summary>
        /// 保证 WebView2 宿主可用，返回 false 就放弃打开（界面保持原生版）。三种情况：
        ///   ① 好的          → 直接用
        ///   ② 正在预热       → 轮询等它完成（双击/并发打开不再误报"没起来"）
        ///   ③ 就绪但内核没了 → 浏览器进程死过一次后 CoreWebView2 会是 null，
        ///      拆掉旧的重新预热，否则"关掉后再打开"永远卡在"没起来"
        /// </summary>
        private async Task<bool> EnsureCelesteWebHostAsync()
        {
            // 正在初始化：等一下再说（预热实测不到 1s，给到 8s 上限）
            for (int i = 0; i < 80 && _celesteWebInitializing; i++)
                await Task.Delay(100);
            if (_celesteWebInitializing)
                StartupLog.Write("[Web试点] 等 WebView2 预热超时，按当前状态继续");

            if (_celesteWebReady && _celesteWeb?.CoreWebView2 == null)
            {
                StartupLog.Write("[Web试点] 宿主内核已失效，重建 WebView2");
                CleanupCelesteWeb(_celesteWeb);
                await PrewarmCelesteWeb();
            }
            else if (!_celesteWebReady && _celesteWeb == null)
            {
                await PrewarmCelesteWeb();
            }

            if (!_celesteWebReady || _celesteWeb?.CoreWebView2 == null)
            {
                StartupLog.Write("[Web试点] WebView2 没起来，试点页放弃，界面保持原样");
                return false;
            }
            return true;
        }

        /// <summary>打开网页试点页。任何一步失败都静默降级：界面保持原生版，程序照常用。</summary>
        public async Task OpenWebAlbumPilotAsync()
        {
            if (_webPilotOpen) return;
            try
            {
                DeployWebAsset("album.html");

                // 第一个 WebView2 实例要吃 286MB，所以不到真正要用的时候不建；
                // 万一宿主内核已经死了（浏览器进程崩溃过就会这样），这里就地拆旧建新
                if (!await EnsureCelesteWebHostAsync()) return;

                // 封面抽一次就够，别每秒推送都去解音频文件
                _webCoverUrl = "";
                if (_openedAlbum != null && !string.IsNullOrEmpty(_openedAlbum.CoverSourcePath))
                    _webCoverUrl = WriteCoverFile(_openedAlbum.CoverSourcePath);

                CelesteWebHostGrid.Visibility = Visibility.Visible;
                // 预热时 WebView2 是 Collapsed（免得启动时闪一下白屏），这会儿才让它现形
                if (_celesteWeb != null)
                {
                    _celesteWeb.Visibility = Visibility.Visible;
                    // 显式要焦点：岛屿架构下网页没有焦点时，第一次点击可能只用于聚焦
                    try { _celesteWeb.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
                    catch { /* 聚焦失败不致命，照常打开 */ }
                }
                if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Visible;
                _webPilotOpen = true;

                // 上面已经判过 _celesteWeb?.CoreWebView2 != null，这里编译器不知道，用 ! 说明
                var cv = _celesteWeb!.CoreWebView2;
                cv.Navigate("http://celeste.local/album.html");

                // 页面自检：导航后探一次页面真实状态写进日志。网页"没反应"时看这一行，
                // 能分清是页面没渲染、宿主对象没注入，还是数据/消息没到。
                _ = ProbeWebPageStateAsync(cv);

                // 深色/浅色先告知网页（网页不提供切换按钮，跟着程序走，避免出现假控件）
                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "theme",
                    ["dark"] = IsDarkUiNow(),
                });

                // 每秒把播放状态推给网页（位置、时长、播放中、音量）
                _webPilotTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webPilotTimer.Tick -= WebPilotTimer_Tick;
                _webPilotTimer.Tick += WebPilotTimer_Tick;
                _webPilotTimer.Start();

                StartupLog.Write("[Web试点] 已打开网页版专辑详情");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebAlbumPilot", ex);
                CloseWebAlbumPilot();
            }
        }

        // EventHandler<object> 的签名是 (object? sender, object e)，sender 必须可空
        private void WebPilotTimer_Tick(object? sender, object e)
        {
            if (!_webPilotOpen) return;
            _ = PostCelesteWebAsync(BuildWebNowMessage());
        }

        /// <summary>当前是不是深色界面（网页跟着程序走，不自己决定）。</summary>
        private bool IsDarkUiNow()
        {
            try
            {
                if (Application.Current is Application app)
                    return app.RequestedTheme == ApplicationTheme.Dark;
            }
            catch { /* 读不到就当浅色 */ }
            return false;
        }

        /// <summary>关掉试点，回到原生界面。</summary>
        public void CloseWebAlbumPilot()
        {
            if (!_webPilotOpen) return;
            _webPilotOpen = false;
            if (_webPilotTimer != null) _webPilotTimer.Tick -= WebPilotTimer_Tick;
            _webPilotTimer?.Stop();
            if (CelesteWebHostGrid != null) CelesteWebHostGrid.Visibility = Visibility.Collapsed;
            if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
            StartupLog.Write("[Web试点] 已关闭，回到原生界面");
        }

        /// <summary>
        /// 试点页自检：Navigate 之后隔一会儿探页面真实状态，写进日志。
        ///   wv    = 页面里有没有 WebView2 宿主对象（没有 → 页面脚本被竞态打断）
        ///   rows  = 曲目行渲染了几行（>0 说明专辑数据已经到达页面）
        ///   title = 页头显示的专辑名（"专辑" = 还是默认值，数据没到）
        ///   empty = 有没有显示"这张专辑里还没有曲目"空态
        /// 页面加载快慢不定（虚拟域名首访要建连接），所以探 5 次、间隔递增；
        /// 每次失败都记 HRESULT——"无文档可执行"(0x8007139F) 和浏览器进程没了
        /// 是两回事，靠它区分。
        /// 另外：WebAssets 目录下存在 _dbg_click.txt 时，顺手替用户点一次返回箭头，
        /// 用来验证"网页 → C#"消息通道（正常环境没这个文件，什么都不发生）。
        /// </summary>
        private async Task ProbeWebPageStateAsync(Microsoft.Web.WebView2.Core.CoreWebView2 cv)
        {
            var delays = new[] { 1000, 1500, 2000, 2500, 3000 };
            for (int i = 0; i < delays.Length; i++)
            {
                try
                {
                    await Task.Delay(delays[i]);
                    var raw = await cv.ExecuteScriptAsync(
                        "(function(){try{" +
                        "var wv=(window.chrome&&window.chrome.webview)?'yes':'no';" +
                        "var rows=document.querySelectorAll('.row').length;" +
                        "var t=document.getElementById('hdrTitle');" +
                        "var e=document.querySelector('.empty');" +
                        "var b=document.getElementById('btnExit');" +
                        "return JSON.stringify({wv:wv,rows:rows,title:t?t.textContent:'?',empty:e?'yes':'no',btn:b?'yes':'no'});" +
                        "}catch(err){return 'PROBE_ERR:'+err;}})()");
                    string state = DecodeScriptResult(raw);
                    StartupLog.Write($"[Web试点] 页面自检 {state}");

                    // 页面渲染正常（wv=yes）却一行数据都没有 → 不等网页的 ready 上行，
                    // C# 主动推一次。ready 上行曾经整体丢失（页面看着正常、显示 0 首、
                    // 日志零 ↑ 记录）；主动推与 ready 触发推是幂等的，网页收到 data
                    // 就整表重画，不会堆重复行。推完继续探，看数据到没到。
                    bool wvOk = false;
                    int rows = -1;
                    try
                    {
                        using var doc = JsonDocument.Parse(state);
                        if (doc.RootElement.TryGetProperty("wv", out var w)) wvOk = w.GetString() == "yes";
                        if (doc.RootElement.TryGetProperty("rows", out var r)) rows = r.GetInt32();
                    }
                    catch { /* 状态解析失败不致命，按 rows=-1 继续探 */ }

                    if (wvOk && rows == 0)
                    {
                        StartupLog.Write("[Web试点] 页面没收到数据，主动推一次（不等 ready）");
                        _ = PushWebAlbumDataAsync();
                    }

                    string flag = Path.Combine(WebAssetRoot, "_dbg_click.txt");
                    if (File.Exists(flag))
                    {
                        var r2 = await cv.ExecuteScriptAsync(
                            "(function(){try{var b=document.getElementById('btnExit');" +
                            "if(!b)return 'no-btn';b.click();return 'clicked';}catch(e){return 'ERR:'+e;}})()");
                        StartupLog.Write($"[Web试点] 自检代点返回箭头 → {DecodeScriptResult(r2)}");
                    }
                    if (rows > 0) return;   // 数据到了就不再探
                }
                catch (Exception ex)
                {
                    StartupLog.Write(
                        $"[Web试点] 页面自检第{i + 1}次失败 hr=0x{ex.HResult:X8} {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        /// <summary>ExecuteScriptAsync 的返回值是 JSON 编码的字符串，解一层才好读。</summary>
        private static string DecodeScriptResult(string raw)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; }
            catch { return raw; }
        }

        /// <summary>专辑详情页上的「新版界面（试点）」按钮。</summary>
        private void AlbumDetailWebPilotButton_Click(object sender, RoutedEventArgs e)
            => _ = OpenWebAlbumPilotAsync();

        /// <summary>网页发过来的消息在这里落地。kind 与 album.html 里 post() 的那些一一对应。</summary>
        internal void HandleWebAlbumMessage(WebInboundMessage msg)
        {
            // 试点没打开就一律不理（网页可能在关掉前又发了一条）
            if (!_webPilotOpen && msg.Kind != "ready") return;

            switch (msg.Kind)
            {
                case "ready":
                    // 页面初始化完成：把专辑数据和当前播放状态一起推过去
                    _ = PushWebAlbumDataAsync();
                    break;

                case "play":
                    {
                        int i = ReadInt(msg.Payload, "index", -1);
                        var track = AlbumTrackAt(i);
                        if (track != null)
                        {
                            // 与原生详情页「播放」按钮同一套行为：整专替换播放队列，
                            // 顺序就是页面顺序（碟号→音轨号→标题），从点中的那首开始。
                            // 直接 PlayPlaylistItem 只会在旧队列里定位——队列还是整个曲库，
                            // 这首播完接的是曲库顺序，用户看到的就是"顺序不对"。
                            if (_albumTracks != null && _albumTracks.Count > 0)
                            {
                                _userPlaylist.Clear();
                                AddSongsToUserPlaylist(_albumTracks.ToList());
                            }
                            StartupLog.Write(
                                $"[Web试点] play：整专替换队列 {_albumTracks?.Count ?? 0} 首，从第 {i + 1} 首开始");
                            PlayPlaylistItem(track);
                        }
                        break;
                    }

                case "pause":
                    TogglePlayPausePublic();
                    break;

                case "resume":
                    TogglePlayPausePublic();
                    break;

                case "next":
                    PlayNext();
                    break;

                case "prev":
                    PlayPrevious();
                    break;

                case "seek":
                    {
                        double sec = ReadDouble(msg.Payload, "position", -1);
                        if (sec >= 0) SeekSourceSeconds(sec);
                        break;
                    }

                case "volume":
                    {
                        double v = ReadDouble(msg.Payload, "value", -1);
                        if (v >= 0) SetVolumePublic(v * 100.0);
                        break;
                    }

                case "love":
                    {
                        int i = ReadInt(msg.Payload, "index", -1);
                        var track = AlbumTrackAt(i);
                        if (track != null && !string.IsNullOrEmpty(track.FilePath))
                        {
                            bool on = true;
                            try
                            {
                                if (msg.Payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                    msg.Payload.TryGetProperty("on", out var v))
                                    on = v.GetBoolean();
                            }
                            catch { /* 没带 on 就当"加入收藏" */ }
                            TrackStatsStore.SetFavorite(track.FilePath, on);
                        }
                        break;
                    }

                case "exit":
                    CloseWebAlbumPilot();
                    break;

                default:
                    StartupLog.Write($"[Web试点] 未处理的消息 kind={msg.Kind}");
                    break;
            }
        }

        /// <summary>把当前专辑（跟原生详情页同一份数据）推给网页。</summary>
        private async Task PushWebAlbumDataAsync()
        {
            if (!_celesteWebReady) return;

            var album = _openedAlbum;
            var tracks = _albumTracks;

            string cover = _webCoverUrl;
            if (string.IsNullOrEmpty(cover) && album != null && !string.IsNullOrEmpty(album.CoverSourcePath))
            {
                cover = WriteCoverFile(album.CoverSourcePath);
                _webCoverUrl = cover;
            }

            var list = new List<object>();
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    list.Add(new Dictionary<string, object?>
                    {
                        ["n"] = t.Track > 0 ? (int)t.Track : i + 1,
                        ["title"] = t.Title,
                        ["artist"] = t.Artist,
                        ["duration"] = t.Duration.TotalSeconds,
                        ["dsd"] = IsDsdFile(t.FilePath),
                        ["fmt"] = CodecOf(t.FilePath),
                        ["hires"] = IsHiResFile(t),
                        ["cover"] = cover,
                    });
                }
            }

            var albumObj = new Dictionary<string, object?>
            {
                ["name"] = album?.Name ?? "未打开专辑",
                ["artist"] = album?.Artist ?? "",
                ["year"] = album != null && album.Year > 0
                    ? album.Year.ToString(CultureInfo.InvariantCulture) : "",
                ["cover"] = cover,
            };

            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "data",
                ["album"] = albumObj,
                ["tracks"] = list,
            });

            // 数据到了紧接着推一次播放状态，网页不用等下一个 tick
            await PostCelesteWebAsync(BuildWebNowMessage());
        }

        /// <summary>每秒推送给网页的播放状态。字段名与 album.html 的 'now' 分支对应。</summary>
        private Dictionary<string, object?> BuildWebNowMessage()
        {
            int idx = -1;
            if (_albumTracks != null && !string.IsNullOrEmpty(_nowPlayingPath))
                idx = _albumTracks.ToList().FindIndex(t =>
                    string.Equals(t.FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase));

            bool playing = _audioEngine?.IsPlaying == true;

            // 收藏状态跟着正在播的那首走（网页上高亮的就是它）
            string? lovePath = _nowPlayingPath;
            if (idx >= 0 && _albumTracks != null && idx < _albumTracks.Count)
                lovePath = _albumTracks[idx].FilePath;

            var msg = new Dictionary<string, object?>
            {
                ["kind"] = "now",
                ["index"] = idx,
                ["playing"] = playing,
                ["position"] = EnginePositionValue.TotalSeconds,
                ["duration"] = EngineDurationValue.TotalSeconds,
                ["volume"] = (VolumeSlider?.Value ?? 0) / 100.0,
            };
            if (!string.IsNullOrEmpty(lovePath))
                msg["love"] = TrackStatsStore.Get(lovePath)?.IsFavorite == true;
            return msg;
        }

        /// <summary>网页里的行号是专辑内序号，这里换成真正的曲目对象。</summary>
        private PlaylistItem? AlbumTrackAt(int i)
        {
            if (i < 0 || _albumTracks == null || i >= _albumTracks.Count) return null;
            return _albumTracks[i];
        }

        /// <summary>封面存成文件让网页用虚拟域名访问，不走 base64（图片动辄几 MB，塞消息里不合适）。</summary>
        private static string WriteCoverFile(string audioPath)
        {
            try
            {
                byte[]? bytes = ExtractCoverBytes(audioPath);
                if (bytes == null || bytes.Length == 0) return "";

                string ext = ".jpg";
                if (bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50) ext = ".png";

                string name = "cover_" + Math.Abs(audioPath.GetHashCode()).ToString("x") + ext;
                string dir = Path.Combine(WebAssetRoot, "covers");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
                return "http://celeste.local/covers/" + name;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WriteCoverFile", ex);
                return "";
            }
        }

        private static string CodecOf(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        }

        /// <summary>
        /// 采样率 ≥ 88.2kHz 算 Hi-Res。从格式胶囊里那段 "xxbit/xxkHz" 解析，
        /// 不另外开文件读——FormatChips 本身有缓存，列表页早用过一遍了。
        /// </summary>
        private static bool IsHiResFile(PlaylistItem t)
        {
            try
            {
                foreach (var chip in t.FormatChips)
                {
                    int k = chip.IndexOf("kHz", StringComparison.OrdinalIgnoreCase);
                    if (k <= 0) continue;
                    // 往前找数字起点
                    int s = k;
                    while (s > 0 && (char.IsDigit(chip[s - 1]) || chip[s - 1] == '.')) s--;
                    if (s < k && double.TryParse(chip.AsSpan(s, k - s),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double khz))
                        return khz >= 88.2;
                }
            }
            catch { /* 解析不了就当不是 Hi-Res，徽章而已 */ }
            return false;
        }

        private static int ReadInt(System.Text.Json.JsonElement payload, string name, int fallback)
        {
            try
            {
                if (payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    payload.TryGetProperty(name, out var v))
                    return v.GetInt32();
            }
            catch { /* 网页给了别的类型就当没给 */ }
            return fallback;
        }

        private static double ReadDouble(System.Text.Json.JsonElement payload, string name, double fallback)
        {
            try
            {
                if (payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    payload.TryGetProperty(name, out var v))
                    return v.GetDouble();
            }
            catch { /* 同上 */ }
            return fallback;
        }
    }
}
