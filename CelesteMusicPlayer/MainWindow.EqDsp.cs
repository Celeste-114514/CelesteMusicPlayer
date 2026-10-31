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
using Windows.UI.Core;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        /// <summary>
        /// 按上次会话的文件夹或文件列表重新扫描，清空并替换当前音乐库展示。
        /// </summary>
        /// <summary>按媒体库设置过滤路径：移除缺失文件 / 忽略过短文件 / 剔除内部缓存产物。</summary>
        private static string[] FilterLibraryPaths(IEnumerable<string> paths)
        {
            AppSettingsState s = AppSettingsStore.Load();
            var result = new List<string>();
            foreach (string path in paths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // 内部缓存产物（DSD DoP 缓存 / 转码缓存 / WebDAV 下载缓存）不是本地曲库内容，永不进媒体库
                if (LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    continue;
                }

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
            "32", "64", "125", "250", "500", "1K", "2K", "4K", "8K", "16K"
        };


        private bool _audioFxEqDragging;
        // 外部曲线（耳机校正 opra-* / Equalizer APO 导入等）在预设下拉里的动态展示项：
        // 按需创建、挂在「管理（删除）我的预设…」之前，选中它纯展示、不切预设
        // （守卫见 AudioFxEqPreset_SelectionChanged 顶部）。
        private ComboBoxItem? _audioFxEqExternalItem;
        // 音效面板是否已完成读写盘的加载。启动阶段(未真正进入面板)控件以 XAML 默认值
        // (限幅器 IsOn=True 等)加载会触发 Toggled/SelectionChanged，若此时允许 ApplyDspToEngine
        // 会把默认的"打开"状态保存到盘，覆盖用户上次关闭的设置 —— 必须用该标志屏蔽。


        private void SelectAudioFxEqPreset(string presetId)
        {
            for (int i = 0; i < AudioFxEqPresetCombo.Items.Count; i++)
            {
                if (AudioFxEqPresetCombo.Items[i] is ComboBoxItem { Tag: string t } && string.Equals(t, presetId, StringComparison.Ordinal))
                {
                    // 回到固定预设：摘掉动态外部项，免得它变成「点了没反应、名字还对不上」的死项
                    DropAudioFxEqExternalItem();
                    AudioFxEqPresetCombo.SelectedIndex = i;
                    return;
                }
            }

            // 固定项里没有这个 id：外部曲线（耳机校正 / APO 导入等）动态挂一项，让下拉显示
            // 真实曲线名而不是回落「平坦」——后者会让用户以为曲线没应用上（2026-10-01 反馈）
            if (!string.IsNullOrEmpty(presetId)
                && EqState.Current != null
                && string.Equals(EqState.Current.PresetId, presetId, StringComparison.Ordinal))
            {
                SelectAudioFxEqExternalItem(EqState.Current);
                return;
            }

            DropAudioFxEqExternalItem();
            AudioFxEqPresetCombo.SelectedIndex = 0;
        }

        /// <summary>从预设下拉里摘掉动态外部项（切回固定预设 / 下拉重建后按需重挂）。</summary>
        private void DropAudioFxEqExternalItem()
        {
            if (_audioFxEqExternalItem == null)
            {
                return;
            }

            AudioFxEqPresetCombo?.Items.Remove(_audioFxEqExternalItem);
            _audioFxEqExternalItem = null;
        }

        /// <summary>
        /// 把外部曲线挂成预设下拉里的动态项（♫ + 曲线名）并选中它。
        /// RefreshAudioFxUserPresetItems 重建下拉时会清掉这一项，这里按需重建 + 重挂。
        /// </summary>
        private void SelectAudioFxEqExternalItem(EqCurveState curve)
        {
            if (AudioFxEqPresetCombo == null || string.IsNullOrEmpty(curve.PresetId))
            {
                return;
            }

            string content = "♫ " + (string.IsNullOrWhiteSpace(curve.PresetName) ? curve.PresetId : curve.PresetName);
            int manageAt = -1;
            for (int i = 0; i < AudioFxEqPresetCombo.Items.Count; i++)
            {
                if (AudioFxEqPresetCombo.Items[i] is ComboBoxItem { Tag: string t } && string.Equals(t, "manage", StringComparison.Ordinal))
                {
                    manageAt = i;
                    break;
                }
            }

            if (_audioFxEqExternalItem == null)
            {
                _audioFxEqExternalItem = new ComboBoxItem { Content = content, Tag = curve.PresetId };
                if (manageAt >= 0)
                {
                    AudioFxEqPresetCombo.Items.Insert(manageAt, _audioFxEqExternalItem);
                }
                else
                {
                    AudioFxEqPresetCombo.Items.Add(_audioFxEqExternalItem);
                }
            }
            else
            {
                _audioFxEqExternalItem.Content = content;
                _audioFxEqExternalItem.Tag = curve.PresetId;
                if (!AudioFxEqPresetCombo.Items.Contains(_audioFxEqExternalItem))
                {
                    if (manageAt >= 0)
                    {
                        AudioFxEqPresetCombo.Items.Insert(manageAt, _audioFxEqExternalItem);
                    }
                    else
                    {
                        AudioFxEqPresetCombo.Items.Add(_audioFxEqExternalItem);
                    }
                }
            }

            if (!ReferenceEquals(AudioFxEqPresetCombo.SelectedItem, _audioFxEqExternalItem))
            {
                AudioFxEqPresetCombo.SelectedItem = _audioFxEqExternalItem;
            }
        }


        private async void AudioFxEqPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 动态外部项（耳机校正 / APO 导入的曲线）只是「当前曲线」的展示位，不切预设：
            // 走到下面会按 presetId 新建曲线，把刚应用的那条覆盖成平坦
            if (ReferenceEquals(AudioFxEqPresetCombo.SelectedItem, _audioFxEqExternalItem))
            {
                return;
            }

            if (_audioFxLoading || AudioFxEqPresetCombo.SelectedItem is not ComboBoxItem { Tag: string presetId })
            {
                return;
            }

            // "管理（删除）我的预设…"入口
            if (string.Equals(presetId, "manage", StringComparison.Ordinal))
            {
                // 复位到当前实际预设，避免下拉停在管理项上
                _audioFxLoading = true;
                try { SelectAudioFxEqPreset(string.IsNullOrWhiteSpace(EqState.Current?.PresetId) ? "flat" : EqState.Current.PresetId); }
                finally { _audioFxLoading = false; }
                await DeleteAudioFxUserPresetFlow();
                return;
            }

            PushEqUndoSnapshot();
            _audioFxLoading = true;
            try
            {
                if (presetId.StartsWith("user:", StringComparison.Ordinal))
                {
                    // 用户预设：从持久化加载完整曲线
                    var user = EqUserPresetStore.FindById(presetId);
                    if (user != null)
                    {
                        EqState.Current = user;
                        EqState.Current.Enabled = AudioFxEqEnableToggle.IsOn;
                    }
                }
                else if (!string.Equals(presetId, "custom", StringComparison.Ordinal)
                         && !string.Equals(presetId, "simple", StringComparison.Ordinal))
                {
                    EqState.Current = EqCurveState.CreatePreset(presetId);
                    EqState.Current.Enabled = AudioFxEqEnableToggle.IsOn;
                }
                else
                {
                    var cur = EqCurveStore.Load();
                    EqState.Current = cur;
                    // 简单模式曲线保留盘上原值（勿改 id/名，避免后续按 custom fallback 误判）
                    if (!string.Equals(presetId, "simple", StringComparison.Ordinal))
                    {
                        EqState.Current.PresetId = "custom";
                        EqState.Current.PresetName = "自定义";
                    }
                }

                SyncAudioFxEqSimpleFromState();
                SelectAudioFxEqBand(EqState.Current.Bands.Count > 0 ? 0 : -1);
                RedrawAudioFxEqCurve();
                RefreshAudioFxEqBandEditor();
            }
            finally
            {
                _audioFxLoading = false;
            }

            ApplyDspToEngine();
        }


        // ---- EQ 曲线绘制（对齐 ECHO：自适应纵轴 / 频谱背景 / 渐变曲线 / 节点字形 / 频率标签避让） ----


        /// <summary>自适应纵轴量程阶梯（dB）：取能包住当前曲线的第一档，曲线平缓时视野更集中。</summary>
        private static readonly double[] EqGainLadder = { 12, 18, 24, 36 };

        /// <summary>各量程档的网格间距（dB），0 线必含。</summary>
        private static readonly double[] EqGainLadderStep = { 3, 6, 6, 12 };

        /// <summary>频率轴刻度（对齐 ECHO：32/64/125/250/500/1k/2k/4k/8k/16k）。</summary>
        private static readonly double[] EqAxisTickFreqs = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        /// <summary>Q 预设（对齐 ECHO：宽 / 中 / 窄）。</summary>
        private static readonly double[] EqQPresets = { 0.7, 1.4, 4.1 };

        private double _eqViewMaxGain = 12;
        private int _eqHoverIndex = -1;

        private void AudioFxEqCurve_SizeChanged(dynamic sender, dynamic e)
        {
            if (_audioFxEqBuilt) RedrawAudioFxEqCurve();
        }


        /// <summary>当前曲线区画布几何（左/右/上/下留白 + 绘图区尺寸）。</summary>
        private (double padL, double padR, double padT, double padB, double plotW, double plotH) GetEqPlotRect()
        {
            double w = AudioFxEqCurveCanvas.ActualWidth, h = AudioFxEqCurveCanvas.ActualHeight;
            double padL = Math.Clamp(w * 0.06, 38, 54);
            double padT = 14, padB = 30, padR = 8;
            double plotW = Math.Max(10, w - padL - padR);
            double plotH = Math.Max(10, h - padT - padB);
            return (padL, padR, padT, padB, plotW, plotH);
        }


        /// <summary>按当前 band 增益峰值选自适应纵轴量程（含 +2dB 余量，避免频繁跳档）。</summary>
        private double ComputeEqViewMaxGain()
        {
            double maxAbs = 0;
            foreach (var b in EqState.Current.Bands)
            {
                if (b is { Enabled: true }) maxAbs = Math.Max(maxAbs, Math.Abs(b.GainDb));
            }

            for (int i = 0; i < EqGainLadder.Length; i++)
            {
                if (EqGainLadder[i] >= maxAbs + 2) return EqGainLadder[i];
            }

            return EqGainLadder[^1];
        }


        /// <summary>曲线宿主底色偏暗 = 深色主题（网格/标签/描边配色据此切换，保证两种主题都清晰）。</summary>
        private bool IsEqCurveDarkTheme()
        {
            try
            {
                if (AudioFxEqCurveHost?.Background is SolidColorBrush sb)
                {
                    double lum = (0.299 * sb.Color.R + 0.587 * sb.Color.G + 0.114 * sb.Color.B) / 255.0;
                    return lum < 0.5;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            return true;
        }


        private static string EqBandGlyph(EqFilterType t) => t switch
        {
            EqFilterType.LowShelf => "S",
            EqFilterType.HighShelf => "S",
            EqFilterType.LowPass => "F",
            EqFilterType.HighPass => "F",
            EqFilterType.Notch => "N",
            _ => "P"
        };


        /// <summary>所有启用段在给定频率的叠加增益（dB，用于悬停读数的「总响应」）。</summary>
        private double SumEnabledBandGainAt(double f)
        {
            double g = 0;
            foreach (var b in EqState.Current.Bands)
            {
                if (b is { Enabled: true }) g += ApproxBandGain(f, b);
            }
            return g;
        }


        /// <summary>实时频谱背景层（post-DSP 真 FFT，播放中生效；未播放时外层定时器负责平滑落零）。</summary>
        private void DrawEqSpectrumLayer(Panel canvas, double padL, double padT, double plotW, double plotH, Color color)
        {
            if (!_eqSpectrumOn) return;
            int n = Math.Min(_eqSpectrumBands.Length, (int)(plotW / 6));
            if (n < 8) return;
            double barW = plotW / n;
            var brush = new SolidColorBrush(color);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(_eqSpectrumBands[i], 0f, 1f);
                if (v <= 0.003f) continue;
                double bh = Math.Max(1, v * plotH * 0.9);
                var bar = new Shapes.Rectangle
                {
                    Width = Math.Max(1, barW - 2),
                    Height = bh,
                    Fill = brush,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(bar, padL + i * barW + 1);
                Canvas.SetTop(bar, padT + plotH - bh);
                canvas.Children.Add(bar);
            }
        }


        private void RedrawAudioFxEqCurve()
        {
            if (AudioFxEqCurveCanvas == null || AudioFxEqCurveCanvas.ActualWidth <= 0 || AudioFxEqCurveCanvas.ActualHeight <= 0)
            {
                return;
            }

            double w = AudioFxEqCurveCanvas.ActualWidth;
            double h = AudioFxEqCurveCanvas.ActualHeight;
            var (padL, padR, padT, padB, plotW, plotH) = GetEqPlotRect();
            double maxGain = ComputeEqViewMaxGain();
            _eqViewMaxGain = maxGain;
            double midY = padT + plotH / 2;
            double gainPerH = plotH / (2 * maxGain);
            double logMin = Log2(EqCurveFreqMin), logMax = Log2(EqCurveFreqMax);

            double X(double freq) => padL + (Log2(Math.Clamp(freq, EqCurveFreqMin, EqCurveFreqMax)) - logMin) / (logMax - logMin) * plotW;
            double Y(double gain) => midY - gain * gainPerH;

            var children = AudioFxEqCurveCanvas.Children;
            children.Clear();

            bool dark = IsEqCurveDarkTheme();
            Color gridLine = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
            Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
            Color labelColor = dark ? Color.FromArgb(175, 255, 255, 255) : Color.FromArgb(150, 30, 30, 30);
            Color c1 = dark ? Color.FromArgb(255, 0x26, 0x4a, 0x63) : Color.FromArgb(255, 0x1d, 0x4e, 0xd8);
            Color c2 = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
            Color c3 = dark ? Color.FromArgb(255, 0x8a, 0x62, 0x35) : Color.FromArgb(255, 0xb4, 0x53, 0x09);
            Color fillTop = dark ? Color.FromArgb(46, 0x2e, 0x71, 0x68) : Color.FromArgb(40, 0x0f, 0x76, 0x6e);
            Color fillBottom = Color.FromArgb(5, fillTop.R, fillTop.G, fillTop.B);
            Color nodeNormal = dark ? Color.FromArgb(235, 0x9d, 0xc3, 0xd8) : Color.FromArgb(235, 0x2f, 0x6f, 0xa8);
            Color nodeSel = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
            Color nodeOff = Color.FromArgb(120, 128, 128, 128);
            Color specColor = dark ? Color.FromArgb(42, 0x2e, 0x71, 0x68) : Color.FromArgb(26, 0x0f, 0x76, 0x6e);

            // 极客皮肤：曲线是代码往 Canvas 上画的 Shape，既不读 ThemeResource 也吃不到元素级资源字典，
            // 必须在这里显式换成磷光色。保留原来的「三段渐变」结构（亮 → 主 → 暗），只把色相换成磷光。
            if (GeekDspAccentColor() is Color ph)
            {
                c1 = MixToward(ph, Color.FromArgb(255, 255, 255, 255), 0.35);
                c2 = ph;
                c3 = MixToward(ph, Color.FromArgb(255, 0, 0, 0), 0.30);
                fillTop = Color.FromArgb(46, ph.R, ph.G, ph.B);
                fillBottom = Color.FromArgb(5, ph.R, ph.G, ph.B);
                nodeNormal = MixToward(ph, Color.FromArgb(255, 255, 255, 255), 0.45);
                nodeSel = ph;
                specColor = Color.FromArgb(42, ph.R, ph.G, ph.B);
            }
            var glyphBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));

            // 1. 实时频谱背景（最底层）
            DrawEqSpectrumLayer(AudioFxEqCurveCanvas, padL, padT, plotW, plotH, specColor);

            // 2. 横向 dB 网格线 + 左侧刻度（自适应纵轴，0 线加粗）
            int stepIdx = Array.IndexOf(EqGainLadder, maxGain);
            double step = EqGainLadderStep[stepIdx];
            int lines = (int)Math.Round(2 * maxGain / step);
            for (int li = -lines; li <= lines; li++)
            {
                double g = li * step;
                double gy = Y(g);
                bool zero = li == 0;
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = gy, X2 = padL + plotW, Y2 = gy,
                    Stroke = new SolidColorBrush(zero ? gridStrong : gridLine),
                    StrokeThickness = zero ? 1.4 : 1
                });
                string txt = (g > 0 ? "+" : "") + g.ToString("0.#");
                children.Add(MakeEqAxisLabel(txt, 0, gy - 7, padL - 8, TextAlignment.Right, labelColor));
            }

            // 3. 纵向频率网格线 + 底部刻度
            foreach (double f in EqAxisTickFreqs)
            {
                double gx = X(f);
                children.Add(new Shapes.Line
                {
                    X1 = gx, Y1 = padT, X2 = gx, Y2 = padT + plotH,
                    Stroke = new SolidColorBrush(gridLine),
                    StrokeThickness = 1
                });
                string txt = f >= 1000 ? (f / 1000.0).ToString("0.#") + "k" : f.ToString("0");
                double lw = 44;
                double lx = Math.Clamp(gx - lw / 2, 0, Math.Max(0, w - lw));
                children.Add(MakeEqAxisLabel(txt, lx, padT + plotH + 6, lw, TextAlignment.Center, labelColor));
            }

            // 底部轴线
            children.Add(new Shapes.Line
            {
                X1 = padL, Y1 = padT + plotH, X2 = padL + plotW, Y2 = padT + plotH,
                Stroke = new SolidColorBrush(gridStrong),
                StrokeThickness = 1
            });

            // 4. 响应曲线：对数频率轴取点求和（对齐 ECHO 180 点密度）
            int samples = Math.Max(120, (int)(plotW / 3));
            var pts = new List<Windows.Foundation.Point>(samples + 1);
            for (int i = 0; i <= samples; i++)
            {
                double f = EqCurveFreqMin * Math.Pow(EqCurveFreqMax / EqCurveFreqMin, (double)i / samples);
                pts.Add(new Windows.Foundation.Point(X(f), Math.Clamp(Y(SumEnabledBandGainAt(f)), padT, padT + plotH)));
            }

            if (pts.Count > 1)
            {
                // 曲线下渐变填充（对齐 ECHO：主色 0.18 → 0.02）
                var fillFig = new PathFigure { StartPoint = pts[0], IsClosed = true };
                for (int i = 1; i < pts.Count; i++) fillFig.Segments.Add(new LineSegment { Point = pts[i] });
                fillFig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(pts[^1].X, padT + plotH) });
                fillFig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(pts[0].X, padT + plotH) });
                var fillGeom = new PathGeometry();
                fillGeom.Figures.Add(fillFig);
                var fillBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = fillTop, Offset = 0 },
                        new GradientStop { Color = fillBottom, Offset = 1 }
                    }
                };
                children.Add(new Shapes.Path { Data = fillGeom, Fill = fillBrush });

                // 描边：横向渐变（对齐 ECHO #264a63 → #2e7168 → #8a6235）
                var strokeFig = new PathFigure { StartPoint = pts[0], IsClosed = false };
                for (int i = 1; i < pts.Count; i++) strokeFig.Segments.Add(new LineSegment { Point = pts[i] });
                var strokeGeom = new PathGeometry();
                strokeGeom.Figures.Add(strokeFig);
                var strokeBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop { Color = c1, Offset = 0 },
                        new GradientStop { Color = c2, Offset = 0.55 },
                        new GradientStop { Color = c3, Offset = 1 }
                    }
                };
                var curvePath = new Shapes.Path { Data = strokeGeom, Stroke = strokeBrush, StrokeThickness = 2 };
                // EQ 关闭时曲线变暗（保留可见性用于预览）
                curvePath.Opacity = AudioFxEqEnableToggle != null && AudioFxEqEnableToggle.IsOn ? 1 : 0.35;
                children.Add(curvePath);
            }

            // 5. 悬停/选中十字线（虚线）
            // ⚠ DoubleCollection 实例不能被多个 Shape 共用：WinRT 的 StrokeDashArray
            // setter 会接管该集合实例，赋给第二个 Shape 时抛 ArgumentException
            // （"Value does not fall within the expected range"）。每条线各自 new 一份。
            foreach (int hi in _eqHoverIndex == _audioFxEqSelected
                         ? new[] { _audioFxEqSelected }
                         : new[] { _eqHoverIndex, _audioFxEqSelected })
            {
                if (hi < 0 || hi >= EqState.Current.Bands.Count) continue;
                double hx = X(EqState.Current.Bands[hi].FrequencyHz);
                double hy = Y(EqState.Current.Bands[hi].GainDb);
                children.Add(new Shapes.Line
                {
                    X1 = hx, Y1 = padT, X2 = hx, Y2 = padT + plotH,
                    Stroke = new SolidColorBrush(gridStrong), StrokeThickness = 1,
                    StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 3 }
                });
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = hy, X2 = padL + plotW, Y2 = hy,
                    Stroke = new SolidColorBrush(gridStrong), StrokeThickness = 1,
                    StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 3 }
                });
            }

            // 6. 节点（字形 P/S/F/N 对齐 ECHO）+ 频率标签（42px 防重叠，选中/hover 优先）
            var labelCandidates = new List<(int index, double prio)>();
            for (int i = 0; i < EqState.Current.Bands.Count; i++)
            {
                double prio = EqFreqToX(EqState.Current.Bands[i].FrequencyHz) * 0.001
                              + Math.Abs(EqState.Current.Bands[i].GainDb) * 10
                              + ((i == _audioFxEqSelected || i == _eqHoverIndex) ? 100000 : 0);
                labelCandidates.Add((i, prio));
            }
            labelCandidates.Sort((a, b) => b.prio.CompareTo(a.prio));

            var placedLabels = new List<Windows.Foundation.Point>();
            foreach (var (i, _) in labelCandidates)
            {
                var b = EqState.Current.Bands[i];
                double bx = X(b.FrequencyHz), by = Y(b.GainDb);
                bool sel = i == _audioFxEqSelected, hov = i == _eqHoverIndex;
                double size = sel ? 20 : 16;
                var node = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Background = new SolidColorBrush(b.Enabled ? (sel ? nodeSel : nodeNormal) : nodeOff),
                    BorderBrush = sel || hov ? glyphBrush : null,
                    BorderThickness = sel || hov ? new Thickness(2) : new Thickness(0),
                    Opacity = b.Enabled ? 1 : 0.5,
                    Child = new TextBlock
                    {
                        Text = EqBandGlyph(b.FilterType),
                        FontSize = 10,
                        FontWeight = sel ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = glyphBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Canvas.SetLeft(node, bx - size / 2);
                Canvas.SetTop(node, by - size / 2);
                children.Add(node);

                // 频率标签：正增益放节点上方，负增益放下方；42px 内同侧已占位则跳过
                double labelY = b.GainDb >= 0 ? by - size / 2 - 16 : by + size / 2 + 3;
                bool clash = false;
                foreach (var p in placedLabels)
                {
                    if (Math.Abs(p.X - bx) < 42 && Math.Abs(p.Y - labelY) < 14) { clash = true; break; }
                }
                if (clash) continue;
                placedLabels.Add(new Windows.Foundation.Point(bx, labelY));
                double lw = 56;
                double lx = Math.Clamp(bx - lw / 2, 0, Math.Max(0, w - lw));
                children.Add(MakeEqAxisLabel(FormatHelper.FormatAudioFxFreq(b.FrequencyHz), lx, labelY, lw, TextAlignment.Center, labelColor));
            }
        }


        // ---- 曲线交互 ----

        private static readonly double EqCurveMinLog2 = Log2(EqCurveFreqMin);
        private static readonly double EqCurveMaxLog2 = Log2(EqCurveFreqMax);

        private void AudioFxEqCurve_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try { AudioFxEqCurveCanvas.Focus(FocusState.Pointer); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            var pos = e.GetCurrentPoint(AudioFxEqCurveCanvas).Position;
            // 优先命中已有拖点（半径 20px）
            for (int i = 0; i < EqState.Current.Bands.Count; i++)
            {
                double bx = EqFreqToX(EqState.Current.Bands[i].FrequencyHz);
                double by = EqGainToY(EqState.Current.Bands[i].GainDb);
                double dx = pos.X - bx, dy = pos.Y - by;
                if (dx * dx + dy * dy <= 20 * 20)
                {
                    SelectAudioFxEqBand(i);
                    _audioFxEqDragging = true;
                    PushEqUndoSnapshot();
                    AudioFxEqCurveCanvas.CapturePointer(e.Pointer);
                    return;
                }
            }

            // 空白点：取消选中
            _audioFxEqSelected = -1;
            SelectAudioFxEqBand(-1);
        }


        private void AudioFxEqCurve_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(AudioFxEqCurveCanvas).Position;

            if (_audioFxEqDragging && _audioFxEqSelected >= 0 && _audioFxEqSelected < EqState.Current.Bands.Count)
            {
                var b = EqState.Current.Bands[_audioFxEqSelected];
                var (padL, _, padT, padB, plotW, plotH) = GetEqPlotRect();
                double nf = EqXToFreq(Math.Clamp(pos.X, padL, padL + plotW));
                // Shift = 0.1dB 精细 / 常规 0.5dB 量化（对齐 ECHO）
                double step = IsShiftDown() ? 0.1 : 0.5;
                double ng = Math.Round(ClampGain(EqYToGain(Math.Clamp(pos.Y, padT, padT + plotH))) / step) * step;
                b.FrequencyHz = Math.Round(nf, 1);
                if (b.FilterType is EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf)
                {
                    b.GainDb = ng;
                    b.Enabled = Math.Abs(ng) > 0.01;
                }
                else
                {
                    // 低通/高通/切除不改增益，只拖频率（对齐 ECHO gainEditable）
                    b.Enabled = true;
                }

                MarkAudioFxEqCustom();
                SyncBandEditorFromState();
                RedrawAudioFxEqCurve();
                ApplyDspToEngine();
                PositionEqReadout(pos);
                return;
            }

            // 悬停：命中节点 → 高亮 + 十字线 + 读数；未命中 → 仅读数
            int hit = -1;
            for (int i = 0; i < EqState.Current.Bands.Count; i++)
            {
                double bx = EqFreqToX(EqState.Current.Bands[i].FrequencyHz);
                double by = EqGainToY(EqState.Current.Bands[i].GainDb);
                double dx = pos.X - bx, dy = pos.Y - by;
                if (dx * dx + dy * dy <= 20 * 20) { hit = i; break; }
            }

            if (hit != _eqHoverIndex)
            {
                _eqHoverIndex = hit;
                RedrawAudioFxEqCurve();
            }

            if (hit >= 0)
            {
                var b = EqState.Current.Bands[hit];
                double total = SumEnabledBandGainAt(b.FrequencyHz);
                AudioFxEqReadoutText.Text = FormatHelper.FormatAudioFxFreq(b.FrequencyHz)
                    + " · 总响应 " + FormatHelper.FormatAudioFxDb(total) + " dB"
                    + " · 本段 " + FormatHelper.FormatAudioFxDb(b.GainDb) + " dB"
                    + " · Q " + b.Q.ToString("0.##")
                    + (b.Enabled ? "" : " · 已停用");
            }
            else
            {
                double f = EqXToFreq(Math.Clamp(pos.X, 0, AudioFxEqCurveCanvas.ActualWidth - 1));
                AudioFxEqReadoutText.Text = FormatHelper.FormatAudioFxFreq(f)
                    + " · 总响应 " + FormatHelper.FormatAudioFxDb(SumEnabledBandGainAt(f)) + " dB";
            }

            AudioFxEqReadout.Visibility = Visibility.Visible;
            PositionEqReadout(pos);
        }


        private void AudioFxEqCurve_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_audioFxEqDragging)
            {
                _audioFxEqDragging = false;
                try { AudioFxEqCurveCanvas.ReleasePointerCapture(e.Pointer); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void AudioFxEqCurve_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_audioFxEqDragging) return;   // 拖动中（指针已捕获）不清悬停态
            _eqHoverIndex = -1;
            if (AudioFxEqReadout != null) AudioFxEqReadout.Visibility = Visibility.Collapsed;
            RedrawAudioFxEqCurve();
        }


        /// <summary>把悬停读数框摆到指针右下（贴边时翻到左上）。</summary>
        private void PositionEqReadout(Windows.Foundation.Point pos)
        {
            if (AudioFxEqReadout == null) return;
            double rw = AudioFxEqReadout.ActualWidth, rh = AudioFxEqReadout.ActualHeight;
            double hostW = AudioFxEqCurveHost?.ActualWidth ?? AudioFxEqCurveCanvas.ActualWidth;
            double hostH = AudioFxEqCurveHost?.ActualHeight ?? AudioFxEqCurveCanvas.ActualHeight;
            double x = pos.X + 14, y = pos.Y + 14;
            if (x + rw > hostW - 2) x = pos.X - rw - 14;
            if (y + rh > hostH - 2) y = pos.Y - rh - 14;
            AudioFxEqReadout.Margin = new Thickness(Math.Max(2, x), Math.Max(2, y), 0, 0);
        }


        private void MarkAudioFxEqCustom()
        {
            EqState.Current.PresetId = "custom";
            EqState.Current.PresetName = "自定义";
            _audioFxLoading = true;
            try { SelectAudioFxEqPreset("custom"); }
            finally { _audioFxLoading = false; }
        }


        private double EqFreqToX(double freq)
        {
            var (padL, _, _, _, plotW, _) = GetEqPlotRect();
            return padL + (Log2(Math.Clamp(freq, EqCurveFreqMin, EqCurveFreqMax)) - EqCurveMinLog2) / (EqCurveMaxLog2 - EqCurveMinLog2) * plotW;
        }


        private double EqGainToY(double gain)
        {
            var (_, _, padT, _, _, plotH) = GetEqPlotRect();
            double midY = padT + plotH / 2;
            return midY - gain * (plotH / (2 * _eqViewMaxGain));
        }


        private double EqXToFreq(double x)
        {
            var (padL, _, _, _, plotW, _) = GetEqPlotRect();
            double t = Math.Clamp((x - padL) / (plotW > 0 ? plotW : 1), 0, 1);
            return EqCurveFreqMin * Math.Pow(EqCurveFreqMax / EqCurveFreqMin, t);
        }


        private double EqYToGain(double y)
        {
            var (_, _, padT, _, _, plotH) = GetEqPlotRect();
            double midY = padT + plotH / 2;
            return ClampGain((midY - y) * (2 * _eqViewMaxGain) / (plotH > 0 ? plotH : 1));
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
            bool pro = AudioFxEqModeRadio.SelectedIndex == 0;
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count)
            {
                AudioFxEqBandEditor.Visibility = Visibility.Collapsed;
                AudioFxEqStepperRow.Visibility = Visibility.Collapsed;
                return;
            }

            AudioFxEqBandEditor.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
            AudioFxEqStepperRow.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
            var b = EqState.Current.Bands[_audioFxEqSelected];
            _audioFxLoading = true;
            try
            {
                SelectAudioFxEqBandType(b.FilterType);
                AudioFxEqBandFreqSlider.Value = Log2(b.FrequencyHz < 20 ? 20 : b.FrequencyHz > 20000 ? 20000 : b.FrequencyHz);
                AudioFxEqBandGainSlider.Value = Math.Clamp(b.GainDb, -24, 24);
                AudioFxEqBandQSlider.Value = Math.Clamp(b.Q, 0.1, 24);
                AudioFxEqBandEnableToggle.IsOn = b.Enabled;
                AudioFxEqBandFreqLabel.Text = FormatHelper.FormatAudioFxFreq(b.FrequencyHz);
                AudioFxEqBandGainLabel.Text = FormatHelper.FormatAudioFxDb(b.GainDb) + " dB";
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
            // preamp 文本 + 滑杆 + 快捷状态条一并同步
            SetAudioFxEqPreampDisplay(EqState.Current.PreampDb);
            UpdateAudioFxEqQuickStrip();
        }


        /// <summary>同步 preamp 文本与滑杆显示（滑杆赋值带 _audioFxLoading 守卫，避免回环触发 ValueChanged）。</summary>
        private void SetAudioFxEqPreampDisplay(double db)
        {
            if (AudioFxEqPreampText != null)
            {
                AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatHelper.FormatAudioFxDb(db) + " dB";
            }

            if (AudioFxEqPreampSlider != null && Math.Abs(AudioFxEqPreampSlider.Value - db) > 0.001)
            {
                _audioFxLoading = true;
                try { AudioFxEqPreampSlider.Value = Math.Clamp(db, AudioFxEqPreampSlider.Minimum, AudioFxEqPreampSlider.Maximum); }
                finally { _audioFxLoading = false; }
            }
        }


        /// <summary>EQ 页快捷状态条：EQ 状态 / bit-perfect / 预增益 / 余量 / 预设（对齐 ECHO quick strip）。</summary>
        private void UpdateAudioFxEqQuickStrip()
        {
            if (AudioFxEqPillStatusText == null)
            {
                return;
            }

            bool on = AudioFxEqEnableToggle != null && AudioFxEqEnableToggle.IsOn && EqState.Current.HasEffect();
            AudioFxEqPillStatusText.Text = on
                ? "EQ 生效中（" + EqState.Current.Bands.Count(b => b.Enabled) + " 段）"
                : "EQ 直通";
            AudioFxEqPillStatusText.Opacity = on ? 1 : 0.75;

            if (AudioFxEqPillPerfectText != null)
            {
                AudioFxEqPillPerfectText.Text = RackParameters.Bypassed
                    ? "bit-perfect 直通（DSP 已旁路）"
                    : (IsDspActiveForBadge() ? "输出非 bit-perfect（DSP 生效）" : "输出 bit-perfect 直出");
            }

            if (AudioFxEqPillPreampText != null)
            {
                AudioFxEqPillPreampText.Text = "预增益 " + FormatHelper.FormatAudioFxDb(EqState.Current.PreampDb) + " dB";
            }

            if (AudioFxEqPillHeadroomText != null && AudioFxSafetyHeadroomSlider != null)
            {
                AudioFxEqPillHeadroomText.Text = "余量 " + FormatHelper.FormatAudioFxDb(AudioFxSafetyHeadroomSlider.Value) + " dB";
            }

            if (AudioFxEqPillPresetText != null)
            {
                AudioFxEqPillPresetText.Text = "预设 " + (string.IsNullOrWhiteSpace(EqState.Current.PresetName) ? "平坦" : EqState.Current.PresetName);
            }
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
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count
                || AudioFxEqBandTypeCombo.SelectedItem is not ComboBoxItem { Tag: EqFilterType t })
            {
                return;
            }

            PushEqUndoSnapshot();
            EqState.Current.Bands[_audioFxEqSelected].FilterType = t;
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandFreq_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            PushEqUndoSnapshot(coalesce: true);
            var b = EqState.Current.Bands[_audioFxEqSelected];
            b.FrequencyHz = Math.Clamp(Math.Pow(2, e.NewValue), 20, 20000);
            AudioFxEqBandFreqLabel.Text = FormatHelper.FormatAudioFxFreq(b.FrequencyHz);
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandGain_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            PushEqUndoSnapshot(coalesce: true);
            var b = EqState.Current.Bands[_audioFxEqSelected];
            b.GainDb = e.NewValue;
            b.Enabled = Math.Abs(b.GainDb) > 0.01;
            AudioFxEqBandGainLabel.Text = FormatHelper.FormatAudioFxDb(b.GainDb) + " dB";
            MarkAudioFxEqCustom();
            SyncBandEditorFromState();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandQ_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            PushEqUndoSnapshot(coalesce: true);
            var b = EqState.Current.Bands[_audioFxEqSelected];
            b.Q = e.NewValue;
            AudioFxEqBandQLabel.Text = b.Q.ToString("0.##");
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandEnable_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioFxLoading || _audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            PushEqUndoSnapshot();
            EqState.Current.Bands[_audioFxEqSelected].Enabled = AudioFxEqBandEnableToggle.IsOn;
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqBandDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteAudioFxEqBandCore();
        }

        /// <summary>删除选中频段核心（网页「音效处理」页直接调）。</summary>
        internal void DeleteAudioFxEqBandCore()
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            PushEqUndoSnapshot();
            EqState.Current.Bands.RemoveAt(_audioFxEqSelected);
            _audioFxEqSelected = EqState.Current.Bands.Count > 0 ? Math.Min(_audioFxEqSelected, EqState.Current.Bands.Count - 1) : -1;
            if (EqState.Current.Bands.Count == 0) EqState.Current.Bands.Add(new EqBand());
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        private void AudioFxEqAddBand_Click(object sender, RoutedEventArgs e)
        {
            AddAudioFxEqBandCore();
        }

        /// <summary>添加频段核心（网页「音效处理」页直接调）。</summary>
        internal void AddAudioFxEqBandCore()
        {
            double freq = 1000;
            if (_audioFxEqSelected >= 0 && _audioFxEqSelected < EqState.Current.Bands.Count)
            {
                freq = Math.Clamp(EqState.Current.Bands[_audioFxEqSelected].FrequencyHz * 2, 20, 20000);
            }

            PushEqUndoSnapshot();
            EqState.Current.Bands.Add(new EqBand { Enabled = true, FrequencyHz = freq, GainDb = 0, Q = 1.0, FilterType = EqFilterType.Peaking });
            _audioFxEqSelected = EqState.Current.Bands.Count - 1;
            MarkAudioFxEqCustom();
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        // ---- EQ 开关 / 模式 ----

        private void AudioFxEqEnable_Toggled(object sender, RoutedEventArgs e)
        {
            if (_audioFxLoading) return;
            PushEqUndoSnapshot();
            EqState.Current.Enabled = AudioFxEqEnableToggle.IsOn;
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        private void AudioFxEqMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool pro = AudioFxEqModeRadio.SelectedIndex == 0;
            AudioFxEqSimplePanel.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
            // 段编辑器 + 步进行可见性统一走同步逻辑（依赖当前选中段）
            SyncBandEditorFromState();
        }


        // ---- 简单模式 ----

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
            FlatAudioFxEqSimpleCore();
        }

        /// <summary>简单模式恢复平坦核心（网页「音效处理」页直接调）。</summary>
        internal void FlatAudioFxEqSimpleCore()
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

            _ = await SaveAudioFxEqPresetCoreAsync(nameBox.Text ?? string.Empty);
        }

        private void AudioFxEqAutoGain_Click(object sender, RoutedEventArgs e)
        {
            AutoGainAudioFxEqCore();
        }

        private void AudioFxEqReset_Click(object sender, RoutedEventArgs e)
        {
            PushEqUndoSnapshot();
            _audioFxLoading = true;
            try
            {
                EqState.Current = EqCurveState.CreatePreset("flat");
                AudioFxEqEnableToggle.IsOn = false;
                EqState.Current.Enabled = false;
                AudioFxEqPreampText.Text = "预增益 (preamp)：0.0 dB";
                SelectAudioFxEqPreset("flat");
                _audioFxEqSelected = EqState.Current.Bands.Count > 0 ? 0 : -1;
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


        // ---- ECHO 化增强：撤销 / 重做 / A-B / 步进 / 键盘微调 / preamp 滑杆 / 频谱背景 ----

        private void AudioFxEqUndo_Click(object sender, RoutedEventArgs e)
        {
            UndoAudioFxEqCore();
        }

        private void AudioFxEqRedo_Click(object sender, RoutedEventArgs e)
        {
            RedoAudioFxEqCore();
        }

        /// <summary>A/B 对比：首次点击把当前曲线存为参考 A；再点在 A / 现场 B 间切换试听；Shift+点击取消。</summary>
        private void AudioFxEqAb_Click(object sender, RoutedEventArgs e)
        {
            ToggleAudioFxEqAbCore();
        }

        private void AudioFxEqFreqStepUp_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandFreq(1);

        private void AudioFxEqFreqStepDown_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandFreq(-1);


        /// <summary>选中段频率微调：每步 2^(1/3) 个八度（≈1.26 倍，对齐 ECHO 粗步进）。</summary>
        private void StepAudioFxEqBandFreq(int dir)
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count)
            {
                NowPlayingText.Text = "先在曲线上选中一个频段";
                return;
            }

            PushEqUndoSnapshot(coalesce: true);
            var b = EqState.Current.Bands[_audioFxEqSelected];
            b.FrequencyHz = Math.Clamp(b.FrequencyHz * Math.Pow(2, dir / 3.0), EqCurveFreqMin, EqCurveFreqMax);
            MarkAudioFxEqCustom();
            SyncBandEditorFromState();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqGainStepDown05_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandGain(-0.5);

        private void AudioFxEqGainStepDown01_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandGain(-0.1);

        private void AudioFxEqGainStepUp01_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandGain(0.1);

        private void AudioFxEqGainStepUp05_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandGain(0.5);


        /// <summary>选中段增益微调（dB）。低通/高通/切除不调增益（对齐 ECHO gainEditable）。</summary>
        private void StepAudioFxEqBandGain(double delta)
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count)
            {
                NowPlayingText.Text = "先在曲线上选中一个频段";
                return;
            }

            var b = EqState.Current.Bands[_audioFxEqSelected];
            if (b.FilterType is not (EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf))
            {
                NowPlayingText.Text = "低通 / 高通 / 切除段不调整增益";
                return;
            }

            PushEqUndoSnapshot(coalesce: true);
            b.GainDb = Math.Clamp(b.GainDb + delta, -EqCurveGainMax, EqCurveGainMax);
            b.Enabled = Math.Abs(b.GainDb) > 0.01;
            MarkAudioFxEqCustom();
            SyncBandEditorFromState();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        private void AudioFxEqQWide_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandQ(0);

        private void AudioFxEqQNormal_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandQ(1);

        private void AudioFxEqQNarrow_Click(object sender, RoutedEventArgs e) => StepAudioFxEqBandQ(2);


        private void StepAudioFxEqBandQ(int preset)
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count)
            {
                NowPlayingText.Text = "先在曲线上选中一个频段";
                return;
            }

            PushEqUndoSnapshot(coalesce: true);
            EqState.Current.Bands[_audioFxEqSelected].Q = EqQPresets[preset];
            MarkAudioFxEqCustom();
            SyncBandEditorFromState();
            RedrawAudioFxEqCurve();
            ApplyDspToEngine();
        }


        /// <summary>曲线键盘微调：↑↓ 增益 ±0.5dB，←→ 频率 2^(1/3) 倍（先点选节点再按键）。</summary>
        private void AudioFxEqCurve_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_audioFxEqSelected < 0 || _audioFxEqSelected >= EqState.Current.Bands.Count) return;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Up: StepAudioFxEqBandGain(0.5); e.Handled = true; break;
                case Windows.System.VirtualKey.Down: StepAudioFxEqBandGain(-0.5); e.Handled = true; break;
                case Windows.System.VirtualKey.Left: StepAudioFxEqBandFreq(-1); e.Handled = true; break;
                case Windows.System.VirtualKey.Right: StepAudioFxEqBandFreq(1); e.Handled = true; break;
            }
        }


        private void AudioFxEqPreamp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading) return;
            EqState.Current.PreampDb = Math.Clamp(e.NewValue, -24, 24);
            PushEqUndoSnapshot(coalesce: true);
            if (AudioFxEqPreampText != null)
            {
                AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatHelper.FormatAudioFxDb(EqState.Current.PreampDb) + " dB";
            }

            UpdateAudioFxEqQuickStrip();
            ApplyDspToEngine();
        }


        private void AudioFxEqSpectrum_Click(object sender, RoutedEventArgs e)
        {
            ToggleAudioFxEqSpectrumCore();
        }

        /// <summary>实时频谱背景开关核心（网页「音效处理」页直接调）。返回结果消息。</summary>
        internal string ToggleAudioFxEqSpectrumCore()
        {
            // ToggleButton（不是 ToggleSwitch）：选中属性是 IsChecked
            _eqSpectrumOn = AudioFxEqSpectrumToggle == null || AudioFxEqSpectrumToggle.IsChecked == true;
            if (_eqSpectrumOn && _dspPageIndex == DspPageEqIndex)
            {
                EnsureEqSpectrumTimer(true);
            }
            else
            {
                EnsureEqSpectrumTimer(false);
            }

            RedrawAudioFxEqCurve();
            return _eqSpectrumOn ? "实时频谱背景：开" : "实时频谱背景：关";
        }


        /// <summary>仅 EQ 页可见时运行的 ~15fps 频谱定时器：拉 post-DSP 频谱并重绘曲线背景。</summary>
        private void EnsureEqSpectrumTimer(bool enable)
        {
            if (_eqSpectrumTimer == null)
            {
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    return;
                }

                _eqSpectrumTimer = queue.CreateTimer();
                _eqSpectrumTimer.Interval = TimeSpan.FromMilliseconds(66);
                _eqSpectrumTimer.Tick += (s, a) =>
                {
                    if (!_eqSpectrumOn || !_audioFxEqBuilt || _dspPageIndex != DspPageEqIndex)
                    {
                        return;
                    }

                    bool live = _audioEngine?.TryGetSpectrum(_eqSpectrumBands) ?? false;
                    if (!live)
                    {
                        // 未播放 / DSD 直出：柱体平滑落零，全零后不再重绘
                        bool any = false;
                        for (int i = 0; i < _eqSpectrumBands.Length; i++)
                        {
                            _eqSpectrumBands[i] *= 0.8f;
                            if (_eqSpectrumBands[i] < 0.002f) _eqSpectrumBands[i] = 0f;
                            else any = true;
                        }

                        if (!any)
                        {
                            return;
                        }
                    }

                    RedrawAudioFxEqCurve();
                };
            }

            if (enable && _eqSpectrumOn)
            {
                _eqSpectrumTimer.Start();
            }
            else
            {
                _eqSpectrumTimer.Stop();
            }
        }


        private void AudioFxChannel_Toggled(object sender, RoutedEventArgs e) { ImportExtraParameterInput(sender); if (!_audioFxLoading) ApplyDspToEngine(); }

        private void AudioFxChannelSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (!_audioFxLoading) ApplyDspToEngine();
            // 左右偏差条与声道滑杆同源，跟着一起动
            UpdateDspChannelBars();
        }


        private void AudioFxChannelCrossfeed_Toggled(object sender, RoutedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (!_audioFxLoading) ApplyDspToEngine();
            // 独立页顶部的电源开关镜像这一个，一起同步
            SyncDspPowerSwitches();
            RedrawDspXfeedCurve();
        }


        private void AudioFxChannelCrossfeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            // 拖动曲线上的截止竖线期间不落盘：一次拖动会触发几百次本事件，
            // 每次 ApplyDspToEngine 都要写一遍 DspExtraStore 并下发内核。松手时统一补一次。
            if (!_audioFxLoading && !_dspXfeedSuppressPush) ApplyDspToEngine();
            // 强度与截止频率共用这个 handler：顺手刷新右侧 Hz 读数和强度百分比
            RefreshCrossfeedCutoffReadout();
            RefreshDspXfeedLevelReadout();
            RedrawDspXfeedCurve();
        }


        private void AudioFxSafetyLimiter_Toggled(object sender, RoutedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxRgPreamp_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (_audioFxLoading) return;
            AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatHelper.FormatAudioFxDb(e.NewValue);
            ApplyReplayGainToEngine();
        }


        /// <summary>DSP 总旁路开关（A/B 对比）：开 = 全部 DSP 立即旁路（bit-perfect），设置保留。
        /// 仅内存态（不持久化），播放中实时切换。</summary>
        private void DspBypassToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            RackParameters.SetBypass(bypass);
            _audioEngine?.SetBypassAll(bypass);
            UpdateDspBitPerfectUi();
            StartupLog.Write("[DSP] 总旁路切换: " + (bypass ? "开（bit-perfect）" : "关（DSP 恢复）"));
        }


        // 频谱带缓存与音效面板构建（2026-10-31 误切归还）。

        private readonly float[] _eqSpectrumBands = new float[FormatHelper.WaveBarCount];
        private DispatcherQueueTimer? _eqSpectrumTimer;
        private bool _eqSpectrumOn = true;

    }
}
