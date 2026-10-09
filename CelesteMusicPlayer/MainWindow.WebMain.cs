// 皮肤系统第 4 步：Apple 风格主界面 · 歌曲浏览 + 专辑面板（试点）
//
// 用户 2026-10-09 拍板：先做歌曲浏览面板、专辑面板、（专辑）艺术家页面；
// 2026-10-09 追加：我喜欢的音乐 / 评分 / 最近播放 / 播放队列 / 播放最多五个列表面板
// （行样式与歌曲面板一致，数据来源按原生同名分类口径），侧栏去掉「流派」「年份」，
// 其余分类入口保留（点未做的分类只显示占位，不删入口）。
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
//   C# → 网页：data（categories + 当前面板列表 + cur）/ data/append（歌曲分块，
//              每首带 fav；「最近播放」面板另带 sub 副标题）
//              / chips（格式胶囊预热补）/ albums（专辑网格）
//              / albumcovers（专辑封面预热补）/ albumtracks（专辑详情曲目，含 tech/dsd/disc）
//              / artists（艺术家墙）/ artistcovers（艺术家头像预热补，按名字匹配）
//              / artistdetail（艺术家详情：albums + tracks）
//              / artistavatar（网络头像下载完成，更新详情大头像）
//              / now（播放状态，每秒）/ theme / nav
//   网页 → C#：ready / nav / album（打开专辑，id=专辑下标）
//              / artist（打开艺术家，id=艺术家下标）/ artistback（详情里返回墙）
//              / play / pause / resume / next / prev / seek / volume / love / exit
//              / enqueue（当前列表加入播放队列）/ rating（评分面板星级过滤）
//
// 列表面板（2026-10-09 用户点单）：Favorites / Ratings / Recent / UserPlaylist /
// MostPlayed 与 Songs 共用同一套行样式，数据来源按原生同名分类口径构建
// （见 BuildWebMainList）；侧栏按用户要求去掉「流派」「年份」两项。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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

        /// <summary>网页艺术家详情打开时的曲目列表（play/love 的 index 按这份）。
        /// null = 没开详情。专辑详情优先于它（两者不会同时开）。</summary>
        private List<PlaylistItem>? _webMainArtistTracks;

        /// <summary>网页艺术家详情打开时的专辑表 + 艺术家名。
        /// 网页在详情里点专辑卡只报下标，靠这两项还原专辑名再推专辑详情。</summary>
        private List<AlbumEntry>? _webMainArtistAlbums;
        private string? _openedWebArtistName;

        /// <summary>数据代际：每次重推歌曲快照/专辑列表就 +1。
        /// 后台预热循环（格式胶囊、专辑封面）开跑时记下代际，中途发现变了立刻停——
        /// 否则旧循环会按旧下标把数据补到新列表的行/卡片上，张冠李戴。</summary>
        private int _webMainGen;

        /// <summary>
        /// 侧栏分类清单：照原生侧栏原样保留（CategoryNavPanel 13 项 + NavToolPanel 3 项），
        /// id 与原生按钮 Tag 一致。
        /// 2026-10-09 用户要求：**去掉「流派」「年份」两项**（原生有，网页侧栏不要），
        /// 其余顺序与原生一致。Songs/Albums/Artists/AlbumArtists 及五个列表面板
        /// （Favorites/Ratings/Recent/UserPlaylist/MostPlayed）有数据（ok=true），
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
            ("MostPlayed",    "播放最多",   "music"),
            ("Folders",       "媒体库",     "media"),
            ("WebDav",        "网络音乐库", "media"),
            ("AudioFX",       "音效处理",   "tool"),
            ("TagSort",       "标签排序",   "tool"),
            ("PlaylistWall",  "播放列表",   "tool"),
        };

        /// <summary>列表面板：与歌曲面板同一套行样式，数据来源不同（原生同名分类口径）。
        /// 这些分类的 nav 都走 ok=true + 推列表，网页只换页头标题/副标题。</summary>
        private static readonly string[] WebMainListPanels =
        {
            "Songs", "Favorites", "Ratings", "Recent", "UserPlaylist", "MostPlayed",
        };

        /// <summary>当前列表面板推的是哪份列表（nav 上报时记下，数据推送按它构建）。</summary>
        private string _webMainListKind = "Songs";

        /// <summary>评分面板的星级过滤：-1=全部已评分（原生默认），0=未评分，1..5=对应星级。</summary>
        private int _webMainRatingFilter = -1;

        /// <summary>列表面板行的副标题覆盖（与 <see cref="_webMainSongs"/> 平行，按下标对齐）。
        /// 目前只有「最近播放」用：原生行显示「播放于 MM-dd HH:mm · 播放 m:ss · 播完/未播完」，
        /// 行样式与歌曲面板一致，差在这一行小字（其余面板为空 = 用 艺术家 · 专辑）。</summary>
        private List<string> _webMainSubs = new();

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
            CloseWebMainDetails();
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
                        _ = HandleWebMainNavAsync(ReadNavId(msg.Payload));
                        break;
                    }

                case "album":
                    {
                        // 网页点了一张专辑卡：推专辑详情（曲目查询/排序与原生详情页一致）
                        int ai = ReadInt(msg.Payload, "id", -1);
                        _ = OpenWebMainAlbumAsync(ai);
                        break;
                    }

                case "artist":
                    {
                        // 网页点了一位艺术家卡：推艺术家详情（专辑网格 + 曲目列表）
                        int xi = ReadInt(msg.Payload, "id", -1);
                        _ = OpenWebMainArtistAsync(xi);
                        break;
                    }

                case "artistback":
                    {
                        // 艺术家详情左上角「← 艺术家」：回墙（走 nav，幂等）
                        _ = HandleWebMainNavAsync("Artists");
                        break;
                    }

                case "xtalbum":
                    {
                        // 艺术家详情里点了某张专辑卡：转成专辑详情。
                        // 下标按本艺人专辑表（artistdetail.albums 的顺序），所以要先
                        // 用名字反查回全局专辑下标——墙和详情用的是两套排序。
                        int xi = ReadInt(msg.Payload, "id", -1);
                        _ = OpenWebMainArtistAlbumAsync(xi);
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
                                $"[Web主界面] play：{(_webMainAlbumTracks != null ? "整专" : _webMainArtistTracks != null ? "整位艺术家" : "整表")}替换队列 {list.Count} 首，从第 {i + 1} 首开始");
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

                case "enqueue":
                    {
                        // 详情页/列表面板的「添加至播放队列」：把当前列表整份加进队列
                        // （复用原生 AddSongsToUserPlaylist，不换当前播放）
                        var list = WebMainActiveList;
                        if (list.Count > 0)
                        {
                            AddSongsToUserPlaylist(list);
                            StartupLog.Write($"[Web主界面] enqueue：{list.Count} 首加入播放队列");
                        }
                        break;
                    }

                case "rating":
                    {
                        // 评分面板的星级胶囊：-1=全部已评分，0=未评分，1..5=对应星级
                        int rv = ReadInt(msg.Payload, "value", -1);
                        _webMainRatingFilter = rv is >= -1 and <= 5 ? rv : -1;
                        StartupLog.Write($"[Web主界面] rating 过滤 = {_webMainRatingFilter}");
                        _ = PushWebMainDataAsync();
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
                                // 我喜欢的音乐面板：取消收藏后该行不该还在表里（原生同理，
                                // 取消喜欢会刷新当前分类）——整表重推，行自然消失
                                if (!on && string.Equals(_webMainListKind, "Favorites",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    _ = PushWebMainDataAsync();
                                }
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

        /// <summary>
        /// 侧栏分类切换。已做的面板：Songs / Albums / Artists / AlbumArtists 及五个
        /// 列表面板（Favorites / Ratings / Recent / UserPlaylist / MostPlayed，
        /// 行样式与歌曲面板一致，数据来源按原生同名分类口径）。
        /// 详情里的「← 专辑」「← 艺术家」也走这里（幂等重推）。
        /// 侧栏条目照原生 MainWindow.xaml 搬，**用户 2026-10-09 要求去掉流派/年份**，
        /// 其余一个不多一个不少；要加分类先问过用户。
        /// </summary>
        private async Task HandleWebMainNavAsync(string id)
        {
            _webMainPage = id;
            if (Array.IndexOf(WebMainListPanels, id) >= 0)
            {
                // 列表面板（歌曲 + 我喜欢的音乐/评分/最近播放/播放队列/播放最多）：
                // 关掉详情 + 记下列表种类 + 重推数据（顺带刷新快照）
                CloseWebMainDetails();
                _webMainListKind = id;
                if (string.Equals(id, "Ratings", StringComparison.OrdinalIgnoreCase))
                    _webMainRatingFilter = -1;   // 进评分页重置回「全部已评分」（原生默认）
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = id, ["ok"] = true,
                });
                await PushWebMainDataAsync();
                return;
            }
            if (string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase))
            {
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "Albums", ["ok"] = true,
                });
                await PushWebMainAlbumsAsync();
                return;
            }
            if (string.Equals(id, "Artists", StringComparison.OrdinalIgnoreCase)
             || string.Equals(id, "AlbumArtists", StringComparison.OrdinalIgnoreCase))
            {
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = id, ["ok"] = true,
                });
                await PushWebMainArtistsAsync(id);
                return;
            }
            // 没做的面板：告诉网页显示占位（侧栏条目保留，不删入口）
            StartupLog.Write($"[Web主界面] nav：「{id}」面板还没做，回占位页");
            CloseWebMainDetails();
            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "nav", ["id"] = id, ["ok"] = false,
            });
        }

        /// <summary>离开任何详情页：清掉详情上下文，play/love 的index 回到歌曲快照。</summary>
        private void CloseWebMainDetails()
        {
            _webMainAlbumTracks = null;
            _webMainArtistTracks = null;
            _webMainArtistAlbums = null;
            _openedWebArtistName = null;
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
        /// 把分类清单 + 当前列表面板的快照推给网页。ready 和 nav 都走这里。
        ///
        /// **必须分块**：3609 首打成一个大 JSON（1MB+）用 PostWebMessageAsString
        /// 一次性发，WebView2 渲染进程直接压死（2026-10-09 用户实测卡死）。
        /// 专辑试点页只推 46 首没暴露过这个问题，主界面不行。
        /// 另外 ready 补发 5 次 + 自检探针都会调本方法，用 _webMainPushing 互斥，
        /// 推完一次就拦住后续重复调用（nav 要刷新时先清标志再调）。
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

        /// <summary>
        /// 按 <see cref="_webMainListKind"/> 构建当前面板的列表（原生同名分类口径）：
        ///   Songs       = 整个媒体库（_playlist）
        ///   Favorites   = TrackStatsStore 收藏（曲库优先，文件还在就从路径建）
        ///   Recent      = LibraryDb 播放历史流水（受「最近播放范围」设置过滤）
        ///   UserPlaylist= 播放队列（_userPlaylist）
        ///   MostPlayed  = 曲库里播放次数 >0 的，按次数降序
        ///   Ratings     = 曲库里已评分的；_webMainRatingFilter≥0 时按星级过滤
        /// </summary>
        private List<PlaylistItem> BuildWebMainList(string kind)
        {
            _webMainSubs = new List<string>();   // 与返回列表平行；只有 Recent 会填
            switch (kind)
            {
                case "Favorites":
                    {
                        var items = new List<PlaylistItem>();
                        foreach (string path in TrackStatsStore.GetAllFavorites())
                        {
                            PlaylistItem? fromLib = FindLibraryItemByPath(path);
                            if (fromLib != null) items.Add(ClonePlaylistItem(fromLib));
                            else if (System.IO.File.Exists(path))
                            {
                                try { items.Add(CreatePlaylistItemFromPath(path)); }
                                catch (Exception caught) { StartupLog.WriteException("WebMainList.Favorites", caught); }
                            }
                        }
                        return items;
                    }
                case "Recent":
                    {
                        // 最近播放 = 播放历史事件流水（每次播放一条记录，含播放时间）。
                        // 「最近播放范围」设置：0=全部；N=只显示最近 N 天。
                        int recentRangeDays = AppSettingsStore.Load().RecentPlayedRangeDays;
                        DateTime rangeFromUtc = recentRangeDays > 0
                            ? DateTime.UtcNow.AddDays(-recentRangeDays)
                            : DateTime.MinValue;
                        var items = new List<PlaylistItem>();
                        foreach (LibraryDb.PlaybackHistoryEntry e in LibraryDb.LoadPlaybackHistory(200))
                        {
                            if (e.PlayedAtUtc < rangeFromUtc) continue;
                            if (!System.IO.File.Exists(e.FilePath)) continue;
                            PlaylistItem? fromLib = FindLibraryItemByPath(e.FilePath);
                            PlaylistItem? item = fromLib != null
                                ? ClonePlaylistItem(fromLib)
                                : null;
                            if (item == null)
                            {
                                try { item = CreatePlaylistItemFromPath(e.FilePath); }
                                catch (Exception caught) { StartupLog.WriteException("WebMainList.Recent", caught); }
                            }
                            if (item == null) continue;
                            items.Add(item);
                            // 原生最近播放行的副标题口径（MainWindow.Features.cs ApplyFavoritesOrRecentCategory）
                            _webMainSubs.Add(RecentSubText(e));
                        }
                        return items;
                    }
                case "UserPlaylist":
                    return _userPlaylist.ToList();
                case "MostPlayed":
                    return _playlist
                        .Select(t => (Item: t, Count: TrackStatsStore.Get(t.FilePath)?.PlayCount ?? 0))
                        .Where(x => x.Count > 0)
                        .OrderByDescending(x => x.Count)
                        .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                        .Select(x => ClonePlaylistItem(x.Item))
                        .ToList();
                case "Ratings":
                    return _playlist
                        .Where(t => _webMainRatingFilter < 0
                            ? t.Rating > 0
                            : t.Rating == _webMainRatingFilter)
                        .Select(ClonePlaylistItem)
                        .ToList();
                default:
                    return _playlist.ToList();
            }
        }

        /// <summary>最近播放行的副标题（原生口径）：播放时间 + 播放时长 + 是否播完。</summary>
        private static string RecentSubText(LibraryDb.PlaybackHistoryEntry e)
        {
            string timeText = e.PlayedAtUtc == DateTime.MinValue
                ? "—"
                : e.PlayedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
            string durText = e.PlayedSeconds < 1
                ? "—"
                : e.PlayedSeconds < 60
                    ? ((int)e.PlayedSeconds) + " 秒"
                    : TimeSpan.FromSeconds(e.PlayedSeconds).ToString(@"m\:ss");
            return "播放于 " + timeText + " · 播放 " + durText + " · " + (e.Completed ? "播完" : "未播完");
        }

        /// <summary>侧栏计数（原生同名分类口径）。 Songs 用当前快照数。</summary>
        private int WebMainPanelCount(string id)
        {
            if (string.Equals(id, "Songs", StringComparison.OrdinalIgnoreCase))
                return _webMainSongs.Count;
            if (string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase))
                return WebMainAlbumEntries().Count;
            if (string.Equals(id, "Artists", StringComparison.OrdinalIgnoreCase)
             || string.Equals(id, "AlbumArtists", StringComparison.OrdinalIgnoreCase))
                return WebMainArtistGroups(
                    string.Equals(id, "AlbumArtists", StringComparison.OrdinalIgnoreCase)).Count;
            if (string.Equals(id, "Favorites", StringComparison.OrdinalIgnoreCase))
                return TrackStatsStore.GetAllFavorites().Count;
            if (string.Equals(id, "Recent", StringComparison.OrdinalIgnoreCase))
            {
                int recentRangeDays = AppSettingsStore.Load().RecentPlayedRangeDays;
                DateTime rangeFromUtc = recentRangeDays > 0
                    ? DateTime.UtcNow.AddDays(-recentRangeDays)
                    : DateTime.MinValue;
                int n = 0;
                foreach (var e in LibraryDb.LoadPlaybackHistory(200))
                    if (e.PlayedAtUtc >= rangeFromUtc) n++;
                return n;
            }
            if (string.Equals(id, "UserPlaylist", StringComparison.OrdinalIgnoreCase))
                return _userPlaylist.Count;
            if (string.Equals(id, "MostPlayed", StringComparison.OrdinalIgnoreCase))
                return _playlist.Count(t => (TrackStatsStore.Get(t.FilePath)?.PlayCount ?? 0) > 0);
            if (string.Equals(id, "Ratings", StringComparison.OrdinalIgnoreCase))
                return _playlist.Count(t => t.Rating > 0);
            return 0;
        }

        private async Task PushWebMainDataCoreAsync()
        {
            // 快照：网页的 index 与这份一一对应（play/love 都按它下标）
            _webMainSongs = BuildWebMainList(_webMainListKind);
            _webMainGen++;   // 快照换人，后台预热循环按代际自行了断

            var cats = new List<object>();
            foreach (var (id, label, group) in WebMainCategories)
            {
                bool ok = Array.IndexOf(WebMainListPanels, id) >= 0
                       || string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "Artists", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "AlbumArtists", StringComparison.OrdinalIgnoreCase);
                cats.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["t"] = label,
                    ["g"] = group,
                    ["ok"] = ok,
                    ["n"] = WebMainPanelCount(id),
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
            StartupLog.Write($"[Web主界面] 开始分块推送[{_webMainListKind}] {_webMainSongs.Count} 首");
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
                        // 收藏态逐首带（我喜欢的音乐面板要显示实心红心；
                        // TrackStatsStore.Get 走内存缓存，3609 次查表不读文件）
                        ["fav"] = TrackStatsStore.Get(t.FilePath)?.IsFavorite == true,
                        // 行副标题覆盖：只有「最近播放」面板有值（播放时间+时长+播完），
                        // 其余为空 = 网页用 艺术家 · 专辑
                        ["sub"] = i < _webMainSubs.Count ? _webMainSubs[i] : "",
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

            // 专辑列表 + 封面预热：**不分当前停在哪个页面都要跑**——
            // 歌曲列表每行的封面就是靠专辑封面回填的（不逐首解封面），
            // 只在 Albums 页推的话，停在 Songs 页就永远只有首字符（2026-10-09 用户实测）。
            // 网页收到 albums 时只在 Albums 页才画网格，这里推了也不影响。
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

        /// <summary>当前网页列表：开着专辑详情就用专辑曲目，开着艺术家详情就用艺术家曲目，
        /// 否则用歌曲快照。网页 play/love 的 index 和 C# 的 now 下标都按这份算，两边永远一致。</summary>
        private List<PlaylistItem> WebMainActiveList
            => _webMainAlbumTracks ?? _webMainArtistTracks ?? _webMainSongs;

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
                                // 专辑名一起带上：网页按专辑名把封面回填给该专辑下的
                                // 所有歌曲行（歌曲快照不逐首解封面，3609 首读不动）。
                                ["album"] = entries[i].Name,
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

        /* ================= 艺术家 / 专辑艺术家 =================
           分组口径完全照原生 RefreshArtistViewAsync：按 Artist（AlbumArtists 页用
           AlbumArtist）分组、名字升序。详情页结构也照原生 OpenArtistDetailCore：
           专辑网格 + 曲目列表两段。

           头像口径与原生 ResolveArtistAvatarAsync 一致（2026-10-09 用户要求
           「和主界面保持一致，优先网络源」）：
             ① 用户自定义头像（右键设置的本地图，ArtistAvatars/&lt;hash&gt;.png）
             ② 网络头像（网易云/iTunes，按设置的面像来源；磁盘缓存在
                ArtistAvatars/Web/&lt;hash&gt;.jpg，没有就后台下载后落盘）
             ③ 兜底：该艺术家年份最晚专辑中音轨 1 的封面（原生同口径） */

        /// <summary>艺术家分组：名字 + 曲目数 + 一张兜底封面路径（年份最晚专辑的音轨1）。</summary>
        private List<(string Name, int Count, string CoverPath)> WebMainArtistGroups(bool albumArtistMode)
        {
            var groups = _webMainSongs
                .GroupBy(
                    t => albumArtistMode ? t.AlbumArtist : t.Artist,
                    StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return groups
                .Select(g =>
                {
                    // 兜底封面口径照原生 ResolveArtistDefaultAvatarAsync：
                    // 年份最晚的专辑（同年按名字倒序）的 CoverSourcePath，空了退回组内第一首
                    string fallback = g.First().FilePath;
                    AlbumEntry? latest = BuildAlbumEntriesFromTracks(g.ToList())
                        .OrderByDescending(a => a.Year)
                        .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .FirstOrDefault();
                    if (latest != null && !string.IsNullOrWhiteSpace(latest.CoverSourcePath))
                        fallback = latest.CoverSourcePath;
                    return (Name: g.Key ?? "", Count: g.Count(), CoverPath: fallback);
                })
                .ToList();
        }

        /// <summary>头像文件拷贝缓存（源路径 → 虚拟域名 URL）。自定义/网络头像都存在
        /// 本地磁盘上，拷贝进 WebAssets 才能被网页按 http://celeste.local/ 引用。</summary>
        private readonly Dictionary<string, string> _webAvatarFileCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>把本地头像文件拷进 WebAssets/avatars 并返回 URL（按路径+修改时间去重，不重复拷）。</summary>
        private string WebAvatarFileUrl(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return "";
            if (_webAvatarFileCache.TryGetValue(sourcePath, out var cached)) return cached;
            try
            {
                var fi = new FileInfo(sourcePath);
                string key = fi.FullName + "|" + fi.LastWriteTimeUtc.Ticks;
                byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
                string name = "av_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
                string ext = fi.Extension.ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg")) ext = ".png";
                name += ext;
                string dir = Path.Combine(WebAssetRoot, "avatars");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, name);
                if (!File.Exists(dst)) File.Copy(sourcePath, dst, true);
                string url = "http://celeste.local/avatars/" + name;
                _webAvatarFileCache[sourcePath] = url;
                return url;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebAvatarFileUrl", ex);
                return "";
            }
        }

        /// <summary>下载中的艺术家名（防重复联网）。自定义/网络缓存命中就不下载。</summary>
        private readonly HashSet<string> _webAvatarDownloading = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim WebAvatarDownloadGate = new(4, 4);

        /// <summary>
        /// 解析艺术家头像 URL（原生优先级：自定义 → 网络 → 专辑封面兜底）。
        /// 网络头像没有磁盘缓存时顺手在后台下载，不阻塞当前推送。
        /// </summary>
        private string WebMainArtistAvatarUrl(string artistName, bool albumArtistMode, string fallbackCoverPath)
        {
            if (string.IsNullOrEmpty(artistName)) return "";
            string storeKey = ArtistAvatarStoreKey(artistName, albumArtistMode);

            // ① 用户自定义头像（album artist 模式的 key 带 "aa|" 前缀，与原生一致）
            string custom = ArtistAvatarStore.GetAvatarFilePath(storeKey);
            if (File.Exists(custom))
            {
                string url = WebAvatarFileUrl(custom);
                if (!string.IsNullOrEmpty(url)) return url;
            }

            // ② 网络头像磁盘缓存（原生按「艺术家名」存，不带 aa| 前缀）
            string webFile = ArtistAvatarStore.GetWebAvatarFilePath(artistName.Trim());
            if (File.Exists(webFile))
            {
                string url = WebAvatarFileUrl(webFile);
                if (!string.IsNullOrEmpty(url)) return url;
                _ = EnsureWebArtistAvatarAsync(artistName);   // 缓存文件坏了，重新拉
                return WebMainTrackCover(fallbackCoverPath);
            }

            // ③ 都没有：先用专辑封面顶上，同时后台联网拉头像
            _ = EnsureWebArtistAvatarAsync(artistName);
            return WebMainTrackCover(fallbackCoverPath);
        }

        /// <summary>
        /// 后台下载网络头像（原生 TryLoadWebArtistAvatarAsync 的网页版）：
        /// 按设置的面像来源搜 URL → 下载 → 落盘 ArtistAvatars/Web（重启免再搜）→
        /// 拷进 WebAssets → 推给网页（墙按名字匹配，详情按 artistavatar 消息）。
        /// 同一艺术家只拉一次（_webAvatarDownloading 去重）；失败静默，兜底封面照用。
        /// </summary>
        private async Task EnsureWebArtistAvatarAsync(string artistName)
        {
            string key = (artistName ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return;
            lock (_webAvatarDownloading)
            {
                if (!_webAvatarDownloading.Add(key)) return;   // 已在拉
            }
            try
            {
                await WebAvatarDownloadGate.WaitAsync();
                string webFile = ArtistAvatarStore.GetWebAvatarFilePath(key);
                if (File.Exists(webFile))
                {
                    PushWebArtistAvatar(key, WebAvatarFileUrl(webFile));
                    return;
                }
                string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(key);
                if (string.IsNullOrWhiteSpace(url)) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                byte[] bytes = await HttpClients.Default.GetByteArrayAsync(url, cts.Token);
                if (bytes.Length == 0) return;
                await ArtistAvatarStore.SaveWebAsync(key, bytes);   // 落盘，重启免再搜
                PushWebArtistAvatar(key, WebAvatarFileUrl(webFile));
            }
            catch (Exception caught)
            {
                // 联网失败不致命：墙/详情继续用专辑封面兜底
                StartupLog.WriteException("EnsureWebArtistAvatar", caught);
            }
            finally
            {
                WebAvatarDownloadGate.Release();
                lock (_webAvatarDownloading) { _webAvatarDownloading.Remove(key); }
            }
        }

        /// <summary>头像下载完成：推给网页。墙按名字匹配（防下标张冠李戴），
        /// 正在看的艺术家详情另收一条 artistavatar。下载在后台线程完成，
        /// 发送切回 UI 线程（PostWebMessageAsString 线程亲和）。</summary>
        private void PushWebArtistAvatar(string artistName, string coverUrl)
        {
            if (string.IsNullOrEmpty(coverUrl) || !_webMainOpen) return;
            StartupLog.Write($"[Web主界面] 艺术家「{artistName}」网络头像已就绪");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_webMainOpen) return;
                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "artistcovers",
                    ["items"] = new List<object>
                    {
                        new Dictionary<string, object?> { ["name"] = artistName, ["cover"] = coverUrl },
                    },
                });
                if (string.Equals(_openedWebArtistName, artistName, StringComparison.OrdinalIgnoreCase))
                {
                    _ = PostCelesteWebAsync(new Dictionary<string, object?>
                    {
                        ["kind"] = "artistavatar",
                        ["cover"] = coverUrl,
                    });
                }
            });
        }

        /// <summary>推艺术家墙（网格）。封面先空着，artistcovers 按批补——
        /// 解封面要开音频文件，同步做会把界面卡住。</summary>
        private async Task PushWebMainArtistsAsync(string categoryId)
        {
            if (!_celesteWebReady) return;
            try
            {
                bool albumArtistMode = string.Equals(categoryId, "AlbumArtists", StringComparison.OrdinalIgnoreCase);
                var groups = WebMainArtistGroups(albumArtistMode);
                var items = new List<object>();
                for (int i = 0; i < groups.Count; i++)
                {
                    items.Add(new Dictionary<string, object?>
                    {
                        ["i"] = i,
                        ["name"] = groups[i].Name,
                        ["n"] = groups[i].Count,
                    });
                }
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "artists",
                    ["mode"] = albumArtistMode ? "albumartist" : "artist",
                    ["items"] = items,
                    ["total"] = groups.Count,
                });
                StartupLog.Write($"[Web主界面] 艺术家墙 {groups.Count} 位已推送（{categoryId}）");
                _ = PrewarmWebMainArtistCoversAsync(groups, albumArtistMode);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PushWebMainArtists", ex);
            }
        }

        /// <summary>后台预热艺术家头像（自定义→网络→专辑封面，原生优先级）。
        /// 算好一批推一批（kind=artistcovers，网页按艺术家名字更新卡片）。
        /// 网络头像的下载在EnsureWebArtistAvatarAsync 里自行补推，这里只推立即可得的。</summary>
        private async Task PrewarmWebMainArtistCoversAsync(
            List<(string Name, int Count, string CoverPath)> groups, bool albumArtistMode)
        {
            int gen = _webMainGen;
            try
            {
                const int BATCH = 10;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int done = 0;
                while (done < groups.Count)
                {
                    if (!_webMainOpen || gen != _webMainGen) break;
                    int end = Math.Min(done + BATCH, groups.Count);
                    int from = done;
                    var items = await Task.Run(() =>
                    {
                        var batch = new List<object>();
                        for (int i = from; i < end; i++)
                        {
                            batch.Add(new Dictionary<string, object?>
                            {
                                ["i"] = i,
                                ["name"] = groups[i].Name,
                                ["cover"] = WebMainArtistAvatarUrl(
                                    groups[i].Name, albumArtistMode, groups[i].CoverPath),
                            });
                        }
                        return batch;
                    });
                    var payload = new Dictionary<string, object?>
                    {
                        ["kind"] = "artistcovers",
                        ["mode"] = albumArtistMode ? "albumartist" : "artist",
                        ["items"] = items,
                    };
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webMainOpen) _ = PostCelesteWebAsync(payload);
                    });
                    done = end;
                    await Task.Delay(30);
                }
                StartupLog.Write(
                    $"[Web主界面] 艺术家头像预热 {done}/{groups.Count}（{sw.ElapsedMilliseconds} ms）");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PrewarmWebMainArtistCovers", ex);
            }
        }

        /// <summary>按曲目路径取封面 URL（走同一份路径缓存；解不开返回空）。</summary>
        private string WebMainTrackCover(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            if (!_webMainCoverCache.TryGetValue(filePath, out var cover))
            {
                cover = WriteCoverFile(filePath);
                _webMainCoverCache[filePath] = cover;
            }
            return cover ?? "";
        }

        /// <summary>网页点了一位艺术家：推艺术家详情（专辑网格 + 曲目列表）。
        /// 分组与排序全部照原生 OpenArtistDetailCore / RebuildArtistTracks /
        /// RebuildArtistAlbumsAsync 同一口径。</summary>
        private async Task OpenWebMainArtistAsync(int artistIndex)
        {
            if (!_celesteWebReady || artistIndex < 0) return;
            try
            {
                bool albumArtistMode = string.Equals(_webMainPage, "AlbumArtists", StringComparison.OrdinalIgnoreCase);
                var groups = WebMainArtistGroups(albumArtistMode);
                if (artistIndex >= groups.Count) return;
                var g = groups[artistIndex];
                if (string.IsNullOrEmpty(g.Name)) return;

                List<PlaylistItem> tracks = _webMainSongs
                    .Where(t => string.Equals(albumArtistMode ? t.AlbumArtist : t.Artist, g.Name,
                            StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
                if (tracks.Count == 0) return;

                // 曲目排序：原生默认 _artistSongSortMode=Title（按标题）。
                var sorted = tracks
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                // 专辑段：原生用 BuildAlbumEntriesFromTracks + ApplyArtistAlbumSort（默认按标题）
                var albumEntries = BuildAlbumEntriesFromTracks(sorted);
                albumEntries = albumEntries
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                _webMainArtistTracks = sorted;
                _webMainArtistAlbums = albumEntries;
                _openedWebArtistName = g.Name;
                _webMainAlbumTracks = null;   // 两类详情互斥

                string cover = WebMainArtistAvatarUrl(g.Name, albumArtistMode, g.CoverPath);
                StartupLog.Write(
                    $"[Web主界面] 打开艺术家「{g.Name}」{sorted.Count} 首 / {albumEntries.Count} 张专辑");

                int cur = -1;
                if (!string.IsNullOrEmpty(_nowPlayingPath))
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (string.Equals(sorted[i].FilePath, _nowPlayingPath,
                                StringComparison.CurrentCultureIgnoreCase))
                        {
                            cur = i;
                            break;
                        }
                    }
                }

                var albums = new List<object>();
                for (int i = 0; i < albumEntries.Count; i++)
                {
                    var a = albumEntries[i];
                    albums.Add(new Dictionary<string, object?>
                    {
                        ["i"] = i,
                        ["name"] = a.Name,
                        ["artist"] = a.Artist,
                        ["year"] = a.Year > 0 ? a.Year.ToString(CultureInfo.InvariantCulture) : "",
                        ["n"] = a.TrackCount,
                        ["cover"] = WebMainAlbumCover(a),
                    });
                }

                var list = new List<object>();
                for (int i = 0; i < sorted.Count; i++)
                {
                    var t = sorted[i];
                    list.Add(new Dictionary<string, object?>
                    {
                        ["n"] = i + 1,
                        ["title"] = t.Title,
                        ["artist"] = t.Artist,
                        ["album"] = t.Album,
                        ["duration"] = t.Duration.TotalSeconds,
                        ["dsd"] = IsDsdFile(t.FilePath),
                        ["fmt"] = CodecOf(t.FilePath),
                        ["cover"] = cover,
                        ["fav"] = TrackStatsStore.Get(t.FilePath)?.IsFavorite == true,
                    });
                }

                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "artistdetail",
                    ["artist"] = new Dictionary<string, object?>
                    {
                        ["name"] = g.Name,
                        ["n"] = sorted.Count,
                        ["dur"] = mmssOf(sorted),
                        ["cover"] = cover,
                    },
                    ["albums"] = albums,
                    ["tracks"] = list,
                    ["cur"] = cur,
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebMainArtist", ex);
            }
        }

        /// <summary>总时长文本（h:mm:ss 或 m:ss，对齐原生 TotalDurationText 口径）。</summary>
        private static string mmssOf(List<PlaylistItem> tracks)
        {
            double total = 0;
            for (int i = 0; i < tracks.Count; i++) total += tracks[i].Duration.TotalSeconds;
            var span = TimeSpan.FromSeconds(total);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
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
                await PushWebMainAlbumDetailAsync(entries[albumIndex]);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebMainAlbum", ex);
            }
        }

        /// <summary>艺术家详情里点了张专辑卡：下标按该艺人的专辑表（artistdetail.albums 顺序），
        /// 还原成专辑名再走同一条详情推送。别拿下标去撞全局专辑表——两套排序范围不同，
        /// 只有名字是对齐键。</summary>
        private async Task OpenWebMainArtistAlbumAsync(int artistAlbumIndex)
        {
            if (!_celesteWebReady || artistAlbumIndex < 0) return;
            try
            {
                bool albumArtistMode = string.Equals(_webMainPage, "AlbumArtists", StringComparison.OrdinalIgnoreCase);
                var groups = WebMainArtistGroups(albumArtistMode);
                string artistName = _openedWebArtistName ?? "";
                if (string.IsNullOrEmpty(artistName) || _webMainArtistAlbums == null) return;
                if (artistAlbumIndex >= _webMainArtistAlbums.Count) return;

                string albumName = _webMainArtistAlbums[artistAlbumIndex].Name;
                var entries = WebMainAlbumEntries();
                var entry = entries.FirstOrDefault(a =>
                    string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
                if (entry == null)
                {
                    StartupLog.Write($"[Web主界面] xtalbum：专辑「{albumName}」在专辑表里找不到，忽略");
                    return;
                }
                _ = groups;   // 分组已随 artistdetail 推过，这里只用 artistName
                await PushWebMainAlbumDetailAsync(entry);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebMainArtistAlbum", ex);
            }
        }

        /// <summary>专辑详情推送（专辑墙点卡片、艺术家详情点专辑卡都走这里）。
        /// 曲目查询/排序与原生 OpenAlbumDetailCore 完全一致：碟号→音轨号→标题。
        /// 布局元素也照原生：左栏大封面 + 质量行（编码器|位深/采样率）+ DSD 提示，
        /// 右栏音轨号|标题|时长（有碟号时按 CD 分组）。</summary>
        private async Task PushWebMainAlbumDetailAsync(AlbumEntry entry)
        {
                List<PlaylistItem> tracks = _webMainSongs
                    .Where(t => string.Equals(t.Album, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (tracks.Count == 0) return;

                _webMainAlbumTracks = tracks;
                _webMainArtistTracks = null;   // 两类详情互斥：开着专辑就别再按艺人曲目算下标

                string cover = WebMainAlbumCover(entry);
                StartupLog.Write($"[Web主界面] 专辑「{entry.Name}」封面 URL={(string.IsNullOrEmpty(cover) ? "(空)" : cover)}");

                // 质量行 + DSD 提示（原生行3 / DsdHint 口径）：读文件，挪后台线程
                string tech = await Task.Run(() =>
                {
                    string quality = BuildAlbumQualityLine(tracks);
                    string text = string.IsNullOrWhiteSpace(quality)
                        ? entry.TotalDurationText
                        : quality.Replace(" · ", " | ") + " | " + entry.TotalDurationText;
                    return text;
                });
                bool allDsd = tracks.All(t => IsDsdFile(t.FilePath));

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
                        ["disc"] = t.Disc > 0 ? (int)t.Disc : 0,
                        ["title"] = t.Title,
                        ["artist"] = t.Artist,
                        ["album"] = t.Album,
                        ["duration"] = t.Duration.TotalSeconds,
                        ["dsd"] = IsDsdFile(t.FilePath),
                        ["fmt"] = CodecOf(t.FilePath),
                        ["cover"] = cover,
                        ["fav"] = TrackStatsStore.Get(t.FilePath)?.IsFavorite == true,
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
                        ["tech"] = tech,
                        ["dsd"] = allDsd,
                    },
                    ["tracks"] = list,
                    ["cur"] = cur,
                });
                StartupLog.Write($"[Web主界面] 打开专辑「{entry.Name}」{tracks.Count} 首");
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
