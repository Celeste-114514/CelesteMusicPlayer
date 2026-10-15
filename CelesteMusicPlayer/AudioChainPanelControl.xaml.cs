using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 链路节点小卡的显示模型（B3 面板 DataTemplate 绑定用）。
    ///
    /// 「显示不出来就不显示」铁律在这里落地：每个可缺数据的行都有一个 Visibility，
    /// 由 <see cref="AudioChainPanelControl"/> 按模型算好 —— 拿不到就 Collapsed，整行不出，
    /// 不写"未知/—"、不留空位、不做灰显。
    /// </summary>
    public sealed class ChainNodeVm
    {
        public string Title { get; init; } = string.Empty;

        /// <summary>状态文字（"处理中"/"已旁路"/"待命"/"未启用"）。Unknown 时为空且徽标整块收起。</summary>
        public string StateText { get; init; } = string.Empty;

        public Visibility StateVisibility { get; init; } = Visibility.Collapsed;

        public Brush StateBrush { get; init; } = null!;

        public string FormatText { get; init; } = string.Empty;

        public Visibility FormatVisibility { get; init; } = Visibility.Collapsed;

        public string NoteText { get; init; } = string.Empty;

        public Visibility NoteVisibility { get; init; } = Visibility.Collapsed;

        public string LatencyText { get; init; } = string.Empty;

        public Visibility LatencyVisibility { get; init; } = Visibility.Collapsed;
    }

    /// <summary>输出事件时间线一行（B3 面板绑定用）。</summary>
    public sealed class ChainEventVm
    {
        public string TimeText { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;
    }

    /// <summary>
    /// 音频链路面板（B 组 B3，2026-10-16）。只读 <see cref="AudioSignalPath"/> 快照渲染，
    /// 不碰音频线程、不自己判状态（判定口径全在模型里，这就是 B2 的意义）。
    ///
    /// 宿主（MainWindow）负责：抽屉的显隐与滑入滑出动画、刷新节拍；关面板时调
    /// <see cref="Refresh"/> 之外的 <see cref="StopLiveRefresh"/> 停掉定时器（"不可见即停"）。
    /// </summary>
    public sealed partial class AudioChainPanelControl : UserControl
    {
        /// <summary>点面板右上角关闭按钮：交给宿主收抽屉（面板自己不管显隐）。</summary>
        public event EventHandler? CloseRequested;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveTimer;
        private bool _diagExpanded;
        private bool _eventsExpanded;

        public AudioChainPanelControl()
        {
            this.InitializeComponent();
            AttachShadow();
        }

        /// <summary>
        /// 卡片投影（与左侧分类栏卡片同一套写法）。挂不上只丢阴影、不丢功能，整体兜底。
        /// </summary>
        private void AttachShadow()
        {
            try
            {
                // 与左栏浮动卡片（RestoreLeftCategoryNavCard）同一套：ThemeShadow + Z=40 景深。
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

        /// <summary>按一份快照重画面板（标题徽标 / 摘要 / 电平 / 链路图 / 诊断 / 事件）。</summary>
        public void Refresh(AudioSignalPath? path)
        {
            if (path == null)
            {
                return;
            }

            try
            {
                RenderVerdict(path);
                RenderSummary(path);
                RenderLevels(path);
                RenderChain(path);
                RenderDiagnostics(path);
                RenderEvents();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioChainPanelControl.Refresh", caught);
            }
        }

        /// <summary>顶部 bit-perfect 徽标：绿=确认直通、琥珀=有 DSP 介入、灰=未确认。</summary>
        private void RenderVerdict(AudioSignalPath path)
        {
            VerdictBadgeText.Text = path.BitPerfect
                ? (path.BitPerfectConfirmed ? "bit-perfect" : "bit-perfect（待确认）")
                : "DSP 已介入";

            // 半透明色块 + 主题文字色：主题切换时文字始终可读（只在底色上叠一层淡色）。
            VerdictBadge.Background = path.BitPerfect
                ? BuildBrush(path.BitPerfectConfirmed ? (byte)0x2E : (byte)0x1C, 0x3F, 0xC9, 0x6B)
                : BuildBrush(0x2E, 0xE0, 0xA0, 0x30);
        }

        private void RenderSummary(AudioSignalPath path)
        {
            SummaryText.Text = string.IsNullOrWhiteSpace(path.SummaryLine) ? string.Empty : path.SummaryLine;
            SummaryText.Visibility = string.IsNullOrWhiteSpace(path.SummaryLine)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// 实时电平（每声道一条）。拿不到（LevelChannels=0 / 无数据）时整块不显示 —— 铁律。
        /// </summary>
        private void RenderLevels(AudioSignalPath path)
        {
            LevelsRoot.Children.Clear();

            int channels = Math.Min(path.LevelChannels,
                Math.Min(path.PeakLevels.Length, path.RmsLevels.Length));
            if (channels <= 0)
            {
                LevelsRoot.Visibility = Visibility.Collapsed;
                return;
            }

            LevelsRoot.Visibility = Visibility.Visible;
            for (int i = 0; i < channels; i++)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = ChannelLabel(i, channels),
                    FontSize = 11,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                float peak = Math.Clamp(path.PeakLevels[i], 0f, 1f);
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = peak,
                    Height = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                ToolTipService.SetToolTip(bar,
                    $"峰值 {peak * 100:0.#}%  ·  RMS {Math.Clamp(path.RmsLevels[i], 0f, 1f) * 100:0.#}%");
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                LevelsRoot.Children.Add(row);
            }
        }

        private static string ChannelLabel(int index, int channels)
        {
            if (channels == 2)
            {
                return index == 0 ? "L" : "R";
            }

            return (index + 1).ToString();
        }

        /// <summary>纵向链路图：按模型给的链序逐节点画卡。节点为空时给一句说明（不是"未知"占位）。</summary>
        private void RenderChain(AudioSignalPath path)
        {
            if (path.Nodes.Count == 0)
            {
                ChainList.ItemsSource = null;
                ChainEmptyHint.Visibility = Visibility.Visible;
                return;
            }

            ChainEmptyHint.Visibility = Visibility.Collapsed;
            ChainList.ItemsSource = path.Nodes.Select(ToNodeVm).ToList();
        }

        private static ChainNodeVm ToNodeVm(SignalNode node)
        {
            (string stateText, Brush brush, bool showState) = node.State switch
            {
                SignalNodeState.Active => ("处理中", BuildBrush(0x33, 0xE0, 0xA0, 0x30), true),
                SignalNodeState.Bypassed => ("已旁路", BuildBrush(0x26, 0x9A, 0x9A, 0x9A), true),
                SignalNodeState.Standby => ("待命", BuildBrush(0x1F, 0xE0, 0xA0, 0x30), true),
                SignalNodeState.Off => ("未启用", BuildBrush(0x1F, 0x9A, 0x9A, 0x9A), true),
                _ => (string.Empty, null!, false)
            };

            string format = DescribeFormats(node.InFormat, node.OutFormat);

            return new ChainNodeVm
            {
                Title = node.Title,
                StateText = stateText,
                StateVisibility = showState ? Visibility.Visible : Visibility.Collapsed,
                StateBrush = brush,
                FormatText = format,
                FormatVisibility = string.IsNullOrEmpty(format) ? Visibility.Collapsed : Visibility.Visible,
                NoteText = node.Note ?? string.Empty,
                NoteVisibility = string.IsNullOrWhiteSpace(node.Note) ? Visibility.Collapsed : Visibility.Visible,
                LatencyText = node.LatencyMs is double ms ? $"延迟 {ms:0.#} ms" : string.Empty,
                LatencyVisibility = node.LatencyMs.HasValue ? Visibility.Visible : Visibility.Collapsed
            };
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

        /// <summary>链路诊断：只罗列真实拿得到的项；一项都没有就整组不显示。</summary>
        private void RenderDiagnostics(AudioSignalPath path)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(path.VerdictReason))
            {
                lines.Add(path.VerdictReason);
            }

            if (path.OutputBufferMs > 0)
            {
                lines.Add($"输出缓冲 {path.OutputBufferMs} ms");
            }

            if (path.ConvolutionLatencyFrames > 0)
            {
                lines.Add($"卷积延迟 {path.ConvolutionLatencyFrames} 帧");
            }

            if (!string.IsNullOrWhiteSpace(path.DeviceId))
            {
                lines.Add("输出设备 " + path.DeviceId);
            }

            DiagText.Text = string.Join("\n", lines);
            DiagGroup.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>事件时间线（最近 20 条，新→旧）。一条都没有就整组不显示。</summary>
        private void RenderEvents()
        {
            IReadOnlyList<ChainEvent> events = AudioSignalPath.RecentEvents(20);
            if (events.Count == 0)
            {
                EventsList.ItemsSource = null;
                EventsGroup.Visibility = Visibility.Collapsed;
                return;
            }

            EventsGroup.Visibility = Visibility.Visible;
            EventsList.ItemsSource = events
                .Select(e => new ChainEventVm
                {
                    TimeText = e.Time.ToString("HH:mm:ss"),
                    Text = string.IsNullOrWhiteSpace(e.Kind) ? e.Text : "[" + e.Kind + "] " + e.Text
                })
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────
        // 折叠 / 关闭
        // ─────────────────────────────────────────────────────────────

        private void DiagToggle_Click(object sender, RoutedEventArgs e)
        {
            _diagExpanded = !_diagExpanded;
            DiagText.Visibility = _diagExpanded ? Visibility.Visible : Visibility.Collapsed;
            DiagChevron.Glyph = _diagExpanded ? "\uE70D" : "\uE76C";
        }

        private void EventsToggle_Click(object sender, RoutedEventArgs e)
        {
            _eventsExpanded = !_eventsExpanded;
            EventsScroll.Visibility = _eventsExpanded ? Visibility.Visible : Visibility.Collapsed;
            EventsChevron.Glyph = _eventsExpanded ? "\uE70D" : "\uE76C";
        }

        /// <summary>宿主重新打开面板时把两个分组复位成默认收起（折叠状态不持久化）。</summary>
        public void ResetGroupStates()
        {
            _diagExpanded = false;
            _eventsExpanded = false;
            DiagText.Visibility = Visibility.Collapsed;
            EventsScroll.Visibility = Visibility.Collapsed;
            DiagChevron.Glyph = "\uE76C";
            EventsChevron.Glyph = "\uE76C";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>半透明色块画刷。⚠ 必须在 UI 线程 new（SolidColorBrush 是 DependencyObject）。</summary>
        private static Brush BuildBrush(byte a, byte r, byte g, byte b)
            => new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
