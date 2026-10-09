// 皮肤系统第 4 步：Apple 风格主界面 · 歌曲浏览 + 专辑面板（试点）
//
// 用户 2026-10-09 拍板：**先只做歌曲浏览面板和专辑面板**，其他分类面板暂不做，
// 但侧栏 16 个分类全部保留（点未做的分类只显示占位，不删入口）。
//
// 分工铁律：**网页只负责长什么样，一件事都不做**——点播放、切分类、拖进度、
// 收藏全都上报给 C#，由 C# 调现有的播放方法。音频链路（独占 / bit-perfect /
// DSD / DSP）一行不用碰。
//
// 与专辑试点页（WebAlbum.cs）的关系：
//   - 共用同一个常驻 WebView2 宿主与覆盖层（CelesteWebHostGrid），两个页面互斥打开；
//   - play 语义与专辑试点页一致（d3c0d99 定稿）：**整表替换播放队列 + 从点中的行开始**，
//     这里"整表"= 当前网页列表（歌曲快照或专辑详情曲目，不是原生曲库队列里定位单曲）；
//   - 消息协议在专辑页基础上加 nav（切分类）/ album（打开专辑）/ albums / albumcovers。
//
// 消息协议（与 WebUI/main.html 里的 post() 一一对应，改一边必须改另一边）：
//   C# → 网页：data（categories + songs + cur）/ data/append（歌曲分块）
//              / chips（格式胶囊预热补）/ albums（专辑网格）
//              / albumcovers（专辑封面预热补）/ albumtracks（专辑详情曲目）
//              / now（播放状态，每秒）/ theme / nav
//   网页 → C#：ready / nav / album（打开专辑，id=专辑下标）
//              / play / pause / resume / next / prev / seek / volume / love / exit

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>封面文件缓存（按曲目路径）。WriteCoverFile 要解音频文件，不能每秒调。
        /// 并发字典：后台预热线程和 UI 线程（播放条封面）会同时碰它。</summary>
        private readonly ConcurrentDictionary<string, string> _webMainCoverCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上一次推封面/曲目信息时的播放路径。now 消息每秒都发，只有换曲才带封面。</summary>
        private string _webMainLastNowPath = "";

        /// <summary>歌曲快照正在分块推送时为 true。网页 ready 会补发 5 次，
        /// 不互mutex就会把 3609 首反复全量推，直接压死 WebView2 渲染进程
        /// （2026-10-09 用户实测"加载巨慢最后卡死"）。</summary>
        private bool _webMainPushing;

        /// <summary>格式胶囊（编码器/位深·采样率/码率）后台预热标志：同一份快照只预热一次。</summary>
        private bool _webMainChipsWarm;

        /// <summary>网页专辑详情打开时的曲目列表（play/love 的 index 按这份）。
        /// null = 没开详情（歌曲视图或专辑网格），此时 index 按 _webMainSongs。</summary>
        private List<PlaylistItem>? _webMainAlbumTracks;

        /// <summary>网页当前停在哪个分类（nav 上报）。歌曲快照推完时如果正停在专辑页，
        /// 顺手把专辑列表也推过去——不然用户先点专辑、歌曲后到，网格会空着。</summary>
        private string _webMainPage = "Songs";

        /// <summary>数据代际：每次重推歌曲快照/专辑列表就 +1。
        /// 后台预热循环（格式胶囊、专辑封面）开跑时记下代际，中途发现变了立刻停——
        /// 否则旧循环会按旧下标把数据补到新列表的行/卡片上，张冠李戴。</summary>
        private int _webMainGen;

        /// <summary>
        /// 侧栏分类清单：照原生侧栏原样保留（CategoryNavPanel 13 项 + NavToolPanel 3 项），
        /// id 与原生按钮 Tag 一致。只有 Songs / Albums 有数据（ok=true），
        /// 其余点一下只显示占位。
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
                if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
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
        ///   cards = 专辑卡渲染了几张（专辑网格视图下 rows 本来就是 0）
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
                        "var cards=document.querySelectorAll('.acard').length;" +
                        "var t=document.getElementById('pgTitle');" +
                        "return JSON.stringify({wv:wv,rows:rows,cards:cards,title:t?t.textContent:'?'});" +
                        "}catch(err){return 'PROBE_ERR:'+err;}})()");
                    string state = DecodeScriptResult(raw);
                    StartupLog.Write($"[Web主界面] 页面自检 {state}");

                    bool wvOk = false;
                    int rows = -1, cards = -1;
                    try
                    {
                        using var doc = JsonDocument.Parse(state);
                        if (doc.RootElement.TryGetProperty("wv", out var w)) wvOk = w.GetString() == "yes";
                        if (doc.RootElement.TryGetProperty("rows", out var r)) rows = r.GetInt32();
                        if (doc.RootElement.TryGetProperty("cards", out var c)) cards = c.GetInt32();
                    }
                    catch { /* 状态解析失败不致命，按 rows=-1 继续探 */ }

                    if (wvOk && rows == 0 && cards == 0)
                    {
                        StartupLog.Write("[Web主界面] 页面没收到数据，主动推一次（不等 ready）");
                        _ = PushWebMainDataAsync();
                    }
                    if (rows > 0 || cards > 0) return;   // 数据到了就不再探
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
                        _webMainPage = id;
                        if (string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase))
                        {
                            // 已做的面板：切回去 + 重推数据（顺带刷新快照）
                            _webMainAlbumTracks = null;
                            _ = PostCelesteWebAsync(new Dictionary<string, object?>
                            {
                                ["kind"] = "nav", ["id"] = "Songs", ["ok"] = true,
                            });
                            _ = PushWebMainDataAsync();
                        }
                        else if (string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase))
                        {
                            // 专辑面板：网格视图（详情里点"← 专辑"也走这条，幂等刷新）
                            _webMainAlbumTracks = null;
                            _ = PostCelesteWebAsync(new Dictionary<string, object?>
                            {
                                ["kind"] = "nav", ["id"] = "Albums", ["ok"] = true,
                            });
                            _ = PushWebMainAlbumsAsync();
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

                case "album":
                    {
                        // 网页点了一张专辑卡：推专辑详情（曲目查询/排序与原生详情页一致）
                        int ai = ReadInt(msg.Payload, "id", -1);
                        _ = OpenWebMainAlbumAsync(ai);
                        break;
                    }

                case "play":
                    {
                        // 与专辑试点页同一套语义（d3c0d99）：整表替换播放队列，
                        // 从点中的那一行开始。直接 PlayPlaylistItem 只会在旧队列里
                        // 定位——队列还是原来的，这首播完接的是旧队列的顺序。
                        // "整表"取当前网页列表：开着专辑详情就是这张专辑，否则是歌曲快照。
                        int i = ReadInt(msg.Payload, "index", -1);
                        var list = WebMainActiveList;
                        if (i >= 0 && i < list.Count)
                        {
                            var track = list[i];
                            _userPlaylist.Clear();
                            AddSongsToUserPlaylist(list);
                            StartupLog.Write(
                                $"[Web主界面] play：{(_webMainAlbumTracks != null ? "整专" : "整表")}替换队列 {list.Count} 首，从第 {i + 1} 首开始");
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
                        var list = WebMainActiveList;
                        if (i >= 0 && i < list.Count)
                        {
                            var track = list[i];
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

                case "covererr":
                    // 网页封面图 <img onerror> 上报：URL 是空的还是图裂了，日志能直接分清
                    StartupLog.Write($"[Web主界面] 封面图片加载失败 src={ReadStr(msg.Payload, "src")}");
                    break;

                default:
                    StartupLog.Write($"[Web主界面] 未处理的消息 kind={msg.Kind}");
                    break;
            }
        }

        /// <summary>从消息里读一个字符串字段（读不到返回空串）。封面诊断用。</summary>
        private static string ReadStr(JsonElement payload, string name)
        {
            try
            {
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty(name, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            }
            catch { /* 读不到按空串 */ }
            return "";
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
            _webMainGen++;   // 快照换人，后台预热循环按代际自行了断

            var cats = new List<object>();
            foreach (var (id, label, group) in WebMainCategories)
            {
                bool ok = string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase);
                int n = 0;
                if (string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase))
                    n = _webMainSongs.Count;
                else if (string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase))
                    n = WebMainAlbumEntries().Count;
                cats.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["t"] = label,
                    ["g"] = group,
                    ["ok"] = ok,
                    ["n"] = n,
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

            // 格式胶囊（编码器/位深·采样率/码率）后台预热：首次访问要开文件读，
            // 云盘曲库下同步算会把 UI 线程卡死——先出列表，胶囊陆续补。
            _webMainChipsWarm = false;
            _ = PrewarmWebMainChipsAsync();

            // 用户先点了专辑、歌曲才推完：顺着把专辑列表补过去（不然网格空着）。
            // 放在胶囊预热之后——PrewarmWebMainAlbumsAsync 不 bump 代际，不会杀了它。
            if (string.Equals(_webMainPage, "Albums", StringComparison.OrdinalIgnoreCase))
                _ = PushWebMainAlbumsAsync();
        }

        /// <summary>
        /// 后台预热格式胶囊，算好一批推一批（kind=chips，网页按歌曲下标更新行的胶囊区）。
        /// 走静态 AudioInfoFormatter.FormatChips（内部 ConcurrentDictionary 缓存，线程安全），
        /// 不走 PlaylistItem.FormatChips 实例属性（那个的懒缓存不是并发安全的）。
        /// 文件读在后台线程，发送切回 UI 线程（PostWebMessageAsString 线程亲和）。
        /// </summary>
        private async Task PrewarmWebMainChipsAsync()
        {
            if (_webMainChipsWarm) return;
            _webMainChipsWarm = true;
            int gen = _webMainGen;   // 快照换了就停：旧下标补到新列表上会张冠李戴
            try
            {
                const int BATCH = 200;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int done = 0;
                while (done < _webMainSongs.Count)
                {
                    if (!_webMainOpen || gen != _webMainGen) break;
                    int end = Math.Min(done + BATCH, _webMainSongs.Count);
                    int from = done;
                    // FormatChips 首次访问要开文件读，必须挪出 UI 线程
                    // （async 方法同步段跑在调用方线程上，不包 Task.Run 第一批就卡界面）
                    var items = await Task.Run(() =>
                    {
                        var batch = new List<object>();
                        for (int i = from; i < end; i++)
                        {
                            var chips = AudioInfoFormatter.FormatChips(_webMainSongs[i].FilePath);
                            var arr = new List<string>();
                            for (int c = 0; c < chips.Count; c++) arr.Add(chips[c]);
                            batch.Add(new Dictionary<string, object?>
                            {
                                ["i"] = i,
                                ["c"] = arr,
                            });
                        }
                        return batch;
                    });
                    var payload = new Dictionary<string, object?>
                    {
                        ["kind"] = "chips",
                        ["items"] = items,
                    };
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webMainOpen) _ = PostCelesteWebAsync(payload);
                    });
                    done = end;
                    if (done % 1000 < BATCH || done >= _webMainSongs.Count)
                        StartupLog.Write($"[Web主界面] 格式胶囊预热 {done}/{_webMainSongs.Count}（{sw.ElapsedMilliseconds} ms）");
                    await Task.Delay(30);
                }
                StartupLog.Write($"[Web主界面] 格式胶囊预热完成，耗时 {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PrewarmWebMainChips", ex);
            }
        }

        /// <summary>当前网页列表：开着专辑详情就用专辑曲目，否则用歌曲快照。
        /// 网页 play/love 的 index 和 C# 的 now 下标都按这份算，两边永远一致。</summary>
        private List<PlaylistItem> WebMainActiveList => _webMainAlbumTracks ?? _webMainSongs;

        /// <summary>
        /// 按原生专辑面板同一套口径分组（BuildAlbumEntriesFromTracks：按专辑名分组，
        /// 封面优先碟1轨1那首），再按专辑名排序（原生默认排序就是按标题）。
        /// 纯内存运算，不开文件——分组 3609 首只要几十毫秒。
        /// </summary>
        private List<AlbumEntry> WebMainAlbumEntries()
        {
            var entries = BuildAlbumEntriesFromTracks(_webMainSongs);
            entries.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return entries;
        }

        /// <summary>把专辑列表推给网页（网格用）。封面不走同步——78 张专辑每张都要解
        /// 一个音频文件，云盘曲库下会把界面卡住；先出列表，封面后台预热按批补。</summary>
        private async Task PushWebMainAlbumsAsync()
        {
            if (!_celesteWebReady) return;
            try
            {
                var entries = WebMainAlbumEntries();
                // 注意：这里不 bump _webMainGen——歌曲推完时会顺着补推专辑（见
                // PushWebMainDataCoreAsync 末尾），bump 会把刚起步的格式胶囊预热杀掉。
                // 旧封面循环的收尾交给"歌曲快照重推"时的代际递增。

                var items = new List<object>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    items.Add(new Dictionary<string, object?>
                    {
                        ["i"] = i,
                        ["name"] = e.Name,
                        ["artist"] = e.Artist,
                        ["year"] = e.Year > 0 ? e.Year.ToString(CultureInfo.InvariantCulture) : "",
                        ["n"] = e.TrackCount,
                    });
                }
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "albums",
                    ["items"] = items,
                    ["total"] = entries.Count,
                });
                StartupLog.Write($"[Web主界面] 专辑列表 {entries.Count} 张已推送");
                _ = PrewarmWebMainAlbumsAsync(entries);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PushWebMainAlbums", ex);
            }
        }

        /// <summary>
        /// 后台预热专辑封面，算好一批推一批（kind=albumcovers，网页按专辑下标更新卡片）。
        /// WriteCoverFile 要解音频文件（云盘曲库下每首几百毫秒），必须走后台线程；
        /// 发送切回 UI 线程（PostWebMessageAsString 线程亲和）。
        /// 每次推专辑列表都会重来一遍：网页收到 albums 会把卡片封面清空。
        /// 代际不相符就停——旧下标补到新列表上会张冠李戴。
        /// </summary>
        private async Task PrewarmWebMainAlbumsAsync(List<AlbumEntry> entries)
        {
            int gen = _webMainGen;
            try
            {
                const int BATCH = 10;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int done = 0;
                while (done < entries.Count)
                {
                    if (!_webMainOpen || gen != _webMainGen) break;
                    int end = Math.Min(done + BATCH, entries.Count);
                    int from = done;
                    // 解封面是文件读，必须挪出 UI 线程（否则第一批就把界面卡住）
                    var items = await Task.Run(() =>
                    {
                        var batch = new List<object>();
                        for (int i = from; i < end; i++)
                        {
                            batch.Add(new Dictionary<string, object?>
                            {
                                ["i"] = i,
                                ["cover"] = WebMainAlbumCover(entries[i]),
                            });
                        }
                        return batch;
                    });
                    var payload = new Dictionary<string, object?>
                    {
                        ["kind"] = "albumcovers",
                        ["items"] = items,
                    };
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webMainOpen) _ = PostCelesteWebAsync(payload);
                    });
                    done = end;
                    await Task.Delay(30);
                }
                StartupLog.Write($"[Web主界面] 专辑封面预热 {done}/{entries.Count}（{sw.ElapsedMilliseconds} ms）");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PrewarmWebMainAlbums", ex);
            }
        }

        /// <summary>专辑封面 URL：优先 AlbumEntry.CoverSourcePath（原生同一口径：
        /// 碟1轨1那首），空了退回专辑内第一首。缓存按曲目路径，重复打开不解第二次。</summary>
        private string WebMainAlbumCover(AlbumEntry entry)
        {
            string first = WebMainAlbumFirstPath(entry.Name);
            foreach (var path in new[] { entry.CoverSourcePath, first })
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!_webMainCoverCache.TryGetValue(path, out var cover))
                {
                    cover = WriteCoverFile(path);
                    _webMainCoverCache[path] = cover;
                }
                if (!string.IsNullOrEmpty(cover)) return cover;
            }
            return "";
        }

        private string WebMainAlbumFirstPath(string albumName)
        {
            return _webMainSongs.FirstOrDefault(t =>
                string.Equals(t.Album, albumName, StringComparison.CurrentCultureIgnoreCase))
                ?.FilePath ?? "";
        }

        /// <summary>
        /// 网页点了一张专辑：把专辑信息和曲目推过去（网页切到详情视图）。
        /// 曲目查询/排序与原生 OpenAlbumDetailCore 完全一致：碟号→音轨号→标题。
        /// </summary>
        private async Task OpenWebMainAlbumAsync(int albumIndex)
        {
            if (!_celesteWebReady || albumIndex < 0) return;
            try
            {
                var entries = WebMainAlbumEntries();
                if (albumIndex >= entries.Count) return;
                var entry = entries[albumIndex];

                List<PlaylistItem> tracks = _webMainSongs
                    .Where(t => string.Equals(t.Album, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (tracks.Count == 0) return;

                _webMainAlbumTracks = tracks;

                string cover = WebMainAlbumCover(entry);
                StartupLog.Write($"[Web主界面] 专辑「{entry.Name}」封面 URL={(string.IsNullOrEmpty(cover) ? "(空)" : cover)}");

                int cur = -1;
                if (!string.IsNullOrEmpty(_nowPlayingPath))
                {
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        if (string.Equals(tracks[i].FilePath, _nowPlayingPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            cur = i;
                            break;
                        }
                    }
                }

                var list = new List<object>();
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    list.Add(new Dictionary<string, object?>
                    {
                        ["n"] = t.Track > 0 ? (int)t.Track : i + 1,
                        ["title"] = t.Title,
                        ["artist"] = t.Artist,
                        ["album"] = t.Album,
                        ["duration"] = t.Duration.TotalSeconds,
                        ["dsd"] = IsDsdFile(t.FilePath),
                        ["fmt"] = CodecOf(t.FilePath),
                        ["cover"] = cover,
                    });
                }

                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "albumtracks",
                    ["album"] = new Dictionary<string, object?>
                    {
                        ["name"] = entry.Name,
                        ["artist"] = entry.Artist,
                        ["year"] = entry.Year > 0 ? entry.Year.ToString(CultureInfo.InvariantCulture) : "",
                        ["n"] = tracks.Count,
                        ["dur"] = entry.TotalDurationText,
                        ["cover"] = cover,
                    },
                    ["tracks"] = list,
                    ["cur"] = cur,
                });
                StartupLog.Write($"[Web主界面] 打开专辑「{entry.Name}」{tracks.Count} 首");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebMainAlbum", ex);
            }
        }

        /// <summary>每秒推送给网页的播放状态。字段名与 main.html 的 'now' 分支对应。</summary>
        private Dictionary<string, object?> BuildWebMainNowMessage()
        {
            // 开着专辑详情时下标按专辑曲目算，不然网页高亮不到正在播的那一行
            var list = WebMainActiveList;
            int idx = -1;
            if (!string.IsNullOrEmpty(_nowPlayingPath))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].FilePath, _nowPlayingPath,
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
                    // 换曲记一次封面 URL（空也记）：网页图裂时靠这行 + covererr 二分定位
                    StartupLog.Write($"[Web主界面] 播放条封面 URL={(string.IsNullOrEmpty(cover) ? "(空)" : cover)}");

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
