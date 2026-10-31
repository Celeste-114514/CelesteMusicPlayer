using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音效处理页面：左侧「处理链路」模块导航 + 右侧模块内容分区。
    /// 2026-09-29 起对齐 ECHO DSP 模块集：左导航按信号链分 6 组
    /// （输入 / 采样率 / 塑形 / 声道 / 空间 / 输出安全），导航项由本文件的元数据表
    /// 动态生成——新增模块只需在 <see cref="BuildDspNav"/> 里加一行，不再手写 XAML。
    /// 置顶两项（听音方案 / DSP 机架编排）不挂组标题，排在分组之上。
    /// 本文件只做页面切换与状态指示，不改动任何 DSP 处理逻辑。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 页面索引。早期写成硬编码常量（Safety=7 / Fir=6 / Channel=5 / Eq=3），
        // 在导航中间插入新模块后必然错位；改为按 Page 引用从已构建的元数据表反查，
        // 增删页面/调整顺序都不需要再同步这里（导航未构建时返回 -1，调用处均能容忍）。
        private int DspPageSafetyIndex => FindDspPageIndex(DspPageSafety);
        // ReplayGain 页（2026-10-12 阶段 2：批量扫描并入本页，需要一个能跳转过去的索引）
        private int DspPageRgIndex => FindDspPageIndex(DspPageRg);
        private int DspPageFirIndex => FindDspPageIndex(DspPageFir);
        private int DspPageChannelIndex => FindDspPageIndex(DspPageChannel);
        private int DspPageEqIndex => FindDspPageIndex(DspPageEq);
        private int DspPageCompressorIndex => FindDspPageIndex(DspPageCompressor);
        private int DspPageSrcIndex => FindDspPageIndex(DspPageSrc);
        private int DspPageMatrixIndex => FindDspPageIndex(DspPageMatrix);
        private int DspPageFieldIndex => FindDspPageIndex(DspPageStereoField);
        private int DspPageRackIndex => FindDspPageIndex(DspPageRack);
        private int DspPageProfilesIndex => FindDspPageIndex(DspPageProfiles);

        // DspModuleActive() 返回数组的具名下标，顺序与 BuildDspNav 元数据表一致。
        // 「链路条 / 活跃 DSP 清单」曾按 names[i] ↔ active[i] 直接对下标取用，机架编排置顶后就整体错位
        // （余量被读成机架编排的恒 false）。改成具名下标显式对应，之后再插页面不会再牵连这两处读数。
        // ⚠ 2026-10-01：Crossfeed 从独立页并回「声道工具」页；同日耳机校正并入 EQ 页，
        // 导航 13 → 12 项，下面整体再前移一位。开关控件都还在，只是不再单独占一页。

        /// <summary>在导航表里查某个页面当前的下标；未构建 / 页面为空时返回 -1。</summary>
        private int FindDspPageIndex(StackPanel? page)
        {
            if (page == null)
            {
                return -1;
            }

            for (int i = 0; i < _dspNavEntries.Count; i++)
            {
                if (ReferenceEquals(_dspNavEntries[i].Page, page))
                {
                    return i;
                }
            }

            return -1;
        }

        private int _dspPageIndex = -1;
        private DispatcherQueueTimer? _dspMonitorTimer;


        /// <summary>导航项运行时态：标题 / 所属组 / 右侧页面 / 徽章控件。</summary>
        private sealed class DspNavEntry
        {
            public required string Title { get; init; }
            public required string Group { get; init; }
            public StackPanel? Page { get; init; }
            public Border? Badge { get; init; }
            public TextBlock? BadgeText { get; init; }
            public Button? Button { get; set; }
            public Border? Bar { get; set; }
            public TextBlock? Label { get; set; }
            public Border? Dot { get; set; }
        }

        private readonly List<DspNavEntry> _dspNavEntries = new();
        private bool _dspNavBuilt;

        // ---------- 导航构建（元数据表驱动） ----------

        /// <summary>
        /// 按元数据表生成左侧导航：先插组标题，再插该组的模块项。
        /// 组的划分对齐 ECHO：输入 / 采样率 / 塑形 / 声道 / 空间 / 输出安全。
        /// </summary>
        private void BuildDspNav()
        {
            if (_dspNavBuilt)
            {
                return;
            }

            _dspNavEntries.Clear();
            DspNavPanel.Children.Clear();

            // ── 元数据表：加模块只需在这里加一行（标题 / 组 / 页面 / 徽章）──
            // ⚠ 顺序同时决定另外两处的下标，新增/调整顺序时必须同步：
            //    ① DspModuleActive() 的返回数组  ② UpdateDspHeroBadges() 的 badges/texts 数组
            //    ③ 不依赖下标：页面索引常量已改为按 Page 引用反查，无需同步
            // 徽章列：2026-09-30 起 10 个可开关模块的页头徽章已换成「电源开关」（见 MainWindow.DspPower.cs），
            // 这里传 null；只有听音方案 / 机架编排 / 输出安全三个非开关页保留信息徽章。
            // 「听音方案」置顶（2026-09-30 用户拍板）：整套 DSP 快照的保存/切换是进门第一件事，
            // 与「DSP 机架编排」一样不挂组标题，排在「输入」组上方。
            AddNavEntry("听音方案", "", DspPageProfiles, DspBadgeProfiles, DspBadgeProfilesText);
            // 「DSP 机架编排」次顶且不挂组标题（2026-09-30 用户拍板）：它是总纲性页面，放在「输入」组上方。
            AddNavEntry("DSP 机架编排", "", DspPageRack, DspBadgeRack, DspBadgeRackText);
            AddNavEntry("输入余量", "输入", DspPageHeadroom, null, null);
            // 「响度 · ReplayGain」为过渡项：阶段 2 起 RG 并入「DSP Rack 编排」页行内展开，本项随之移除
            AddNavEntry("响度 · ReplayGain", "输入", DspPageRg, null, null);
            AddNavEntry("SRC / 升频", "采样率", DspPageSrc, null, null);
            AddNavEntry("参数 EQ", "塑形", DspPageEq, null, null);
            // 耳机校正 2026-10-01 并入 EQ 页（品牌墙 / 搜索 / 曲线列表 / 预览 / 应用都在该页下半部分），
            // 不再单列一项；进入 EQ 页时会一并初始化（见 SelectDspPage）
            AddNavEntry("动态压缩器", "塑形", DspPageCompressor, null, null);
            // Crossfeed 2026-10-01 并回「声道工具」页（不再单列一项）
            AddNavEntry("声道工具", "声道", DspPageChannel, null, null);
            AddNavEntry("立体声场", "声道", DspPageStereoField, null, null);
            AddNavEntry("声道矩阵", "声道", DspPageMatrix, null, null);
            AddNavEntry("FIR / 房间校正", "空间", DspPageFir, null, null);
            AddNavEntry("输出安全", "输出安全", DspPageSafety, DspBadgeSafety, DspBadgeSafetyText);

            string? currentGroup = null;
            for (int navIndex = 0; navIndex < _dspNavEntries.Count; navIndex++)
            {
                DspNavEntry entry = _dspNavEntries[navIndex];
                // 空组名 = 置顶项（如「DSP 机架编排」），不渲染组标题
                if (!string.IsNullOrEmpty(entry.Group) && entry.Group != currentGroup)
                {
                    currentGroup = entry.Group;
                    TextBlock header = new()
                    {
                        Text = currentGroup,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.55,
                        Margin = new Thickness(8, 10, 0, 2)
                    };
                    DspNavPanel.Children.Add(header);
                }

                DspNavPanel.Children.Add(BuildNavItemGrid(entry, navIndex));
            }

            _dspNavBuilt = true;

            // 自检日志：动态导航静默退化时（条目漏建/组标题漏插）只有这行能立刻定位
            int groupCount = 0;
            string? lastGroup = null;
            foreach (DspNavEntry e in _dspNavEntries)
            {
                if (e.Group != lastGroup)
                {
                    lastGroup = e.Group;
                    groupCount++;
                }
            }
            int wiredButtons = 0;
            int wiredBars = 0;
            int wiredBadges = 0;
            foreach (DspNavEntry e in _dspNavEntries)
            {
                if (e.Button != null)
                {
                    wiredButtons++;
                }
                if (e.Bar != null)
                {
                    wiredBars++;
                }
                if (e.Badge != null && e.BadgeText != null)
                {
                    wiredBadges++;
                }
            }
            StartupLog.Write($"[DSP导航] 条目={_dspNavEntries.Count} 组={groupCount} 按钮={wiredButtons} 强调条={wiredBars} 徽章={wiredBadges}");
        }

        private void AddNavEntry(string title, string group, StackPanel? page, Border? badge, TextBlock? badgeText)
        {
            _dspNavEntries.Add(new DspNavEntry
            {
                Title = title,
                Group = group,
                Page = page,
                Badge = badge,
                BadgeText = badgeText
            });
        }

        /// <summary>生成单个导航项：Grid(38px) 内 Button（圆点+标题），左侧叠强调条。</summary>
        private Grid BuildNavItemGrid(DspNavEntry entry, int index)
        {

            Border dot = new()
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(255, 0xB4, 0xB2, 0xA9))
            };
            TextBlock label = new()
            {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Text = entry.Title
            };
            Grid inner = new() { ColumnSpacing = 8 };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dot.SetValue(Grid.ColumnProperty, 0);
            label.SetValue(Grid.ColumnProperty, 1);
            inner.Children.Add(dot);
            inner.Children.Add(label);

            Button button = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(10, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Tag = index,
                Content = inner
            };
            button.Click += DspNavButton_Click;

            Border bar = new()
            {
                Width = 3,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(1.5),
                Visibility = Visibility.Collapsed
            };
            // 选中条的颜色原来直接读全局 AccentFillColorDefaultBrush —— 极客模式下那仍是蓝的
            // （极客不改全局资源），得改走「面板强调色」入口：极客返回磷光，否则回落系统主题色。
            bar.Background = DspAccentBrush();

            Grid grid = new() { Height = 38 };
            grid.Children.Add(button);
            grid.Children.Add(bar);

            entry.Button = button;
            entry.Bar = bar;
            entry.Label = label;
            entry.Dot = dot;
            return grid;
        }

        // ---------- 页面切换 ----------

        private void DspNavButton_Click(dynamic sender, dynamic e)
        {
            if (sender is Button b && b.Tag is int idx)
            {
                SelectDspPage(idx);
            }
        }

        /// <summary>切换到指定模块页面（下标对应 <see cref="BuildDspNav"/> 元数据表的行号）。</summary>
        private void SelectDspPage(int idx)
        {
            if (idx < 0 || idx >= _dspNavEntries.Count)
            {
                return;
            }

            _dspPageIndex = idx;

            for (int i = 0; i < _dspNavEntries.Count; i++)
            {
                DspNavEntry entry = _dspNavEntries[i];
                bool on = i == idx;
                if (entry.Page != null)
                {
                    entry.Page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }

                if (entry.Bar != null)
                {
                    entry.Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }

                if (entry.Label != null)
                {
                    entry.Label.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                    entry.Label.Opacity = on ? 1.0 : 0.72;
                }
            }

            // 只有在「输出安全监控」页才跑 500ms 刷新定时器，其它页面不占 UI 线程
            EnsureDspMonitorTimer(idx == DspPageSafetyIndex);
            if (idx == DspPageSafetyIndex)
            {
                UpdateDspOutputMonitor();
                UpdateDspSafetyChain();
            }

            if (idx == DspPageFirIndex)
            {
                // 房间校正的 IR 导入已内嵌到本页（2026-10-12 阶段 2）：切进来把已保存的设置填进去
                LoadRoomCorrectionIntoPanel();
                RefreshRoomCorrectionInfo();
            }

            if (idx == DspPageChannelIndex)
            {
                UpdateDspChannelBars();
                // Crossfeed 已并回本页：它的侧声道频响曲线同样折叠时没有布局尺寸，切进来补画
                RedrawDspXfeedCanvas();
            }

            // EQ 页：只有本页可见时才跑 ~15fps 频谱定时器（曲线背景用），离开即停
            if (idx == DspPageEqIndex)
            {
                // 耳机校正（2026-10-01 并入本页）：第一次进入加载 OPRA 数据库（之后复用内存态）；
                // 历史列表与曲线预览都可能被别处改动过，切进来刷新一次
                EnsureOpraLoaded();
                RefreshOpraRecentList();
                RedrawOpraCanvas();
            }

            // 压缩器页：只有本页可见时轮询增益衰减读数（200ms），离开即停
            EnsureDspCompGrTimer(idx == DspPageCompressorIndex);
            if (idx == DspPageCompressorIndex)
            {
                // 画布折叠时 ActualWidth=0、画不出来，切回来立刻补一次（SizeChanged 也会补一路）
                RedrawDspCompCanvas();
                DspCompGr_Tick(this, EventArgs.Empty);
            }

            // 声道矩阵页：信号流图同理，折叠时没有布局尺寸，切进来补画
            if (idx == DspPageMatrixIndex)
            {
                RedrawDspMatrixCanvas();
            }

            // 立体声场页：M/S 圆环同理
            if (idx == DspPageFieldIndex)
            {
                RedrawDspFieldCanvas();
            }

            // 机架编排页：进入时刷新列表（顺序可能被别处改动过）
            if (idx == DspPageRackIndex)
            {
                RefreshDspRackList(DspRackStore.Load().RackOrder);
            }

            // SRC 页：链路图/示意曲线折叠时没有布局尺寸，切进来补一次
            if (idx == DspPageSrcIndex)
            {
                RedrawSrcCanvas();
            }

            // 听音方案页：进入时重算列表与「当前设置属于哪份方案」（刚在别处改过设置的判定在这落地）
            if (idx == DspPageProfilesIndex)
            {
                RefreshDspProfileUi();
            }
        }

        /// <summary>首次进入音效页面时构建导航、定位到第一个模块，并刷新各模块启用指示。</summary>
        private void InitDspNav()
        {
            BuildDspNav();
            InitRoomCorrectionTrimUi();
            LoadDspRackUi();
            _dspRackReady = true;

            // 页头电源开关表也要在这一刻建立（x:Name 字段已连上），随后统一刷指示。
            // _dspPowerReady 在首次同步之后才置位——同步过程回写 IsOn 会就地触发 Toggled，
            // 未就绪时 handler 一律忽略（XAML 解析期秒崩铁律的同款守卫）。
            InitDspPowerEntries();

            // 先备好方案页状态（徽章/圆点要用协调器里的生效方案名），再统一刷指示
            RefreshDspProfileUi();
            UpdateDspNavIndicators();
            _dspPowerReady = true;
            if (_dspPageIndex < 0)
            {
                SelectDspPage(0);
            }
        }

        // ---------- 模块启用指示（左侧小圆点）----------



        // ---------- 模块页 Hero 状态徽章 ----------

        /// <summary>刷新每个模块页右上角的徽章：绿 = 正在生效，灰 = 未启用，琥珀 = 已旁路。
        /// 数组顺序必须与 BuildDspNav 元数据表、DspModuleActive() 保持一致。
        /// 2026-09-30 起 10 个可开关模块的徽章已换成页头电源开关（MainWindow.DspPower.cs），
        /// 对应位置填 null，循环里跳过；下标保持不变以维持与 active[] 的对应关系。</summary>
        private void UpdateDspHeroBadges()
        {
            Border?[] badges =
            {
                DspBadgeProfiles, DspBadgeRack, null, null, null, null,
                null, null, null, null, null,
                DspBadgeSafety
            };
            TextBlock?[] texts =
            {
                DspBadgeProfilesText, DspBadgeRackText, null, null, null, null,
                null, null, null, null, null,
                DspBadgeSafetyText
            };

            int safetyIndex = DspPageSafetyIndex;
            bool[] active = DspModuleActive();
            bool bypass = RackParameters.Bypassed;

            // 末位是监控页，恒亮（它显示的是状态而不是处理模块）
            for (int i = 0; i < badges.Length; i++)
            {
                Border? badge = badges[i];
                TextBlock? text = texts[i];
                if (badge == null || text == null)
                {
                    continue;
                }

                // 方案页显示的是"当前设置来自哪份方案"，不是开关状态：不参与"生效/旁路"口径。
                // 按控件本身认，不看下标 —— 导航未构建时下标是 -1，这里也不会误走通用分支。
                if (ReferenceEquals(badge, DspBadgeProfiles))
                {
                    ApplyDspProfileBadge();
                    continue;
                }

                bool on = i == safetyIndex || i == DspPageRackIndex || active[i];
                if (bypass && i != safetyIndex)
                {
                    SetDspBadgeState(badge, text, "bypassed", "已旁路");
                }
                else if (on)
                {
                    SetDspBadgeState(badge, text, "on",
                        i == safetyIndex ? "监控中"
                        : i == DspPageRackIndex ? (DspRackOrderIsDefault() ? "默认顺序" : "自定义")
                        : "生效中");
                }
                else
                {
                    SetDspBadgeState(badge, text, "off", "未启用");
                }
            }
        }

        // ---------- 余量快捷预设 ----------

        private void DspHeadroomPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || !double.TryParse(tag, out double db))
            {
                return;
            }

            if (AudioFxSafetyHeadroomSlider == null)
            {
                return;
            }

            AudioFxSafetyHeadroomSlider.Value = Math.Clamp(db, AudioFxSafetyHeadroomSlider.Minimum, AudioFxSafetyHeadroomSlider.Maximum);
            ApplyDspToEngine();
            UpdateDspNavIndicators();
        }

        // ---------- FIR 快捷 trim / 安全启用 ----------

        private void DspFirTrimQuick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || !double.TryParse(tag, out double db))
            {
                return;
            }

            if (SetModelControl("RoomTrimSlider", NumberMessage(db)))
            {
                ProjectExtraParameters();
                ApplyFirParameters();
            }
        }

        /// <summary>先把 trim 降到 -6 dB 再启用卷积，避免一开就爆音（对齐 ECHO enableFirSafely）。</summary>
        private void DspFirSafeEnable_Click(object sender, RoutedEventArgs e)
        {
            FirSafeEnableCore();
        }

        /// <summary>安全启用卷积核心（网页「音效处理」页直接调）。</summary>
        internal void FirSafeEnableCore()
        {
            // IrReady = 已选 IR 且文件还在盘上（原来只看路径非空，文件被删时也会照样开起来）
            if (!FirParameters.IrReady)
            {
                if (RoomClipRiskText != null)
                {
                }

                return;
            }

            double trim = Math.Min(Math.Round(FirParameters.Current.TrimDb, 1), -6.0);
            SetModelControl("RoomTrimSlider", NumberMessage(trim));
            SetModelControl("RoomIrEnabledToggle", SwitchMessage(true));
            ProjectExtraParameters();
            ApplyFirParameters();

            RefreshRoomCorrectionInfo();
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
        }

        // ---------- 声道左右偏差可视化 ----------

        /// <summary>用两条进度条显示左右输出电平差：越长代表该侧越响，居中时两条等长。</summary>
        private void UpdateDspChannelBars()
        {
            if (DspChannelLeftBar == null || DspChannelRightBar == null || DspChannelSkewText == null)
            {
                return;
            }

            double balance = AudioFxChannelBalanceSlider?.Value ?? 0;
            double leftGain = AudioFxChannelLeftGainSlider?.Value ?? 0;
            double rightGain = AudioFxChannelRightGainSlider?.Value ?? 0;

            // 平衡 -1..1 折算成最多 ±6 dB 的等效偏差，再加上两侧手动增益差
            double skewDb = (rightGain - leftGain) + (balance * 6.0);
            double leftPct = Math.Clamp(50 - (skewDb * 4.0), 8, 92);
            double rightPct = Math.Clamp(50 + (skewDb * 4.0), 8, 92);


            double abs = Math.Abs(skewDb);
        }

        // ---------- 输出链路条 ----------

        /// <summary>刷新「输出 · 安全监控」页的链路条：输入 → 余量 → 处理 → 输出。</summary>
        private void UpdateDspSafetyChain()
        {
            if (DspChainInputText == null)
            {
                return;
            }

            bool bypass = RackParameters.Bypassed;

            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;

            double headroom = AudioFxSafetyHeadroomSlider?.Value ?? 0;

            // 顺序按信号链：余量 / RG / SRC / EQ / 耳机校正 / 声道工具（含 Crossfeed）/ FIR
            // 下标用 DspActive* 具名常量显式对应 —— 曾经的 names[i] ↔ active[i] 在页面增删后会整体错位
            bool[] active = DspModuleActive();
            var on = new List<string>();
            if (DspActiveHeadroom < active.Length && active[DspActiveHeadroom])
            {
                on.Add("余量");
            }

            if (DspActiveRg < active.Length && active[DspActiveRg])
            {
                on.Add("ReplayGain");
            }

            if (DspActiveSrc < active.Length && active[DspActiveSrc])
            {
                on.Add("SRC");
            }

            if (DspActiveEq < active.Length && active[DspActiveEq])
            {
                on.Add("EQ");
            }

            // 耳机校正 2026-10-01 并入 EQ 页，不再占导航下标：按当前 EQ 曲线直读（与下面 Crossfeed 同处理）
            if (_opraApplied)
            {
                on.Add("耳机校正");
            }

            if (DspActiveChannel < active.Length && active[DspActiveChannel])
            {
                on.Add("声道工具");
            }

            // Crossfeed 2026-10-01 并回声道工具页后不再单独占一个下标，
            // 这里按控件直读，好在链路条上仍然看得见它有没有真的在跑
            if (AudioFxChannelCrossfeedToggle?.IsOn == true && (AudioFxChannelCrossfeedSlider?.Value ?? 0) > 0.1)
            {
                on.Add("Crossfeed");
            }

            if (DspActiveFir < active.Length && active[DspActiveFir])
            {
                on.Add("FIR");
            }


            int clip = _audioEngine?.OutputClipCount ?? 0;
            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
        }

        // ---------- 房间校正（FIR）：IR 信息 / 微调余量 trim / 削波风险 ----------

        private void RoomTrimSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || !_audioFxPanelReady)
            {
                return;
            }

            // trim 是「实时生效」的那一个：拖动即进模型并落盘下发（与旧行为一致）
            ImportExtraParameterInput(sender);
            UpdateRoomTrimLabel();
            ApplyFirParameters();
            UpdateDspBitPerfectUi();
        }

        /// <summary>从模型初始化 trim 滑杆（只在加载面板时调用一次，避免打断拖动）。</summary>
        private void InitRoomCorrectionTrimUi()
        {
            // 面板初始化这一步就是模型的首次装载点（进入 FIR 页时 LoadRoomCorrectionIntoPanel 会再装一次，幂等）
            FirParameters.Load(RoomCorrectionStore.Load());

            if (RoomTrimSlider != null)
            {
            }

            UpdateRoomTrimLabel();
            RefreshRoomCorrectionInfo();
        }

        // ---------- 输出安全监控 ----------

        private void OutMonitorReset_Click(object sender, RoutedEventArgs e)
        {
            ResetOutputMonitorCore();
        }

        /// <summary>重置输出统计核心（网页「音效处理」页直接调）。</summary>
        internal void ResetOutputMonitorCore()
        {
            _audioEngine?.ResetOutputStats();
            UpdateDspOutputMonitor();
        }

        /// <summary>刷新「输出 · 安全监控」页的数值。数据全部来自引擎侧真实统计。</summary>
        private void UpdateDspOutputMonitor()
        {
            if (OutMonitorPeakText == null)
            {
                return;
            }

            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
            int clip = _audioEngine?.OutputClipCount ?? 0;


            // 活跃 DSP 清单：按信号链顺序列出正在参与处理的模块（具名下标，见 DspActive* 常量）
            string[] names = { "余量/限幅", "ReplayGain", "SRC 升频", "参数 EQ", "声道工具", "FIR 卷积" };
            int[] slots =
            {
                DspActiveHeadroom, DspActiveRg, DspActiveSrc, DspActiveEq,
                DspActiveChannel, DspActiveFir
            };
            bool[] active = DspModuleActive();
            var on = new List<string>();
            for (int i = 0; i < names.Length && i < slots.Length && slots[i] < active.Length; i++)
            {
                if (active[slots[i]])
                {
                    on.Add(names[i]);
                }
            }

            // 耳机校正曲线寄存在 EQ 里（2026-10-01 并入 EQ 页）：按曲线直读，插在「参数 EQ」之后保持链序
            if (_opraApplied)
            {
                int eqAt = on.IndexOf("参数 EQ");
                on.Insert(eqAt >= 0 ? eqAt + 1 : on.Count, "耳机校正");
            }

            bool bypass = RackParameters.Bypassed;

            // 采样率链路：沿用 SRC 会话状态文本（未升频时显示直出）
            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;

            // 链路条与上面的监控数值同源，一起刷新
            UpdateDspSafetyChain();
        }

        /// <summary>仅监控页可见时运行的 500ms 刷新定时器。</summary>
        private void EnsureDspMonitorTimer(bool enable)
        {
            if (_dspMonitorTimer == null)
            {
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    return;
                }

                _dspMonitorTimer = queue.CreateTimer();
                _dspMonitorTimer.Interval = TimeSpan.FromMilliseconds(500);
                _dspMonitorTimer.Tick += (s, a) => UpdateDspOutputMonitor();
            }

            if (enable)
            {
                _dspMonitorTimer.Start();
            }
            else
            {
                _dspMonitorTimer.Stop();
            }
        }
    }
}
