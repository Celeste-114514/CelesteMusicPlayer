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
        private int DspPageFirIndex => FindDspPageIndex(DspPageFir);
        private int DspPageChannelIndex => FindDspPageIndex(DspPageChannel);
        private int DspPageEqIndex => FindDspPageIndex(DspPageEq);
        private int DspPageCompressorIndex => FindDspPageIndex(DspPageCompressor);
        private int DspPageMatrixIndex => FindDspPageIndex(DspPageMatrix);
        private int DspPageFieldIndex => FindDspPageIndex(DspPageStereoField);
        private int DspPageRackIndex => FindDspPageIndex(DspPageRack);
        private int DspPageOpraIndex => FindDspPageIndex(DspPageOpra);
        private int DspPageProfilesIndex => FindDspPageIndex(DspPageProfiles);

        // DspModuleActive() 返回数组的具名下标，顺序与 BuildDspNav 元数据表一致。
        // 「链路条 / 活跃 DSP 清单」曾按 names[i] ↔ active[i] 直接对下标取用，机架编排置顶后就整体错位
        // （余量被读成机架编排的恒 false）。改成具名下标显式对应，之后再插页面不会再牵连这两处读数。
        private const int DspActiveProfiles = 0;
        private const int DspActiveRack = 1;
        private const int DspActiveHeadroom = 2;
        private const int DspActiveRg = 3;
        private const int DspActiveSrc = 4;
        private const int DspActiveEq = 5;
        private const int DspActiveOpra = 6;
        private const int DspActiveComp = 7;
        private const int DspActiveChannel = 8;
        private const int DspActiveField = 9;
        private const int DspActiveMatrix = 10;
        private const int DspActiveFir = 11;
        private const int DspActiveSafety = 12;

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

        /// <summary>耳机校正（OPRA）曲线是否已应用到当前 EQ。</summary>
        private bool _opraApplied;

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
            AddNavEntry("耳机校正", "塑形", DspPageOpra, null, null);
            AddNavEntry("动态压缩器", "塑形", DspPageCompressor, null, null);
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

        private void DspNavButton_Click(object sender, RoutedEventArgs e)
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
                RefreshRoomCorrectionInfo();
            }

            if (idx == DspPageChannelIndex)
            {
                UpdateDspChannelBars();
            }

            // EQ 页：只有本页可见时才跑 ~15fps 频谱定时器（曲线背景用），离开即停
            EnsureEqSpectrumTimer(idx == DspPageEqIndex);
            if (idx == DspPageEqIndex)
            {
                RedrawAudioFxEqCurve();
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

            // 机架编排页：进入时刷新列表（顺序可能被别处改动过）
            if (idx == DspPageRackIndex)
            {
                RefreshDspRackList(DspRackStore.Load().RackOrder);
            }

            // 耳机校正页：第一次进入时加载 OPRA 数据库（之后复用内存态）
            if (idx == DspPageOpraIndex)
            {
                EnsureOpraLoaded();
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

            // 先备好方案页状态（徽章/圆点要用 _dspProfileActiveName），再统一刷指示
            RefreshDspProfileUi();
            UpdateDspNavIndicators();
            _dspPowerReady = true;
            if (_dspPageIndex < 0)
            {
                SelectDspPage(0);
            }
        }

        // ---------- 模块启用指示（左侧小圆点）----------

        /// <summary>各模块当前是否参与处理（索引与导航顺序一致；末位为监控页恒亮）。</summary>
        private bool[] DspModuleActive()
        {
            // 圆点语义 = "正在参与处理"。余量(负增益)才是安全模块真正逐样本处理的情形；
            // 限幅开关单独开着只待命（源 PCM 不超 ±1 时无需削波），不能点亮圆点
            // （2026-09-22 用户实测"限幅没开却显示开了"的同类口径问题，已对齐）。
            bool headroom = AudioFxSafetyHeadroomSlider != null && Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01;

            int srcHz = 0;
            if (SrcRateCombo != null && SrcRateCombo.SelectedIndex >= 0 && SrcRateCombo.SelectedIndex < SrcRateOptions.Length)
            {
                srcHz = SrcRateOptions[SrcRateCombo.SelectedIndex].Hz;
            }

            bool eq = _audioFxEq != null && _audioFxEq.Enabled && _audioFxEq.HasEffect();
            bool fir = RoomCorrectionStore.Load().Enabled;
            bool ch = AudioFxChannelToggle != null && AudioFxChannelToggle.IsOn;
            bool rg = ReplayGainStore.Load().Mode != ReplayGainMode.Off;

            // 顺序与 BuildDspNav 元数据表一致（具名下标见 DspActive* 常量）：
            // 听音方案 / 机架编排 / 余量 / RG / SRC / EQ / OPRA / 压缩 / 声道 / 声场 / 矩阵 / FIR / 监控
            // 方案页圆点 = 当前设置属于某份已保存方案（是"当前状态"，不是处理模块，故跟着方案走）；
            // 机架编排恒 false：编排顺序不是"效果开关"，改顺序不产生处理，点不亮圆点
            bool comp = DspCompActive();
            bool field = DspFieldActive();
            bool matrix = DspMatrixActive();
            return new[]
            {
                !string.IsNullOrEmpty(_dspProfileActiveName), false, headroom, rg, srcHz > 0, eq, _opraApplied,
                comp, ch, field, matrix, fir, true
            };
        }

        /// <summary>刷新左侧导航圆点：绿 = 该模块正在参与处理，灰 = 未启用。</summary>
        private void UpdateDspNavIndicators()
        {
            // 先判一次"当前设置是否已脱离生效方案"：圆点/徽章都要用这个最终状态
            EnsureDspProfileFresh();

            bool[] active = DspModuleActive();
            for (int i = 0; i < _dspNavEntries.Count && i < active.Length; i++)
            {
                Border? dot = _dspNavEntries[i].Dot;
                if (dot == null)
                {
                    continue;
                }

                // 极客下「启用」圆点走磷光色；熄灭色也换一套（原来的 #B4B2A9 在极客深底上偏亮、太扎眼）
                dot.Background = new SolidColorBrush(active[i]
                    ? (GeekDspAccentColor() ?? Color.FromArgb(255, 0x3B, 0x6D, 0x11))
                    : (_geekDspActive ? Color.FromArgb(255, 0x4A, 0x4A, 0x42) : Color.FromArgb(255, 0xB4, 0xB2, 0xA9)));
            }

            if (OutMonitorActiveDspText != null)
            {
                UpdateDspOutputMonitor();
            }

            // 机架编排页的行状态与导航圆点同源，一起刷（页面不可见时刷新无副作用，控件判空即可）
            RefreshDspRackRows();

            UpdateDspHeroBadges();
            // 页头电源开关与徽章/圆点同源同步（10 个可开关模块；同步过程有 _dspPowerSyncing 守卫）
            SyncDspPowerSwitches();
            UpdateDspChannelBars();
        }

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
                null, null, null, null,
                null, null, DspBadgeSafety
            };
            TextBlock?[] texts =
            {
                DspBadgeProfilesText, DspBadgeRackText, null, null, null, null,
                null, null, null, null,
                null, null, DspBadgeSafetyText
            };

            int safetyIndex = DspPageSafetyIndex;
            bool[] active = DspModuleActive();
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

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

            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = Math.Clamp(db, RoomTrimSlider.Minimum, RoomTrimSlider.Maximum);
            }
        }

        /// <summary>先把 trim 降到 -6 dB 再启用卷积，避免一开就爆音（对齐 ECHO enableFirSafely）。</summary>
        private void DspFirSafeEnable_Click(object sender, RoutedEventArgs e)
        {
            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (string.IsNullOrWhiteSpace(st.IrPath))
            {
                if (RoomClipRiskText != null)
                {
                    RoomClipRiskText.Text = "还没导入 IR：先点上方「打开房间校正」导入脉冲响应 WAV，再启用卷积。";
                }

                return;
            }

            st.Enabled = true;
            st.TrimDb = Math.Min(Math.Round(st.TrimDb, 1), -6.0);
            RoomCorrectionStore.Save(st);
            _audioEngine?.SetRoomCorrection(st);

            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = st.TrimDb;
            }

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

            DspChannelLeftBar.Value = leftPct;
            DspChannelRightBar.Value = rightPct;

            double abs = Math.Abs(skewDb);
            DspChannelSkewText.Text = abs < 0.05
                ? "居中"
                : (skewDb > 0 ? "偏右 " : "偏左 ") + abs.ToString("0.0") + " dB";
        }

        // ---------- 输出链路条 ----------

        /// <summary>刷新「输出 · 安全监控」页的链路条：输入 → 余量 → 处理 → 输出。</summary>
        private void UpdateDspSafetyChain()
        {
            if (DspChainInputText == null)
            {
                return;
            }

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;
            DspChainInputText.Text = (!string.IsNullOrWhiteSpace(src) && src != "—")
                ? src
                : "原始采样率直出";

            double headroom = AudioFxSafetyHeadroomSlider?.Value ?? 0;
            DspChainHeadroomText.Text = FormatHelper.FormatAudioFxDb(headroom) + " dB";

            // 顺序按信号链：余量 / RG / SRC / EQ / 耳机校正 / 声道 / FIR
            // 下标用 DspActive* 具名常量显式对应 —— 曾经的 names[i] ↔ active[i] 在页面增删后会整体错位
            string[] names = { "余量", "ReplayGain", "SRC", "EQ", "耳机校正", "声道工具", "FIR" };
            int[] slots =
            {
                DspActiveHeadroom, DspActiveRg, DspActiveSrc, DspActiveEq,
                DspActiveOpra, DspActiveChannel, DspActiveFir
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

            DspChainProcessText.Text = bypass ? "全部旁路" : (on.Count == 0 ? "无（直通）" : string.Join(" → ", on));

            int clip = _audioEngine?.OutputClipCount ?? 0;
            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
            DspChainOutputText.Text = clip > 0
                ? "已削波"
                : (peak > -0.5f ? "接近满刻度" : "正常");
        }

        // ---------- 房间校正（FIR）：IR 信息 / 微调余量 trim / 削波风险 ----------

        private void RoomTrimSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (RoomTrimLabel != null)
            {
                RoomTrimLabel.Text = "微调余量 (trim)：" + FormatHelper.FormatAudioFxDb(e.NewValue) + " dB";
            }

            if (_audioFxLoading || !_audioFxPanelReady)
            {
                return;
            }

            RoomCorrectionState st = RoomCorrectionStore.Load();
            st.TrimDb = Math.Round(e.NewValue, 1);
            RoomCorrectionStore.Save(st);
            _audioEngine?.SetRoomCorrection(st);
            UpdateDspBitPerfectUi();
        }

        /// <summary>从持久化状态初始化 trim 滑杆（只在加载面板时调用一次，避免打断拖动）。</summary>
        private void InitRoomCorrectionTrimUi()
        {
            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = Math.Clamp(st.TrimDb, -24, 6);
            }

            if (RoomTrimLabel != null)
            {
                RoomTrimLabel.Text = "微调余量 (trim)：" + FormatHelper.FormatAudioFxDb(st.TrimDb) + " dB";
            }

            RefreshRoomCorrectionInfo();
        }

        /// <summary>刷新 IR 信息与削波风险提示。</summary>
        private void RefreshRoomCorrectionInfo()
        {
            if (RoomIrInfoText == null)
            {
                return;
            }

            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (!st.Enabled || string.IsNullOrWhiteSpace(st.IrPath))
            {
                RoomIrInfoText.Text = "未导入 IR：点上方卡片的「打开房间校正」导入脉冲响应 WAV。";
                if (RoomClipRiskText != null)
                {
                    RoomClipRiskText.Text = "削波风险：—";
                }

                return;
            }

            string name = string.IsNullOrWhiteSpace(st.IrName) ? System.IO.Path.GetFileName(st.IrPath) : st.IrName;
            int taps = st.IrTapCount > 0 ? st.IrTapCount : (_audioEngine?.ConvolutionIrTaps ?? 0);
            int latencyFrames = _audioEngine?.ConvolutionLatencyFrames ?? 0;
            double latencyMs = latencyFrames > 0 && st.IrSampleRate > 0 ? latencyFrames * 1000.0 / st.IrSampleRate : 0;
            string chText = st.IrChannels >= 2 ? "立体声" : st.IrChannels == 1 ? "单声道" : "—";

            RoomIrInfoText.Text = "IR：" + name
                + " ｜ 长度 " + (taps > 0 ? taps + " taps" : "—")
                + " ｜ 源采样率 " + (st.IrSampleRate > 0 ? st.IrSampleRate + " Hz" : "—")
                + " ｜ 声道 " + chText
                + " ｜ 卷积延迟 " + (latencyMs > 0 ? latencyMs.ToString("F1") + " ms" : "—");

            if (RoomClipRiskText != null)
            {
                bool risk = _audioEngine?.ConvolutionClippingRisk ?? false;
                RoomClipRiskText.Text = risk
                    ? "⚠ 卷积输出已达到满刻度（已自动钳位）：把 trim 调低，或到「输入 · 余量」加大余量。"
                    : "削波风险：无";
            }
        }

        // ---------- 输出安全监控 ----------

        private void OutMonitorReset_Click(object sender, RoutedEventArgs e)
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

            OutMonitorPeakText.Text = float.IsInfinity(peak) ? "—（无数据）" : peak.ToString("F1") + " dBFS";
            OutMonitorClipText.Text = clip == 0 ? "0 次" : clip + " 次";
            OutMonitorOverloadText.Text = clip > 0 ? "⚠ 已削波" : (peak > -0.5f ? "接近满刻度" : "正常");

            // 活跃 DSP 清单：按信号链顺序列出正在参与处理的模块（具名下标，见 DspActive* 常量）
            string[] names = { "余量/限幅", "ReplayGain", "SRC 升频", "参数 EQ", "耳机校正", "声道工具", "FIR 卷积" };
            int[] slots =
            {
                DspActiveHeadroom, DspActiveRg, DspActiveSrc, DspActiveEq,
                DspActiveOpra, DspActiveChannel, DspActiveFir
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

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            OutMonitorActiveDspText.Text = bypass
                ? "无（DSP 总旁路中）"
                : (on.Count == 0 ? "无" : string.Join("、", on));

            // 采样率链路：沿用 SRC 会话状态文本（未升频时显示直出）
            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;
            OutMonitorChainText.Text = string.IsNullOrWhiteSpace(src) || src == "—"
                ? "未升频（直出原始采样率）"
                : src;

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
