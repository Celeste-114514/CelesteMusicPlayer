using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        /// <summary>
        /// 按上次会话的文件夹或文件列表重新扫描，清空并替换当前音乐库展示。
        /// </summary>
        /// <summary>按媒体库设置过滤路径：移除缺失文件 / 忽略过短文件。</summary>
        private static string[] FilterLibraryPaths(IEnumerable<string> paths)
        {
            AppSettingsState s = AppSettingsStore.Load();
            var result = new List<string>();
            foreach (string path in paths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (s.RemoveMissingOnUpdate && !System.IO.File.Exists(path))
                {
                    continue;
                }

                if (s.IgnoreTooShortOnUpdate && s.FileTooShortSec > 0 && System.IO.File.Exists(path))
                {
                    try
                    {
                        using TagLib.File tagFile = TagLib.File.Create(path);
                        if (tagFile.Properties.Duration.TotalSeconds < s.FileTooShortSec)
                        {
                            continue;
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                result.Add(path);
            }

            return result.ToArray();
        }


        // ---------------- 音效处理 DSP 工作台（EQ / 声道平衡 / 安全限幅） ----------------

        private static readonly string[] AudioFxEqBandLabels =
        {
            "31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K"
        };


        private EqCurveState _audioFxEq = EqCurveState.Default();
        private int _audioFxEqSelected = -1;
        private bool _audioFxEqDragging;
        private bool _audioFxEqBuilt;
        private bool _audioFxLoading;
        // 音效面板是否已完成读写盘的加载。启动阶段(未真正进入面板)控件以 XAML 默认值
        // (限幅器 IsOn=True 等)加载会触发 Toggled/SelectionChanged，若此时允许 ApplyDspToEngine
        // 会把默认的"打开"状态保存到盘，覆盖用户上次关闭的设置 —— 必须用该标志屏蔽。
        private bool _audioFxPanelReady;

        /// <summary>首次进入音效面板时构建 EQ 滑杆与预设 / 单声道下拉。</summary>
        private void EnsureAudioFxUiBuilt()
        {
            if (_audioFxEqBuilt)
            {
                return;
            }

            _audioFxEqBuilt = true;

            // 曲线画布有尺寸后再绘制（首次进入在布局完成后重画）
            AudioFxEqCurveCanvas.SizeChanged -= AudioFxEqCurve_SizeChanged;
            AudioFxEqCurveCanvas.SizeChanged += AudioFxEqCurve_SizeChanged;

            if (AudioFxEqPresetCombo != null)
            {
                AudioFxEqPresetCombo.Items.Clear();
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "平坦", Tag = "flat" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "古典", Tag = "classical" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "流行", Tag = "pop" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "爵士", Tag = "jazz" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "摇滚", Tag = "rock" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "柔和", Tag = "soft" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "低音增强", Tag = "bass" });
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "custom" });
                // 用户预设（含分隔 + 各命名预设）
                var userPresets = EqUserPresetStore.Load();
                if (userPresets.Count > 0)
                {
                    AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "—— 我的预设 ——", IsEnabled = false });
                    foreach (var p in userPresets)
                    {
                        AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "★ " + (string.IsNullOrWhiteSpace(p.PresetName) ? "未命名" : p.PresetName), Tag = p.PresetId });
                    }
                }

                // 删除我的预设（长按/右键除名）
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "管理（删除）我的预设…", Tag = "manage" });
            }

            if (AudioFxEqBandTypeCombo != null)
            {
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "峰值 (Peak)", Tag = EqFilterType.Peaking });
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "低架 (Low Shelf)", Tag = EqFilterType.LowShelf });
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "高架 (High Shelf)", Tag = EqFilterType.HighShelf });
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "低通 (Low Pass)", Tag = EqFilterType.LowPass });
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "高通 (High Pass)", Tag = EqFilterType.HighPass });
                AudioFxEqBandTypeCombo.Items.Add(new ComboBoxItem { Content = "切除 (Notch)", Tag = EqFilterType.Notch });
            }

            if (AudioFxChannelMonoCombo != null)
            {
                AudioFxChannelMonoCombo.Items.Clear();
                AddMonoComboItem("off", "关闭（立体声）");
                AddMonoComboItem("left", "只用左声道");
                AddMonoComboItem("right", "只用右声道");
                AddMonoComboItem("sum", "左右求和");
            }

            AudioFxEqBandFreqSlider.Minimum = Math.Log10(20) / Math.Log10(2); // ~4.32 (log2)
            AudioFxEqBandFreqSlider.Maximum = Math.Log10(20000) / Math.Log10(2); // ~14.29

            if (AudioFxRgModeCombo != null)
            {
                AudioFxRgModeCombo.Items.Clear();
                AudioFxRgModeCombo.Items.Add(new ComboBoxItem { Content = "关闭", Tag = ReplayGainMode.Off });
                AudioFxRgModeCombo.Items.Add(new ComboBoxItem { Content = "单曲 (Track)", Tag = ReplayGainMode.Track });
                AudioFxRgModeCombo.Items.Add(new ComboBoxItem { Content = "专辑 (Album)", Tag = ReplayGainMode.Album });
            }
        }


        /// <summary>应用 OPRA 耳机校正曲线到播放器 EQ。</summary>
        internal void ApplyOpraCurve(EqCurveState curve) => ApplyEqCurveToPlayer(curve);

        /// <summary>把一条曲线应用到播放器（曲线状态 + 持久化 + 面板同步 + DSP 实时生效 + 链路显示）。
        /// OPRA 耳机校正、Equalizer APO 导入等「外部曲线入口」共用这一条路径。</summary>
        internal void ApplyEqCurveToPlayer(EqCurveState curve)
        {
            if (curve == null)
            {
                return;
            }

            curve.Enabled = true;
            _audioFxEq = curve;
            EqCurveStore.Save(curve);

            // 若音效面板已构建，同步其 EQ 显示（打开 OPRA 面板前通常已打开音效工作台）。
            if (_audioFxEqBuilt)
            {
                try
                {
                    AudioFxEqEnableToggle.IsOn = true;
                    AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatAudioFxDb(curve.PreampDb) + " dB";
                    SelectAudioFxEqPreset(curve.PresetId);
                    SelectAudioFxEqBand(_audioFxEqSelected);
                    RedrawAudioFxEqCurve();
                    RefreshAudioFxEqBandEditor();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            _audioEngine?.SetEqCurve(curve);
            UpdateDspBitPerfectUi();
            UpdateSignalChainDisplay();
            StartupLog.Write("EQ 曲线已应用: " + curve.PresetId + " bands=" + (curve.Bands?.Count ?? 0) + " preamp=" + curve.PreampDb);
        }

        /// <summary>应用房间校正（卷积 FIR）状态到引擎 + 刷新 bit-perfect 提示 + 链路显示。
        /// 由 RoomCorrectionWindow 调用（播放中实时生效）。</summary>
        internal void ApplyRoomCorrection(RoomCorrectionState state)
        {
            if (state == null)
            {
                return;
            }

            _audioEngine?.SetRoomCorrection(state);
            UpdateDspBitPerfectUi();
            UpdateSignalChainDisplay();
            StartupLog.Write($"房间校正已应用: enabled={state.Enabled} ir={state.IrPath} gain={state.GainDb}dB");
        }


        private void SelectAudioFxEqPreset(string presetId)
        {
            for (int i = 0; i < AudioFxEqPresetCombo.Items.Count; i++)
            {
                if (AudioFxEqPresetCombo.Items[i] is ComboBoxItem { Tag: string t } && string.Equals(t, presetId, StringComparison.Ordinal))
                {
                    AudioFxEqPresetCombo.SelectedIndex = i;
                    return;
                }
            }

            AudioFxEqPresetCombo.SelectedIndex = 0;
        }


        private async void AudioFxEqPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioFxLoading || AudioFxEqPresetCombo.SelectedItem is not ComboBoxItem { Tag: string presetId })
            {
                return;
            }

            // "管理（删除）我的预设…"入口
            if (string.Equals(presetId, "manage", StringComparison.Ordinal))
            {
                // 复位到当前实际预设，避免下拉停在管理项上
                _audioFxLoading = true;
                try { SelectAudioFxEqPreset(string.IsNullOrWhiteSpace(_audioFxEq?.PresetId) ? "flat" : _audioFxEq.PresetId); }
                finally { _audioFxLoading = false; }
                await DeleteAudioFxUserPresetFlow();
                return;
            }

            _audioFxLoading = true;
            try
            {
                if (presetId.StartsWith("user:", StringComparison.Ordinal))
                {
                    // 用户预设：从持久化加载完整曲线
                    var user = EqUserPresetStore.FindById(presetId);
                    if (user != null)
                    {
                        _audioFxEq = user;
                        _audioFxEq.Enabled = AudioFxEqEnableToggle.IsOn;
                    }
                }
                else if (!string.Equals(presetId, "custom", StringComparison.Ordinal)
                         && !string.Equals(presetId, "simple", StringComparison.Ordinal))
                {
                    _audioFxEq = EqCurveState.CreatePreset(presetId);
                    _audioFxEq.Enabled = AudioFxEqEnableToggle.IsOn;
                }
                else
                {
                    var cur = EqCurveStore.Load();
                    _audioFxEq = cur;
                    // 简单模式曲线保留盘上原值（勿改 id/名，避免后续按 custom fallback 误判）
                    if (!string.Equals(presetId, "simple", StringComparison.Ordinal))
                    {
                        _audioFxEq.PresetId = "custom";
                        _audioFxEq.PresetName = "自定义";
                    }
                }

                SyncAudioFxEqSimpleFromState();
                SelectAudioFxEqBand(_audioFxEq.Bands.Count > 0 ? 0 : -1);
                RedrawAudioFxEqCurve();
                RefreshAudioFxEqBandEditor();
            }
            finally
            {
                _audioFxLoading = false;
            }

            ApplyDspToEngine();
        }


        // ---- EQ 曲线绘制 ----

        private const double EqCurveFreqMin = 20.0, EqCurveFreqMax = 20000.0;
        private const double EqCurveGainMax = 24.0;

        private static double Log2(double v) => Math.Log(v) / Math.Log(2.0);

        private void AudioFxEqCurve_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_audioFxEqBuilt) RedrawAudioFxEqCurve();
        }


        private void RedrawAudioFxEqCurve()
        {
            if (AudioFxEqCurveCanvas == null || AudioFxEqCurveCanvas.ActualWidth <= 0 || AudioFxEqCurveCanvas.ActualHeight <= 0)
            {
                return;
            }

            double w = AudioFxEqCurveCanvas.ActualWidth;
            double h = AudioFxEqCurveCanvas.ActualHeight;
            AudioFxEqCurveCanvas.Children.Clear();

            double pad = 12;
            double plotW = w - pad * 2;
            double plotH = h - pad * 2;
            double midY = pad + plotH / 2;
            double gainPerH = plotH / (2 * EqCurveGainMax);
            double logMin = Log2(EqCurveFreqMin), logMax = Log2(EqCurveFreqMax);

            double X(double freq) => pad + (Log2(freq) - logMin) / (logMax - logMin) * plotW;
            double Y(double gain) => midY - gain * gainPerH;

            // 0dB 参考线
            var zeroLine = new Shapes.Line
            {
                X1 = pad, Y1 = midY, X2 = pad + plotW, Y2 = midY,
                Stroke = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                StrokeThickness = 1
            };
            AudioFxEqCurveCanvas.Children.Add(zeroLine);

            // 频率网线（对数刻度标记）
            double[] gridFreqs = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
            foreach (double f in gridFreqs)
            {
                double gx = X(f);
                AudioFxEqCurveCanvas.Children.Add(new Shapes.Line
                {
                    X1 = gx, Y1 = pad, X2 = gx, Y2 = pad + plotH,
                    Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    StrokeThickness = 1
                });
            }

            // 曲线：对频率均匀取点做幅度求和近似（对数频率轴）
            if (_audioFxEq.Bands.Count > 0)
            {
                var points = new List<Windows.Foundation.Point>();
                int samples = Math.Max(120, (int)(plotW / 3));
                for (int i = 0; i <= samples; i++)
                {
                    double f = EqCurveFreqMin * Math.Pow(EqCurveFreqMax / EqCurveFreqMin, (double)i / samples);
                    double g = 0;
                    foreach (var b in _audioFxEq.Bands)
                    {
                        if (b is not { Enabled: true }) continue;
                        g += ApproxBandGain(f, b);
                    }

                    points.Add(new Windows.Foundation.Point(X(f), Math.Clamp(Y(g), pad, pad + plotH)));
                }

                if (points.Count > 1)
                {
                    var pg = new PathGeometry();
                    var fig = new PathFigure { StartPoint = points[0], IsClosed = false };
                    for (int i = 1; i < points.Count; i++) fig.Segments.Add(new LineSegment { Point = points[i] });
                    pg.Figures.Add(fig);
                    AudioFxEqCurveCanvas.Children.Add(new Shapes.Path
                    {
                        Data = pg,
                        Stroke = new SolidColorBrush(Color.FromArgb(230, 0, 140, 255)),
                        StrokeThickness = 2
                    });
                }
            }

            // band 拖点
            for (int i = 0; i < _audioFxEq.Bands.Count; i++)
            {
                var b = _audioFxEq.Bands[i];
                double bx = X(b.FrequencyHz);
                double by = Y(b.GainDb);
                bool sel = i == _audioFxEqSelected;
                var dot = new Border
                {
                    Width = sel ? 18 : 14,
                    Height = sel ? 18 : 14,
                    CornerRadius = new CornerRadius(9),
                    Background = b.Enabled
                        ? new SolidColorBrush(sel ? Color.FromArgb(255, 0, 180, 255) : Color.FromArgb(230, 70, 130, 180))
                        : new SolidColorBrush(Color.FromArgb(90, 140, 140, 140)),
                    Opacity = b.Enabled ? 1 : 0.5,
                    Tag = i
                };
                Canvas.SetLeft(dot, bx - dot.Width / 2);
                Canvas.SetTop(dot, by - dot.Height / 2);
                AudioFxEqCurveCanvas.Children.Add(dot);
            }
        }


        /// <summary>单段在给定频率处的近似幅度贡献（dB）。峰值/架近似用理想带响应，低通/高通/切除按阶近似。</summary>
        private static double ApproxBandGain(double f, EqBand b)
        {
            switch (b.FilterType)
            {
                case EqFilterType.LowPass:
                    var cutoff = Math.Max(20, b.FrequencyHz);
                    if (f >= cutoff) { double x = (f / cutoff); return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                case EqFilterType.HighPass:
                    var hc = Math.Max(20, b.FrequencyHz);
                    if (f <= hc) { double x = (hc / f); return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                case EqFilterType.Notch:
                    double dNotch = Math.Abs(Log2(f / b.FrequencyHz));
                    double n = b.Q <= 0 ? 1 : b.Q;
                    if (dNotch <= 0.5 / n) return -6 * Math.Min(1, (0.5 / n - dNotch) * n * 2);
                    return 0;
                case EqFilterType.LowShelf:
                case EqFilterType.HighShelf:
                {
                    double gain = Math.Clamp(b.GainDb, -24, 24);
                    double pivot = b.FilterType == EqFilterType.LowShelf ? 200 : 4000;
                    double dPivot = Math.Abs(Log2(f / pivot));
                    // 简化搁架曲线：靠近目标频段趋近 gain
                    double reach = b.FilterType == EqFilterType.LowShelf ? (f < pivot ? 0 : dPivot) : (f > pivot ? 0 : dPivot);
                    double ratio = Math.Clamp(reach > 0 ? 1.0 - 0.05 : 1.0, 0, 1);
                    // 用频率偏移近似：目标频段外逐渐累积到 gain
                    double extent = Math.Abs(Log2(f / b.FrequencyHz));
                    return gain * Math.Clamp(1.0 / (1.0 + 0.5 * extent), 0.2, 1.0) * ratio;
                }
                default: // Peaking / 其它
                {
                    if (Math.Abs(b.GainDb) < 0.01) return 0;
                    double w = b.FrequencyHz;
                    double x = Math.Log10(f / w) * 6; // 以 octave 计
                    double q = Math.Max(0.1, b.Q);
                    // 峰值带宽 ~ f/Q，用高斯近似
                    double sigma = q > 0 ? 0.7 * (1.0 / q) : 0.6; // octaves
                    return b.GainDb * Math.Exp(-(x * x) / (2 * sigma * sigma));
                }
            }
        }


        // ---- 曲线交互 ----

        private static readonly double EqCurveMinLog2 = Log2(EqCurveFreqMin);
        private static readonly double EqCurveMaxLog2 = Log2(EqCurveFreqMax);

        private void AudioFxEqCurve_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(AudioFxEqCurveCanvas).Position;
            // 优先命中已有拖点（半径 20px）
            for (int i = 0; i < _audioFxEq.Bands.Count; i++)
            {
                double bx = EqFreqToX(_audioFxEq.Bands[i].FrequencyHz);
                double by = EqGainToY(_audioFxEq.Bands[i].GainDb);
                double dx = pos.X - bx, dy = pos.Y - by;
                if (dx * dx + dy * dy <= 20 * 20)
                {
                    SelectAudioFxEqBand(i);
                    _audioFxEqDragging = true;
                    AudioFxEqCurveCanvas.CapturePointer(e.Pointer);
                    return;
                }
            }

            // 空白点：新增段并选中（不立即创建直到拖动，避免误触）
            _audioFxEqSelected = -1;
            SelectAudioFxEqBand(-1);
        }


        private void AudioFxEqCurve_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(AudioFxEqCurveCanvas).Position;

            if (_audioFxEqDragging && _audioFxEqSelected >= 0 && _audioFxEqSelected < _audioFxEq.Bands.Count)
            {
                var b = _audioFxEq.Bands[_audioFxEqSelected];
                double nf = EqXToFreq(Math.Clamp(pos.X, 0, AudioFxEqCurveCanvas.ActualWidth - 1));
                double ng = ClampGain(EqYToGain(Math.Clamp(pos.Y, 0, AudioFxEqCurveCanvas.ActualHeight - 1)));
                b.FrequencyHz = nf;
                b.GainDb = ng;
                b.Enabled = Math.Abs(ng) > 0.01;
                MarkAudioFxEqCustom();
                SyncBandEditorFromState();
                RedrawAudioFxEqCurve();
                ApplyDspToEngine();
            }
            else if (AudioFxEqCurveCanvas.ActualWidth > 0)
            {
                // 悬停：可选预选
            }
        }


        private void AudioFxEqCurve_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_audioFxEqDragging)
            {
                _audioFxEqDragging = false;
                try { AudioFxEqCurveCanvas.ReleasePointerCapture(e.Pointer); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void MarkAudioFxEqCustom()
        {
            _audioFxEq.PresetId = "custom";
            _audioFxEq.PresetName = "自定义";
            _audioFxLoading = true;
            try { SelectAudioFxEqPreset("custom"); }
            finally { _audioFxLoading = false; }
        }


        private double EqFreqToX(double freq)
        {
            double w = AudioFxEqCurveCanvas.ActualWidth, h = AudioFxEqCurveCanvas.ActualHeight;
            double pad = 12, plotW = w - 24, plotH = h - 24;
            return pad + (Log2(freq) - EqCurveMinLog2) / (EqCurveMaxLog2 - EqCurveMinLog2) * plotW;
        }


        private double EqGainToY(double gain)
        {
            double h = AudioFxEqCurveCanvas.ActualHeight;
            double pad = 12, plotH = h - 24;
            double midY = pad + plotH / 2;
            return midY - gain * (plotH / (2 * EqCurveGainMax));
        }


        private double EqXToFreq(double x)
        {
            double w = AudioFxEqCurveCanvas.ActualWidth, h = AudioFxEqCurveCanvas.ActualHeight;
            double pad = 12, plotW = w - 24;
            double t = Math.Clamp((x - pad) / (plotW > 0 ? plotW : 1), 0, 1);
            return EqCurveFreqMin * Math.Pow(EqCurveFreqMax / EqCurveFreqMin, t);
        }


        private double EqYToGain(double y)
        {
            double h = AudioFxEqCurveCanvas.ActualHeight;
            double pad = 12, plotH = h - 24;
            double midY = pad + plotH / 2;
            return ClampGain((midY - y) * (2 * EqCurveGainMax) / (plotH > 0 ? plotH : 1));
        }


        private static double ClampGain(double g) => Math.Clamp(g, -EqCurveGainMax, EqCurveGainMax);

        // ---- 选中段 / 编辑器 ----

        private void SelectAudioFxEqBand(int index)
        {
            _audioFxEqSelected = index;
            SyncBandEditorFromState();
        }


        private void SyncBandEditorFromState()
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count)
            {
                AudioFxEqBandEditor.Visibility = Visibility.Collapsed;
                return;
            }

            AudioFxEqBandEditor.Visibility = AudioFxEqModeRadio.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            var b = _audioFxEq.Bands[_audioFxEqSelected];
            _audioFxLoading = true;
            try
            {
                SelectAudioFxEqBandType(b.FilterType);
                AudioFxEqBandFreqSlider.Value = Log2(b.FrequencyHz < 20 ? 20 : b.FrequencyHz > 20000 ? 20000 : b.FrequencyHz);
                AudioFxEqBandGainSlider.Value = Math.Clamp(b.GainDb, -24, 24);
                AudioFxEqBandQSlider.Value = Math.Clamp(b.Q, 0.1, 24);
                AudioFxEqBandEnableToggle.IsOn = b.Enabled;
                AudioFxEqBandFreqLabel.Text = FormatAudioFxFreq(b.FrequencyHz);
                AudioFxEqBandGainLabel.Text = FormatAudioFxDb(b.GainDb) + " dB";
                AudioFxEqBandQLabel.Text = b.Q.ToString("0.##");
            }
            finally
            {
                _audioFxLoading = false;
            }
        }


        private void RefreshAudioFxEqBandEditor()
        {
            SyncBandEditorFromState();
            // 更新 preamp 显示
            if (AudioFxEqPreampText != null)
            {
                AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatAudioFxDb(_audioFxEq.PreampDb) + " dB";
            }
        }


        private static string FormatAudioFxFreq(double f)
        {
            if (f >= 1000) return (f / 1000.0).ToString("0.##") + " kHz";
            return f.ToString("0") + " Hz";
        }


        private void SelectAudioFxEqBandType(EqFilterType type)
        {
            for (int i = 0; i < AudioFxEqBandTypeCombo.Items.Count; i++)
            {
                if (AudioFxEqBandTypeCombo.Items[i] is ComboBoxItem { Tag: EqFilterType t } && t == type)
                {
                    AudioFxEqBandTypeCombo.SelectedIndex = i;
                    return;
                }
            }
        }


        private void AudioFxEqBandType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count
                || AudioFxEqBandTypeCombo.SelectedItem is not ComboBoxItem { Tag: EqFilterType t })
            {
                return;
            }

            _audioFxEq.Bands[_audioFxEqSelected].FilterType = t;
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandFreq_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count) return;
            var b = _audioFxEq.Bands[_audioFxEqSelected];
            b.FrequencyHz = Math.Clamp(Math.Pow(2, e.NewValue), 20, 20000);
            AudioFxEqBandFreqLabel.Text = FormatAudioFxFreq(b.FrequencyHz);
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandGain_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count) return;
            var b = _audioFxEq.Bands[_audioFxEqSelected];
            b.GainDb = e.NewValue;
            b.Enabled = Math.Abs(b.GainDb) > 0.01;
            AudioFxEqBandGainLabel.Text = FormatAudioFxDb(b.GainDb) + " dB";
            MarkAudioFxEqCustom();
            SyncBandEditorFromState();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandQ_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count) return;
            var b = _audioFxEq.Bands[_audioFxEqSelected];
            b.Q = e.NewValue;
            AudioFxEqBandQLabel.Text = b.Q.ToString("0.##");
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandEnable_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count) return;
            _audioFxEq.Bands[_audioFxEqSelected].Enabled = AudioFxEqBandEnableToggle.IsOn;
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= _audioFxEq.Bands.Count) return;
            _audioFxEq.Bands.RemoveAt(_audioFxEqSelected);
            _audioFxEqSelected = _audioFxEq.Bands.Count > 0 ? Math.Min(_audioFxEqSelected, _audioFxEq.Bands.Count - 1) : -1;
            if (_audioFxEq.Bands.Count == 0) _audioFxEq.Bands.Add(new EqBand());
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        private void AudioFxEqAddBand_Click(object sender, RoutedEventArgs e)
        {
            double freq = 1000;
            if (_audioFxEqSelected >= 0 && _audioFxEqSelected < _audioFxEq.Bands.Count)
            {
                freq = Math.Clamp(_audioFxEq.Bands[_audioFxEqSelected].FrequencyHz * 2, 20, 20000);
            }

            _audioFxEq.Bands.Add(new EqBand { Enabled = true, FrequencyHz = freq, GainDb = 0, Q = 1.0, FilterType = EqFilterType.Peaking });
            _audioFxEqSelected = _audioFxEq.Bands.Count - 1;
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        // ---- EQ 开关 / 模式 ----

        private void AudioFxEqEnable_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioFxLoading) return;
            _audioFxEq.Enabled = AudioFxEqEnableToggle.IsOn;
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        private void AudioFxEqMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool pro = AudioFxEqModeRadio.SelectedIndex == 0;
            AudioFxEqBandEditor.Visibility = pro && _audioFxEqSelected >= 0 ? Visibility.Visible : Visibility.Collapsed;
            AudioFxEqSimplePanel.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        }


        // ---- 简单模式 ----
        private double _eqSimpleBass, _eqSimpleVocal, _eqSimpleAir, _eqSimpleWarm;

        private void AudioFxEqSimple_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading) return;
            _eqSimpleBass = AudioFxEqSimpleBassSlider.Value;
            _eqSimpleVocal = AudioFxEqSimpleVocalSlider.Value;
            _eqSimpleAir = AudioFxEqSimpleAirSlider.Value;
            _eqSimpleWarm = AudioFxEqSimpleWarmSlider.Value;
            ApplySimpleTones();
        }


        private void AudioFxEqSimpleFlat_Click(object sender, RoutedEventArgs e)
        {
            _audioFxLoading = true;
            try
            {
                _eqSimpleBass = _eqSimpleVocal = _eqSimpleAir = _eqSimpleWarm = 0;
                AudioFxEqSimpleBassSlider.Value = 0;
                AudioFxEqSimpleVocalSlider.Value = 0;
                AudioFxEqSimpleAirSlider.Value = 0;
                AudioFxEqSimpleWarmSlider.Value = 0;
            }
            finally { _audioFxLoading = false; }

            ApplySimpleTones();
        }


        private static void AppendSimple(EqCurveState s, string tone, double strength)
        {
            double[] fr = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            foreach (double f in fr)
            {
                double g = 0;
                if (tone == "bass")
                {
                    if (f <= 80) g += 2.5 * strength;
                    else if (f <= 160) g += 1.6 * strength;
                    else if (f <= 315) g += 0.7 * strength;
                    else if (f >= 10000) g += -0.4 * strength;
                }
                else if (tone == "vocal")
                {
                    if (f >= 800 && f <= 2500) g += 1.7 * strength;
                    else if (f >= 315 && f < 800) g += 0.7 * strength;
                    else if (f >= 5000 && f <= 8000) g += -0.8 * strength;
                    else if (f <= 80) g += -0.4 * strength;
                }
                else if (tone == "air")
                {
                    if (f >= 10000) g += 2.0 * strength;
                    else if (f >= 5000) g += 1.1 * strength;
                    else if (f <= 160) g += -0.5 * strength;
                }
                else if (tone == "warm")
                {
                    if (f <= 125) g += 1.4 * strength;
                    else if (f >= 4000) g += -0.9 * strength;
                    else if (f >= 250 && f <= 1000) g += 0.4 * strength;
                }

                g = Math.Round(Math.Clamp(g, -12, 12) * 10) / 10;
                s.Bands.Add(new EqBand { Enabled = Math.Abs(g) > 0.01, FrequencyHz = f, GainDb = g, Q = 1.0, FilterType = EqFilterType.Peaking });
            }
        }


        /// <param name="restoreFromStore">true=从持久化恢复上次滑块值；false=清空（用于重置按钮）。</param>
        private void SyncAudioFxEqSimpleFromState(bool restoreFromStore = true)
        {
            // 简单模式各滑块值：默认从持久化恢复（避免重启后回到 0），重置时清空。
            _audioFxLoading = true;
            try
            {
                if (restoreFromStore)
                {
                    var t = SimpleEqStore.Load();
                    _eqSimpleBass = t.Bass;
                    _eqSimpleVocal = t.Vocal;
                    _eqSimpleAir = t.Air;
                    _eqSimpleWarm = t.Warm;
                }
                else
                {
                    _eqSimpleBass = _eqSimpleVocal = _eqSimpleAir = _eqSimpleWarm = 0;
                    SimpleEqStore.Save(new SimpleEqState());
                }

                AudioFxEqSimpleBassSlider.Value = _eqSimpleBass;
                AudioFxEqSimpleVocalSlider.Value = _eqSimpleVocal;
                AudioFxEqSimpleAirSlider.Value = _eqSimpleAir;
                AudioFxEqSimpleWarmSlider.Value = _eqSimpleWarm;
            }
            finally { _audioFxLoading = false; }
        }


        // ---- 自动增益 ----

        /// <summary>保存当前曲线为命名用户预设（可覆盖同名）。</summary>
        private async void AudioFxEqSavePreset_Click(object sender, RoutedEventArgs e)
        {
            var nameBox = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = "", // 当前名称留空让用户默认自定义
                PlaceholderText = "输入预设名称",
                Width = 280,
                SelectionStart = 0
            };
            var dialog = new ContentDialog
            {
                Title = "保存 EQ 预设",
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "把当前曲线保存为我的预设", FontSize = 12, Opacity = 0.7 }, nameBox } },
                XamlRoot = this.Content?.XamlRoot ?? AudioFxBorder.XamlRoot
            };
            ContentDialogResult r;
            try
            {
                r = await dialog.ShowAsync();
            }
            catch
            {
                r = ContentDialogResult.None;
            }

            if (r != ContentDialogResult.Primary)
            {
                return;
            }

            string name = (nameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                NowPlayingText.Text = "预设名称不能为空，未保存";
                return;
            }

            var toSave = _audioFxEq.Clone();
            toSave.PresetName = name.Length > 40 ? name.Substring(0, 40) : name;
            toSave.PresetId = "user:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string newId = EqUserPresetStore.Upsert(toSave);

            // 刷新下拉并在列表中选择刚保存的预设
            RefreshAudioFxUserPresetItems();
            SelectAudioFxEqPreset(newId);
            NowPlayingText.Text = "EQ 预设已保存：" + toSave.PresetName;
        }


        private void AudioFxEqAutoGain_Click(object sender, RoutedEventArgs e)
        {
            // 估算峰值叠加增益：所有 band 在任一频率的最大正贡献 + headroom
            double peak = 0;
            double logMin = Log2(EqCurveFreqMin), logMax = Log2(EqCurveFreqMax);
            double dAddFreq = Math.Pow(EqCurveFreqMax / EqCurveFreqMin, 1.0 / 200.0);
            double f = EqCurveFreqMin;
            for (int i = 0; i <= 200; i++)
            {
                double g = 0;
                foreach (var b in _audioFxEq.Bands) { if (b is { Enabled: true }) g += ApproxBandGain(f, b); }
                peak = Math.Max(peak, g);
                f *= dAddFreq;
            }

            double preampDb = -Math.Max(0, peak);
            _audioFxEq.PreampDb = Math.Clamp(preampDb, -24, 24);
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
            NowPlayingText.Text = "自动增益：preamp = " + FormatAudioFxDb(_audioFxEq.PreampDb) + " dB";
        }


        private void AudioFxEqReset_Click(object sender, RoutedEventArgs e)
        {
            _audioFxLoading = true;
            try
            {
                _audioFxEq = EqCurveState.CreatePreset("flat");
                AudioFxEqEnableToggle.IsOn = false;
                _audioFxEq.Enabled = false;
                AudioFxEqPreampText.Text = "预增益 (preamp)：0.0 dB";
                SelectAudioFxEqPreset("flat");
                _audioFxEqSelected = _audioFxEq.Bands.Count > 0 ? 0 : -1;
                SyncAudioFxEqSimpleFromState(restoreFromStore: false);
            }
            finally
            {
                _audioFxLoading = false;
            }

            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
            NowPlayingText.Text = "均衡器已重置";
        }


        private void AudioFxChannel_Toggled(object sender, RoutedEventArgs e) => ApplyDspToEngine();

        private void AudioFxChannelSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxSafetyLimiter_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxRgPreamp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading) return;
            AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatAudioFxDb(e.NewValue);
            ApplyReplayGainToEngine();
        }


        /// <summary>收集面板当前状态 → 持久化 + 应用到播放引擎。</summary>
        private void ApplyDspToEngine()
        {
            // 面板未就绪（启动阶段控件以 XAML 默认值加载触发的 Toggled 等）不得持久化/应用，
            // 否则会用默认的“打开”状态覆盖盘上用户上次关闭的设置，导致“关闭后重启又打开”。
            if (!_audioFxPanelReady)
            {
                return;
            }

            // DSP 面板 EQ：曲线状态持久化 + 应用到引擎（HiFi 输出，各输出模式均走统一 DSP 链）
            EqCurveStore.Save(_audioFxEq);

            // 诊断：记录触发保存的调用来源，便于定位“关闭后又变回打开”是被谁触发的。
            string trigger = "";
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.GetFrames()?.Length; i++)
                {
                    System.Reflection.MethodBase? m = st.GetFrame(i)?.GetMethod();
                    string? decl = m?.DeclaringType?.Name;
                    if (decl != null && decl != "MainWindow" && decl != "AppWindow" && !decl.StartsWith("<>c"))
                    {
                        trigger = decl + "." + m.Name;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(trigger) && st.GetFrames()?.Length > 0)
                {
                    System.Reflection.MethodBase? m0 = st.GetFrame(0)?.GetMethod();
                    trigger = m0?.DeclaringType?.Name + "." + m0?.Name;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            var ch = new ChannelBalanceState
            {
                Enabled = AudioFxChannelToggle.IsOn,
                Balance = AudioFxChannelBalanceSlider.Value,
                LeftGainDb = AudioFxChannelLeftGainSlider.Value,
                RightGainDb = AudioFxChannelRightGainSlider.Value,
                SwapChannels = AudioFxChannelSwapToggle.IsOn,
                InvertLeft = AudioFxChannelInvertLToggle.IsOn,
                InvertRight = AudioFxChannelInvertRToggle.IsOn,
                MonoMode = CurrentAudioFxMonoMode()
            };

            var safety = new DspSafetyState
            {
                HeadroomDb = AudioFxSafetyHeadroomSlider.Value,
                EnableLimiter = AudioFxSafetyLimiterToggle.IsOn
            };

            DspExtraStore.Save(new DspExtraState { ChannelBalance = ch, Safety = safety });
            StartupLog.Write($"[DSP] 已保存 eq(Enabled={_audioFxEq.Enabled},bands={_audioFxEq.Bands.Count}) ch(Enabled={ch.Enabled}) limiter={safety.EnableLimiter}  触发={trigger}");

            _audioEngine?.SetEqCurve(_audioFxEq);
            _audioEngine?.SetChannelBalance(ch);
            _audioEngine?.SetSafety(safety);
            _audioEngine?.SetRoomCorrection(RoomCorrectionStore.Load());

            UpdateDspBitPerfectUi();
        }


        /// <summary>更新 DSP 激活状态提示：任一 DSP 生效 → 非 bit-perfect。</summary>
        private void UpdateDspBitPerfectUi()
        {
            if (AudioFxBitPerfectStatusText == null)
            {
                return;
            }

            bool eqActive = _audioFxEq != null && _audioFxEq.HasEffect();
            bool active = eqActive
                || AudioFxChannelToggle.IsOn
                || Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01
                || AudioFxSafetyLimiterToggle.IsOn
                || RoomCorrectionStore.Load().Enabled;

            AudioFxBitPerfectStatusText.Text = active ? "非 bit-perfect（已使用 DSP）" : "bit-perfect 直通";
            // 主界面信息条（左上角）提示：使用 DSP 时输出非 bit-perfect
            if (_currentCategory == "AudioFX")
            {
                NowPlayingText.Text = active
                    ? "⚠ 使用 DSP（EQ/声道平衡/限幅）→ 输出非 bit-perfect"
                    : "音效处理：全部关闭 → bit-perfect 直通";
            }
        }


        private static bool LibraryNavStatesEqual(LibraryNavState a, LibraryNavState b)
            => string.Equals(a.Category, b.Category, StringComparison.Ordinal)
               && string.Equals(a.ArtistName, b.ArtistName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AlbumName, b.AlbumName, StringComparison.OrdinalIgnoreCase)
               && a.AlbumFromArtist == b.AlbumFromArtist
               && a.UsesAlbumArtist == b.UsesAlbumArtist;

        /// <summary>执行会改变中间界面的导航，并写入后退栈（清空前进栈）。</summary>
        private void CommitLibraryNavigation(Action navigate)
        {
            if (_suppressNavHistory)
            {
                navigate();
                _navCurrent = CaptureLibraryNavState();
                return;
            }

            LibraryNavState before = _navCurrent ?? CaptureLibraryNavState();
            navigate();
            LibraryNavState after = CaptureLibraryNavState();
            if (!LibraryNavStatesEqual(before, after))
            {
                _navBackStack.Add(before);
                _navForwardStack.Clear();
            }

            _navCurrent = after;
        }


        private void ApplyAlbumsSearchFilter()
        {
            if (_currentCategory != "Albums" || AlbumGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(AlbumGridView.ItemsSource, _albums))
                {
                    AlbumGridView.ItemsSource = _albums;
                }

                RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
                return;
            }

            List<AlbumEntry> filtered = _albums
                .Where(a =>
                    ContainsIgnoreCase(a.Name, q)
                    || ContainsIgnoreCase(a.Artist, q))
                .ToList();

            AlbumGridView.ItemsSource = filtered;
            RefreshAlbumWallSelectionChrome(AlbumGridView, filtered);
        }


        private void ApplyArtistsSearchFilter()
        {
            if ((_currentCategory != "Artists" && _currentCategory != "AlbumArtists") || ArtistGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(ArtistGridView.ItemsSource, _artists))
                {
                    ArtistGridView.ItemsSource = _artists;
                }

                return;
            }

            List<ArtistEntry> filtered = _artists
                .Where(a => ContainsIgnoreCase(a.Name, q))
                .ToList();

            ArtistGridView.ItemsSource = filtered;
        }


        private ObservableCollection<AlbumEntry> GetAlbumCollectionForGrid(GridView grid)
            => ReferenceEquals(grid, AlbumGridView) ? _albums : _artistAlbums;

        private ListView? ResolveMultiSelectTargetList()
        {
            if (PlaylistDetailBorder.Visibility == Visibility.Visible
                && PlaylistDetailListView != null)
            {
                return PlaylistDetailListView;
            }

            if (AlbumDetailPanel.Visibility == Visibility.Visible
                && AlbumTrackListView != null)
            {
                return AlbumTrackListView;
            }

            if (ArtistDetailPanel.Visibility == Visibility.Visible
                && ArtistTrackListView != null)
            {
                return ArtistTrackListView;
            }

            if (PlaylistListBorder.Visibility == Visibility.Visible)
            {
                return PlaylistView;
            }

            // 标签排序板块：面板曲目视角（Songs）时多选针对该列表
            if (string.Equals(_currentCategory, "TagSort", StringComparison.Ordinal)
                && _tagSortPanelMode == "Songs"
                && TagSortPanelSongListView != null
                && TagSortPanelSongListView.Visibility == Visibility.Visible)
            {
                return TagSortPanelSongListView;
            }

            return null;
        }


        private async Task HandleCloseRequestAsync()
        {
            if (_closePromptOpen)
            {
                return;
            }

            _closePromptOpen = true;
            try
            {
                AppClosePreferencesState prefs = AppClosePreferences.Load();
                CloseWindowAction action = AppClosePreferences.ResolveAction(prefs);
                if (action == CloseWindowAction.Ask)
                {
                    action = await ShowCloseChoiceDialogAsync();
                }

                switch (action)
                {
                    case CloseWindowAction.MinimizeToTray:
                        MinimizeToTray();
                        break;
                    case CloseWindowAction.Exit:
                        ExitApplication();
                        break;
                }
            }
            finally
            {
                _closePromptOpen = false;
            }
        }
    }
}
