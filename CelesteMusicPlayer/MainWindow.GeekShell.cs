using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客模式的「布局控制器」：把界面从卡片流重排成终端屏。
    ///
    /// 分两块，各自独立开关，切回其它风格时完整还原：
    ///   D 顶栏链路读数条 —— OUT / FMT / FLAG / BUF 四项引擎真数据常驻显示；
    ///   A 底部命令行条   —— 控制键 ASCII 化 + 音量字符条 + 状态灯。
    ///
    /// 为什么不用图标字形：图标用的是字体里的特殊符号位（PUA 区），等宽字体里没有这些字符，
    /// 渲染出来就是缺字的问号框（用户实测：播放键与右上角按钮变问号）。
    /// 极客界面一律用普通字符画键位（[&gt;] [||] [&gt;&gt;]），从根上不碰 PUA。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>音量字符条有多少格。布局宽度拿不到时的兜底值。</summary>
        private const int GeekVolumeBlocks = 10;

        private bool _geekVolumeCanvasSized;

        private TextBlock? _geekPlayLabel;
        private Visibility _volumeIconVisibility = Visibility.Visible;
        private Visibility _volumeCanvasVisibility = Visibility.Visible;

        /// <summary>被 ASCII 化过的按钮 → 原始内容与图标可见性备份（切风格时还原用）。</summary>
        private readonly Dictionary<Button, GeekButtonBackup> _geekAsciiButtons = new();

        private sealed class GeekButtonBackup
        {
            public object? Content;
            public TextBlock? Label;

            /// <summary>被藏掉的图标 → 原可见性。用字典去重：补藏可能对同一个图标跑多次。</summary>
            public Dictionary<UIElement, Visibility> Icons = new();
        }

        /// <summary>极客布局总入口：由 ApplyGeekChrome 在界面风格切换时调用。</summary>
        internal void ApplyGeekShell(bool geek)
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                var phosphor = new SolidColorBrush(PhosphorColor(settings.GeekPhosphorColor));
                var mono = new FontFamily("Consolas");

                ApplyGeekChainStrip(geek, phosphor);
                ApplyGeekAsciiKeys(geek, mono);
                ApplyGeekVolumeBar(geek, phosphor);
                ApplyGeekSidebar(geek, mono, phosphor);

                if (GeekStatusLamp != null)
                {
                    GeekStatusLamp.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
                }

                if (geek)
                {
                    UpdateGeekShellTick();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ApplyGeekShell", caught);
            }
        }

        /// <summary>每秒刷一次的三项读数（顶栏链路 / 音量条 / 状态灯）。挂在既有读数定时器上。</summary>
        internal void UpdateGeekShellTick()
        {
            UpdateGeekChainStripText();
            UpdateGeekVolumeText();
            UpdateGeekStatusLamp();
            UpdateGeekLibStatsThrottled();
        }

        // ---------- B：左栏编号菜单 + 曲库统计块 ----------

        /// <summary>编号菜单：数字键直达的前十个分类（1~9 + 0），其余分类不编号。</summary>
        private static readonly (string Digit, string NavTag)[] GeekNavKeys =
        {
            ("1", "Songs"), ("2", "Albums"), ("3", "Artists"), ("4", "AlbumArtists"),
            ("5", "Favorites"), ("6", "Ratings"), ("7", "Recent"), ("8", "PlaylistWall"),
            ("9", "Genre"), ("0", "Year"),
        };

        /// <summary>插进导航条目前缀里的编号 TextBlock → 还原时要摘掉。</summary>
        private readonly Dictionary<Button, TextBlock> _geekNavPrefixes = new();

        private int _geekStatsTickCounter;

        private void ApplyGeekSidebar(bool geek, FontFamily mono, SolidColorBrush phosphor)
        {
            try
            {
                // 编号**不再显示在界面上**（用户要求：左侧浏览不要数字前缀）。
                // 数字键直达仍然有效，只是变成不显形的快捷键；这里只负责把历史残留的前缀摘干净。
                foreach ((string digit, string tag) in GeekNavKeys)
                {
                    _ = digit;
                    Button? button = FindNavButtonByTag(tag);
                    if (button == null)
                    {
                        continue;
                    }

                    if (_geekNavPrefixes.TryGetValue(button, out TextBlock? prefix))
                    {
                        (prefix.Parent as Panel)?.Children.Remove(prefix);
                        _geekNavPrefixes.Remove(button);
                    }
                }

                if (GeekLibStats != null)
                {
                    GeekLibStats.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
                    if (geek)
                    {
                        GeekLibStats.Foreground = phosphor;
                        UpdateGeekLibStats();
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ApplyGeekSidebar", caught);
            }
        }

        /// <summary>数字键直达：按 Tag 找导航按钮（分类导航都走 CategoryNavButton_Click）。</summary>
        private Button? FindNavButtonByTag(string tag)
            => tag switch
            {
                "Songs" => NavSongsButton,
                "Albums" => NavAlbumsButton,
                "Artists" => NavArtistsButton,
                "AlbumArtists" => NavAlbumArtistsButton,
                "Favorites" => NavFavoritesButton,
                "Ratings" => NavRatingsButton,
                "Recent" => NavRecentButton,
                "PlaylistWall" => NavPlaylistWallButton,
                "Genre" => NavGenreButton,
                "Year" => NavYearButton,
                _ => null,
            };

        /// <summary>数字键按下 → 直达对应分类。极客模式专属（由 Root_KeyDown 调用）。</summary>
        internal bool TryHandleGeekNavDigit(VirtualKey key)
        {
            if (!GeekUiStyleCached)
            {
                return false;
            }

            string? digit = key switch
            {
                VirtualKey.Number1 or VirtualKey.NumberPad1 => "1",
                VirtualKey.Number2 or VirtualKey.NumberPad2 => "2",
                VirtualKey.Number3 or VirtualKey.NumberPad3 => "3",
                VirtualKey.Number4 or VirtualKey.NumberPad4 => "4",
                VirtualKey.Number5 or VirtualKey.NumberPad5 => "5",
                VirtualKey.Number6 or VirtualKey.NumberPad6 => "6",
                VirtualKey.Number7 or VirtualKey.NumberPad7 => "7",
                VirtualKey.Number8 or VirtualKey.NumberPad8 => "8",
                VirtualKey.Number9 or VirtualKey.NumberPad9 => "9",
                VirtualKey.Number0 or VirtualKey.NumberPad0 => "0",
                _ => null,
            };

            if (digit == null)
            {
                return false;
            }

            (string, string)? hit = null;
            foreach ((string d, string t) in GeekNavKeys)
            {
                if (d == digit)
                {
                    hit = (d, t);
                    break;
                }
            }

            if (hit == null)
            {
                return false;
            }

            Button? button = FindNavButtonByTag(hit.Value.Item2);
            if (button == null || button.Visibility != Visibility.Visible)
            {
                return false;
            }

            // 与鼠标点击同一入口：Tag 在 sender 上，逻辑全部复用
            CategoryNavButton_Click(button, new RoutedEventArgs());
            return true;
        }

        /// <summary>统计块内容：TRK 曲目 / ALB 专辑 / ART 艺术家，全是索引库真数据。</summary>
        private void UpdateGeekLibStats()
        {
            if (GeekLibStats == null || GeekLibStats.Visibility != Visibility.Visible) return;

            try
            {
                (int tracks, int albums, int artists) = LibraryDb.CountLibraryStats();
                GeekLibStats.Text = tracks < 0
                    ? "TRK --  ALB --  ART --"
                    : $"TRK {tracks}  ALB {albums}  ART {artists}";
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("UpdateGeekLibStats", caught);
            }
        }

        /// <summary>统计不必每秒刷：每 15 秒查一次库（索引变更 / 扫描完成后下一次跳动就会跟上）。</summary>
        private void UpdateGeekLibStatsThrottled()
        {
            if (GeekLibStats == null || GeekLibStats.Visibility != Visibility.Visible) return;

            _geekStatsTickCounter++;
            if (_geekStatsTickCounter >= 15)
            {
                _geekStatsTickCounter = 0;
                UpdateGeekLibStats();
            }
        }

        // ---------- D：顶栏链路读数条 ----------

        private void ApplyGeekChainStrip(bool geek, SolidColorBrush phosphor)
        {
            if (GeekChainStrip == null || GeekChainReadout == null)
            {
                return;
            }

            GeekChainStrip.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
            if (!geek)
            {
                return;
            }

            GeekChainReadout.Foreground = phosphor;
        }

        /// <summary>
        /// 顶栏读数：OUT 输出方式 │ FMT 设备端格式 │ FLAG 是否 bit-perfect │ BUF 缓冲毫秒。
        /// 全是引擎真数据 —— 这是别家播放器没有的东西：把"现在是不是 bit-perfect"直接顶在界面上。
        /// </summary>
        private void UpdateGeekChainStripText()
        {
            if (GeekChainStrip == null || GeekChainStrip.Visibility != Visibility.Visible) return;
            if (GeekChainReadout == null) return;

            try
            {
                var parts = new List<string> { "OUT " + DescribeOutputMode(), "FMT " + DescribeFormat() };

                string flag = DescribeBitPerfectFlag();
                parts.Add("FLAG " + flag);

                int bufferMs = _audioEngine?.OutputBufferMs ?? 0;
                parts.Add("BUF " + (bufferMs > 0 ? bufferMs + "ms" : "--"));

                GeekChainReadout.Text = string.Join("   │   ", parts);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("UpdateGeekChainStripText", caught);
            }
        }

        private string DescribeOutputMode()
        {
            ChainFormatState? chain = _audioEngine?.ChainFormat;
            if (chain != null && chain.IsDsdPath)
            {
                return "DSD-DOP";
            }

            return _audioEngine?.CurrentOutputMode switch
            {
                HiFiOutputBackend.OutputMode.WasapiExclusive => "WASAPI-EXCL",
                HiFiOutputBackend.OutputMode.Asio => "ASIO",
                HiFiOutputBackend.OutputMode.WasapiShared => "WASAPI-SHARED",
                _ => "--",
            };
        }

        private string DescribeFormat()
        {
            string? fmt = _audioEngine?.ActualOutputFormat;
            return string.IsNullOrWhiteSpace(fmt) ? "--" : fmt;
        }

        /// <summary>
        /// bit-perfect 判定：与徽章口径一致 —— 只认真正逐样本处理的模块与共享模式，
        /// 绝不去反解析 ActualOutputFormat 那种给人看的描述串（2026-09-22 教训）。
        /// </summary>
        private string DescribeBitPerfectFlag()
        {
            ChainFormatState? chain = _audioEngine?.ChainFormat;
            if (chain == null || !chain.HasSession)
            {
                return "IDLE";
            }

            // 共享模式：系统混音器必然介入，与格式无关地非 bit-perfect
            if (chain.SharedMode)
            {
                return "SHARED";
            }

            // 变速（atempo）重塑采样值，与 DSP 同级地非 bit-perfect
            if (chain.TempoShifted)
            {
                return "TEMPO";
            }

            bool dsp = chain.DspChainActive
                       || (_audioEngine?.IsSoftwareVolumeActive ?? false);
            // DSP 总旁路（A/B 对比）时全部 DSP 不参与处理，输出恢复 bit-perfect
            if (_audioEngine?.IsBypassAll == true)
            {
                dsp = false;
            }

            if (dsp)
            {
                return "DSP ON";
            }

            if (chain.IsDsdPath)
            {
                return "BIT-PERFECT";
            }

            return chain.Outcome switch
            {
                // 容器扩容（16bit 源 → 24-in-32 端点）数值无损，仍是 bit-perfect
                TranscodeOutcome.SameFormat => "BIT-PERFECT",
                TranscodeOutcome.ContainerWidened => "BIT-PERFECT",
                TranscodeOutcome.ResampledToDevice => "RESAMPLED",
                TranscodeOutcome.Downsampled => "DOWNGRADED",
                TranscodeOutcome.FailedFallback => "FALLBACK",
                _ => "BIT-PERFECT",
            };
        }

        // ---------- A：底部命令行条 ----------

        private void ApplyGeekVolumeBar(bool geek, SolidColorBrush phosphor)
        {
            if (GeekVolumeText == null)
            {
                return;
            }

            GeekVolumeText.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
            if (geek)
            {
                GeekVolumeText.Foreground = phosphor;
            }

            // 字符条的格数按原画布实测宽度算（和原来 140px 音量条同长），画布尺寸一变要重排。
            if (VolumeStyleCanvas != null && geek && !_geekVolumeCanvasSized)
            {
                VolumeStyleCanvas.SizeChanged -= GeekVolumeCanvas_SizeChanged;
                VolumeStyleCanvas.SizeChanged += GeekVolumeCanvas_SizeChanged;
                _geekVolumeCanvasSized = true;
            }

            // 极客下音量条由字符画，原画布的竖线与喇叭图标让位（保留备份，切风格还原）
            if (geek)
            {
                if (VolumeIcon != null)
                {
                    _volumeIconVisibility = VolumeIcon.Visibility;
                    VolumeIcon.Visibility = Visibility.Collapsed;
                }

                // ⚠ 画布**不能收**：音量拖动全部挂在 VolumeStyleCanvas 的三个 Pointer 事件上，
                // 而 Canvas 自身 Background=null 不可命中 —— 之前把画布 Collapsed 掉，
                // 等于把唯一的点击靶撤了，字符条就完全拖不动（用户实测）。
                if (VolumeStyleCanvas != null)
                {
                    _volumeCanvasVisibility = VolumeStyleCanvas.Visibility;
                    VolumeStyleCanvas.Visibility = Visibility.Visible;
                    EnsureVolumeHitTarget();
                }
            }
            else
            {
                if (VolumeIcon != null) VolumeIcon.Visibility = _volumeIconVisibility;
                if (VolumeStyleCanvas != null)
                {
                    VolumeStyleCanvas.Visibility = _volumeCanvasVisibility;
                    // 退出极客时把自绘竖线画回来（极客期间 DrawVolumeStyle 一直只铺透明靶）
                    DrawVolumeStyle();
                }
            }
        }

        /// <summary>
        /// 极客下音量画布不画竖线，但要铺一层**几乎全透明**的矩形当点击靶 ——
        /// Canvas 自身 Background=null 不接受命中，没有这一层就没地方按下去拖。
        /// </summary>
        private bool EnsureVolumeHitTarget()
        {
            if (VolumeStyleCanvas == null)
            {
                return false;
            }

            DrawVolumeStyle();   // 极客分支里会清掉竖线并铺靶

            if (VolumeStyleCanvas.ActualWidth > 1)
            {
                return true;
            }

            // 首次布局时尺寸还没定，等一次 SizeChanged 再补（只等一次，用完即摘）
            void Once(object? s, Microsoft.UI.Xaml.SizeChangedEventArgs e)
            {
                VolumeStyleCanvas.SizeChanged -= Once;
                DrawVolumeStyle();
            }

            VolumeStyleCanvas.SizeChanged += Once;
            return false;
        }

        private void GeekVolumeCanvas_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            UpdateGeekVolumeText();
        }

        private void UpdateGeekVolumeText()
        {
            if (GeekVolumeText == null || GeekVolumeText.Visibility != Visibility.Visible) return;
            if (VolumeSlider == null) return;

            double ratio = Math.Clamp(VolumeSlider.Value / 100.0, 0.0, 1.0);

            // 格数按原音量画布实测宽度算：字符条和原来的竖线条**同长**，
            // 只是样式换了（用户要求：长短一致、行为一致）。
            const string prefix = "VOL ";
            double width = VolumeStyleCanvas?.ActualWidth ?? 0;
            if (width <= 10)
            {
                width = GeekVolumeText.ActualWidth;
            }

            double perChar = Math.Max(1.0, GeekVolumeText.FontSize * 0.55);
            int total = width > 10
                ? (int)Math.Clamp(Math.Round(width / perChar), 8, 60)
                : GeekVolumeBlocks + prefix.Length;
            int blocks = Math.Max(4, total - prefix.Length);

            int filled = (int)Math.Round(ratio * blocks);
            filled = Math.Clamp(filled, 0, blocks);

            SetGeekBarText(GeekVolumeText, prefix, filled, prefix.Length + blocks);
        }

        private void UpdateGeekStatusLamp()
        {
            if (GeekStatusLamp == null || GeekStatusLamp.Visibility != Visibility.Visible) return;

            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                string flag = DescribeBitPerfectFlag();
                bool perfect = flag is "BIT-PERFECT";

                GeekStatusLamp.Text = perfect ? "● BP" : "● " + flag;
                GeekStatusLamp.Foreground = new SolidColorBrush(perfect
                    ? Windows.UI.Color.FromArgb(255, 0x3E, 0xFF, 0x7A)
                    : PhosphorColor(settings.GeekPhosphorColor));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("UpdateGeekStatusLamp", caught);
            }
        }

        /// <summary>播放状态变化时把 ASCII 播放键切成 [&gt;] / [||]（由播放状态同步定时器调用）。</summary>
        internal void SetGeekPlayKeyState(bool playing)
        {
            if (_geekPlayLabel != null)
            {
                _geekPlayLabel.Text = playing ? "||" : ">";
            }
        }

        /// <summary>
        /// 播放顺序切换时同步字符键。极客模式把原图标藏了、盖了字符，而切换顺序的代码
        /// 只改被藏的图标 —— 用户实测"能切换但图标永远不变"。这里让字符跟着顺序走。
        /// </summary>
        internal void SetGeekPlaybackOrderKey(PlaybackOrder order)
        {
            TextBlock? label = PlaybackOrderButton != null
                               && _geekAsciiButtons.TryGetValue(PlaybackOrderButton, out GeekButtonBackup? backup)
                ? backup.Label
                : null;
            if (label == null)
            {
                return;
            }

            label.Text = order switch
            {
                PlaybackOrder.Sequential => "→",
                PlaybackOrder.Random => "~",
                PlaybackOrder.ListLoop => "∞",
                PlaybackOrder.TrackLoop => "1∞",
                PlaybackOrder.TrackOnce => "1×",
                _ => "∞",
            };
        }

        /// <summary>
        /// 喜欢状态变化时同步心形键：♡ 空心 / ♥ 实心红 —— 形状加颜色双重区分，
        /// 和经典模式"实心红心"的语义一致。
        /// </summary>
        internal void SetGeekFavoriteState(bool fav)
        {
            TextBlock? label = FavoriteButton != null
                               && _geekAsciiButtons.TryGetValue(FavoriteButton, out GeekButtonBackup? backup)
                ? backup.Label
                : null;
            if (label == null)
            {
                return;
            }

            label.Text = fav ? "♥" : "♡";
            label.Foreground = fav
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x4D, 0x4D))
                : new SolidColorBrush(PhosphorColor(AppSettingsStore.Load().GeekPhosphorColor));
        }

        // ---------- ASCII 键位：把图标按钮换成字符键 ----------

        private IEnumerable<(Button? Button, string Ascii)> GeekAsciiKeys()
        {
            // 顶栏：功能键用字母（与工具提示一致），系统键用窗口符号的普通字符写法。
            // 一律不套方括号 —— 用户明确要求，加了框看着像标签不像按键。
            yield return (AudioSettingsButton, "A");
            yield return (SelectLocalAudioButton, "M");
            yield return (RefreshCurrentPageButton, "R");
            yield return (FullScreenButton, "F");
            // 最小化用「减号」而不是下划线：下划线压在字底，看着整个键往下掉
            yield return (WindowMinButton, "−");
            yield return (WindowMaxRestoreButton, "□");
            yield return (WindowCloseButton, "×");

            // 底部 transport
            yield return (PlaybackOrderButton, "~");
            yield return (PreviousButton, "|<");
            yield return (SeekBackButton, "<<");
            yield return (PlayPauseButton, ">");
            yield return (SeekForwardButton, ">>");
            yield return (NextButton, ">|");
            // 爱心不能用星号凑数（用户明确反馈）：♡ / ♥ 是真正的心形字符，
            // 等宽字体缺字时会由系统字体补上，按钮里居中显示没问题。
            yield return (FavoriteButton, "♡");
            yield return (ShowCurrentPlaylistButton, "=");
        }

        private void ApplyGeekAsciiKeys(bool geek, FontFamily mono)
        {
            if (geek)
            {
                foreach ((Button? button, string ascii) in GeekAsciiKeys())
                {
                    WrapButtonAscii(button, ascii, mono);
                }

                _geekPlayLabel = PlayPauseButton != null
                                 && _geekAsciiButtons.TryGetValue(PlayPauseButton, out GeekButtonBackup? backup)
                    ? backup.Label
                    : null;
                SetGeekPlayKeyState(IsPlaybackActuallyPlaying());
                return;
            }

            _geekPlayLabel = null;
            RestoreAsciiButtons();
        }

        private void WrapButtonAscii(Button? button, string ascii, FontFamily mono)
        {
            if (button == null)
            {
                return;
            }

            if (_geekAsciiButtons.TryGetValue(button, out GeekButtonBackup? already))
            {
                // 之前包过：再补藏一次图标。启动极早阶段包的按钮视觉树还没长好，
                // 那一次扫描会扑空（图标找不到 → 没藏 → ASCII 字符叠画在原图标上，用户实测的重影就是它）
                HideIconsInTree(button, already);
                return;
            }

            var backup = new GeekButtonBackup { Content = button.Content };

            // 先藏掉所有图标字形：它们在等宽字体里缺字，会渲染成问号框
            HideIconsInTree(button, backup);

            var label = new TextBlock
            {
                Text = ascii,
                FontFamily = mono,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            backup.Label = label;

            if (button.Content is Panel panel)
            {
                // 图标 + 文字并排的按钮（如播放速度 "1x ⌄"）：字符直接追加到同一排
                panel.Children.Add(label);
            }
            else
            {
                // 纯图标按钮：原图标（已隐藏）留在树里，字符盖在上面。
                // 保留原元素很关键 —— 代码里还有地方按名字改它的字形，摘掉会空引用。
                var host = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (button.Content is UIElement old)
                {
                    host.Children.Add(old);
                }

                host.Children.Add(label);
                button.Content = host;
            }

            _geekAsciiButtons[button] = backup;

            // 内容挂好后立刻再扫一次（图标已随原内容搬进宿主）。
            // 但启动极早阶段树还没长好、扫描可能仍扑空 —— 所以再挂一个 Loaded 补藏，
            // 图标真正进入可视树的那一刻一定被藏掉。Loaded 只在首次入树时触发，不会重复刷。
            HideIconsInTree(button, backup);
            GeekButtonBackup captured = backup;
            button.Loaded += (s, e) => HideIconsInTree(button, captured);
        }

        private static void HideIconsInTree(DependencyObject node, GeekButtonBackup backup)
        {
            if (node is FontIcon icon)
            {
                backup.Icons[icon] = icon.Visibility;
                icon.Visibility = Visibility.Collapsed;
            }
            else if (node is SymbolIcon symbol)
            {
                backup.Icons[symbol] = symbol.Visibility;
                symbol.Visibility = Visibility.Collapsed;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                HideIconsInTree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), backup);
            }
        }

        private void RestoreAsciiButtons()
        {
            foreach (KeyValuePair<Button, GeekButtonBackup> pair in _geekAsciiButtons)
            {
                Button button = pair.Key;
                GeekButtonBackup backup = pair.Value;

                if (backup.Label != null && button.Content is Panel panel)
                {
                    panel.Children.Remove(backup.Label);
                }

                foreach (KeyValuePair<UIElement, Visibility> iconState in backup.Icons)
                {
                    iconState.Key.Visibility = iconState.Value;
                }

                // 纯图标按钮：把原始内容放回（字符宿主随之丢弃）
                if (button.Content is not Panel)
                {
                    button.Content = backup.Content;
                }
            }

            _geekAsciiButtons.Clear();
        }
    }
}
