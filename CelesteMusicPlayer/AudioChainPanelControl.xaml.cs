using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>技术档案胶囊的显示模型（背景/前景画刷由面板按主题取，UI 不自己配颜色）。</summary>
    public sealed class ChainTagVm
    {
        public string Text { get; init; } = string.Empty;

        public Brush BackgroundBrush { get; init; } = null!;

        public Brush ForegroundBrush { get; init; } = null!;
    }

    /// <summary>双列参数网格里的一列（标签 / 值 / 可选说明）。</summary>
    public sealed class ChainFieldVm
    {
        public string Label { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;

        public string Note { get; init; } = string.Empty;

        public Visibility NoteVisibility { get; init; } = Visibility.Collapsed;
    }

    /// <summary>
    /// 双列参数网格的一行。长值 / 长说明的字段由面板判成整行（IsWide），
    /// 跨两列显示，避免把长句挤在半列里（Echo 参考明确禁止两列互相挤压）。
    /// </summary>
    public sealed class ChainFieldRowVm
    {
        public ChainFieldVm Left { get; init; } = new();

        public ChainFieldVm Right { get; init; } = new();

        public Visibility RightVisibility { get; init; } = Visibility.Collapsed;

        public Visibility NarrowVisibility { get; init; } = Visibility.Visible;

        public Visibility WideVisibility { get; init; } = Visibility.Collapsed;
    }

    /// <summary>链路节点一行的显示模型（图标 Style 由面板按节点 Id 给，节点自己不认识图标）。</summary>
    public sealed class ChainNodeVm
    {
        public string Title { get; init; } = string.Empty;

        public string StateText { get; init; } = string.Empty;

        public Visibility StateVisibility { get; init; } = Visibility.Collapsed;

        public Brush StateBackground { get; init; } = null!;

        public Brush StateForeground { get; init; } = null!;

        /// <summary>核心状态 / 格式信息（等宽字体，Echo 参考里这一行是技术信息）。</summary>
        public string DetailText { get; init; } = string.Empty;

        public Visibility DetailVisibility { get; init; } = Visibility.Collapsed;

        public string NoteText { get; init; } = string.Empty;

        public Visibility NoteVisibility { get; init; } = Visibility.Collapsed;

        public string LatencyText { get; init; } = string.Empty;

        public Visibility LatencyVisibility { get; init; } = Visibility.Collapsed;

        public Style IconStyle { get; init; } = null!;

        /// <summary>左侧连接轴线（最后一个节点不画，避免拖一条多余的尾巴）。</summary>
        public Visibility AxisVisibility { get; init; } = Visibility.Visible;
    }

    /// <summary>链路诊断一条（类别 / 标题 / 原因 / 建议）。</summary>
    public sealed class ChainDiagnosticVm
    {
        public string Category { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Reason { get; init; } = string.Empty;

        public Visibility ReasonVisibility { get; init; } = Visibility.Collapsed;

        public string Advice { get; init; } = string.Empty;

        public Visibility AdviceVisibility { get; init; } = Visibility.Collapsed;
    }

    /// <summary>输出记录时间线一行（时间戳 / 轴+圆点 / 三层事件信息）。</summary>
    public sealed class ChainEventVm
    {
        public string TimeText { get; init; } = string.Empty;

        public string ScopeText { get; init; } = string.Empty;

        public Visibility ScopeVisibility { get; init; } = Visibility.Collapsed;

        public string NameText { get; init; } = string.Empty;

        public string DetailText { get; init; } = string.Empty;

        public Visibility DetailVisibility { get; init; } = Visibility.Collapsed;

        public Brush DotBrush { get; init; } = null!;

        public Visibility AxisVisibility { get; init; } = Visibility.Visible;
    }

    /// <summary>
    /// 音频链路面板（B 组 B3 UI，2026-10-17 按 Echo「信号路径」面板重做外观）。
    ///
    /// 只做两件事：把 <see cref="AudioSignalPath"/> 快照渲染成 Echo 风格布局；把折叠状态管好。
    /// 数据口径一律在模型里（B2 的意义），状态判定 UI 不自己来一遍。
    ///
    /// 宿主（MainWindow）负责：抽屉显隐与滑入滑出、刷新节拍；收起时务必调
    /// <see cref="StopLiveRefresh"/>（"不可见即停"）。
    /// </summary>
    public sealed partial class AudioChainPanelControl : UserControl
    {
        /// <summary>点右上角关闭：交给宿主收抽屉（面板自己不管显隐）。</summary>
        public event EventHandler? CloseRequested;

        /// <summary>点底部「打开音频设置」：交给宿主开设置面板（入口从标题栏挪到这里）。</summary>
        public event EventHandler? OpenAudioSettingsRequested;

        /// <summary>电平条段数（Echo 参考：由多个相邻短矩形组成，不是连续进度条）。</summary>
        private const int MeterSegments = 24;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveTimer;
        private readonly List<Border> _meterBars = new();

        // 折叠默认：诊断展开（它就是给人看的）、DAC 与输出记录收起（避免一开面板就被历史撑长）
        private bool _diagExpanded = true;
        private bool _dacExpanded;
        private bool _eventsExpanded;

        public AudioChainPanelControl()
        {
            this.InitializeComponent();
            AttachShadow();
            BuildMeter();
        }

        /// <summary>卡片投影（与左侧分类栏卡片同一套写法）。挂不上只丢阴影、不丢功能。</summary>
        private void AttachShadow()
        {
            try
            {
                if (PanelCard.Shadow is not ThemeShadow)
                {
                    PanelCard.Shadow = new ThemeShadow();
                    PanelCard.Translation = new System.Numerics.Vector3(0, 0, 40f);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.AttachShadow", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 主题画刷：一律从本控件的 ThemeDictionaries 按当前实际主题取，
        // 不在代码里硬编码颜色（浅色/深色各一套，写在 XAML 里）。
        // ⚠ 沿用项目铁律：主题判断用元素自己的 ActualTheme，不读 App.RequestedTheme。
        // ─────────────────────────────────────────────────────────────

        private Brush ThemeBrush(string key)
        {
            try
            {
                string themeKey = ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
                object? themeDict = Resources.ThemeDictionaries[themeKey];
                if (themeDict is ResourceDictionary dict)
                {
                    object? value = dict[key];
                    if (value is Brush brush)
                    {
                        return brush;
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.ThemeBrush", caught);
            }

            // 主题字典取不到时的兜底灰（正常路径走不到这里）
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0x80));
        }

        // ─────────────────────────────────────────────────────────────
        // 实时刷新（面板可见期间才跑；宿主在收起时务必调 StopLiveRefresh）
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 开始按固定节拍重抓快照刷新面板。200ms 足够看清状态与电平变化，
        /// 又不至于把 Store.Load/电平拷贝跑成热循环。**面板不可见时必须停**。
        /// </summary>
        public void StartLiveRefresh(Func<AudioSignalPath> capture)
        {
            StopLiveRefresh();
            try
            {
                _liveTimer = DispatcherQueue.CreateTimer();
                _liveTimer.Interval = TimeSpan.FromMilliseconds(200);
                _liveTimer.Tick += (_, _) =>
                {
                    try
                    {
                        Refresh(capture());
                    }
                    catch (Exception caught)
                    {
                        StartupLog.WriteException("AudioChainPanelControl.LiveTick", caught);
                    }
                };
                _liveTimer.Start();
                Refresh(capture());
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.StartLiveRefresh", caught);
            }
        }

        /// <summary>停掉刷新定时器（面板收起时调）。可重复调用。</summary>
        public void StopLiveRefresh()
        {
            try
            {
                if (_liveTimer != null)
                {
                    _liveTimer.Stop();
                    _liveTimer = null;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.StopLiveRefresh", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 渲染
        // ─────────────────────────────────────────────────────────────

        /// <summary>按一份快照重画面板（Echo 分区顺序：摘要→阶段→电平→双列→余量→档案→链路→诊断→DAC→记录→稳定性）。</summary>
        public void Refresh(AudioSignalPath? path)
        {
            if (path == null)
            {
                return;
            }

            try
            {
                RenderSummary(path);
                RenderStage(path);
                RenderLevels(path);
                RenderSourceProcess(path);
                RenderFields(path);
                RenderHeadroom(path);
                RenderTechTags(path);
                RenderChain(path);
                RenderDiagnostics(path);
                RenderDac(path);
                RenderEvents();
                RenderStability(path);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.Refresh", caught);
            }
        }

        /// <summary>② 摘要行：音源摘要 + 右侧「N 层链路」胶囊（拿不到就不出，铁律）。</summary>
        private void RenderSummary(AudioSignalPath path)
        {
            bool hasSource = !string.IsNullOrWhiteSpace(path.SourceValue);
            SummaryText.Text = hasSource ? path.SourceValue! : string.Empty;
            SummaryText.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;

            bool hasLayers = path.LayerCount > 0;
            LayerBadge.Visibility = hasLayers ? Visibility.Visible : Visibility.Collapsed;
            LayerBadgeText.Text = hasLayers ? $"{path.LayerCount} 层链路" : string.Empty;
        }

        /// <summary>③ 当前处理阶段：20px 大标题 + 紧随的状态行。</summary>
        private void RenderStage(AudioSignalPath path)
        {
            SetText(StageTitleText, path.StageTitle);
            SetText(StageNoteText, path.StageNote);
        }

        /// <summary>
        /// ④ 实时电平：分段矩形条 + dB 刻度。
        /// 没有实时信号时数值显示双短横线、所有段保持浅灰 —— 按 Echo 参考的既定样式，
        /// 这是全面板唯一允许出现的占位符（参考图明确要求），其余任何缺数据仍然是整行不出。
        /// </summary>
        private void RenderLevels(AudioSignalPath path)
        {
            int channels = Math.Min(path.LevelChannels,
                Math.Min(path.PeakLevels.Length, path.RmsLevels.Length));

            if (channels <= 0)
            {
                LevelValueText.Text = "--";
                PaintMeter(null);
                return;
            }

            float peak = 0f;
            for (int i = 0; i < channels; i++)
            {
                peak = Math.Max(peak, Math.Clamp(path.PeakLevels[i], 0f, 1f));
            }

            double db = peak > 0.0001 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
            LevelValueText.Text = double.IsNegativeInfinity(db)
                ? "--"
                : $"{db:0.#} dB";

            PaintMeter(peak);
        }

        /// <summary>建立电平条的 24 段短矩形（一次性建好，之后只改底色，不反复 new 控件）。</summary>
        private void BuildMeter()
        {
            try
            {
                LevelMeterGrid.ColumnDefinitions.Clear();
                _meterBars.Clear();
                LevelMeterGrid.Children.Clear();

                for (int i = 0; i < MeterSegments; i++)
                {
                    LevelMeterGrid.ColumnDefinitions.Add(
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var bar = new Border
                    {
                        CornerRadius = new CornerRadius(1.5),
                        Margin = new Thickness(0, 0, i == MeterSegments - 1 ? 0 : 1.5, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    Grid.SetColumn(bar, i);
                    LevelMeterGrid.Children.Add(bar);
                    _meterBars.Add(bar);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.BuildMeter", caught);
            }
        }

        /// <summary>按峰值点亮电平段。level=null 表示无数据（全段浅灰）。</summary>
        private void PaintMeter(float? level)
        {
            Brush idle = ThemeBrush("ChainMeterIdleBrush");
            Brush on = ThemeBrush("ChainMeterOnBrush");
            Brush clip = ThemeBrush("ChainMeterClipBrush");

            for (int i = 0; i < _meterBars.Count; i++)
            {
                _meterBars[i].Background = idle;
            }

            if (level is not float peak || peak <= 0f)
            {
                return;
            }

            double db = 20.0 * Math.Log10(Math.Clamp(peak, 0.0001f, 1f));
            bool clipping = peak >= 0.999f;
            for (int i = 0; i < _meterBars.Count; i++)
            {
                // 段 i 的阈值：-60dB 起，每段 2.5dB，最右一段到 0dBFS
                double threshold = -60.0 + 2.5 * (i + 1);
                if (db < threshold)
                {
                    continue;
                }

                _meterBars[i].Background = (clipping && i >= _meterBars.Count - 2) ? clip : on;
            }
        }

        /// <summary>⑤ 双列「来源 / 处理」。两列都拿不到时整块收起，不留空网格。</summary>
        private void RenderSourceProcess(AudioSignalPath path)
        {
            bool hasSource = !string.IsNullOrWhiteSpace(path.SourceValue);
            bool hasProcess = !string.IsNullOrWhiteSpace(path.ProcessValue);

            SourceColumn.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;
            ProcessColumn.Visibility = hasProcess ? Visibility.Visible : Visibility.Collapsed;
            SourceProcessGrid.Visibility = hasSource || hasProcess ? Visibility.Visible : Visibility.Collapsed;

            SetText(SourceValueText, path.SourceValue);
            SetText(SourceNoteText, path.SourceNote);
            SetText(ProcessValueText, path.ProcessValue);
            SetText(ProcessNoteText, path.ProcessNote);
        }

        /// <summary>⑤b 后续字段（输出设备 / 输出模式 / 内部格式 / 实际输出 / 缓冲 / 卷积 / 变速）走统一双列网格。</summary>
        private void RenderFields(AudioSignalPath path)
        {
            FieldList.ItemsSource = BuildFieldRows(path.Fields);
        }

        /// <summary>⑥ DSP 实时余量：没在实时播放就是「等待信号」，不编 CPU / 处理速度。</summary>
        private void RenderHeadroom(AudioSignalPath path)
        {
            bool has = !string.IsNullOrWhiteSpace(path.DspHeadroomValue);
            HeadroomRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has)
            {
                return;
            }

            HeadroomValueText.Text = path.DspHeadroomValue!;
            SetText(HeadroomNoteText, path.DspHeadroomNote);
        }

        /// <summary>⑦ 技术档案胶囊：一条都没有就整块收起。</summary>
        private void RenderTechTags(AudioSignalPath path)
        {
            if (path.TechTags.Count == 0)
            {
                TechTagGroup.Visibility = Visibility.Collapsed;
                TechTagList.ItemsSource = null;
                return;
            }

            TechTagGroup.Visibility = Visibility.Visible;
            TechTagList.ItemsSource = path.TechTags.Select(tag => new ChainTagVm
            {
                Text = tag.Text,
                BackgroundBrush = ThemeBrush(TagBackgroundKey(tag.Tone)),
                ForegroundBrush = ThemeBrush(TagForegroundKey(tag.Tone))
            }).ToList();
        }

        private static string TagBackgroundKey(ChainTagTone tone) => tone switch
        {
            ChainTagTone.Accent => "ChainTagAccentBg",
            ChainTagTone.Ok => "ChainTagOkBg",
            ChainTagTone.Warn => "ChainTagWarnBg",
            _ => "ChainTagNeutralBg"
        };

        private static string TagForegroundKey(ChainTagTone tone) => tone switch
        {
            ChainTagTone.Accent => "ChainTagAccentFg",
            ChainTagTone.Ok => "ChainTagOkFg",
            ChainTagTone.Warn => "ChainTagWarnFg",
            _ => "ChainTagNeutralFg"
        };

        /// <summary>⑧ 音频链路节点：图标 + 轴线串起来的纵向链，不给节点套边框卡片。</summary>
        private void RenderChain(AudioSignalPath path)
        {
            if (path.Nodes.Count == 0)
            {
                ChainList.ItemsSource = null;
                ChainEmptyHint.Visibility = Visibility.Visible;
                return;
            }

            ChainEmptyHint.Visibility = Visibility.Collapsed;
            var vms = new List<ChainNodeVm>(path.Nodes.Count);
            for (int i = 0; i < path.Nodes.Count; i++)
            {
                vms.Add(ToNodeVm(path.Nodes[i], isLast: i == path.Nodes.Count - 1));
            }

            ChainList.ItemsSource = vms;
        }

        private ChainNodeVm ToNodeVm(SignalNode node, bool isLast)
        {
            (string stateText, string bgKey, string fgKey, bool showState) = node.State switch
            {
                SignalNodeState.Active => ("处理中", "ChainTagAccentBg", "ChainTagAccentFg", true),
                SignalNodeState.Bypassed => ("已旁路", "ChainTagWarnBg", "ChainTagWarnFg", true),
                SignalNodeState.Standby => ("待命", "ChainTagNeutralBg", "ChainTagNeutralFg", true),
                SignalNodeState.Off => ("未启用", "ChainTagNeutralBg", "ChainTagNeutralFg", true),
                _ => (string.Empty, "ChainTagNeutralBg", "ChainTagNeutralFg", false)
            };

            string detail = DescribeFormats(node.InFormat, node.OutFormat);

            return new ChainNodeVm
            {
                Title = node.Title,
                StateText = stateText,
                StateVisibility = showState ? Visibility.Visible : Visibility.Collapsed,
                StateBackground = ThemeBrush(bgKey),
                StateForeground = ThemeBrush(fgKey),
                DetailText = detail,
                DetailVisibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible,
                NoteText = node.Note ?? string.Empty,
                NoteVisibility = string.IsNullOrWhiteSpace(node.Note) ? Visibility.Collapsed : Visibility.Visible,
                LatencyText = node.LatencyMs is double ms
                    ? $"延迟 {ms.ToString("0.#", CultureInfo.InvariantCulture)} ms"
                    : string.Empty,
                LatencyVisibility = node.LatencyMs.HasValue ? Visibility.Visible : Visibility.Collapsed,
                IconStyle = NodeIconStyle(node.Id),
                AxisVisibility = isLast ? Visibility.Collapsed : Visibility.Visible
            };
        }

        /// <summary>节点 Id → 线框图标 Style（查不到就用文件图标兜底，绝不显示空图标）。</summary>
        private Style NodeIconStyle(string id)
        {
            string key = id switch
            {
                "decode" => "IcoSource",
                "cache" => "IcoCache",
                "gapless" => "IcoGapless",
                "downmix" => "IcoDownmix",
                "tempo" => "IcoTempo",
                "safety" => "IcoSafety",
                "output" => "IcoOutput",
                // 一个 DSP 都没开时面板上那一枚"DSP 未启用"节点
                "dsp:none" => "IcoEq",
                "dsp:0" => "IcoEq",
                "dsp:1" => "IcoConvolution",
                "dsp:2" => "IcoGain",
                "dsp:3" => "IcoCompressor",
                "dsp:4" => "IcoCrossfeed",
                "dsp:5" => "IcoField",
                "dsp:6" => "IcoMatrix",
                "dsp:7" => "IcoBalance",
                _ => "IcoSource"
            };

            try
            {
                if (Resources[key] is Style style)
                {
                    return style;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.NodeIconStyle", caught);
            }

            return new Style(typeof(Path));
        }

        /// <summary>进/出格式拼成一行；两端都拿不到就返回空串（调用方把行收起，铁律）。</summary>
        private static string DescribeFormats(AudioFormat? inFormat, AudioFormat? outFormat)
        {
            if (inFormat == null && outFormat == null)
            {
                return string.Empty;
            }

            if (inFormat == null)
            {
                return "→ " + outFormat!.Value.DescribeShort();
            }

            if (outFormat == null)
            {
                return inFormat.Value.DescribeShort();
            }

            AudioFormat a = inFormat.Value;
            AudioFormat b = outFormat.Value;
            if (a.Rate == b.Rate && a.Bits == b.Bits && a.Channels == b.Channels)
            {
                // 这一环节不改格式：只写一次，避免"44.1k/16/2 → 44.1k/16/2"这种废话
                return a.DescribeShort();
            }

            return a.DescribeShort() + " → " + b.DescribeShort();
        }

        /// <summary>⑨ 链路诊断：一条都没有就整组不显示（不留空标题）。</summary>
        private void RenderDiagnostics(AudioSignalPath path)
        {
            if (path.Diagnostics.Count == 0)
            {
                DiagGroup.Visibility = Visibility.Collapsed;
                DiagList.ItemsSource = null;
                return;
            }

            DiagGroup.Visibility = Visibility.Visible;
            DiagCountText.Text = $"{path.Diagnostics.Count} 条提示";
            DiagList.ItemsSource = path.Diagnostics.Select(d => new ChainDiagnosticVm
            {
                Category = d.Category,
                Title = d.Title,
                Reason = d.Reason,
                ReasonVisibility = string.IsNullOrWhiteSpace(d.Reason) ? Visibility.Collapsed : Visibility.Visible,
                Advice = d.Advice,
                AdviceVisibility = string.IsNullOrWhiteSpace(d.Advice) ? Visibility.Collapsed : Visibility.Visible
            }).ToList();
        }

        /// <summary>⑩ DAC 能力图谱：设备名拿不到就整组不显示（铁律：不编能力）。</summary>
        private void RenderDac(AudioSignalPath path)
        {
            if (string.IsNullOrWhiteSpace(path.DacDeviceName) || path.DacFields.Count == 0)
            {
                DacGroup.Visibility = Visibility.Collapsed;
                DacList.ItemsSource = null;
                return;
            }

            DacGroup.Visibility = Visibility.Visible;
            DacDeviceText.Text = path.DacDeviceName!;
            DacList.ItemsSource = BuildFieldRows(path.DacFields);
        }

        /// <summary>
        /// ⑪ 输出记录时间线：折叠时只留标题与条数；展开时显示「时间戳 / 轴+圆点 / 三层事件信息」。
        /// 「当前曲目 N 条」= 最近一次曲目切换之后的条数（没有切换事件时全部算当前曲目）。
        /// </summary>
        private void RenderEvents()
        {
            IReadOnlyList<ChainEvent> events = AudioSignalPath.RecentEvents(50);
            if (events.Count == 0)
            {
                EventsGroup.Visibility = Visibility.Collapsed;
                EventsList.ItemsSource = null;
                return;
            }

            EventsGroup.Visibility = Visibility.Visible;

            int currentTrackCount = 0;
            while (currentTrackCount < events.Count
                && !string.Equals(events[currentTrackCount].Kind, "track", StringComparison.OrdinalIgnoreCase))
            {
                currentTrackCount++;
            }

            EventsCountText.Text = $"当前曲目 {currentTrackCount} 条 / 共 {events.Count} 条";

            Brush accent = ThemeBrush("ChainAccentBrush");
            Brush warn = ThemeBrush("ChainWarnTextBrush");

            var vms = new List<ChainEventVm>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                ChainEvent e = events[i];
                bool inCurrentTrack = i < currentTrackCount;
                vms.Add(new ChainEventVm
                {
                    TimeText = e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ScopeText = inCurrentTrack ? "当前曲目" : "上一次曲目",
                    ScopeVisibility = Visibility.Visible,
                    NameText = DescribeEventKind(e.Kind),
                    DetailText = e.Text,
                    DetailVisibility = string.IsNullOrWhiteSpace(e.Text) ? Visibility.Collapsed : Visibility.Visible,
                    DotBrush = LooksLikeWarning(e.Text) ? warn : accent,
                    AxisVisibility = i == events.Count - 1 ? Visibility.Collapsed : Visibility.Visible
                });
            }

            EventsList.ItemsSource = vms;
        }

        /// <summary>事件分类的人话（只是把模型里的 kind 翻译成中文，不做任何状态判定）。</summary>
        private static string DescribeEventKind(string kind)
        {
            return kind switch
            {
                "track" => "曲目切换",
                "device" => "输出设备",
                "mode" => "输出模式",
                "negotiate" => "格式协商",
                "tempo" => "变速",
                "dsp" => "DSP",
                "system" => "系统",
                _ => string.IsNullOrWhiteSpace(kind) ? "事件" : kind
            };
        }

        /// <summary>事件文本里出现失败/回退/降级这类词时用橙色圆点（只影响圆点颜色，不改文案）。</summary>
        private static bool LooksLikeWarning(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf("失败", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("回退", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("降级", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>⑫ 稳定性反馈：总结 + 说明 + 双列最终状态。</summary>
        private void RenderStability(AudioSignalPath path)
        {
            bool hasTitle = !string.IsNullOrWhiteSpace(path.StabilityTitle);
            StabilityGroup.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
            if (!hasTitle)
            {
                StabilityList.ItemsSource = null;
                return;
            }

            StabilityTitleText.Text = path.StabilityTitle!;
            SetText(StabilityNoteText, path.StabilityNote);
            StabilityList.ItemsSource = BuildFieldRows(path.StabilityFields);
        }

        // ─────────────────────────────────────────────────────────────
        // 双列网格构造
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 把一列字段排成双列网格的行：长值 / 长说明的字段判成整行（跨两列），
        /// 其余两两配对。空列表返回 null（调用方整块收起）。
        /// </summary>
        private static List<ChainFieldRowVm> BuildFieldRows(IReadOnlyList<ChainField> fields)
        {
            var rows = new List<ChainFieldRowVm>();
            if (fields == null || fields.Count == 0)
            {
                return rows;
            }

            ChainField? pending = null;
            foreach (ChainField field in fields)
            {
                if (IsWide(field))
                {
                    if (pending != null)
                    {
                        rows.Add(SingleRow(pending));
                        pending = null;
                    }

                    rows.Add(WideRow(field));
                    continue;
                }

                if (pending == null)
                {
                    pending = field;
                    continue;
                }

                rows.Add(PairRow(pending, field));
                pending = null;
            }

            if (pending != null)
            {
                rows.Add(SingleRow(pending));
            }

            return rows;
        }

        /// <summary>长值或长说明 → 占整行（两列宽度装不下，硬塞会把另一列挤变形）。</summary>
        private static bool IsWide(ChainField field)
        {
            return field.Value.Length > 22 || (field.Note?.Length ?? 0) > 26;
        }

        private static ChainFieldRowVm PairRow(ChainField left, ChainField right)
        {
            return new ChainFieldRowVm
            {
                Left = ToFieldVm(left),
                Right = ToFieldVm(right),
                RightVisibility = Visibility.Visible,
                NarrowVisibility = Visibility.Visible,
                WideVisibility = Visibility.Collapsed
            };
        }

        private static ChainFieldRowVm SingleRow(ChainField only)
        {
            return new ChainFieldRowVm
            {
                Left = ToFieldVm(only),
                Right = new ChainFieldVm(),
                RightVisibility = Visibility.Collapsed,
                NarrowVisibility = Visibility.Visible,
                WideVisibility = Visibility.Collapsed
            };
        }

        private static ChainFieldRowVm WideRow(ChainField only)
        {
            return new ChainFieldRowVm
            {
                Left = ToFieldVm(only),
                Right = new ChainFieldVm(),
                RightVisibility = Visibility.Collapsed,
                NarrowVisibility = Visibility.Collapsed,
                WideVisibility = Visibility.Visible
            };
        }

        private static ChainFieldVm ToFieldVm(ChainField field)
        {
            return new ChainFieldVm
            {
                Label = field.Label,
                Value = field.Value,
                Note = field.Note ?? string.Empty,
                NoteVisibility = string.IsNullOrWhiteSpace(field.Note) ? Visibility.Collapsed : Visibility.Visible
            };
        }

        private static void SetText(TextBlock target, string? value)
        {
            bool has = !string.IsNullOrWhiteSpace(value);
            target.Text = has ? value! : string.Empty;
            target.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────────
        // 折叠 / 关闭 / 设置入口
        // ─────────────────────────────────────────────────────────────

        private void DiagToggle_Click(object sender, RoutedEventArgs e)
        {
            _diagExpanded = !_diagExpanded;
            DiagList.Visibility = _diagExpanded ? Visibility.Visible : Visibility.Collapsed;
            DiagChevron.Glyph = _diagExpanded ? "\uE70D" : "\uE76C";
        }

        private void DacToggle_Click(object sender, RoutedEventArgs e)
        {
            _dacExpanded = !_dacExpanded;
            DacList.Visibility = _dacExpanded ? Visibility.Visible : Visibility.Collapsed;
            DacChevron.Glyph = _dacExpanded ? "\uE70D" : "\uE76C";
        }

        private void EventsToggle_Click(object sender, RoutedEventArgs e)
        {
            _eventsExpanded = !_eventsExpanded;
            EventsList.Visibility = _eventsExpanded ? Visibility.Visible : Visibility.Collapsed;
            EventsChevron.Glyph = _eventsExpanded ? "\uE70D" : "\uE76C";
        }

        /// <summary>宿主重新打开面板时把折叠状态复位（诊断展开、DAC 与输出记录收起；不持久化）。</summary>
        public void ResetGroupStates()
        {
            _diagExpanded = true;
            _dacExpanded = false;
            _eventsExpanded = false;

            DiagList.Visibility = Visibility.Visible;
            DacList.Visibility = Visibility.Collapsed;
            EventsList.Visibility = Visibility.Collapsed;

            DiagChevron.Glyph = "\uE70D";
            DacChevron.Glyph = "\uE76C";
            EventsChevron.Glyph = "\uE76C";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void OpenAudioSettingsButton_Click(object sender, RoutedEventArgs e)
            => OpenAudioSettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
