using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Color = Windows.UI.Color;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 纯工具函数：不碰任何界面状态，谁都能调。
    /// 2026-10-31 W13 第三刀从旧界面壳（Misc / Search / Library2）整块搬出来 ——
    /// 它们是活代码（播放页、网页桥、标签服务）借得最多的一批，
    /// 留在壳文件里壳就永远拆不掉。搬移逐行原样，行为零变化。
    /// </summary>
    public sealed partial class MainWindow
    {

        /// <summary>
        /// 文件/文件夹选取器 InitializeWithWindow 用的宿主窗口句柄（给 UserControl 用）。
        /// ⚠ UserControl 不能 GetWindowHandle(this)：UserControl 没实现 IWindowNative，
        /// 会抛 "Failed to create a CCW … the specified cast is not valid"。2026-10-13
        /// 标签页实测过一次；2026-10-17 设置页「导入主题」按钮又连崩 8 次，用户根本
        /// 导不进主题文件，连带「测试皮肤选圆角主题文件」这条路整个堵死。
        /// 主窗口自己的选取器继续用 this（MainWindow 就是 Window，没问题）。
        /// </summary>
        internal static nint PickerHostHandle()
            => Instance is MainWindow host ? host._webShellWindow?.Handle ?? host.NativeHandle : 0;

        // ==== HiFi 模式状态查询（2026-10-31 从 MainWindow.Misc.cs 搬来）====
        // 只读设置和音频引擎状态，不碰任何界面对象 —— 18 + 10 处调用点全在活代码里。

        /// <summary>当前设置是否处于 HiFi 独占输出模式（WASAPI 独占 / ASIO）。</summary>
        private static bool IsHiFiModeSelected()
        {
            string mode = AppSettingsStore.Load().OutputMode;
            return string.Equals(mode, "WasapiExclusive", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Asio", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>HiFi 独占/ASIO 下主界面音量条是否应解冻：设置页「HiFi 软件音量」开关开启，
        /// 且当前不是 DSD 直出播放（DSD 数据源绕过 DSP 链，软件音量无效，滑块保持冻结）。
        /// 共享模式恒 false（共享下滑条本就实时可调，不经此判断）。</summary>
        private bool IsHiFiSoftVolumeUiUnlocked()
        {
            if (!IsHiFiModeSelected())
            {
                return false;
            }

            if (!AppSettingsStore.Load().HiFiSoftwareVolume)
            {
                return false;
            }

            string? src = _audioEngine?.SourceFormatDescription;
            return string.IsNullOrWhiteSpace(src)
                || src.IndexOf("DSD", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }

            if (time.TotalHours >= 1)
            {
                return time.ToString(@"h\:mm\:ss");
            }

            return time.ToString(@"mm\:ss");
        }

        private static bool ContainsIgnoreCase(string? source, string query)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query))
            {
                return false;
            }

            string s = SearchNormalize(source);
            string q = SearchNormalize(query);
            if (s.Contains(q, System.StringComparison.Ordinal))
            {
                return true;
            }

            // 拼音首字母：query 为纯字母、源含中文时，命中“周杰伦 ← zjl”
            if (ContainsHan(source) && IsAsciiLetters(q))
            {
                string py = PinyinInitials(source);
                if (!string.IsNullOrEmpty(py) && py.Contains(q, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>中文搜索友好归一化：转小写 → 全角转半角 → 繁体转简体，实现容错匹配（搜繁体命中简体、搜全角命中半角等）。</summary>
        private static string SearchNormalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\u3000')
                {
                    sb.Append(' ');
                }
                else if (c >= '\uFF01' && c <= '\uFF5E')
                {
                    sb.Append((char)(c - 0xFEE0)); // 全角 → 半角
                }
                else if (c < 0x20)
                {
                    continue;
                }
                else if (_tradToSimp.TryGetValue(c, out char simp))
                {
                    sb.Append(simp); // 繁体 → 简体
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().ToLowerInvariant();
        }

        private static bool IsAsciiLetters(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsHan(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取汉字串的拼音首字母（如“周杰伦”→“zjl”），基于 ToolGood.Words 拼音库。</summary>
        private static string PinyinInitials(string text)
        {
            try
            {
                string py = ToolGood.Words.Pinyin.WordsHelper.GetFirstPinyin(text ?? string.Empty);
                return string.IsNullOrEmpty(py) ? string.Empty : py.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static readonly (char Trad, char Simp)[] _tradSimpPairs = new[]
        {
            ('樂','乐'),
            ('個','个'),
            ('們','们'),
            ('與','与'),
            ('見','见'),
            ('現','现'),
            ('視','视'),
            ('覺','觉'),
            ('還','还'),
            ('過','过'),
            ('來','来'),
            ('這','这'),
            ('點','点'),
            ('員','员'),
            ('戶','户'),
            ('體','体'),
            ('動','动'),
            ('網','网'),
            ('電','电'),
            ('話','话'),
            ('讓','让'),
            ('該','该'),
            ('開','开'),
            ('關','关'),
            ('說','说'),
            ('請','请'),
            ('講','讲'),
            ('認','认'),
            ('識','识'),
            ('訊','讯'),
            ('華','华'),
            ('結','结'),
            ('構','构'),
            ('組','组'),
            ('約','约'),
            ('紙','纸'),
            ('細','细'),
            ('終','终'),
            ('紀','纪'),
            ('紅','红'),
            ('綠','绿'),
            ('繼','继'),
            ('續','续'),
            ('級','级'),
            ('縣','县'),
            ('綜','综'),
            ('維','维'),
            ('績','绩'),
            ('經','经'),
            ('總','总'),
            ('線','线'),
            ('編','编'),
            ('練','练'),
            ('義','义'),
            ('習','习'),
            ('職','职'),
            ('舊','旧'),
            ('節','节'),
            ('蘭','兰'),
            ('藍','蓝'),
            ('藝','艺'),
            ('藥','药'),
            ('應','应'),
            ('戲','戏'),
            ('趙','赵'),
            ('遠','远'),
            ('遲','迟'),
            ('選','选'),
            ('團','团'),
            ('顧','顾'),
            ('頭','头'),
            ('題','题'),
            ('額','额'),
            ('類','类'),
            ('風','风'),
            ('飛','飞'),
            ('飲','饮'),
            ('飯','饭'),
            ('馬','马'),
            ('駕','驾'),
            ('驚','惊'),
            ('齊','齐'),
            ('齒','齿'),
            ('龍','龙'),
            ('龐','庞'),
            ('麗','丽'),
            ('麥','麦'),
            ('麵','面'),
            ('車','车'),
            ('軌','轨'),
            ('轉','转'),
            ('軟','软'),
            ('輕','轻'),
            ('載','载'),
            ('輪','轮'),
            ('詞','词'),
            ('詩','诗'),
            ('語','语'),
            ('誤','误'),
            ('誠','诚'),
            ('敗','败'),
            ('質','质'),
            ('賬','账'),
            ('貫','贯'),
            ('貼','贴'),
            ('贈','赠'),
            ('則','则'),
            ('側','侧'),
            ('銀','银'),
            ('鍵','键'),
            ('鎖','锁'),
            ('銅','铜'),
            ('門','门'),
            ('問','问'),
            ('間','间'),
            ('聞','闻'),
            ('閣','阁'),
            ('長','长'),
            ('張','张'),
            ('樣','样'),
            ('楊','杨'),
            ('樹','树'),
            ('極','极'),
            ('機','机'),
            ('樓','楼'),
            ('欄','栏'),
            ('權','权'),
            ('檢','检'),
            ('檔','档'),
            ('橋','桥'),
            ('標','标'),
            ('聲','声'),
            ('聽','听'),
            ('聯','联'),
            ('葉','叶'),
            ('蓋','盖'),
            ('簡','简'),
            ('筆','笔'),
            ('範','范'),
            ('簽','签'),
            ('籃','篮'),
            ('絕','绝'),
            ('統','统'),
            ('絲','丝'),
            ('腦','脑'),
            ('臉','脸'),
            ('勝','胜'),
            ('騰','腾'),
            ('膽','胆'),
            ('眾','众'),
            ('書','书'),
            ('會','会'),
            ('陳','陈'),
            ('陣','阵'),
            ('隨','随'),
            ('離','离'),
            ('雖','虽'),
            ('雞','鸡'),
            ('靜','静'),
            ('顯','显'),
            ('飄','飘'),
            ('韻','韵'),
            ('項','项'),
            ('順','顺'),
            ('頑','顽'),
            ('領','领'),
            ('顆','颗'),
            ('頻','频'),
            ('預','预'),
            ('髮','发'),
            ('飼','饲'),
            ('驗','验'),
            ('髒','脏'),
            ('貴','贵'),
            ('買','买'),
            ('賣','卖'),
            ('讓','让'),
            ('彈','弹'),
            ('強','强'),
            ('張','张'),
            ('學','学'),
            ('寶','宝'),
            ('實','实'),
            ('導','导'),
            ('將','将'),
            ('帥','帅'),
            ('廣','广'),
            ('廳','厅'),
            ('後','后'),
            ('復','复'),
            ('從','从'),
            ('應','应'),
            ('當','当'),
            ('寧','宁'),
            ('帶','带'),
            ('幫','帮'),
            ('憶','忆'),
            ('憂','忧'),
            ('惡','恶'),
            ('愛','爱'),
            ('慶','庆'),
            ('應','应'),
            ('趕','赶'),
            ('超','超'),
            ('踴','踊'),
            ('跩','跩'),
            ('單','单'),
            ('麼','么'),
            ('廠','厂'),
            ('廚','厨'),
            ('廟','庙'),
            ('廢','废'),
            ('廣','广'),
            ('廳','厅'),
            ('彈','弹'),
            ('強','强'),
        };

        // ⚠ 声明顺序有讲究：_tradSimpPairs 必须在 _tradToSimp 之前，
        // 否则静态初始化器先跑 BuildTradToSimpMap() 时表还是 null，启动即 TypeInitializationException。
        // （2026-10-31 实测踩过：搬把两个声明的先后写反，程序直接起不来。）
        private static readonly System.Collections.Generic.Dictionary<char, char> _tradToSimp = BuildTradToSimpMap();

        private static System.Collections.Generic.Dictionary<char, char> BuildTradToSimpMap()
        {
            var d = new System.Collections.Generic.Dictionary<char, char>();
            foreach (var p in _tradSimpPairs)
            {
                d[p.Trad] = p.Simp;
            }
            return d;
        }

        /// <summary>
        /// 封面解码目标像素 = 元素当前显示尺寸 × 屏幕缩放。
        /// 显示尺寸取不到（元素隐藏/未布局）时用 fallbackLayoutPx。
        /// </summary>
        private int CoverDecodeTargetPx(FrameworkElement? element, double fallbackLayoutPx)
        {
            double layoutPx = 0;
            if (element != null)
            {
                // ActualWidth 布局后才有；Width 是布局代码显式设的（未设为 NaN），可提前用
                layoutPx = element.ActualWidth > 0
                    ? element.ActualWidth
                    : (double.IsNaN(element.Width) ? 0 : element.Width);
            }
            if (layoutPx <= 0)
            {
                layoutPx = fallbackLayoutPx;
            }
            return (int)Math.Clamp(Math.Ceiling(layoutPx * CoverRasterizationScale()), 96, 1024);
        }

        /// <summary>当前屏幕缩放（RasterizationScale，100%=1.0）。取不到按 1.0。</summary>
        private double CoverRasterizationScale()
        {
            // ⚠ W13 之后这里恒为 1.0，这不是"待办"，是现状：
            // 旧实现是 `var root = NowPlayingCoverImage?.XamlRoot ?? Content?.XamlRoot;`
            // 而旧控件在 LegacyControls 里全是 dynamic 声明，shim 的 Image / Window 上根本没有
            // XamlRoot 这个成员 —— 每次调用都在运行时抛 RuntimeBinderException，被 catch 成 1.0。
            // 换句话说这个函数从"界面脱离 WinUI"那天起就没成功读过缩放值。
            //
            // 这里保留 1.0 而不是改用 GetDpiForWindow：后者会让封面按 1.25/1.5 倍解码，
            // 内存与解码耗时都会变 —— 那是行为变更，不属于"搬迁"。要真的支持高 DPI 另开任务。
            return 1.0;
        }

        // 封面字节缓存（原在 MainWindow.Library2.cs）。ExtractCoverBytes 搬来这里时把它一起带走，
        // 否则工具文件反过来被壳文件拴住：Library2 一旦退出编译，ExtractCoverBytes 就 CS0103。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> CoverBytesCache = new();
        private const int CoverBytesCacheMax = 200;

        internal static byte[]? ExtractCoverBytes(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            if (CoverBytesCache.TryGetValue(audioPath, out byte[]? cached))
            {
                return cached;
            }

            // 2026-10-29：取字节的实现搬到 CoverSourceReader（原 TryLoadInnerCover/TryLoadOuterCover），
            // 规则不变：按 UseInnerCoverFirst 决定内嵌与外置的先后，缺哪边就用另一边。
            byte[]? result = CoverSourceReader.Read(audioPath, AppSettingsStore.Load().UseInnerCoverFirst);

            if (CoverBytesCache.Count >= CoverBytesCacheMax)
            {
                CoverBytesCache.Clear();
            }

            CoverBytesCache[audioPath] = result;
            return result;
        }

        // ==== 错误对话框（2026-10-31 从 MainWindow.Misc.cs 搬来）====
        // 22 处调用点分散在 Playback / Playback2 / Playback3 / Library2 / Features，
        // 定义原来留在壳文件里，等于整个壳被这 22 处拴住。
        private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null)
        {
            try
            {
                // 无条件留痕 + 立即落盘：用户遇到"弹了个窗但什么信息都没有"时，
                // 日志里至少能查到 弹窗标题/正文/时间。之前只有部分调用点写日志，
                // 且日志是内存缓冲（被强杀会丢最后 300ms），排查时全是盲区。
                global::CelesteMusicPlayer.StartupLog.Write(
                    $"[对话框] {title} | {message?.Replace("\r\n", " / ").Replace("\n", " / ")}");
                global::CelesteMusicPlayer.StartupLog.Flush();

                // 早期启动（如 FirstActivated 阶段）Content.XamlRoot 可能尚未建立，
                // 此时弹 ContentDialog 会抛 "This element does not have a XamlRoot" 并被静默吞掉。
                // 改为等待 XamlRoot 就绪再弹，最多约 2s；仍拿不到则把错误写入日志而非丢失。
                XamlRoot? root = await GetXamlRootWhenReadyAsync(xamlRoot);
                if (root == null)
                {
                    global::CelesteMusicPlayer.StartupLog.Write("ShowErrorAsync: XamlRoot 不可用，错误未弹出 title=" + title + " msg=" + message);
                    return;
                }
                ContentDialog dialog = new()
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = root
                };
                await dialog.ShowAsync();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private System.Threading.Tasks.Task<XamlRoot?> GetXamlRootWhenReadyAsync(XamlRoot? preferred)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<XamlRoot?>();
            int tries = 0;
            void Poll()
            {
                XamlRoot? r = preferred ?? Content?.XamlRoot;
                if (r != null) { tcs.TrySetResult(r); return; }
                if (++tries > 40) { tcs.TrySetResult(null); return; } // 约 2s 上限，避免无限等待
                DispatcherQueue?.TryEnqueue(Poll);
            }
            DispatcherQueue?.TryEnqueue(Poll);
            return tcs.Task;
        }

        // 曲目在播放列表里的下标（纯查表，2026-10-31 从 MainWindow.Library2.cs 搬来）

        private int FindLibraryIndex(string filePath)
        {
            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
        // 在资源管理器里定位文件（纯工具，2026-10-31 从 MainWindow.Misc.cs 搬来）

        /// <summary>在资源管理器中选中并显示该文件。</summary>
        private static void OpenFileLocationInExplorer(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("打开文件位置失败: " + ex.Message);
            }
        }

        // 界面风格与强调色（2026-10-31 从 MainWindow.UiTheme.cs 搬来；只读设置 + 纯计算，不碰控件）

        /// <summary>极客界面模式的设置值（设置项「界面风格」下拉里的「极客」）。</summary>
        internal const string UiStyleGeek = "Geek";

        /// <summary>极客磷光色的三档（设置项「极客磷光色」）。琥珀为默认。</summary>
        internal const string PhosphorAmber = "Amber";
        internal const string PhosphorGreen = "Green";
        internal const string PhosphorCyan = "Cyan";

        /// <summary>极客界面模式已于 2026-10-31 整体移除（用户拍板：新外壳不做极客皮肤）。
        /// 这里恒返回 false，保留方法名是为了不改动全项目几十处调用点；
        /// 老存档里的 UiStyleMode="Geek" 由 AppSettingsStore 归一到 ClassicSystem。</summary>
        internal static bool IsGeekUiStyleActive() => false;

        private Brush ResolveAccentBrush()
        {
            // 极客界面：磷光色是界面里唯一的彩色 —— 选中竖条/选中底/排序胶囊/导航高亮等
            // 一切"强调色"都跟随它，不混用设置里的主题色（用户实测反馈过选中背景漏主题色）。
            if (IsGeekUiStyleActive())
            {
                return new SolidColorBrush(GeekPhosphorColor());
            }

            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.AccentSource == "Custom")
            {
                return new SolidColorBrush(ColorHelper.ParseHexColor(settings.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212));
            }

            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj)
                && colorObj is Color color)
            {
                return new SolidColorBrush(color);
            }

            return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        }

        // 极客磷光色（2026-10-31 从 MainWindow.GeekTransport.cs 搬来；纯查表 + 解析设置里的色值）

        private static Color PhosphorColor(string? name)
        {
            // 自定义磷光色：设置里存 "Custom:#RRGGBB"（兼容裸 "#RRGGBB"）。解析失败落回琥珀。
            if (!string.IsNullOrWhiteSpace(name))
            {
                string value = name.Trim();
                if (value.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring("Custom:".Length).Trim();
                }

                if (value.StartsWith("#") && value.Length >= 7
                    && uint.TryParse(value.AsSpan(1, 6), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
                {
                    return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                }

                if (!string.Equals(value, PhosphorGreen, StringComparison.Ordinal)
                    && !string.Equals(value, PhosphorCyan, StringComparison.Ordinal)
                    && !string.Equals(value, PhosphorAmber, StringComparison.Ordinal))
                {
                    // 未知值（含被截坏的十六进制）：回退默认，避免整界面丢色
                    return Color.FromArgb(255, 0xFF, 0xB0, 0x00);
                }

                switch (value)
                {
                    case PhosphorGreen: return Color.FromArgb(255, 0x3E, 0xFF, 0x7A);   // 荧光绿
                    case PhosphorCyan: return Color.FromArgb(255, 0x35, 0xE0, 0xFF);    // 青
                }
            }

            return Color.FromArgb(255, 0xFF, 0xB0, 0x00);                               // 琥珀（默认）：老式 CRT 的暖黄
        }

        /// <summary>当前设置选中的磷光色（极客界面里唯一的彩色）。供桌面歌词等自绘窗口取色。</summary>
        internal static Color GeekPhosphorColor()
            => PhosphorColor(AppSettingsStore.Load().GeekPhosphorColor);

        // 封面字节→位图（2026-10-31 从 MainWindow.Misc.cs 搬来）：纯静态解码，与 ExtractCoverBytes 同属封面域。

        /// <summary>
        /// 字节 → BitmapImage（须在 UI 线程）。
        /// maxPixel &gt; 0 且源图更大时，按显示尺寸解码（解码期高质量重采样），
        /// 避免 1400px 大图直接被 UI 层双线性缩到 320px 显示导致的发糊/锯齿。
        /// </summary>
        private static async Task<BitmapImage?> CreateBitmapFromBytesAsync(byte[] bytes, int maxPixel = 0)
        {
            try
            {
                var image = new BitmapImage();
                if (maxPixel > 0 && CoverSourceReader.TryMeasureImageSize(bytes, out int w, out int h))
                {
                    // 只缩不放：源图更大才按显示尺寸解码；源图更小保持原样（解码期放大没有意义）
                    if (Math.Max(w, h) > maxPixel)
                    {
                        // 只设长边对应的那一维，另一维由解码器按比例推（同时设两维会拉变形）
                        if (w >= h)
                        {
                            image.DecodePixelWidth = maxPixel;
                        }
                        else
                        {
                            image.DecodePixelHeight = maxPixel;
                        }
                    }
                }
                using InMemoryRandomAccessStream stream = new();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("封面解码失败: " + ex.Message);
                return null;
            }
        }
        // DSD 判断与胶囊底色（2026-10-31 从 MainWindow.Misc.cs 搬来）：纯函数，只读扩展名 / FrostedGlass 经典模式 / 应用资源。

        /// <summary>当前设置是否处于 HiFi 独占输出模式（WASAPI 独占 / ASIO）。</summary>
        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        /// <summary>返回 DSD 容器扩展名大写（"DSF"/"DFF"），非 DSD 返回 null。</summary>
        private static string? DsdExtOf(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext switch { ".dsf" => "DSF", ".dff" => "DFF", _ => null };
        }

        private Brush ResolveCapsuleFillBrush()
        {
            // 同上：经典界面下按经典主题给一套明确的中性底色
            if (FrostedGlass.ClassicMode != null)
            {
                return new SolidColorBrush(FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(16, 0, 0, 0)
                    : Color.FromArgb(26, 255, 255, 255));
            }

            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? controlBrush)
                && controlBrush is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }
        // 强调色/界面基色/风格判断（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：依赖 ResolveAccentBrush 与 ColorHelper（活），IsClassicUiStyleActive 是纯读设置。

        private Color ResolveAccentColor()
        {
            if (ResolveAccentBrush() is SolidColorBrush scb && scb.Color.A > 0)
            {
                return scb.Color;
            }

            return Color.FromArgb(255, 0, 120, 212);
        }

        /// <summary>
        /// 静态版的 ResolveAccentColor：给没有窗口实例的上下文（ColorHelper 的弹窗强调、
        internal static Color ResolveAccentColorStatic()
            => Instance?.ResolveAccentColor() ?? Color.FromArgb(255, 0, 120, 212);

        private Brush ResolveAccentForegroundBrush()
            => ColorHelper.ResolveContrastingForeground(ResolveAccentBrush());

        internal static bool IsClassicUiStyleActive()
        {
            string mode;
            try
            {
                mode = AppSettingsStore.Load().UiStyleMode;
            }
            catch
            {
                return false;
            }

            return mode is "ClassicSystem" or "ClassicLight" or "ClassicDark";
        }

        /// <summary>当前是否处于「不透明风格」（经典或极客）：这两类模式下背景图层 / 封面背景 / 预设渐变一律不显示。</summary>
        internal static bool IsOpaqueUiStyleActive() => IsClassicUiStyleActive() || IsGeekUiStyleActive();

        private Color ResolveUiBaseTintColor()
        {
            // 经典界面：直接给出经典底色（浅 = 浅灰、深 = 炭灰）。
            // 不能走下面的候选色：浅色主题下那几个键取到的都是"接近白"被跳过，
            // 最后落到深灰回退色，浅色界面里的描边/胶囊/多选底色就会发黑。
            if (FrostedGlass.ClassicMode != null)
            {
                return FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(255, 243, 243, 243)
                    : Color.FromArgb(255, 32, 32, 32);
            }

            // 优先取右侧面板实际底色（用户看到的整体 UI 区域色）
            if (ColorHelper.TryGetBrushColor(NowPlayingPane?.Background, out Color paneColor)
                && paneColor.A > 0
                && !ColorHelper.IsNearWhite(paneColor))
            {
                return Color.FromArgb(255, paneColor.R, paneColor.G, paneColor.B);
            }

            FrameworkElement? anchor = Content as FrameworkElement ?? NowPlayingPaneContent;
            string[] keys =
            {
                "CardBackgroundFillColorDefault",
                "CardBackgroundFillColorDefaultBrush",
                "SolidBackgroundFillColorBase",
                "SolidBackgroundFillColorBaseBrush",
                "ApplicationPageBackgroundThemeBrush"
            };

            foreach (string key in keys)
            {
                if (ColorHelper.TryGetThemeColor(anchor, key, out Color themeColor)
                    && themeColor.A > 0
                    && !ColorHelper.IsNearWhite(themeColor))
                {
                    return Color.FromArgb(255, themeColor.R, themeColor.G, themeColor.B);
                }
            }

            // 深色 Mica / 深灰 UI 回退（勿用浅灰，否则矩形发白）
            return Color.FromArgb(255, 42, 42, 42);
        }
        // 主窗口单例（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：全项目都在用的核心静态属性，声明搬家零行为变化；随行的 ResolveAccentColorStatic 依赖它。

        /// <summary>设置页等子窗口用来回写主窗口状态。</summary>
        internal static MainWindow? Instance { get; private set; }
        // 极客歌词强调色与磷光色分层（2026-10-31 从 MainWindow.GeekNowPlaying.cs 搬来）：PhosphorShade 纯计算；GeekLyricAccentColor 读 _geekNowPlaying 状态字段（随批移动）。

        /// <summary>
        /// 歌词磷光色：非极客返回 null（歌词走主题强调色链，行为与以前完全一致）。
        /// 切风格 / 换磷光色时由 ApplyGeekNowPlaying 触发一次强制重染。
        /// </summary>
        internal Color? GeekLyricAccentColor()
            => _geekNowPlayingActive && _geekNowPlayingPhosphor is Color color ? color : (Color?)null;

        /// <summary>磷光色叠加 alpha 的画刷（歌词分层用：邻近 / 远处 / 未唱字）。</summary>
        internal static SolidColorBrush PhosphorShade(Color baseColor, byte alpha)
            => new(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));

        // 极客播放页状态（2026-10-31 随 GeekLyricAccentColor 从 MainWindow.GeekNowPlaying.cs 搬来）

        /// <summary>当前生效的磷光色；null = 极客没开。设置里换了色只重染，不重复刷整页。</summary>
        private Color? _geekNowPlayingPhosphor;

        private bool _geekNowPlayingActive;

        // 极客风格缓存（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：IsGeekUiStyleActive 已在本文件

        /// <summary>
        /// 极客模式的进程内缓存（第一次问的时候读一次设置，之后沿用）。
        /// 给"每条列表行都要判断一次"的高频路径用：PlaylistItem.FormatChips 的极客包装、行分隔线等。
        /// AppSettingsStore.Load() 每次都会 Clone 一整个设置对象，逐行调用太浪费。
        /// 界面风格切换时由 ApplyUiStyleMode 立即刷新，不会读到过期值。
        /// </summary>
        private static bool? _geekUiStyleCached;

        internal static bool GeekUiStyleCached => _geekUiStyleCached ??= IsGeekUiStyleActive();
        // 电平表空闲值与旧列表已实现项枚举（2026-10-31 从 MainWindow.Misc.cs 搬来）：IdleLevel 纯计算（SpectrumEnvelope/FormatHelper 在活类），枚举只操作传入对象（VisualTreeWalker 活类）。

        /// <summary>未播放时的静态频谱形状（始终可见，中间高两边低）。</summary>
        private static double IdleLevel(int index)
        {
            double shape = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(index * 1.7 + 1.3));
            return Math.Max(0.18, shape * FormatHelper.SpectrumEnvelope(index));
        }

        // 行悬停画刷（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：纯计算，依赖 IsOpaqueUiStyleActive（已在本文件）。

        /// <summary>行悬停底色：经典 / 极客界面用半透明灰（原浅白在浅色主题与近黑底上几乎看不见）。</summary>
        internal static Brush ResolveRowHoverBrush(FrameworkElement? anchor)
        {
            if (!IsOpaqueUiStyleActive())
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
            }

            bool dark = anchor?.ActualTheme == ElementTheme.Dark;
            return new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(34, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 128, 128, 128));
        }
    }
}
