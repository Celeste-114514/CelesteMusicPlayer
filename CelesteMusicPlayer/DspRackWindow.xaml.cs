// DspRackWindow.xaml.cs
// ECHO DSP 机架设置窗口（Stage C 新增）：
//   ① 8 模块处理顺序（上移/下移，恢复默认）
//   ② 压缩器（电源即时生效 + 7 参数草稿 + 250ms 实时压缩量 GR 表）
//   ③ 立体声场（电源即时生效 + 3 参数草稿）
//   ④ 声道矩阵（电源即时生效 + 4 系数草稿 + 直通/交换/单声道预设）
//   ⑤ Crossfeed 截止频率（走旧 dsp-extra.json 链路，ChannelBalanceState.CrossfeedCutoffHz）
//
// 交互（对齐 ECHO 面板）：滑杆/顺序/预设改的是「草稿」，点「应用」才写盘并经
// Applied 事件让主窗口推送给引擎；三个模块的电源开关立即提交（即时可闻）。
// 窗口骨架（单例 / Applied / 标题栏 / 毛玻璃）照搬 EqualizerWindow 既有模式。

using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>ECHO DSP 机架设置窗口（顺序 + 压缩器 + 立体声场 + 声道矩阵 + Crossfeed 截止）。</summary>
    public sealed partial class DspRackWindow : Window
    {
        private static DspRackWindow? _instance;

        /// <summary>机架模块名（索引 = DspCoreInterop.Rack* 常量，与 RackState.RackOrder 同位）。</summary>
        private static readonly string[] RackModuleNames =
        {
            "参数 EQ", "卷积（房间校正）", "ReplayGain", "压缩器",
            "Crossfeed", "立体声场", "声道矩阵", "声道平衡"
        };

        private RackState _draft = new();
        private bool _loadingUi;
        private DispatcherQueueTimer? _grTimer;

        // 矩阵滑杆引用（预设按钮回写用）
        private Slider? _mLeftToLeft;
        private Slider? _mRightToLeft;
        private Slider? _mLeftToRight;
        private Slider? _mRightToRight;

        /// <summary>机架状态已提交（写盘后由主窗口推送给引擎）。</summary>
        public static event Action? Applied;

        public DspRackWindow()
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "DSP 机架";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(880, 960));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            BuildCompressorSliders();
            BuildStereoFieldSliders();
            BuildMatrixSliders();
            BuildRackOrderRows();
            LoadFromStore();

            Closed += (_, _) =>
            {
                _grTimer?.Stop();
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.LoadFromStore();
                _instance.Activate();
                return;
            }

            _instance = new DspRackWindow();
            _instance.Activate();
        }

        public static void CloseIfOpen()
        {
            if (_instance == null)
            {
                return;
            }

            DspRackWindow win = _instance;
            _instance = null;
            win.Close();
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
                // 关毛玻璃时窗口不经过 ApplyWindowBackdrop，这里补一次：经典界面下弹窗也要分深浅色
                FrostedGlass.ApplyWindowTheme(this);
            }
        }

        // ---------- 滑杆行构建 ----------

        /// <summary>往宿主面板加一行「标签 + 滑杆 + 数值」。滑杆改动直接写草稿（应用才提交）。</summary>
        private void AddSliderRow(
            StackPanel host,
            string label,
            double min,
            double max,
            double step,
            double value,
            Func<double, string> format,
            Action<double> onValue)
        {
            Grid grid = new() { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock labelText = new()
            {
                Text = label,
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock valueText = new()
            {
                Text = format(value),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 72,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
            };
            Slider slider = new()
            {
                Minimum = min,
                Maximum = max,
                StepFrequency = step,
                Value = value,
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, e) =>
            {
                valueText.Text = format(e.NewValue);
                if (!_loadingUi)
                {
                    onValue(e.NewValue);
                    RefreshBadge();
                }
            };

            grid.Children.Add(labelText);
            grid.Children.Add(slider);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(valueText, 2);
            host.Children.Add(grid);
        }

        private void BuildCompressorSliders()
        {
            CompressorSliders.Children.Clear();
            AddSliderRow(CompressorSliders, "阈值", -60, 0, 0.5, _draft.Compressor.ThresholdDb, FormatDb,
                v => _draft.Compressor.ThresholdDb = v);
            AddSliderRow(CompressorSliders, "压缩比", 1, 20, 0.1, _draft.Compressor.Ratio, v => v.ToString("0.0") + " : 1",
                v => _draft.Compressor.Ratio = v);
            AddSliderRow(CompressorSliders, "启动", 0.1, 200, 0.1, _draft.Compressor.AttackMs, v => v.ToString("0.0") + " ms",
                v => _draft.Compressor.AttackMs = v);
            AddSliderRow(CompressorSliders, "释放", 5, 2000, 1, _draft.Compressor.ReleaseMs, v => v.ToString("0") + " ms",
                v => _draft.Compressor.ReleaseMs = v);
            AddSliderRow(CompressorSliders, "软拐点", 0, 24, 0.5, _draft.Compressor.KneeDb, FormatDb,
                v => _draft.Compressor.KneeDb = v);
            AddSliderRow(CompressorSliders, "补偿增益", -12, 24, 0.5, _draft.Compressor.MakeupDb, FormatDb,
                v => _draft.Compressor.MakeupDb = v);
            AddSliderRow(CompressorSliders, "干湿混合", 0, 1, 0.01, _draft.Compressor.Mix, v => (v * 100).ToString("0") + " %",
                v => _draft.Compressor.Mix = v);
        }

        private void BuildStereoFieldSliders()
        {
            StereoFieldSliders.Children.Clear();
            AddSliderRow(StereoFieldSliders, "宽度", 0, 2, 0.01, _draft.StereoField.Width, v => v.ToString("0.00") + " ×",
                v => _draft.StereoField.Width = v);
            AddSliderRow(StereoFieldSliders, "中置增益", -18, 18, 0.5, _draft.StereoField.CenterGainDb, FormatDb,
                v => _draft.StereoField.CenterGainDb = v);
            AddSliderRow(StereoFieldSliders, "侧向增益", -18, 18, 0.5, _draft.StereoField.SideGainDb, FormatDb,
                v => _draft.StereoField.SideGainDb = v);
        }

        private void BuildMatrixSliders()
        {
            MatrixSliders.Children.Clear();
            _mLeftToLeft = AddSliderRowRef(MatrixSliders, "左 → 左", -2, 2, 0.01, _draft.Matrix.LeftToLeft, FormatCoeff,
                v => _draft.Matrix.LeftToLeft = v);
            _mRightToLeft = AddSliderRowRef(MatrixSliders, "右 → 左", -2, 2, 0.01, _draft.Matrix.RightToLeft, FormatCoeff,
                v => _draft.Matrix.RightToLeft = v);
            _mLeftToRight = AddSliderRowRef(MatrixSliders, "左 → 右", -2, 2, 0.01, _draft.Matrix.LeftToRight, FormatCoeff,
                v => _draft.Matrix.LeftToRight = v);
            _mRightToRight = AddSliderRowRef(MatrixSliders, "右 → 右", -2, 2, 0.01, _draft.Matrix.RightToRight, FormatCoeff,
                v => _draft.Matrix.RightToRight = v);
        }

        /// <summary>同 <see cref="AddSliderRow"/>，额外返回行内滑杆引用（预设回写用）。</summary>
        private Slider AddSliderRowRef(StackPanel host, string label, double min, double max, double step, double value, Func<double, string> format, Action<double> onValue)
        {
            AddSliderRow(host, label, min, max, step, value, format, onValue);
            // 取回最后加的那一行里的滑杆（行网格第 2 个子节点 = 滑杆列）
            if (host.Children[^1] is Grid g && g.Children.Count >= 2 && g.Children[1] is Slider s)
            {
                return s;
            }

            return new Slider();
        }

        // ---------- 机架顺序行 ----------

        private void BuildRackOrderRows()
        {
            RackOrderPanel.Children.Clear();
            int[] order = _draft.RackOrder;
            for (int i = 0; i < order.Length && i < RackModuleNames.Length; i++)
            {
                int idx = i;
                int moduleId = order[i];
                string moduleName = moduleId >= 0 && moduleId < RackModuleNames.Length
                    ? RackModuleNames[moduleId]
                    : "模块 " + moduleId;

                Grid row = new() { ColumnSpacing = 6, Padding = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBlock indexText = new()
                {
                    Text = (i + 1).ToString(),
                    FontSize = 12,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center
                };
                TextBlock nameText = new()
                {
                    Text = moduleName,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Button upButton = new()
                {
                    Width = 34,
                    Height = 28,
                    Padding = new Thickness(0),
                    IsEnabled = i > 0,
                    Content = new FontIcon { Glyph = "\uE74A", FontSize = 11 }
                };
                ToolTipService.SetToolTip(upButton, "上移");
                upButton.Click += (_, _) => MoveRackModule(idx, -1);
                Button downButton = new()
                {
                    Width = 34,
                    Height = 28,
                    Padding = new Thickness(0),
                    IsEnabled = i < order.Length - 1,
                    Content = new FontIcon { Glyph = "\uE74B", FontSize = 11 }
                };
                ToolTipService.SetToolTip(downButton, "下移");
                downButton.Click += (_, _) => MoveRackModule(idx, 1);

                row.Children.Add(indexText);
                row.Children.Add(nameText);
                Grid.SetColumn(nameText, 1);
                row.Children.Add(upButton);
                Grid.SetColumn(upButton, 2);
                row.Children.Add(downButton);
                Grid.SetColumn(downButton, 3);
                RackOrderPanel.Children.Add(row);
            }
        }

        private void MoveRackModule(int idx, int delta)
        {
            int target = idx + delta;
            int[] order = _draft.RackOrder;
            if (target < 0 || target >= order.Length)
            {
                return;
            }

            (order[idx], order[target]) = (order[target], order[idx]);
            BuildRackOrderRows();
            RefreshBadge();
        }

        // ---------- 加载 / 保存 / 应用 ----------

        private void LoadFromStore()
        {
            _draft = DspRackStore.Load();
            _loadingUi = true;
            try
            {
                CompressorToggle.IsOn = _draft.Compressor.Enabled;
                BuildCompressorSliders();
                BuildStereoFieldSliders();
                BuildMatrixSliders();
                BuildRackOrderRows();
                StereoFieldToggle.IsOn = _draft.StereoField.Enabled;
                MatrixToggle.IsOn = _draft.Matrix.Enabled;

                var extra = DspExtraStore.Load();
                double cutoff = extra.ChannelBalance?.CrossfeedCutoffHz ?? 700;
                CrossfeedCutoffSlider.Value = Math.Clamp(cutoff, 100, 4000);
                CrossfeedCutoffValue.Text = cutoff.ToString("0") + " Hz";
            }
            finally
            {
                _loadingUi = false;
            }

            RefreshBadge();
            EnsureGrTimer(_draft.Compressor.Enabled);
        }

        /// <summary>提交整个机架：写盘 + 通知主窗口推送给引擎。</summary>
        private void CommitRack(string reason)
        {
            _draft.Normalize();
            DspRackStore.Save(_draft);
            StartupLog.Write("[DSP] 机架已应用：" + reason);
            try
            {
                Applied?.Invoke();
            }
            catch (Exception caught) { StartupLog.WriteException("DspRackWindow.xaml.cs", caught); }
            RefreshBadge();
        }

        /// <summary>Crossfeed 截止频率走旧的 dsp-extra.json 链路（与引擎 UpdateChannel 同一数据源）。</summary>
        private void FlushCrossfeedCutoff()
        {
            double hz = CrossfeedCutoffSlider.Value;
            DspExtraState extra = DspExtraStore.Load();
            extra.ChannelBalance ??= ChannelBalanceState.Default();
            extra.ChannelBalance.CrossfeedCutoffHz = (int)Math.Round(hz);
            DspExtraStore.Save(extra);
            StartupLog.Write("[DSP] Crossfeed 截止频率已应用：" + ((int)Math.Round(hz)) + " Hz");
        }

        private void RefreshBadge()
        {
            bool active = _draft.Compressor.IsActive || _draft.StereoField.IsActive || _draft.Matrix.IsActive;
            RackBitPerfectText.Text = active ? "处理链生效 · 非 bit-perfect" : "默认直通";
            RackBitPerfectBadge.Background = new SolidColorBrush(active
                ? Color.FromArgb(255, 0xC0, 0x7A, 0x1A)
                : Color.FromArgb(255, 0x3B, 0x6D, 0x11));
        }

        // ---------- 电源开关（即时生效） ----------

        private void CompressorToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            _draft.Compressor.Enabled = CompressorToggle.IsOn;
            EnsureGrTimer(_draft.Compressor.Enabled);
            CommitRack("压缩器电源 " + (CompressorToggle.IsOn ? "开" : "关"));
        }

        private void StereoFieldToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            _draft.StereoField.Enabled = StereoFieldToggle.IsOn;
            CommitRack("立体声场电源 " + (StereoFieldToggle.IsOn ? "开" : "关"));
        }

        private void MatrixToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            _draft.Matrix.Enabled = MatrixToggle.IsOn;
            CommitRack("声道矩阵电源 " + (MatrixToggle.IsOn ? "开" : "关"));
        }

        // ---------- 其它控件事件 ----------

        private void RackOrderDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            _draft.RackOrder = new[]
            {
                DspCoreInterop.RackEqualizer, DspCoreInterop.RackConvolution, DspCoreInterop.RackReplayGain,
                DspCoreInterop.RackCompressor, DspCoreInterop.RackCrossfeed, DspCoreInterop.RackStereoField,
                DspCoreInterop.RackChannelMatrix, DspCoreInterop.RackChannelBalance
            };
            BuildRackOrderRows();
            RefreshBadge();
        }

        private void CompDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            CompressorSettings defaults = new();
            _draft.Compressor.ThresholdDb = defaults.ThresholdDb;
            _draft.Compressor.Ratio = defaults.Ratio;
            _draft.Compressor.AttackMs = defaults.AttackMs;
            _draft.Compressor.ReleaseMs = defaults.ReleaseMs;
            _draft.Compressor.KneeDb = defaults.KneeDb;
            _draft.Compressor.MakeupDb = defaults.MakeupDb;
            _draft.Compressor.Mix = defaults.Mix;
            BuildCompressorSliders();
            RefreshBadge();
        }

        private void MatrixPresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag)
            {
                return;
            }

            var m = _draft.Matrix;
            switch (tag)
            {
                case "pass":
                    m.LeftToLeft = 1; m.RightToLeft = 0; m.LeftToRight = 0; m.RightToRight = 1;
                    break;
                case "swap":
                    m.LeftToLeft = 0; m.RightToLeft = 1; m.LeftToRight = 1; m.RightToRight = 0;
                    break;
                case "mono":
                    m.LeftToLeft = 0.5; m.RightToLeft = 0.5; m.LeftToRight = 0.5; m.RightToRight = 0.5;
                    break;
                default:
                    return;
            }

            _loadingUi = true;
            try
            {
                if (_mLeftToLeft != null) _mLeftToLeft.Value = m.LeftToLeft;
                if (_mRightToLeft != null) _mRightToLeft.Value = m.RightToLeft;
                if (_mLeftToRight != null) _mLeftToRight.Value = m.LeftToRight;
                if (_mRightToRight != null) _mRightToRight.Value = m.RightToRight;
            }
            finally
            {
                _loadingUi = false;
            }

            RefreshBadge();
        }

        private void CrossfeedCutoffSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            CrossfeedCutoffValue.Text = e.NewValue.ToString("0") + " Hz";
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            // 先落 Crossfeed（旧 dsp-extra.json 链路），再提交机架抛 Applied ——
            // 主窗口处理器会一次性把机架 + 声道平衡都推给引擎，顺序反了会用旧截止值。
            FlushCrossfeedCutoff();
            CommitRack("应用按钮");
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // 丢弃草稿，从磁盘重新加载（已应用的设置不受影响）
            LoadFromStore();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---------- 压缩量（GR）表 ----------

        private void EnsureGrTimer(bool enable)
        {
            if (enable && _grTimer == null)
            {
                DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    return;
                }

                _grTimer = queue.CreateTimer();
                _grTimer.Interval = TimeSpan.FromMilliseconds(250);
                _grTimer.Tick += (_, _) => UpdateGrMeter();
            }

            if (_grTimer == null)
            {
                return;
            }

            if (enable)
            {
                UpdateGrMeter();
                _grTimer.Start();
            }
            else
            {
                _grTimer.Stop();
                CompGrBar.Value = 0;
                CompGrText.Text = "--";
            }
        }

        private void UpdateGrMeter()
        {
            // 内核未创建（未播放）时桥接返回 0，安全；显示为 -- 表示当前无压缩
            float gr = DspCoreInterop.celeste_dsp_compressor_gr_db();
            if (gr <= 0.02f)
            {
                CompGrBar.Value = 0;
                CompGrText.Text = "--";
                return;
            }

            CompGrBar.Value = Math.Clamp(gr, 0, 24);
            CompGrText.Text = "-" + gr.ToString("0.0") + " dB";
        }

        // ---------- 格式化 ----------

        private static string FormatDb(double v) =>
            (v > 0.001 ? "+" : string.Empty) + v.ToString("0.0") + " dB";

        private static string FormatCoeff(double v) =>
            (v > 0.001 ? "+" : string.Empty) + v.ToString("0.00");
    }
}
