// 皮肤系统第 4 步：Apple 风格主界面 · 歌曲浏览 + 专辑面板
//
// 用户 2026-10-09 拍板：先做歌曲浏览面板、专辑面板、（专辑）艺术家页面；
// 2026-10-09 追加：我喜欢的音乐 / 评分 / 最近播放 / 播放队列 / 播放最多五个列表面板
// （行样式与歌曲面板一致，数据来源按原生同名分类口径），侧栏去掉「流派」「年份」，
// 其余分类入口保留（点未做的分类只显示占位，不删入口）。
// 2026-10-09 再追加：媒体库（文件夹浏览）面板，布局/交互照原生 FolderBrowserView。
// 2026-10-09 三追加：标签排序（TagSort）面板——分类字段按钮组 + 分类卡墙 +
// 钻取面板（曲目/专辑/艺术家/排序方式/分组浏览五个视角），口径照原生
// TagSortBorder（MainWindow.Library.cs 标签排序板块 + MainWindow.Playback.cs
// 分类墙/分组）。列配置/自定义排序/自定义分组/分类字段配置四个弹窗直接复用
// 原生窗口（网页只发意图，C# 开窗、回调里重推）。
// 2026-10-09 四追加：网络音乐库（WebDav）——界面复用媒体库那套（左树右栏），
// 数据源换 WebDavClient；没配 WebDAV 时侧栏条目不显示（用户明确要求）。
// 2026-10-09 五追加：播放列表墙 + 命名单详情（口径照原生 PlaylistWall）。
//
// 分工铁律：**网页只负责长什么样，一件事都不做**——点播放、切分类、拖进度、
// 收藏全都上报给 C#，由 C# 调现有的播放方法。音频链路（独占 / bit-perfect /
// DSD / DSP）一行不用碰。
//
// 与 Apple 风格专辑页（WebAlbum.cs）的关系：
//   - 共用同一个常驻 WebView2 宿主与覆盖层（CelesteWebHostGrid），两个页面互斥打开；
//   - play 语义与 Apple 风格专辑页一致（d3c0d99 定稿）：**整表替换播放队列 + 从点中的行开始**，
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
//              / folderroots（媒体库根目录）/ folderchildren（展开一层的子项）
//              / foldersongs（右栏歌曲：文件名 + 艺术家·专辑 + 时长）
//              / tagsortcats（标签排序：字段按钮组 + 分类卡 + total/shown）
//              / tagsortpanel（标签排序面板：mode/title/cols/songs/grid/sort/group）
//              / tagsortcovers（标签排序卡片封面按批补，items:[{i,cover}]）
//              / now（播放状态，每秒）/ theme / nav
//   网页 → C#：ready / nav / album（打开专辑，id=专辑下标）
//              / artist（打开艺术家，id=艺术家下标）/ artistback（详情里返回墙）
//              / folder（展开文件夹，path）/ folderplay（播放媒体库里某个文件，path）
//              / tagsortfield（切分类字段，key）/ tagsortopen（点分类卡，name）
//              / tagsortdrill（专辑/艺术家卡钻取，name+sub）/ tagsortmode（切视角，mode）
//              / tagsortback（回分类墙）/ tagsortcol（列头点击排序，key）
//              / tagsortplay（播放当前列表）/ tagsortsong（播分类曲目，index）
//              / tagsortgroupplay（播整组，path）/ tagsortgroupsong（播组内歌曲，path+group+file）
//              / tagsorttoggle（组头展开折叠，path）/ tagsortexpand（全部展开/折叠，on）
//              / tagsortgroup（分组预设切换，tag）/ tagsortcustom（开原生配置窗，what）
//              / tagsortsort（排序方式预设，preset）/ tagsortorder（排序升/降序，asc）
//              / tagsortmore（分类墙加载更多）
//              / plopen（打开播放列表详情，name）/ plplay（详情行播放，name+path）
//              / plqueue（整单加入播放队列，name）/ pldel（删除播放列表，name）
//              / plrename（重命名，name+newName）/ plnew（新建播放列表）
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

                // 两个网页覆盖层互斥：开主界面先关 Apple 风格专辑页（反之亦然）
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

        /// <summary>关掉主界面 Apple 风格页，回到原生界面。</summary>
        public void CloseWebMain()
        {
            if (!_webMainOpen) return;
            _webMainOpen = false;
            CloseEmbeddedWebDsp();     // 右栏可能还开着音效处理面板，一起收掉
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
        /// 主界面 Apple 风格页自检：Navigate 之后隔一会儿探页面真实状态，写进日志。
        ///   wv    = 页面里有没有 WebView2 宿主对象（没有 → 页面脚本被竞态打断）
        ///   rows  = 曲目行渲染了几行（>0 说明歌曲数据已经到达页面）
        ///   cards = 专辑卡渲染了几张（专辑网格视图下 rows 本来就是 0）
        ///   title = 页头显示的标题
        /// wv=yes 却一行都没有 → 不等网页的 ready 上行，C# 主动推一次（与 Apple 风格专辑页同款兜底）。
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

            // 右栏开着音效处理面板时，DSP 那几条消息转交给 MainWindow.WebDsp.cs
            // （面板嵌在主界面里，网页上行不经过 dsp.html，得在这里分流）
            if (WebDspEmbedded && IsWebDspKind(msg.Kind))
            {
                HandleWebDspMessage(msg);
                return;
            }

            switch (msg.Kind)
            {
                case "ready":
                    // 页面初始化完成：分类清单 + 歌曲快照一起推过去
                    _ = PushWebMainDataAsync();
                    break;

                case "lyricsreq":
                    // 网页要当前歌词（页面打开时、换曲时各发一次）。加载完的才推，
                    // 没加载完就静默跳过——网页继续显示上一首的，和原生同一行为。
                    PushWebLyrics();
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

                case "folder":
                    {
                        // 媒体库/网络音乐库：网页点开一个文件夹（原生点箭头/双击的口径——
                        // 展开一层 + 把该文件夹的歌曲加载到右栏）。wd=true 走 WebDAV 分支。
                        string folderPath = ReadStr(msg.Payload, "path");
                        bool wd = ReadBool(msg.Payload, "wd");
                        if (wd) _ = PushWebDavChildrenAsync(folderPath);
                        else _ = PushWebFolderChildrenAsync(folderPath);
                        break;
                    }

                case "folderplay":
                    {
                        // 媒体库/网络音乐库点了一个文件：入库并直接播（原生双击文件口径，
                        // 不整表替换队列）。wd=true 走 WebDAV 分支（先下缓存再播）。
                        string filePath = ReadStr(msg.Payload, "path");
                        bool wd = ReadBool(msg.Payload, "wd");
                        if (wd) _ = PlayWebDavFileAsync(filePath);
                        else if (!string.IsNullOrEmpty(filePath))
                        {
                            PlaylistItem? track = EnsureTrackInLibrary(filePath);
                            if (track != null) PlayPlaylistItem(track);
                        }
                        break;
                    }

                case "plopen":
                    {
                        // 播放列表墙点卡：推该单详情（原生 ShowPlaylistDetail 口径）
                        string name = ReadStr(msg.Payload, "name");
                        if (!string.IsNullOrEmpty(name)) _ = PushWebPlaylistDetailAsync(name);
                        break;
                    }

                case "plplay":
                    {
                        // 详情行播放：整单替换队列、从该首起（原生 PlayNamedPlaylistFromTrack）
                        // path 为空 = 从整单第一首开始（2026-10-10：墙右键「播放该播放列表」）
                        string name = ReadStr(msg.Payload, "name");
                        string filePath = ReadStr(msg.Payload, "path");
                        if (string.IsNullOrEmpty(name)) break;
                        if (string.IsNullOrEmpty(filePath))
                        {
                            var all = NamedPlaylistStore.LoadSongs(name);
                            for (int pi = 0; pi < all.Count; pi++)
                            {
                                if (System.IO.File.Exists(all[pi])) { filePath = all[pi]; break; }
                            }
                        }
                        if (!string.IsNullOrEmpty(filePath)) PlayNamedPlaylistFromTrack(name, filePath);
                        break;
                    }

                case "plqueue":
                    {
                        // 详情「添加至播放队列」：整单追加到当前队列（原生 AddNamedPlaylistToQueue）
                        string name = ReadStr(msg.Payload, "name");
                        if (!string.IsNullOrEmpty(name)) AddNamedPlaylistToQueue(name);
                        break;
                    }

                case "pldel":
                    {
                        // 详情/墙删除播放列表（原生墙右键删除口径）
                        string name = ReadStr(msg.Payload, "name");
                        if (!string.IsNullOrEmpty(name)) _ = DeleteWebPlaylistAsync(name);
                        break;
                    }

                case "plrename":
                    {
                        // 详情重命名（原生 RenamePlaylistFromWallAsync 口径，失败推 plmsg）
                        string name = ReadStr(msg.Payload, "name");
                        string newName = ReadStr(msg.Payload, "newName");
                        if (!string.IsNullOrEmpty(name)) _ = RenameWebPlaylistAsync(name, newName);
                        break;
                    }

                case "plnew":
                    {
                        // 墙头「新建播放列表」（原生 CreatePlaylistWallButton_Click 口径）
                        _ = CreateWebPlaylistAsync();
                        break;
                    }

                case "tagsortfield":
                    {
                        // 标签排序：切分类字段（原生 TagSortFieldBoundButton_Click 口径：
                        // 换字段即回分类墙重算分组）
                        string key = ReadStr(msg.Payload, "key");
                        if (!string.IsNullOrEmpty(key) && _tagSortCategoryFields.Contains(key))
                        {
                            _webTagSortField = key;
                            _tagSortClassField = key;
                            _webTagSortValue = "";
                            _webTagSortTitle = "";
                            _webTagSortMode = "Wall";
                            _ = PushWebTagSortWallAsync();
                        }
                        break;
                    }

                case "tagsortopen":
                    {
                        // 点分类卡：进入该分类的曲目面板（原生 TagSortClassGridView_ItemClick）
                        string name = ReadStr(msg.Payload, "name");
                        if (!string.IsNullOrEmpty(name)) _ = OpenWebTagSortCategoryAsync(name);
                        break;
                    }

                case "tagsortdrill":
                    {
                        // 专辑/艺术家视角点卡：在当前分类曲目里再按子值过滤
                        // （原生 TagSortPanelGridView_ItemClick 口径）
                        string name = ReadStr(msg.Payload, "name");
                        string sub = ReadStr(msg.Payload, "sub");
                        if (!string.IsNullOrEmpty(name)) _ = OpenWebTagSortDrillAsync(name, sub);
                        break;
                    }

                case "tagsortmode":
                    {
                        // 视角切换：Songs / Albums / Artists / Sort / GroupBy
                        // （原生 TagSortViewModeItem_Click 口径）
                        string mode = ReadStr(msg.Payload, "mode");
                        if (mode is "Songs" or "Albums" or "Artists" or "Sort" or "GroupBy")
                        {
                            // 分类墙上的「分组浏览」按钮（原生 TagSortClassWallSwitchToGroupButton_Click）：
                            // 从墙上进分组时分组字段与当前分类字段一致（所见即所得）；
                            // 已进分类后再切分组不动字段序列（原生同样只在那一个入口重置）
                            if (string.Equals(mode, "GroupBy", StringComparison.Ordinal)
                                && string.Equals(_webTagSortMode, "Wall", StringComparison.Ordinal))
                            {
                                EnterWebTagSortGroupMode();
                            }
                            _webTagSortMode = mode;
                            _ = PushWebTagSortPanelAsync();
                        }
                        break;
                    }

                case "tagsortback":
                    {
                        // 面板左上角返回：回分类墙（原生 TagSortPanelBackButton_Click）
                        _webTagSortValue = "";
                        _webTagSortTitle = "";
                        _webTagSortMode = "Wall";
                        _ = PushWebTagSortWallAsync();
                        break;
                    }

                case "tagsortcol":
                    {
                        // 列头点击排序（原生 TagSortColumnHeader_Click：同列切升降序，新列升序）
                        string key = ReadStr(msg.Payload, "key");
                        ApplyWebTagSortColumnSort(key);
                        break;
                    }

                case "tagsortplay":
                    {
                        // 播放当前列表（原生 TagSortPanelPlayAllButton_Click：分组视角播整库，
                        // 其余视角播当前分类曲目；都是整表替换队列从第一首）
                        PlayWebTagSortPanel();
                        break;
                    }

                case "tagsortsong":
                    {
                        // 曲目行播放。原生 Songs 视角双击 = PlayPlaylistItem（不换队列，
                        // 播完接用户当前队列）——照原生口径，不与网页其它面板的整表替换混
                        int i = ReadInt(msg.Payload, "index", -1);
                        if (i >= 0 && i < _webTagSortSongs.Count)
                        {
                            PlayPlaylistItem(_webTagSortSongs[i]);
                        }
                        break;
                    }

                case "tagsortgroupplay":
                    {
                        // 分组视角组头播放钮：整组替换队列从第一首（原生 PlayTagSortGroup）
                        string path = ReadStr(msg.Payload, "path");
                        PlayWebTagSortGroupNode(path);
                        break;
                    }

                case "tagsortgroupsong":
                    {
                        // 分组视角歌曲行：播所在末级分组全部、从该首开始
                        // （原生 TagSortGroupListView_DoubleTapped 歌曲口径）
                        string path = ReadStr(msg.Payload, "path");
                        string group = ReadStr(msg.Payload, "group");
                        string file = ReadStr(msg.Payload, "file");
                        PlayWebTagSortGroupSong(path, group, file);
                        break;
                    }

                case "tagsorttoggle":
                    {
                        // 组头展开/折叠（原生 ToggleTagSortNode）
                        string path = ReadStr(msg.Payload, "path");
                        if (!string.IsNullOrEmpty(path))
                        {
                            if (!_webTagSortOpen.Add(path)) _webTagSortOpen.Remove(path);
                            _ = PushWebTagSortPanelAsync();
                        }
                        break;
                    }

                case "tagsortexpand":
                    {
                        // 全部展开 / 全部折叠（原生 TagSortGroupExpandAll/CollapseAll）
                        bool on = false;
                        try
                        {
                            if (msg.Payload.ValueKind == JsonValueKind.Object &&
                                msg.Payload.TryGetProperty("on", out var ev))
                                on = ev.GetBoolean();
                        }
                        catch { /* 没带 on 按折叠 */ }
                        if (on) foreach (var p in _webTagSortAllPaths) _webTagSortOpen.Add(p);
                        else _webTagSortOpen.Clear();
                        _ = PushWebTagSortPanelAsync();
                        break;
                    }

                case "tagsortgroup":
                    {
                        // 分组预设切换（原生 TagSortGroupPresetCombo_SelectionChanged：
                        // "__custom__" = 已保存的自定义快照，其余 = 字段序列）
                        string tag = ReadStr(msg.Payload, "tag");
                        if (!string.IsNullOrEmpty(tag)) ApplyWebTagSortGroupPreset(tag);
                        break;
                    }

                case "tagsortsort":
                    {
                        // 排序方式视角：点预设（原生 TagSortPresetCombo_SelectionChanged 口径：
                        // 中文标签 → 字段链 → 应用到整个曲库）
                        string preset = ReadStr(msg.Payload, "preset");
                        if (!string.IsNullOrEmpty(preset))
                        {
                            var fields = PresetToFields(preset);
                            if (fields.Count > 0)
                            {
                                _tagSortCustom = fields;
                                _tagSortPreset = preset;
                                ApplyTagSortToLibrary();
                                _ = PushWebTagSortPanelAsync();
                            }
                        }
                        break;
                    }

                case "tagsortorder":
                    {
                        // 排序方式视角：升/降序（原生 TagSortOrderClick 口径：切方向即重排）
                        bool asc = true;
                        try
                        {
                            if (msg.Payload.ValueKind == JsonValueKind.Object &&
                                msg.Payload.TryGetProperty("asc", out var ov))
                                asc = ov.GetBoolean();
                        }
                        catch { /* 没带 asc 按升序 */ }
                        _tagSortAscending = asc;
                        ApplyTagSortToLibrary();
                        _ = PushWebTagSortPanelAsync();
                        break;
                    }

                case "tagsortcustom":
                    {
                        // 开原生配置窗（分工铁律：配置 UI 直接用原生窗口，网页只发意图）
                        string what = ReadStr(msg.Payload, "what");
                        if (what == "fields") OpenWebTagSortFieldConfig();
                        else if (what == "group") OpenWebTagSortGroupCustom();
                        else if (what == "sort") OpenWebTagSortSortCustom();
                        break;
                    }

                case "tagsortmore":
                    {
                        // 分类墙加载更多（原生 TagSortClassWallLoadMoreButton_Click）
                        if (_webTagSortShown < _webTagSortWallAll.Count)
                        {
                            _webTagSortShown = Math.Min(
                                _webTagSortShown + WebTagSortWallStep, _webTagSortWallAll.Count);
                            _ = PushWebTagSortWallAsync(reslice: true);
                        }
                        break;
                    }

                case "play":
                    {
                        // 与 Apple 风格专辑页同一套语义（d3c0d99）：整表替换播放队列，
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

                case "enqueueone":
                    {
                        // 右键菜单「添加到播放队列」（2026-10-10）：只加这一首，不动当前播放
                        int qi = ReadInt(msg.Payload, "index", -1);
                        var list = WebMainActiveList;
                        if (qi >= 0 && qi < list.Count)
                        {
                            AddSongsToUserPlaylist(new[] { list[qi] });
                            StartupLog.Write($"[Web主界面] enqueueone：加入播放队列《{list[qi].Title}》");
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

                // ---- 右键菜单：曲目动作（2026-10-10 补齐，语义照搬原生右键菜单）----
                case "songact":
                    {
                        // act = openloc（打开文件位置）/ edittags（编辑标签）/ delfromlib（从媒体库删除）
                        //   / queue（这一首加入播放队列）
                        // 曲目可以按 index 给（列表面板 / 专辑·艺术家详情），也可以直接按 path 给
                        // （媒体库的右栏文件行没在下标表里）。
                        string act = ReadStr(msg.Payload, "act");
                        string path = ReadStr(msg.Payload, "path");
                        PlaylistItem? t = null;
                        int i = ReadInt(msg.Payload, "index", -1);
                        var list = WebMainActiveList;
                        if (i >= 0 && i < list.Count)
                        {
                            t = list[i];
                            if (path.Length == 0) path = t.FilePath ?? "";
                        }
                        if (path.Length == 0) break;
                        if (t == null)
                        {
                            t = _playlist.FirstOrDefault(x =>
                                string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
                        }
                        if (act == "openloc") OpenFileLocationInExplorer(path);
                        else if (act == "edittags") TagEditorWindow.ShowBatch(new[] { path });
                        else if (act == "delfromlib" && t != null) _ = DeleteMediaSongWithConfirmAsync(t);
                        else if (act == "queue" && t != null) AddSongsToUserPlaylist(new[] { t });
                        break;
                    }

                // ---- 右键菜单：专辑卡动作（原生 AlbumGridView_RightTapped 口径）----
                case "albumact":
                    {
                        // act = play（播放该专辑）/ queue（添加至播放队列）/ addto（添加到命名播放列表）
                        string act = ReadStr(msg.Payload, "act");
                        int ai = ReadInt(msg.Payload, "id", -1);
                        var entries = WebMainAlbumEntries();
                        if (ai < 0 || ai >= entries.Count) break;
                        AlbumEntry album = entries[ai];
                        if (act == "play") PlayAlbum(album, replacePlaylist: true);
                        else if (act == "queue") AddSongsToUserPlaylist(GetTracksForAlbum(album));
                        else if (act == "addto") _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(album));
                        break;
                    }

                // ---- 右键菜单：艺术家卡动作（按艺术家名整表取曲目，与原生艺术家页口径一致）----
                case "artistact":
                    {
                        string act = ReadStr(msg.Payload, "act");
                        string nm = ReadStr(msg.Payload, "name");
                        if (nm.Length == 0) break;
                        var tracks = _playlist.Where(x =>
                            string.Equals(x.Artist, nm, StringComparison.CurrentCultureIgnoreCase)).ToList();
                        if (tracks.Count == 0) break;
                        if (act == "play")
                        {
                            _userPlaylist.Clear();
                            AddSongsToUserPlaylist(tracks);
                            PlayPlaylistItem(tracks[0]);
                        }
                        else if (act == "queue") AddSongsToUserPlaylist(tracks);
                        else if (act == "addto") _ = ShowNamedPlaylistPickerAsync(tracks);
                        break;
                    }

                // ---- 右键菜单：播放列表内曲目动作 ----
                case "plsongact":
                    {
                        // act = queue（加入播放队列）/ remove（从该播放列表移除）/ openloc
                        string act = ReadStr(msg.Payload, "act");
                        string name = ReadStr(msg.Payload, "name");
                        string path = ReadStr(msg.Payload, "path");
                        if (path.Length == 0) break;
                        if (act == "openloc")
                        {
                            OpenFileLocationInExplorer(path);
                        }
                        else if (act == "queue")
                        {
                            var one = _playlist.FirstOrDefault(x =>
                                string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));
                            if (one != null) AddSongsToUserPlaylist(new[] { one });
                        }
                        else if (act == "remove" && name.Length > 0)
                        {
                            var songs = NamedPlaylistStore.LoadSongs(name);
                            NamedPlaylistStore.SaveSongs(name, songs.Where(s =>
                                !string.Equals(s, path, StringComparison.OrdinalIgnoreCase)));
                            _ = PushWebPlaylistDetailAsync(name);
                        }
                        break;
                    }

                // ---- 底部播放条：补齐原生那一排按钮（2026-10-10）----
                case "order":
                    // 播放顺序循环：与点原生按钮同一条代码路径（顺序→随机→列表循环→单曲循环→单曲播放）
                    PlaybackOrderButton_Click(this, new RoutedEventArgs());
                    break;

                case "rate":
                    {
                        // 播放速度：网页直接给倍率（原生右侧那个菜单同理，最终都走 SetPlaybackRateAsync）
                        double r = ReadDouble(msg.Payload, "v", -1);
                        if (r >= 0.5 && r <= 2.0) _ = SetPlaybackRateAsync(r);
                        break;
                    }

                case "favcur":
                    // 收藏/取消收藏正在播的那首
                    FavoriteButton_Click(this, new RoutedEventArgs());
                    break;

                case "showqueue":
                    ShowCurrentPlaylistButton_Click(this, new RoutedEventArgs());
                    break;

                case "mini":
                    MiniPlayerButton_Click(this, new RoutedEventArgs());
                    break;

                case "lyrics":
                    DesktopLyricsButton_Click(this, new RoutedEventArgs());
                    break;

                case "feat":
                    {
                        // 「更多功能」那张菜单（原生是挂在按钮上的 flyout，网页盖着看不见，
                        // 这里按 act 直接调菜单里对应的入口，功能一个不少）
                        string act = ReadStr(msg.Payload, "act");
                        switch (act)
                        {
                            case "onlinesearch": OnlineSearchWindow.ShowOrActivate(); break;
                            case "lyric": _ = DownloadLyricForCurrentAsync(); break;
                            case "batchlyric": _ = BatchDownloadLyricsAsync(); break;
                            case "cover": _ = DownloadCoverForCurrentAsync(); break;
                            case "dup": DuplicateFilesWindow.ShowOrActivate(this); break;
                            case "rgscan": OpenReplayGainScan(); break;
                            case "edittag":
                                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                                    TagEditorWindow.Show(_nowPlayingPath);
                                break;
                            case "editlyric":
                                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                                    LyricsEditorWindow.Show(_nowPlayingPath);
                                break;
                            case "openloc": OpenFileLocationInExplorer(_nowPlayingPath); break;
                            case "sleeptimer": _ = ShowSleepTimerDialogAsync(); break;
                            case "importm3u": ImportM3u_Click(this, new RoutedEventArgs()); break;
                            case "exportm3u": ExportM3u_Click(this, new RoutedEventArgs()); break;
                            case "opencue": OpenCue_Click(this, new RoutedEventArgs()); break;
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
            // 切走音效处理页就把右栏的 DSP 面板收掉（它的每秒推流也一起停）
            if (!string.Equals(id, "AudioFX", StringComparison.OrdinalIgnoreCase))
                CloseEmbeddedWebDsp();

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
            if (string.Equals(id, "Folders", StringComparison.OrdinalIgnoreCase))
            {
                // 媒体库（文件夹浏览）：布局/交互照原生 FolderBrowserView——
                // 左栏文件夹树（根=设置里的媒体库目录，显示完整路径），右栏选中文件夹的歌曲。
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "Folders", ["ok"] = true,
                });
                PushWebFolderRoots();
                return;
            }
            if (string.Equals(id, "WebDav", StringComparison.OrdinalIgnoreCase))
            {
                // 网络音乐库（WebDav）：复用媒体库那套界面（原生同口径——SetFolderBrowserMode
                // 只换台头/藏「添加文件夹」），数据源换成 WebDavClient。没配 WebDAV 时
                // 侧栏条目根本不显示（cats 推送时过滤），走到这里只可能是配置被删掉的边角。
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "WebDav", ["ok"] = true,
                });
                _ = PushWebDavRootsAsync();
                return;
            }
            if (string.Equals(id, "TagSort", StringComparison.OrdinalIgnoreCase))
            {
                // 标签排序：分类字段按钮组 + 分类卡墙 + 钻取面板（曲目/专辑/艺术家/
                // 排序方式/分组浏览），口径照原生 TagSortBorder。
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "TagSort", ["ok"] = true,
                });
                EnterWebTagSortAsync();
                return;
            }
            if (string.Equals(id, "PlaylistWall", StringComparison.OrdinalIgnoreCase))
            {
                // 播放列表墙：命名单卡片（原生 PlaylistWallBorder 口径，内建「我喜欢的音乐」
                // 不显示——侧栏有专门入口）。点卡进详情，详情里播/入队/改名/删除。
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "PlaylistWall", ["ok"] = true,
                });
                await PushWebPlaylistWallAsync();
                return;
            }
            if (string.Equals(id, "AudioFX", StringComparison.OrdinalIgnoreCase))
            {
                // 音效处理（DSP）：2026-10-10 用户要求——面板只占右侧内容区，左栏分类留着。
                // 所以不再整页切到 dsp.html，而是把 DSP 面板嵌进主界面右栏
                // （OpenEmbeddedWebDspAsync 只推状态，不换页面、不动主界面层）。
                // 所有 DSP 操作仍在原生控件/handler 上落地，音频链路零改动。
                CloseWebMainDetails();
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "nav", ["id"] = "AudioFX", ["ok"] = true,
                });
                await OpenEmbeddedWebDspAsync();
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

        /* ================= 媒体库（文件夹浏览） =================
           口径照原生 FolderBrowserView（MainWindow.Library.cs RefreshFolderBrowserRoots /
           FolderBrowserView_DoubleTapped / MainWindow.Playback.cs LoadMediaFolderSongs）：
             - 根 = AppSettingsStore.LibraryWatchFolders 里存在的目录，行上显示完整路径；
               一个都没配时退回 _browseFolderPath 单文件夹树（枚举其子项当根级行）；
               两者都空 → 网页显示「请选择文件夹」。
             - 展开一层：子文件夹（按名字排序、跳过隐藏/系统）+ 音频文件（按名字排序、
               按扩展名过滤）——直接复用原私有 EnumerateFolderChildren。
             - 右栏歌曲：后台线程递归枚举 + CreatePlaylistItemFromPath + OrderAlbumTracks
               （Disc→Track），行显示文件名 + 艺术家·专辑 + 时长（原生 MediaDetailsList 口径）。
             - 双击文件：EnsureTrackInLibrary + PlayPlaylistItem（原生口径，不换队列）。 */

        /// <summary>推媒体库根（原生 RefreshFolderBrowserRoots 的网页版）。</summary>
        private void PushWebFolderRoots()
        {
            var roots = AppSettingsStore.Load().LibraryWatchFolders?
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .ToList() ?? new List<string>();

            // 没配媒体库目录：退回旧的单文件夹树（原生同口径——枚举它的子项当根级行）
            var fallbackChildren = new List<Dictionary<string, object?>>();
            if (roots.Count == 0
             && !string.IsNullOrWhiteSpace(_browseFolderPath) && Directory.Exists(_browseFolderPath))
            {
                foreach (FolderBrowserItem child in EnumerateFolderChildren(_browseFolderPath, depth: 0))
                {
                    fallbackChildren.Add(new Dictionary<string, object?>
                    {
                        ["name"] = child.DisplayName,
                        ["path"] = child.FullPath,
                        ["isFolder"] = child.IsFolder,
                    });
                }
            }

            var items = roots.Select(r => new Dictionary<string, object?>
            {
                ["name"] = r,          // 根行显示完整路径（原生 DisplayName = root）
                ["path"] = r,
                ["isFolder"] = true,
            }).Cast<object>().ToList();
            items.AddRange(fallbackChildren);

            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "folderroots",
                ["roots"] = items,
                ["empty"] = items.Count == 0,
            });
            StartupLog.Write($"[Web主界面] 媒体库根 {items.Count} 项已推送");
        }

        /// <summary>网页点开一个文件夹：推一层子项，并把该文件夹的歌曲推右栏
        /// （原生点箭头/双击文件夹都是「展开 + 加载歌曲」一套）。</summary>
        private async Task PushWebFolderChildrenAsync(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderchildren", ["path"] = folderPath, ["items"] = new List<object>(),
                });
                return;
            }

            // 枚举一层是磁盘 I/O，挪后台线程（云盘目录下同步枚举会卡 UI）
            List<FolderBrowserItem> children = await Task.Run(
                () => EnumerateFolderChildren(folderPath, depth: 0));
            var items = children.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.DisplayName,
                ["path"] = c.FullPath,
                ["isFolder"] = c.IsFolder,
            }).Cast<object>().ToList();

            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "folderchildren", ["path"] = folderPath, ["items"] = items,
            });
            await PushWebFolderSongsAsync(folderPath);
        }

        /// <summary>右栏歌曲（原生 LoadMediaFolderSongs 口径）：递归枚举 + 读标签 + 按专辑顺序排。
        /// 大文件夹在读盘期间网页先显示「加载中…」，数据到了自动替换。</summary>
        private async Task PushWebFolderSongsAsync(string folderPath)
        {
            List<PlaylistItem> songs = await Task.Run(() =>
            {
                var list = new List<PlaylistItem>();
                foreach (string path in EnumerateAudioFiles(folderPath))
                {
                    if (!File.Exists(path)) continue;
                    try { list.Add(CreatePlaylistItemFromPath(path)); }
                    catch (Exception caught) { StartupLog.WriteException("WebMainFolder.Songs", caught); }
                }
                list = OrderAlbumTracks(list);
                for (int i = 0; i < list.Count; i++) list[i].Index = i + 1;
                return list;
            });

            var rows = songs.Select(t => new Dictionary<string, object?>
            {
                // 原生右栏行主文本是文件名（不是标题），副标题是 艺术家 · 专辑
                ["t"] = string.IsNullOrEmpty(t.FileName) ? t.Title : t.FileName,
                ["sub"] = t.ArtistAlbumText,
                ["dur"] = t.Duration.TotalSeconds,
                ["fav"] = TrackStatsStore.Get(t.FilePath)?.IsFavorite == true,
                ["path"] = t.FilePath,
            }).Cast<object>().ToList();

            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "foldersongs",
                ["path"] = folderPath,
                ["header"] = folderPath,
                ["songs"] = rows,
            });
            StartupLog.Write($"[Web主界面] 媒体库「{folderPath}」歌曲 {rows.Count} 首已推送");
        }

        /* ================= 网络音乐库（WebDav） =================
           口径照原生 MainWindow.WebDav.cs：界面复用媒体库那套（左树右栏），
           数据源换 WebDavClient（PROPFIND 列目录），播放先下缓存再走普通链路
           （bit-perfect 铁律不动：下载到本地缓存，字节流与本地文件完全一致）。
           网页消息与媒体库共用（folderroots/folderchildren/foldersongs），
           靠 wd=true 区分分支；侧栏条目没配 WebDAV 时不显示（cats 推送时过滤）。 */

        /// <summary>推网络音乐库根（原生 RefreshWebDavBrowserRoots 的网页版）。</summary>
        private async Task PushWebDavRootsAsync()
        {
            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null)
            {
                // 配置被删掉的边角：给一句原生同款提示，别让网页干转
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderroots", ["wd"] = true, ["roots"] = new List<object>(),
                    ["empty"] = true,
                    ["emptyText"] = "还没配置网络音乐库。到「选项设置 → WebDAV」里填上地址，保存后这里就有内容了。",
                    ["lib"] = "",
                });
                return;
            }

            try
            {
                IReadOnlyList<WebDavEntry> entries = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    return await client.ListAsync(string.Empty);
                });

                var items = entries.Select(e =>
                {
                    FolderBrowserItem it = ToFolderBrowserItem(e, 0, string.Empty);
                    return new Dictionary<string, object?>
                    {
                        ["name"] = it.DisplayName,
                        ["path"] = it.FullPath,
                        ["isFolder"] = it.IsFolder,
                    };
                }).Cast<object>().ToList();

                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderroots", ["wd"] = true, ["roots"] = items,
                    ["empty"] = items.Count == 0,
                    ["emptyText"] = items.Count == 0 ? "服务器根目录下没有内容。" : "",
                    ["lib"] = loc.LibraryName,
                });
                StartupLog.Write($"[Web主界面] 网络音乐库根 {items.Count} 项已推送（{loc.LibraryName}）");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("WebMainDav.Roots", caught);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderroots", ["wd"] = true, ["roots"] = new List<object>(),
                    ["empty"] = true,
                    ["emptyText"] = "连不上网络音乐库：" + DescribeWebDavFailure(caught),
                    ["lib"] = loc.LibraryName,
                });
            }
        }

        /// <summary>网页点开一个网络文件夹：推一层子项 + 该文件夹的歌曲（原生
        /// ToggleWebDavFolderExpandAsync + LoadWebDavFolderSongs 的网页版）。</summary>
        private async Task PushWebDavChildrenAsync(string folderPath)
        {
            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null || string.IsNullOrEmpty(folderPath))
            {
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderchildren", ["wd"] = true, ["path"] = folderPath,
                    ["items"] = new List<object>(),
                });
                return;
            }

            try
            {
                IReadOnlyList<WebDavEntry> entries = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    return await client.ListAsync(folderPath);
                });

                var items = entries.Select(e =>
                {
                    FolderBrowserItem it = ToFolderBrowserItem(e, 0, folderPath);
                    return new Dictionary<string, object?>
                    {
                        ["name"] = it.DisplayName,
                        ["path"] = it.FullPath,
                        ["isFolder"] = it.IsFolder,
                    };
                }).Cast<object>().ToList();

                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderchildren", ["wd"] = true, ["path"] = folderPath,
                    ["items"] = items,
                });
                await PushWebDavSongsAsync(loc, folderPath);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("WebMainDav.Children", caught);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "folderchildren", ["wd"] = true, ["path"] = folderPath,
                    ["items"] = new List<object>(),
                });
            }
        }

        /// <summary>网络文件夹的歌曲（原生 LoadWebDavFolderSongs 口径）：只列音频文件，
        /// 已缓存的读本地标签（时长是真的），没缓存的只给文件名+大小。</summary>
        private async Task PushWebDavSongsAsync(WebDavLocation loc, string folderPath)
        {
            List<PlaylistItem> songs;
            try
            {
                songs = await Task.Run(() =>
                {
                    using WebDavClient client = loc.CreateClient();
                    IReadOnlyList<WebDavEntry> entries = client.ListAsync(folderPath).GetAwaiter().GetResult();
                    var list = new List<PlaylistItem>();
                    foreach (WebDavEntry e in entries)
                    {
                        if (e.IsFolder) continue;
                        if (!AudioExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)) continue;
                        string rel = CombineRemotePath(folderPath, e.RelativePath);
                        list.Add(CreateRemotePlaylistItem(loc, new WebDavEntry
                        {
                            Name = e.Name,
                            RelativePath = rel,
                            IsFolder = false,
                            Size = e.Size,
                            ModifiedUtc = e.ModifiedUtc
                        }, folderPath));
                    }
                    return list;
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("WebMainDav.Songs", caught);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "foldersongs", ["wd"] = true, ["path"] = folderPath,
                    ["header"] = folderPath, ["songs"] = new List<object>(),
                    ["error"] = "读取失败：" + DescribeWebDavFailure(caught),
                });
                return;
            }

            for (int i = 0; i < songs.Count; i++) songs[i].Index = i + 1;

            var rows = songs.Select(t => new Dictionary<string, object?>
            {
                // 主文本：有标签用标题，没缓存过只有文件名（原生同口径）
                ["t"] = string.IsNullOrEmpty(t.FileName) ? t.Title : t.FileName,
                ["sub"] = string.IsNullOrEmpty(t.RemoteHint) ? t.ArtistAlbumText : t.RemoteHint,
                ["dur"] = t.Duration.TotalSeconds,
                ["path"] = t.RemotePath,
            }).Cast<object>().ToList();

            await PostCelesteWebAsync(new Dictionary<string,object?>
            {
                ["kind"] = "foldersongs", ["wd"] = true, ["path"] = folderPath,
                ["header"] = folderPath, ["songs"] = rows,
            });
            StartupLog.Write($"[Web主界面] 网络音乐库「{folderPath}」歌曲 {rows.Count} 首已推送");
        }

        /// <summary>播放网络曲目（原生 PlayWebDavItemAsync 口径）：先下载到缓存再播。</summary>
        private async Task PlayWebDavFileAsync(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath)) return;
            WebDavLocation? loc = GetWebDavLocation();
            if (loc == null) return;

            string name = Path.GetFileName(remotePath);
            var item = CreateRemotePlaylistItem(loc, new WebDavEntry
            {
                Name = name,
                RelativePath = remotePath,
                IsFolder = false,
                Size = 0
            }, GetRemoteParent(remotePath));
            await PlayWebDavItemAsync(item);
        }

        /* ================= 播放列表（命名单墙 + 详情） =================
           口径照原生 PlaylistWallBorder / ShowPlaylistDetail：
           - 墙 = 命名单卡片（PlaylistLibraryService.Refresh + Items），内建
             「我喜欢的音乐」过滤（侧栏有专门入口）；原生空单也出卡片，这里同口径。
           - 详情 = 该单有序曲目（NamedPlaylistStore.LoadSongs，行序即单内顺序），
             行播放 = PlayNamedPlaylistFromTrack（整单替换队列、从该首起）。
           - 封面：首曲封面优先（WriteCoverFile，与专辑封面同一缓存），歌曲全无封面
             时回落用户自定义封面（PlaylistLibraryService.CustomCoverPath →
             WebAvatarFileUrl 落 WebAssets）。解封面要开文件读，后台分批预热，
             算好推 plcovers，网页按下标回填卡片。 */

        /// <summary>推播放列表墙（原生 ApplyPlaylistWallCategory 的网页版）。
        /// backToWall=true 给删除后用：网页收到就回墙（原生删完单也是回墙）。</summary>
        private async Task PushWebPlaylistWallAsync(bool backToWall = false)
        {
            List<(string Name, int Count)> rows;
            try
            {
                // Refresh/LoadSongs 走 SQLite，挪后台线程（云盘/大盘库下 UI 线程会卡）
                rows = await Task.Run(() =>
                {
                    PlaylistLibraryService.Refresh();
                    var list = new List<(string, int)>();
                    foreach (var p in PlaylistLibraryService.Items)
                    {
                        if (string.Equals(p.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                            continue;
                        list.Add((p.Name, NamedPlaylistStore.LoadSongs(p.Name).Count));
                    }
                    return list;
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebMainPlaylist.Wall", ex);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "plwall", ["items"] = new List<object>(), ["empty"] = true,
                });
                return;
            }

            var items = rows.Select(r => new Dictionary<string, object?>
            {
                ["name"] = r.Name,
                ["n"] = r.Count,
                ["cover"] = "",   // 封面后台预热按批补（plcovers）
            }).Cast<object>().ToList();

            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "plwall",
                ["items"] = items,
                ["empty"] = items.Count == 0,
                ["back"] = backToWall,
            });
            StartupLog.Write($"[Web主界面] 播放列表墙 {items.Count} 个已推送");
            _ = PrewarmWebPlaylistCoversAsync(rows.Select(r => r.Name).ToList());
        }

        /// <summary>后台预热播放列表封面，算好一批推一批（kind=plcovers，网页按下标回填）。
        /// 代际不相符就停——旧下标补到新墙上会张冠李戴。</summary>
        private async Task PrewarmWebPlaylistCoversAsync(List<string> names)
        {
            int gen = _webMainGen;
            try
            {
                const int BATCH = 5;
                int done = 0;
                while (done < names.Count)
                {
                    if (!_webMainOpen || gen != _webMainGen) break;
                    int end = Math.Min(done + BATCH, names.Count);
                    int from = done;
                    // 解封面是文件读，必须挪出 UI 线程（不包 Task.Run 第一批就卡界面）
                    var items = await Task.Run(() =>
                    {
                        var batch = new List<object>();
                        for (int i = from; i < end; i++)
                        {
                            batch.Add(new Dictionary<string, object?>
                            {
                                ["i"] = i,
                                ["cover"] = WebPlaylistCover(names[i]),
                            });
                        }
                        return batch;
                    });
                    var payload = new Dictionary<string, object?>
                    {
                        ["kind"] = "plcovers",
                        ["items"] = items,
                    };
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webMainOpen) _ = PostCelesteWebAsync(payload);
                    });
                    done = end;
                    await Task.Delay(30);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PrewarmWebPlaylistCovers", ex);
            }
        }

        /// <summary>播放列表封面 URL：首曲封面优先，全无回落用户自定义封面
        /// （原生 LoadPlaylistWallCoverAsync 口径）。</summary>
        private string WebPlaylistCover(string name)
        {
            try
            {
                foreach (string path in NamedPlaylistStore.LoadSongs(name))
                {
                    if (!System.IO.File.Exists(path)) continue;
                    if (!_webMainCoverCache.TryGetValue(path, out var cover))
                    {
                        cover = WriteCoverFile(path);
                        _webMainCoverCache[path] = cover;
                    }
                    if (!string.IsNullOrEmpty(cover)) return cover;
                }
                string? custom = PlaylistLibraryService.CustomCoverPath(name);
                if (!string.IsNullOrEmpty(custom)) return WebAvatarFileUrl(custom);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("WebPlaylistCover", caught);
            }
            return "";
        }

        /// <summary>推播放列表详情（原生 ShowPlaylistDetail / FillPlaylistDetailItems 口径）：
        /// 单内有序曲目，行显示 标题 + 艺术家·专辑 + 时长 + 收藏心。</summary>
        private async Task PushWebPlaylistDetailAsync(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            List<PlaylistItem> songs;
            try
            {
                songs = await Task.Run(() =>
                {
                    var list = new List<PlaylistItem>();
                    foreach (string path in NamedPlaylistStore.LoadSongs(name))
                    {
                        if (!System.IO.File.Exists(path)) continue;
                        try { list.Add(CreatePlaylistItemFromPath(path)); }
                        catch (Exception caught) { StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                    }
                    return list;
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebMainPlaylist.Detail", ex);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "pldetail", ["name"] = name, ["songs"] = new List<object>(),
                });
                return;
            }

            var rows = songs.Select(t => new Dictionary<string, object?>
            {
                ["t"] = string.IsNullOrEmpty(t.Title) ? System.IO.Path.GetFileName(t.FilePath) : t.Title,
                ["sub"] = t.ArtistAlbumText,
                ["dur"] = t.Duration.TotalSeconds,
                ["fav"] = TrackStatsStore.Get(t.FilePath)?.IsFavorite == true,
                ["path"] = t.FilePath,
            }).Cast<object>().ToList();

            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "pldetail",
                ["name"] = name,
                ["songs"] = rows,
            });
            StartupLog.Write($"[Web主界面] 播放列表「{name}」{rows.Count} 首已推送");
        }

        /// <summary>删除播放列表（原生墙右键删除口径：删单 + 清自定义封面 + 刷新墙）。</summary>
        private async Task DeleteWebPlaylistAsync(string name)
        {
            if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                return;
            try
            {
                await Task.Run(() =>
                {
                    NamedPlaylistStore.Delete(name);
                    PlaylistLibraryService.ClearCustomCover(name);
                    PlaylistLibraryService.Refresh();
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebMainPlaylist.Delete", ex);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "plmsg", ["text"] = "删除失败：" + ex.Message,
                });
                return;
            }
            await PushWebPlaylistWallAsync(backToWall: true);
        }

        /// <summary>重命名播放列表（原生 RenamePlaylistFromWallAsync 口径）。
        /// 失败（重名等）推 plmsg，网页在改名框旁显示。</summary>
        private async Task RenameWebPlaylistAsync(string oldName, string newName)
        {
            if (string.Equals(oldName, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                return;
            newName = (newName ?? string.Empty).Trim();
            if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal)) return;
            try
            {
                await Task.Run(() =>
                {
                    NamedPlaylistStore.Rename(oldName, newName);
                    PlaylistLibraryService.Refresh();
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebMainPlaylist.Rename", ex);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "plmsg", ["text"] = "重命名失败：这个名字可能已被占用",
                });
                return;
            }
            await PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "plrenamed", ["old"] = oldName, ["name"] = newName,
            });
        }

        /// <summary>新建播放列表（原生 CreatePlaylistWallButton_Click 口径：默认名去重），
        /// 建完直接进详情（空单）。</summary>
        private async Task CreateWebPlaylistAsync()
        {
            string name;
            try
            {
                name = await Task.Run(() =>
                {
                    string candidate = "新建播放列表";
                    int n = 2;
                    var existing = NamedPlaylistStore.List();
                    while (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        candidate = "新建播放列表 (" + n + ")";
                        n++;
                    }
                    NamedPlaylistStore.Create(candidate);
                    PlaylistLibraryService.Refresh();
                    return candidate;
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebMainPlaylist.Create", ex);
                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "plmsg", ["text"] = "新建失败：" + ex.Message,
                });
                return;
            }
            await PushWebPlaylistDetailAsync(name);
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

        /// <summary>从消息里读一个布尔字段（读不到/不是布尔都按 false）。</summary>
        private static bool ReadBool(JsonElement payload, string name)
        {
            try
            {
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty(name, out var v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    return v.GetBoolean();
            }
            catch { /* 读不到按 false */ }
            return false;
        }

        /// <summary>
        /// 把分类清单 + 当前列表面板的快照推给网页。ready 和 nav 都走这里。
        ///
        /// **必须分块**：3609 首打成一个大 JSON（1MB+）用 PostWebMessageAsString
        /// 一次性发，WebView2 渲染进程直接压死（2026-10-09 用户实测卡死）。
        /// Apple 风格专辑页只推 46 首没暴露过这个问题，主界面不行。
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
            if (string.Equals(id, "Folders", StringComparison.OrdinalIgnoreCase))
            {
                // 媒体库：侧栏计数 = 设置里存在的媒体库根目录数（与页面台头「N 个目录」同口径；
                // 一个都没配时退回单文件夹浏览，计数不适用，显示 0）
                return AppSettingsStore.Load().LibraryWatchFolders?
                    .Count(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) ?? 0;
            }
            if (string.Equals(id, "TagSort", StringComparison.OrdinalIgnoreCase))
            {
                // 标签排序：侧栏计数 = 当前分类字段下的分类数（与分类墙卡片总数同口径）
                string f = string.IsNullOrEmpty(_webTagSortField) ? _tagSortClassField : _webTagSortField;
                if (string.IsNullOrEmpty(f)) return 0;
                return _playlist
                    .GroupBy(p => TagSortFieldVal(p, f), StringComparer.CurrentCultureIgnoreCase)
                    .Count();
            }
            if (string.Equals(id, "PlaylistWall", StringComparison.OrdinalIgnoreCase))
            {
                // 播放列表：侧栏计数 = 命名单数（内建「我喜欢的音乐」不计，与墙同口径）
                int n = 0;
                foreach (string name in NamedPlaylistStore.List())
                    if (!string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                        n++;
                return n;
            }
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
                // 网络音乐库：没配 WebDAV 时侧栏不显示这一项（原生 ApplyWebDavNavEntry
                // 同口径——SetNavEntryVisibility(NavWebDavButton, loc.IsConfigured)）。
                // 用户 2026-10-09 明确要求：设置了再显示。
                if (string.Equals(id, "WebDav", StringComparison.OrdinalIgnoreCase)
                 && GetWebDavLocation() == null)
                {
                    continue;
                }
                bool ok = Array.IndexOf(WebMainListPanels, id) >= 0
                       || string.Equals(id, "Albums", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "Artists", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "AlbumArtists", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "Folders", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "WebDav", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "TagSort", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(id, "PlaylistWall", StringComparison.OrdinalIgnoreCase);
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
                        // 播放次数：只有「播放最多」面板用（原生不显示，用户 2026-10-09
                        // 要求补上；TrackStatsStore.Get 内存查表，零文件 I/O）
                        ["plays"] = string.Equals(_webMainListKind, "MostPlayed", StringComparison.OrdinalIgnoreCase)
                            ? TrackStatsStore.Get(t.FilePath)?.PlayCount ?? 0
                            : 0,
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
            => _webMainAlbumTracks ?? _webMainArtistTracks
            ?? (_webTagSortMode is "Songs" && _webTagSortSongs.Count > 0
                ? _webTagSortSongs : _webMainSongs);

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
                // 正在播的文件路径：播放列表详情页靠它高亮正在播的那一行
                // （now 的 index 按当前列表面板算，对不上播放列表的行序）
                ["path"] = _nowPlayingPath ?? "",
                // 播放顺序 / 倍速：底部播放条那排按钮要回显当前值（2026-10-10）
                ["order"] = _orderResolver?.Order.ToString() ?? "",
                ["rate"] = PlaybackRateText?.Text ?? "1.0x",
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

        /// <summary>
        /// 把当前歌词推给网页主界面（正在播放页的歌词区）。_lyricLines 不是当前曲子的
        /// （换曲后还在异步加载）就不推：网页留着上一首的歌词，等加载完 BuildLyricsUi 再推。
        /// </summary>
        internal void PushWebLyrics()
        {
            if (!_webMainOpen) return;
            var path = _nowPlayingPath ?? "";
            if (!string.Equals(_lyricsLoadedPath, path, StringComparison.OrdinalIgnoreCase)) return;
            var lines = new List<object?>(_lyricLines.Count);
            foreach (var line in _lyricLines)
            {
                lines.Add(new Dictionary<string, object?>
                {
                    ["t"] = line.Time.TotalSeconds,
                    ["x"] = line.Text ?? "",
                    ["tr"] = line.IsTranslation,
                });
            }
            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "lyrics",
                ["path"] = path,
                ["lines"] = lines,
            });
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

        /* ================= 标签排序（TagSort） =================
         * 口径照原生 TagSortBorder（MainWindow.Library.cs 标签排序板块 +
         * MainWindow.Playback.cs 分类墙/分组）：
         *   - 分类墙：按当前分类字段分组整库，组按 key 排序；初始 200 张，溢出提示 + 加载更多；
         *   - 钻取面板：曲目（可配置列 + 列头排序）/ 专辑 / 艺术家 / 排序方式 / 分组浏览；
         *   - 播放语义：曲目行 = PlayPlaylistItem（不换队列，原生同款）；面板播放全部 /
         *     整组播放 = 替换队列从第一首；组内歌曲 = 播该末级分组全部从该首开始；
         *   - 列配置/分类字段/自定义排序/自定义分组四个弹窗直接复用原生窗口
         *     （分工铁律：配置 UI 用原生，网页只发意图）。
         * 状态全部放本文件私有字段，不动原生 TagSort 的 UI 控件；仅排序/分组
         * 应用时照原生口径写 _tagSortCustom/_tagSortGroupFields 等并落设置。 */

        /// <summary>分类墙每次加载更多加的卡片数（与原生 TagSortClassWallLoadMoreStep 一致）。</summary>
        private const int WebTagSortWallStep = 200;

        private string _webTagSortField = "";          // 网页当前分类字段（空=跟原生 _tagSortClassField）
        private string _webTagSortValue = "";          // 当前进入的分类值（空=停在分类墙）
        private string _webTagSortMode = "Wall";       // Wall / Songs / Albums / Artists / Sort / GroupBy
        private string _webTagSortTitle = "";          // 面板标题（「字段标签：值」）
        private List<PlaylistItem> _webTagSortSongs = new();  // 当前分类曲目（= 原生 _tagSortClassSongs）
        private List<TagSortCategoryEntry> _webTagSortWallAll = new(); // 全部分组（已排序）
        private int _webTagSortShown;                  // 当前已显示卡片数
        private readonly HashSet<string> _webTagSortOpen = new(StringComparer.Ordinal);   // 展开的组路径
        private readonly List<string> _webTagSortAllPaths = new();                        // 全部组路径（全部展开用）
        private List<TagSortGroupHeader> _webTagSortGroupTree = new();                    // 分组树（原生 BuildGroupTree 构建）

        /// <summary>排序方式预设（中文标签必须与 PresetToFields 的键一致）。</summary>
        private static readonly string[] WebTagSortSortPresets =
        {
            "专辑", "专辑艺术家 / 专辑", "专辑艺术家 / 年份 / 专辑",
            "艺术家 / 专辑", "流派 / 专辑", "年份 / 专辑",
        };

        /// <summary>分组浏览预设（Tag 与原生 TagSortGroupPresetCombo 项一致）。</summary>
        private static readonly (string Tag, string Label)[] WebTagSortGroupPresetList =
        {
            ("__custom__", "自定义（已保存）"),
            ("Artist,Album", "艺术家 / 专辑"),
            ("Artist,Album,Year", "艺术家 / 专辑 / 年份"),
            ("Artist,Album,Title", "艺术家 / 专辑 / 标题"),
            ("Album,Year", "专辑 / 年份"),
            ("Genre,Artist", "流派 / 艺术家"),
            ("Year,Album", "年份 / 专辑"),
            ("Format,DepthRate", "格式 / 位深采样率"),
        };

        /// <summary>当前分类字段：网页显式选过就用网页的，否则跟原生。</summary>
        private string CurrentWebTagSortField()
            => string.IsNullOrEmpty(_webTagSortField) ? _tagSortClassField : _webTagSortField;

        /// <summary>进入标签排序页：读配置 → 回分类墙（原生 BreakoutTagSortView 口径）。</summary>
        private async Task EnterWebTagSortAsync()
        {
            LoadTagSortConfig();
            _webTagSortField = "";
            _webTagSortValue = "";
            _webTagSortTitle = "";
            _webTagSortMode = "Wall";
            _webTagSortSongs = new List<PlaylistItem>();
            await PushWebTagSortWallAsync();
            StartupLog.Write($"[Web主界面] 进入标签排序：字段 {CurrentWebTagSortField()}，{_webTagSortWallAll.Count} 个分类");
        }

        /// <summary>推分类墙（字段按钮组 + 分类卡 + total/shown）。reslice=只换可见数量不重算分组。</summary>
        private async Task PushWebTagSortWallAsync(bool reslice = false)
        {
            if (!_celesteWebReady) return;
            try
            {
                string f = CurrentWebTagSortField();
                if (!reslice)
                {
                    // 分组口径与原生 ShowTagSortClassWall 完全一致（CurrentCultureIgnoreCase + 按 key 排序）
                    _webTagSortWallAll = _playlist
                        .GroupBy(p => TagSortFieldVal(p, f), StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new TagSortCategoryEntry
                        {
                            Name = g.Key,
                            Count = g.Count(),
                            FirstFilePath = g.First().FilePath,
                        })
                        .ToList();
                    _webTagSortShown = 0;
                }

                int total = _webTagSortWallAll.Count;
                int show = Math.Min(_webTagSortShown > 0 ? _webTagSortShown : WebTagSortWallStep, total);
                var slice = _webTagSortWallAll.Take(show).ToList();

                var cards = new List<object>();
                foreach (var c in slice)
                    cards.Add(new Dictionary<string, object?> { ["name"] = c.Name, ["n"] = c.Count });

                // 溢出提示照原生 UpdateClassWallOverflowBar：高基数字段给换字段建议
                string advice = "";
                if (total > show)
                {
                    advice = TagSortFields.Find(f)?.Cardinality == TagSortFields.Cardinality.High
                        ? "当前分类字段基数过高（每首曲目几乎都不同）。建议改用低基数字段（如流派/年份/格式），或使用「分组浏览」按字段分组查看。"
                        : "分类数量较多，仅显示部分卡片以避免内存占用过高。";
                }

                await PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "tagsortcats",
                    ["field"] = f,
                    ["fields"] = WebTagSortFieldButtons(),
                    ["cards"] = cards,
                    ["total"] = total,
                    ["shown"] = show,
                    ["advice"] = advice,
                });
                _ = LoadWebTagSortCoversAsync(slice, "wall");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PushWebTagSortWall", ex);
            }
        }

        /// <summary>推钻取面板（按当前视角带 cols/songs/grid/sort/group 中对应的一段）。</summary>
        private async Task PushWebTagSortPanelAsync()
        {
            if (!_celesteWebReady) return;
            try
            {
                var msg = new Dictionary<string, object?>
                {
                    ["kind"] = "tagsortpanel",
                    ["mode"] = _webTagSortMode,
                    ["title"] = _webTagSortTitle,
                    ["fields"] = WebTagSortFieldButtons(),
                };

                if (string.Equals(_webTagSortMode, "Songs", StringComparison.Ordinal))
                {
                    // 排序后回写 _webTagSortSongs：网页行下标与 tagsortsong 的 index 才能对上
                    // （原生按 DataContext 对象播放，网页只有下标）
                    var ordered = SortTagSortPanelSongs(_webTagSortSongs.ToList());
                    _webTagSortSongs = ordered;

                    var visible = _tagSortColumns.Where(c => c.Visible).ToList();
                    var cols = new List<object>();
                    foreach (var c in visible)
                    {
                        cols.Add(new Dictionary<string, object?>
                        {
                            ["key"] = c.Key,
                            ["label"] = TagSortFields.Find(c.Key)?.Label ?? c.Key,
                            ["w"] = c.Weight,
                            ["on"] = string.Equals(_tagSortPanelSongSortField, c.Key, StringComparison.Ordinal),
                            ["asc"] = _tagSortPanelSongSortAsc,
                        });
                    }
                    var rows = new List<object>();
                    for (int i = 0; i < _webTagSortSongs.Count; i++)
                    {
                        var s = _webTagSortSongs[i];
                        var cells = new List<object>();
                        foreach (var c in visible) cells.Add(TagSortFields.ColumnText(s, c.Key));
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["n"] = i + 1,
                            ["cells"] = cells,
                        });
                    }
                    msg["cols"] = cols;
                    msg["songs"] = rows;
                }
                else if (_webTagSortMode is "Albums" or "Artists")
                {
                    // 对当前分类曲目再分组出卡（原生 ApplyTagSortPanelMode 口径：
                    // 空专辑名归"未知"，未 trim——与原生逐字一致）
                    string sub = _webTagSortMode == "Artists" ? "Artist" : "Album";
                    var cards = _webTagSortSongs
                        .GroupBy(p => string.IsNullOrWhiteSpace(sub == "Artist" ? p.Artist : p.Album)
                            ? "未知" : (sub == "Artist" ? p.Artist : p.Album),
                            StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new TagSortCategoryEntry
                        {
                            Name = g.Key,
                            Count = g.Count(),
                            FirstFilePath = g.First().FilePath,
                            Sub = sub,
                        })
                        .ToList();
                    var items = new List<object>();
                    foreach (var c in cards)
                        items.Add(new Dictionary<string, object?>
                        {
                            ["name"] = c.Name, ["n"] = c.Count, ["sub"] = sub,
                        });
                    msg["grid"] = items;
                    _ = LoadWebTagSortCoversAsync(cards, "grid");
                }
                else if (string.Equals(_webTagSortMode, "Sort", StringComparison.Ordinal))
                {
                    var presets = new List<object>();
                    foreach (var label in WebTagSortSortPresets)
                    {
                        presets.Add(new Dictionary<string, object?>
                        {
                            ["label"] = label,
                            ["on"] = string.Equals(_tagSortPreset, label, StringComparison.Ordinal),
                        });
                    }
                    msg["sort"] = new Dictionary<string, object?>
                    {
                        ["presets"] = presets,
                        ["asc"] = _tagSortAscending,
                        ["status"] = WebTagSortStatusText(),
                    };
                }
                else if (string.Equals(_webTagSortMode, "GroupBy", StringComparison.Ordinal))
                {
                    var rows = new List<object>();
                    if (_tagSortGroupFields != null && _tagSortGroupFields.Count > 0)
                        AppendWebTagSortGroupRows(rows, _webTagSortGroupTree, "");
                    msg["group"] = new Dictionary<string, object?>
                    {
                        ["presets"] = WebTagSortGroupPresets(),
                        ["rows"] = rows,
                    };
                }

                await PostCelesteWebAsync(msg);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PushWebTagSortPanel", ex);
            }
        }

        /// <summary>分类字段按钮组（原生 TagSortFieldButtonsPanel：当前字段高亮，末尾 ＋ 走配置窗）。</summary>
        private List<object> WebTagSortFieldButtons()
        {
            var list = new List<object>();
            string cur = CurrentWebTagSortField();
            foreach (var key in _tagSortCategoryFields)
            {
                var def = TagSortFields.Find(key);
                if (def == null) continue;
                list.Add(new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["label"] = def.Label,
                    ["on"] = string.Equals(key, cur, StringComparison.Ordinal),
                });
            }
            return list;
        }

        /// <summary>分组预设列表 + 当前激活项（口径照原生 SyncTagSortGroupFieldCombo：命中预设否则高亮自定义）。</summary>
        private List<object> WebTagSortGroupPresets()
        {
            string current = string.Join(",", _tagSortGroupFields);
            bool custom = true;
            var list = new List<object>();
            foreach (var (tag, label) in WebTagSortGroupPresetList)
            {
                bool on;
                if (string.Equals(tag, "__custom__", StringComparison.Ordinal))
                {
                    on = custom;   // 占位，循环结束后统一按"没命中任何预设"回填
                }
                else
                {
                    on = string.Equals(tag, current, StringComparison.Ordinal);
                    if (on) custom = false;
                }
                list.Add(new Dictionary<string, object?> { ["tag"] = tag, ["label"] = label, ["on"] = on });
            }
            if (custom)
            {
                foreach (var d in list)
                {
                    if (d is Dictionary<string, object?> m &&
                        string.Equals(m["tag"] as string, "__custom__", StringComparison.Ordinal))
                        m["on"] = true;
                }
            }
            return list;
        }

        /// <summary>「当前排序依据：」状态文本（与原生 WriteTagSortStatus 逐字一致）。</summary>
        private string WebTagSortStatusText()
        {
            string desc = _tagSortPreset;
            var tags = _tagSortCustom.Count == 0 ? new List<(string field, bool asc)>() : _tagSortCustom;
            if (tags.Count > 0)
            {
                desc += "（" + string.Join(" → ", tags.Select(t => TagSortFieldLabel(t.field) + (t.asc ? "↑" : "↓"))) + "）";
            }
            return "当前排序依据：" + desc;
        }

        /// <summary>点分类卡：进入该分类的曲目面板（原生 TagSortClassGridView_ItemClick + ShowTagSortPanel）。</summary>
        private async Task OpenWebTagSortCategoryAsync(string name)
        {
            string f = CurrentWebTagSortField();
            // 与分类墙分组同一比对规则（CurrentCultureIgnoreCase），原生同款注释：
            // 用 Ordinal 会在 "The Beatles"/"the beatles" 这类大小写不一致时漏曲目
            var songs = _playlist
                .Where(p => string.Equals(TagSortFieldVal(p, f), name, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            _webTagSortValue = name;
            _webTagSortTitle = (TagSortFields.Find(f)?.Label ?? f) + "：" + name;
            _webTagSortMode = "Songs";
            _webTagSortSongs = songs;
            await PushWebTagSortPanelAsync();
        }

        /// <summary>专辑/艺术家视角点卡：在当前分类曲目里再按子值过滤（原生 TagSortPanelGridView_ItemClick）。
        /// 原生标题在艺术家分支漏了值名（"艺术家："），网页两种都带上值名。</summary>
        private async Task OpenWebTagSortDrillAsync(string name, string sub)
        {
            string field = string.Equals(sub, "Artist", StringComparison.Ordinal) ? "Artist" : "Album";
            var filtered = _webTagSortSongs
                .Where(p => string.Equals(field == "Artist" ? (p.Artist ?? "") : (p.Album ?? ""), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _webTagSortSongs = filtered;
            _webTagSortMode = "Songs";
            _webTagSortTitle = (field == "Artist" ? "艺术家：" : "专辑：") + name;
            await PushWebTagSortPanelAsync();
        }

        /// <summary>列头点击排序（原生 TagSortColumnHeader_Click：同列切升降序，新列升序）。</summary>
        private void ApplyWebTagSortColumnSort(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.Equals(_tagSortPanelSongSortField, key, StringComparison.Ordinal))
            {
                _tagSortPanelSongSortAsc = !_tagSortPanelSongSortAsc;
            }
            else
            {
                _tagSortPanelSongSortField = key;
                _tagSortPanelSongSortAsc = true;
            }
            if (string.Equals(_webTagSortMode, "Songs", StringComparison.Ordinal))
                _ = PushWebTagSortPanelAsync();
        }

        /// <summary>播放当前列表（原生 TagSortPanelPlayAllButton_Click：分组视角播整库，其余播当前分类曲目）。</summary>
        private void PlayWebTagSortPanel()
        {
            if (string.Equals(_webTagSortMode, "GroupBy", StringComparison.Ordinal))
            {
                if (_playlist.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(_playlist.ToList());
                PlayUserPlaylistAt(0);
            }
            else
            {
                if (_webTagSortSongs.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(_webTagSortSongs.ToList());
                PlayUserPlaylistAt(0);
            }
        }

        /// <summary>分类墙「分组浏览」入口（原生 TagSortClassWallSwitchToGroupButton_Click：
        /// 分组字段与当前分类字段保持一致，所见即所得）。</summary>
        private void EnterWebTagSortGroupMode()
        {
            _tagSortGroupFields = new List<string> { CurrentWebTagSortField() };
            _webTagSortTitle = "分组浏览";
            RebuildWebTagSortGroupTree();
        }

        /// <summary>重建分组树（原生 BuildGroupTree 口径：OrdinalIgnoreCase 分组 + 按 key 文化排序），
        /// 并按高基数默认折叠初始化展开集。</summary>
        private void RebuildWebTagSortGroupTree()
        {
            _webTagSortGroupTree = new List<TagSortGroupHeader>();
            if (_tagSortGroupFields == null || _tagSortGroupFields.Count == 0) return;
            _webTagSortGroupTree = BuildGroupTree(_playlist.ToList(), 0, _tagSortGroupFields);
            _webTagSortOpen.Clear();
            _webTagSortAllPaths.Clear();
            CollectWebTagSortGroupDefaults(_webTagSortGroupTree, "");
        }

        /// <summary>收集全部组路径；高基数字段所在层级默认折叠（原生 autoCollapse 口径）。</summary>
        private void CollectWebTagSortGroupDefaults(List<TagSortGroupHeader> nodes, string prefix)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                string path = prefix.Length == 0
                    ? i.ToString(CultureInfo.InvariantCulture)
                    : prefix + "." + i.ToString(CultureInfo.InvariantCulture);
                _webTagSortAllPaths.Add(path);
                bool autoCollapse = TagSortFields.Find(n.Field)?.Cardinality == TagSortFields.Cardinality.High;
                if (!autoCollapse) _webTagSortOpen.Add(path);
                if (n.Children != null) CollectWebTagSortGroupDefaults(n.Children, path);
            }
        }

        /// <summary>按展开集扁平化分组树为网页行（组头 + 展开层级内的歌曲行，缩进 depth 由网页渲染）。</summary>
        private void AppendWebTagSortGroupRows(List<object> rows, List<TagSortGroupHeader> nodes, string prefix)
        {
            string lastField = _tagSortGroupFields is { Count: > 0 } ? _tagSortGroupFields[^1] : "";
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                string path = prefix.Length == 0
                    ? i.ToString(CultureInfo.InvariantCulture)
                    : prefix + "." + i.ToString(CultureInfo.InvariantCulture);
                bool open = _webTagSortOpen.Contains(path);
                rows.Add(new Dictionary<string, object?>
                {
                    ["h"] = 1,
                    ["path"] = path,
                    ["value"] = n.Value,
                    ["label"] = n.FieldLabel,
                    ["n"] = n.Count,
                    ["depth"] = n.Depth,
                    ["open"] = open,
                });
                if (!open) continue;
                if (n.Children != null)
                {
                    AppendWebTagSortGroupRows(rows, n.Children, path);
                }
                else if (n.Songs != null)
                {
                    foreach (var s in n.Songs)
                    {
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["h"] = 0,
                            ["path"] = path,
                            // 末级分组字段值：组内歌曲双击按它在整库里过滤（原生同款）
                            ["group"] = lastField.Length > 0 ? TagSortFieldVal(s, lastField) : "",
                            ["depth"] = n.Depth + 1,
                            ["title"] = TagSortFields.ColumnText(s, "Title"),
                            ["artist"] = TagSortFields.ColumnText(s, "Artist"),
                            ["dur"] = TagSortFields.ColumnText(s, "Duration"),
                            ["file"] = s.FilePath,
                        });
                    }
                }
            }
        }

        /// <summary>按路径（"0/1/2" 点分）找分组节点。</summary>
        private TagSortGroupHeader? FindWebTagSortGroupNode(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            List<TagSortGroupHeader> nodes = _webTagSortGroupTree;
            TagSortGroupHeader? found = null;
            foreach (string part in path.Split('.'))
            {
                if (!int.TryParse(part, out int idx) || idx < 0 || idx >= nodes.Count) return null;
                found = nodes[idx];
                nodes = found.Children ?? new List<TagSortGroupHeader>();
            }
            return found;
        }

        /// <summary>组头播放：整组替换队列从第一首（原生 PlayTagSortGroup，递归收集节点下全部歌曲）。</summary>
        private void PlayWebTagSortGroupNode(string path)
        {
            var node = FindWebTagSortGroupNode(path);
            if (node == null) return;
            var songs = CollectNodeSongs(node);
            if (songs.Count == 0) return;
            _userPlaylist.Clear();
            AddSongsToUserPlaylist(songs);
            PlayUserPlaylistAt(0);
        }

        /// <summary>组内歌曲：播所在末级分组全部、从该首开始（原生 TagSortGroupListView_DoubleTapped 歌曲口径）。
        /// path 只为协议对齐/排障，实际按 group（末级字段值）+ file 定位。</summary>
        private void PlayWebTagSortGroupSong(string path, string group, string file)
        {
            if (_tagSortGroupFields == null || _tagSortGroupFields.Count == 0) return;
            string lastField = _tagSortGroupFields[^1];
            var playlist = _playlist
                .Where(p => string.Equals(TagSortFieldVal(p, lastField), group, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (playlist.Count == 0) return;
            int startIdx = playlist.FindIndex(p => string.Equals(p.FilePath, file, StringComparison.OrdinalIgnoreCase));
            if (startIdx < 0) startIdx = 0;
            _userPlaylist.Clear();
            AddSongsToUserPlaylist(playlist);
            PlayUserPlaylistAt(startIdx);
        }

        /// <summary>分组预设切换（原生 TagSortGroupPresetCombo_SelectionChanged：__custom__ 回已保存快照）。</summary>
        private void ApplyWebTagSortGroupPreset(string tag)
        {
            if (string.Equals(tag, "__custom__", StringComparison.Ordinal))
            {
                _tagSortGroupFields = _tagSortGroupCustom.ToList();
                _tagSortGroupActivePreset = "__custom__";
            }
            else
            {
                var fields = tag.Split(',').ToList();
                if (fields.Count == 0) return;
                _tagSortGroupFields = fields;
                _tagSortGroupActivePreset = tag;
            }
            RebuildWebTagSortGroupTree();
            AppSettingsStore.Update(s =>
            {
                s.TagSortGroupFields = _tagSortGroupCustom;
                s.TagSortGroupActivePreset = _tagSortGroupActivePreset;
            });
            _ = PushWebTagSortPanelAsync();
        }

        /// <summary>＋配置分类字段：复用原生 TagSortFieldConfigWindow，回调里同步原生字段 + 落设置 + 重推墙。</summary>
        private void OpenWebTagSortFieldConfig()
        {
            var win = new TagSortFieldConfigWindow(_tagSortCategoryFields);
            win.FieldsConfirmed += fields =>
            {
                _tagSortCategoryFields = fields;
                if (!fields.Contains(_tagSortClassField))
                    _tagSortClassField = fields.FirstOrDefault() ?? "Artist";
                _webTagSortField = "";
                AppSettingsStore.Update(s => s.TagSortCategoryFields = fields.ToList());
                // 字段组换了就回分类墙（原生 BuildTagSortFieldButtons + ShowTagSortClassWall 口径）
                _webTagSortValue = "";
                _webTagSortTitle = "";
                _webTagSortMode = "Wall";
                _webTagSortSongs = new List<PlaylistItem>();
                _ = PushWebTagSortWallAsync();
            };
            win.Activate();
        }

        /// <summary>分组「自定义…」：复用原生 TagSortGroupFieldsWindow。</summary>
        private void OpenWebTagSortGroupCustom()
        {
            var win = new TagSortGroupFieldsWindow(_tagSortGroupCustom);
            win.FieldsConfirmed += fields =>
            {
                _tagSortGroupFields = fields;
                _tagSortGroupCustom = fields.ToList();
                _tagSortGroupActivePreset = "__custom__";
                RebuildWebTagSortGroupTree();
                AppSettingsStore.Update(s =>
                {
                    s.TagSortGroupFields = _tagSortGroupCustom;
                    s.TagSortGroupActivePreset = _tagSortGroupActivePreset;
                });
                _ = PushWebTagSortPanelAsync();
            };
            win.Activate();
        }

        /// <summary>排序「自定义排序…」：复用原生 CustomSortOrderWindow（确认即应用到整个曲库）。</summary>
        private void OpenWebTagSortSortCustom()
        {
            var win = new CustomSortOrderWindow(_tagSortCustom, _tagSortAscending);
            win.SortConfirmed += (fields, asc) =>
            {
                _tagSortCustom = fields;
                _tagSortAscending = asc;
                _tagSortPreset = "自定义";
                ApplyTagSortToLibrary();
                _ = PushWebTagSortPanelAsync();
            };
            win.Activate();
        }

        /// <summary>
        /// 分类墙/面板卡片封面按批补（kind=tagsortcovers，网页按下标回填）。
        /// WriteCoverFile 要解音频文件（云盘曲库下每首几百毫秒），必须走后台线程；
        /// 发送切回 UI 线程（PostCelesteWebAsync 线程亲和）。ctx=wall/grid 防串台。
        /// </summary>
        private async Task LoadWebTagSortCoversAsync(List<TagSortCategoryEntry> slice, string ctx)
        {
            try
            {
                const int batchSize = 8;
                const int batchDelayMs = 30;
                for (int i = 0; i < slice.Count; i += batchSize)
                {
                    if (!_webMainOpen) break;
                    int from = i;
                    int end = Math.Min(i + batchSize, slice.Count);
                    var items = await Task.Run(() =>
                    {
                        var batch = new List<object>();
                        for (int j = from; j < end; j++)
                        {
                            string cover = "";
                            string path = slice[j].FirstFilePath;
                            if (!string.IsNullOrEmpty(path))
                            {
                                if (!_webMainCoverCache.TryGetValue(path, out cover))
                                {
                                    cover = WriteCoverFile(path);
                                    _webMainCoverCache[path] = cover;
                                }
                            }
                            batch.Add(new Dictionary<string, object?>
                            {
                                ["i"] = j,
                                ["cover"] = cover ?? "",
                            });
                        }
                        return batch;
                    });
                    var payload = new Dictionary<string, object?>
                    {
                        ["kind"] = "tagsortcovers",
                        ["ctx"] = ctx,
                        ["items"] = items,
                    };
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_webMainOpen) _ = PostCelesteWebAsync(payload);
                    });
                    await Task.Delay(batchDelayMs);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LoadWebTagSortCovers", ex);
            }
        }

        /// <summary>歌曲库工具栏里的「Apple 风格界面」按钮。</summary>
        private void WebMainPilotButton_Click(object sender, RoutedEventArgs e)
            => _ = OpenWebMainAsync();
    }
}
