// 皮肤系统第 4 步：Apple 风格主界面 · 歌曲浏览面板（试点）
//
// 用户 2026-10-09 拍板：**先只做歌曲浏览面板**，其他分类面板暂不做，
// 但侧栏 16 个分类全部保留（点未做的分类只显示占位，不删入口）。
//
// 分工铁律：**网页只负责长什么样，一件事都不做**——点播放、切分类、拖进度、
// 收藏全都上报给 C#，由 C# 调现有的播放方法。音频链路（独占 / bit-perfect /
// DSD / DSP）一行不用碰。
//
// 与专辑试点页（WebAlbum.cs）的关系：
//   - 共用同一个常驻 WebView2 宿主与覆盖层（CelesteWebHostGrid），两个页面互斥打开；
//   - play 语义与专辑试点页一致（d3c0d99 定稿）：**整表替换播放队列 + 从点中的行开始**，
//     这里"整表"= 推给网页的歌曲快照（不是原生曲库队列里定位单曲）；
//   - 消息协议在专辑页基础上加一个 nav（切分类）。
//
// 消息协议（与 WebUI/main.html 里的 post() 一一对应，改一边必须改另一边）：
//   C# → 网页：data（categories + songs + cur）/ now（播放状态，每秒）/ theme / nav
//   网页 → C#：ready / nav / play / pause / resume / next / prev / seek / volume / love / exit

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webMainOpen;
        private DispatcherTimer? _webMainTimer;

        /// <summary>推给网页的歌曲快照。网页 play 消息里的 index 按这份的下标，
        /// 所以"换队列播放"也必须用这份——两边下标永远一致。</summary>
        private List<PlaylistItem> _webMainSongs = new();

        /// <summary>封面文件缓存（按曲目路径）。WriteCoverFile 要解音频文件，不能每秒调。</summary>
        private readonly Dictionary<string, string> _webMainCoverCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上一次推封面/曲目信息时的播放路径。now 消息每秒都发，只有换曲才带封面。</summary>
        private string _webMainLastNowPath = "";

        /// <summary>歌曲快照正在分块推送时为 true。网页 ready 会补发 5 次，
        /// 不互mutex就会把 3609 首反复全量推，直接压死 WebView2 渲染进程
        /// （2026-10-09 用户实测"加载巨慢最后卡死"）。</summary>
        private bool _webMainPushing;

        /// <summary>
        /// 侧栏分类清单：照原生侧栏原样保留（CategoryNavPanel 13 项 + NavToolPanel 3 项），
        /// id 与原生按钮 Tag 一致。只有 Songs 有数据（ok=true），其余点一下只显示占位。
        /// </summary>
        private static readonly (string Id, string Label, string Group)[] WebMainCategories =
        {
            ("Songs",         "歌曲",       "music"),
            ("Albums",        "专辑",       "music"),
            ("Artists",       "艺术家",     "music"),
            ("AlbumArtists",  "专辑艺术家", "music"),
            ("Favorites",     "我喜欢的音乐", "music"),
            ("Ratings",       "评分",       "music"),
            ("Recent",        "最近播放",   "music"),
            ("UserPlaylist",  "播放队列",   "music"),
            ("Genres",        "流派",       "music"),
            ("Years",         "年份",       "music"),
            ("MostPlayed",    "播放最多",   "music"),
            ("Folders",       "媒体库",     "media"),
            ("WebDav",        "网络音乐库", "media"),
            ("AudioFX",       "音效处理",   "tool"),
            ("TagSort",       "标签排序",   "tool"),
            ("PlaylistWall",  "播放列表",   "tool"),
        };

        /// <summary>打开歌曲浏览面板。任何一步失败都静默降级：界面保持原生版，程序照常用。</summary>
        public async Task OpenWebMainAsync()
        {
            if (_webMainOpen) return;
            try
            {
                DeployWebAsset("main.html");

                // 两个网页覆盖层互斥：开主界面先关专辑试点（反之亦然）
                if (_webPilotOpen) CloseWebAlbumPilot();

                if (!await EnsureCelesteWebHostAsync()) return;

                _webMainLastNowPath = "";
                CelesteWebHostGrid.Visibility = Visibility.Visible;
                // 预热时 WebView2 是 Collapsed（免得启动时闪一下白屏），这会儿才让它现形
                if (_celesteWeb != null)
                {
                    _celesteWeb.Visibility = Visibility.Visible;
                    try { _celesteWeb.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
                    catch { /* 聚焦失败不致命，照常打开 */ }
                }
                if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Visible;
                _webMainOpen = true;

                var cv = _celesteWeb!.CoreWebView2;
                cv.Navigate("http://celeste.local/main.html");

                // 页面自检：导航后探几次页面真实状态写进日志（探到 rows>0 为止）
                _ = ProbeWebMainStateAsync(cv);

                // 深浅色先告知网页（网页不提供切换按钮，跟着程序走）
                // 2026-10-09 用户拍板：暂不跟随系统，默认浅色（WinUI 的 RequestedTheme
                // 在本程序里恒为 Light，"跟随系统"要等主题设置页做完再做）
                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "theme",
                    ["dark"] = false,
                });

                // 每秒把播放状态推给网页（位置、时长、播放中、音量、收藏）
                _webMainTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webMainTimer.Tick -= WebMainTimer_Tick;
                _webMainTimer.Tick += WebMainTimer_Tick;
                _webMainTimer.Start();

                StartupLog.Write("[Web主界面] 已打开歌曲浏览面板");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebMain", ex);
                CloseWebMain();
            }
        }

        /// <summary>关掉主界面试点，回到原生界面。</summary>
        public void CloseWebMain()
        {
            if (!_webMainOpen) return;
            _webMainOpen = false;
            if (_webMainTimer != null) _webMainTimer.Tick -= WebMainTimer_Tick;
            _webMainTimer?.Stop();
            if (CelesteWebHostGrid != null) CelesteWebHostGrid.Visibility = Visibility.Collapsed;
            if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
            StartupLog.Write("[Web主界面] 已关闭，回到原生界面");
        }

        // EventHandler<object> 的签名是 (object? sender, object e)，sender 必须可空
        private void WebMainTimer_Tick(object? sender, object e)
        {
            if (!_webMainOpen) return;
            _ = PostCelesteWebAsync(BuildWebMainNowMessage());
        }

        /// <summary>
        /// 主界面试点页自检：Navigate 之后隔一会儿探页面真实状态，写进日志。
        ///   wv    = 页面里有没有 WebView2 宿主对象（没有 → 页面脚本被竞态打断）
        ///   rows  = 曲目行渲染了几行（>0 说明歌曲数据已经到达页面）
        ///   title = 页头显示的标题
        /// wv=yes 却一行都没有 → 不等网页的 ready 上行，C# 主动推一次（与专辑试点页同款兜底）。
        /// </summary>
        private async Task ProbeWebMainStateAsync(Microsoft.Web.WebView2.Core.CoreWebView2 cv)
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
                        "var t=document.getElementById('pgTitle');" +
                        "return JSON.stringify({wv:wv,rows:rows,title:t?t.textContent:'?'});" +
                        "}catch(err){return 'PROBE_ERR:'+err;}})()");
                    string state = DecodeScriptResult(raw);
                    StartupLog.Write($"[Web主界面] 页面自检 {state}");

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
                        StartupLog.Write("[Web主界面] 页面没收到数据，主动推一次（不等 ready）");
                        _ = PushWebMainDataAsync();
                    }
                    if (rows > 0) return;   // 数据到了就不再探
                }
                catch (Exception ex)
                {
                    StartupLog.Write(
                        $"[Web主界面] 页面自检第{i + 1}次失败 hr=0x{ex.HResult:X8} {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        /// <summary>网页发过来的消息在这里落地。kind 与 main.html 里 post() 的那些一一对应。</summary>
        internal void HandleWebMainMessage(WebInboundMessage msg)
        {
            // 页面没打开就一律不理（网页可能在关掉前又发了一条）
            if (!_webMainOpen && msg.Kind != "ready") return;

            switch (msg.Kind)
            {
                case "ready":
                    // 页面初始化完成：分类清单 + 歌曲快照一起推过去
                    _ = PushWebMainDataAsync();
                    break;

                case "nav":
                    {
                        string id = ReadNavId(msg.Payload);
                        if (string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase))
                        {
                            // 已做的面板：切回去 + 重推数据（顺带刷新快照）
                            _ = PostCelesteWebAsync(new Dictionary<string, object?>
                            {
                                ["kind"] = "nav", ["id"] = "Songs", ["ok"] = true,
                            });
                            _ = PushWebMainDataAsync();
                        }
                        else
                        {
                            // 没做的面板：告诉网页显示占位（侧栏条目保留，不删入口）
                            StartupLog.Write($"[Web主界面] nav：「{id}」面板还没做，回占位页");
                            _ = PostCelesteWebAsync(new Dictionary<string, object?>
                            {
                                ["kind"] = "nav", ["id"] = id, ["ok"] = false,
                            });
                        }
                        break;
                    }

                case "play":
                    {
                        // 与专辑试点页同一套语义（d3c0d99）：整表替换播放队列，
                        // 从点中的那一行开始。直接 PlayPlaylistItem 只会在旧队列里
                        // 定位——队列还是原来的，这首播完接的是旧队列的顺序。
                        int i = ReadInt(msg.Payload, "index", -1);
                        if (i >= 0 && i < _webMainSongs.Count)
                        {
                            var track = _webMainSongs[i];
                            _userPlaylist.Clear();
                            AddSongsToUserPlaylist(_webMainSongs);
                            StartupLog.Write(
                                $"[Web主界面] play：整表替换队列 {_webMainSongs.Count} 首，从第 {i + 1} 首开始");
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
                        if (i >= 0 && i < _webMainSongs.Count)
                        {
                            var track = _webMainSongs[i];
                            if (!string.IsNullOrEmpty(track.FilePath))
                            {
                                bool on = true;
                                try
                                {
                                    if (msg.Payload.ValueKind == JsonValueKind.Object &&
                                        msg.Payload.TryGetProperty("on", out var v))
                                        on = v.GetBoolean();
                                }
                                catch { /* 没带 on 就当"加入收藏" */ }
                                TrackStatsStore.SetFavorite(track.FilePath, on);
                            }
                        }
                        break;
                    }

                case "exit":
                    CloseWebMain();
                    break;

                default:
                    StartupLog.Write($"[Web主界面] 未处理的消息 kind={msg.Kind}");
                    break;
            }
        }

        /// <summary>
        /// 把分类清单 + 歌曲快照推给网页。ready 和 nav 回 Songs 都走这里。
        ///
        /// **必须分块**：3609 首打成一个大 JSON（1MB+）用 PostWebMessageAsString
        /// 一次性发，WebView2 渲染进程直接压死（2026-10-09 用户实测卡死）。
        /// 专辑试点页只推 46 首没暴露过这个问题，主界面不行。
        /// 另外 ready 补发 5 次 + 自检探针都会调本方法，用 _webMainPushing 互斥，
        /// 推完一次就拦住后续重复调用（nav 回 Songs 要刷新时先清标志再调）。
        /// </summary>
        private async Task PushWebMainDataAsync()
        {
            if (!_celesteWebReady || _webMainPushing) return;
            _webMainPushing = true;
            try
            {
                await PushWebMainDataCoreAsync();
            }
            finally
            {
                _webMainPushing = false;
            }
        }

        private async Task PushWebMainDataCoreAsync()
        {
            // 快照：网页的 index 与这份一一对应（play/love 都按它下标）
            _webMainSongs = _playlist.ToList();

            var cats = new List<object>();
            foreach (var (id, label, group) in WebMainCategories)
            {
                cats.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["t"] = label,
                    ["g"] = group,
                    ["ok"] = string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase),
                    ["n"] = string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase)
                        ? _webMainSongs.Count : 0,
                });
            }

            int cur = -1;
            if (!string.IsNullOrEmpty(_nowPlayingPath))
            {
                for (int i = 0; i < _webMainSongs.Count; i++)
                {
                    if (string.Equals(_webMainSongs[i].FilePath, _nowPlayingPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        cur = i;
                        break;
                    }
                }
            }

            // 首包：分类 + 总数（歌曲先空着，网页先建侧栏、显示"加载中 x/total"）
            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "data",
                ["categories"] = cats,
                ["songs"] = new List<object>(),
                ["total"] = _webMainSongs.Count,
                ["cur"] = cur,
            });

            // 分块推歌曲：每块 400 首，块之间让出 16ms 给渲染进程消化。
            // ⚠ 块内只准碰内存属性：FormatChips 是懒加载、首次访问要开文件读
            // （云盘曲库下一块 400 首就要几十秒，2026-10-09 用户实测卡死 90 秒
            // 只有第一块——IsHiResFile 读 FormatChips，已去掉 Hi-Res 徽章）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StartupLog.Write($"[Web主界面] 开始分块推送 {_webMainSongs.Count} 首");
            const int CHUNK = 400;
            for (int off = 0; off < _webMainSongs.Count; off += CHUNK)
            {
                int end = Math.Min(off + CHUNK, _webMainSongs.Count);
                var chunk = new List<object>();
                for (int i = off; i < end; i++)
                {
                    var t = _webMainSongs[i];
                    chunk.Add(new Dictionary<string, object?>
                    {
                        ["title"] = t.Title,
                        ["artist"] = t.Artist,
                        ["album"] = t.Album,
                        ["duration"] = t.Duration.TotalSeconds,
                        ["dsd"] = IsDsdFile(t.FilePath),
                        ["fmt"] = CodecOf(t.FilePath),
                    });
                }
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "data/append",
                    ["songs"] = chunk,
                });
                StartupLog.Write($"[Web主界面] 已推 {end} / {_webMainSongs.Count} 首（{sw.ElapsedMilliseconds} ms）");
                await Task.Delay(16);
            }
            StartupLog.Write($"[Web主界面] 分块推送完成，共 {_webMainSongs.Count} 首，耗时 {sw.ElapsedMilliseconds} ms");

            // 数据到了紧接着推一次播放状态，网页不用等下一个 tick
            await PostCelesteWebAsync(BuildWebMainNowMessage());
        }

        /// <summary>每秒推送给网页的播放状态。字段名与 main.html 的 'now' 分支对应。</summary>
        private Dictionary<string, object?> BuildWebMainNowMessage()
        {
            int idx = -1;
            if (!string.IsNullOrEmpty(_nowPlayingPath))
            {
                for (int i = 0; i < _webMainSongs.Count; i++)
                {
                    if (string.Equals(_webMainSongs[i].FilePath, _nowPlayingPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }

            bool playing = _audioEngine?.IsPlaying == true;

            var msg = new Dictionary<string, object?>
            {
                ["kind"] = "now",
                ["index"] = idx,
                ["playing"] = playing,
                ["position"] = EnginePositionValue.TotalSeconds,
                ["duration"] = EngineDurationValue.TotalSeconds,
                ["volume"] = (VolumeSlider?.Value ?? 0) / 100.0,
            };

            // 收藏态跟着正在播的那首走（网页上高亮的就是它）
            if (!string.IsNullOrEmpty(_nowPlayingPath))
                msg["love"] = TrackStatsStore.Get(_nowPlayingPath)?.IsFavorite == true;

            // 换曲时才带封面和曲目信息：这条消息每秒都发，WriteCoverFile 要解音频文件，
            // 不能每次调。封面走虚拟域名文件（album.html 同款做法）。
            if (!string.Equals(_nowPlayingPath, _webMainLastNowPath, StringComparison.OrdinalIgnoreCase))
            {
                _webMainLastNowPath = _nowPlayingPath ?? "";
                if (!string.IsNullOrEmpty(_nowPlayingPath))
                {
                    if (!_webMainCoverCache.TryGetValue(_nowPlayingPath, out var cover))
                    {
                        cover = WriteCoverFile(_nowPlayingPath);
                        _webMainCoverCache[_nowPlayingPath] = cover;
                    }
                    if (!string.IsNullOrEmpty(cover)) msg["cover"] = cover;

                    // 正在播的曲子不在当前快照里（idx<0，比如从别的分类/原生界面播的）时，
                    // 网页自己的列表里找不到它——把曲目信息一起带过去，播放条才不至于空着。
                    var t = _playlist.FirstOrDefault(x =>
                        string.Equals(x.FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase));
                    if (t != null)
                    {
                        msg["track"] = new Dictionary<string, object?>
                        {
                            ["title"] = t.Title,
                            ["artist"] = t.Artist,
                            ["album"] = t.Album,
                        };
                    }
                }
            }
            return msg;
        }

        private static string ReadNavId(JsonElement payload)
        {
            try
            {
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("id", out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            catch { /* 读不到按空串处理，调用方会走"没做"分支 */ }
            return "";
        }

        /// <summary>歌曲库工具栏里的「新版界面（试点）」按钮。</summary>
        private void WebMainPilotButton_Click(object sender, RoutedEventArgs e)
            => _ = OpenWebMainAsync();
    }
}
