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

        /// <summary>打开网页试点页。任何一步失败都静默降级：界面保持原生版，程序照常用。</summary>
        public async Task OpenWebAlbumPilotAsync()
        {
            if (_webPilotOpen) return;
            try
            {
                DeployWebAsset("album.html");

                // 第一个 WebView2 实例要吃 286MB，所以不到真正要用的时候不建
                if (!_celesteWebReady)
                    await PrewarmCelesteWeb();

                if (!_celesteWebReady || _celesteWeb?.CoreWebView2 == null)
                {
                    StartupLog.Write("[Web试点] WebView2 没起来，试点页放弃，界面保持原样");
                    return;
                }

                // 封面抽一次就够，别每秒推送都去解音频文件
                _webCoverUrl = "";
                if (_openedAlbum != null && !string.IsNullOrEmpty(_openedAlbum.CoverSourcePath))
                    _webCoverUrl = WriteCoverFile(_openedAlbum.CoverSourcePath);

                CelesteWebHostGrid.Visibility = Visibility.Visible;
                // 预热时 WebView2 是 Collapsed（免得启动时闪一下白屏），这会儿才让它现形
                if (_celesteWeb != null) _celesteWeb.Visibility = Visibility.Visible;
                _webPilotOpen = true;

                // 上面已经判过 _celesteWeb?.CoreWebView2 != null，这里编译器不知道，用 ! 说明
                var cv = _celesteWeb!.CoreWebView2;
                cv.Navigate("http://celeste.local/album.html");

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
            StartupLog.Write("[Web试点] 已关闭，回到原生界面");
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
                        if (track != null) PlayPlaylistItem(track);
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
