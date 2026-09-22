using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客「终端」播放页：把播放当成一条信号流在监控（foobar 的工程师视角 + ECHO 那种终端排版）。
    /// 三栏：左 = 音频流/信号链/文件；中 = 方块频谱 + 电平 + 峰值；右 = 相位图 + 状态；底部 = 播放头 + 队列。
    /// 底色固定近黑（用户拍板），强调色跟应用主题（换主题会跟着变）。
    /// 所有数字都是真数据：格式走 AudioInfoFormatter、信号链走引擎的源/输出格式、
    /// 电平走 LevelMeter 的 peak/RMS、频谱与相位走现成的 FFT / 立体声采样抽头 —— 全部只读，bit-perfect 无关。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const int TerminalBarCount = 32;
        private const int TerminalPlayheadBlocks = 48;

        private readonly float[] _terminalPeakBuf = new float[8];
        private readonly float[] _terminalRmsBuf = new float[8];
        private readonly float[] _terminalLeft = new float[1024];
        private readonly float[] _terminalRight = new float[1024];

        private readonly Border?[] _terminalBars = new Border?[TerminalBarCount];
        private readonly Border?[] _terminalBlocks = new Border?[TerminalPlayheadBlocks];
        private Polyline? _terminalPhaseLine;

        /// <summary>李萨如自动增益（平滑后）。≤0 表示下一帧直接取初值，不做平滑。</summary>
        private double _terminalPhaseGain;

        /// <summary>队列上一次的签名（曲目数 + 当前下标），没变就不重建那几行文本。</summary>
        private string _terminalQueueSignature = string.Empty;

        /// <summary>终端面板当前是否可见（非终端布局下所有刷新一律秒退，不白干活）。</summary>
        private bool _terminalStageVisible;

        /// <summary>
        /// 终端布局的可见性 / 强调色 / 首帧。由 ApplyNowPlayingLayout 末尾调用。
        /// 非终端布局时只做一件事：关掉立体声采样捕获（李萨如专用）。
        /// </summary>
        internal void ApplyTerminalLayout(bool layoutChanged)
        {
            if (TerminalStage == null)
            {
                return;
            }

            bool on = _layoutIsTerminal;
            _terminalStageVisible = on;
            TerminalStage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            // 面板底色按界面风格走：极客模式才是那块不透光黑板，
            // 其它模式下改成半透明（内部文字色是按深底调的，不能换成浅色卡片，否则字看不见）。
            ApplyTerminalChrome();

            if (!on)
            {
                return;
            }

            // 相位图需要 L/R 分声道样本（其它模式都关着，零开销原则）
            _audioEngine?.SetStereoCapture(true);

            ApplyTerminalAccent();
            UpdateTerminalInfo();
            UpdateTerminalQueue(force: true);
            DrawTerminalSpectrum();
            UpdateTerminalLevels();
            DrawTerminalPhase();
            UpdateTerminalPlayhead();
        }

        /// <summary>
        /// 终端面板底板配色：极客界面下是完整的不透光近黑板（那一页本来就该是块监控屏）；
        /// 其它界面风格下压成半透明 —— 用户反馈"别的模式下一整块大黑板太突兀"，
        /// 但面板里的文字色全是按深底调的（#D7DAE0 / #7A8290），换成浅色卡片会看不清，
        /// 所以只降不透明度让背景透出来，不换配色体系。
        /// </summary>
        private void ApplyTerminalChrome()
        {
            if (TerminalStage == null)
            {
                return;
            }

            bool geek = IsGeekUiStyleActive();
            // 极客界面：面板底完全透明 —— 主窗口本身就是近黑底，再叠一层不同色的板
            // 就是用户说的「终端外面一圈矩形框」。其它界面保留半透明深卡
            //（面板文字全按深底调色，不能换浅卡，只能降不透明度）。
            TerminalStage.Background = geek
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(0xA6, 0x0B, 0x0C, 0x10));
            // 边框一律不画：用户明确不要外圈矩形框。
            TerminalStage.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        /// <summary>强调色的元素按应用强调色刷一遍（不写死颜色，换主题自动跟随）。</summary>
        internal void ApplyTerminalAccent()
        {
            try
            {
                Color accent = _waveAccentColor;
                var brush = new SolidColorBrush(accent);

                if (TerminalHeaderText != null)
                {
                    TerminalHeaderText.Foreground = brush;
                }

                if (TerminalLevelLFill != null)
                {
                    TerminalLevelLFill.Background = brush;
                }

                if (TerminalLevelRFill != null)
                {
                    TerminalLevelRFill.Background = brush;
                }

                if (_terminalPhaseLine != null)
                {
                    _terminalPhaseLine.Stroke = brush;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ApplyTerminalAccent", caught);
            }
        }

        // =====================================================================
        // 左栏：音频流 / 信号链 / 文件
        // =====================================================================

        /// <summary>填左栏文本。切歌、切设备、信号链刷新时调用。</summary>
        internal void UpdateTerminalInfo()
        {
            if (!_terminalStageVisible)
            {
                return;
            }

            try
            {
                string path = _nowPlayingPath ?? string.Empty;
                if (TerminalPathText != null)
                {
                    TerminalPathText.Text = string.IsNullOrEmpty(path) ? "—" : path;
                }

                bool hifi = IsHiFiModeSelected();
                var chain = _audioEngine?.ChainFormat;

                if (TerminalSrcText != null)
                {
                    // 与链路面板同一口径（ChainFormat 结构化值）：源文件 → 转码 WAV 一段讲清，不混一行
                    if (chain != null && chain.HasSession)
                    {
                        string sf = chain.SourceFile?.Describe() ?? chain.SourceFileDescription ?? "DSD/未探测";
                        string wv = chain.TranscodeWav?.Describe() ?? "?";
                        TerminalSrcText.Text = sf + " → WAV " + wv + chain.OutcomeNote();
                    }
                    else
                    {
                        string? srcFmt = _audioEngine?.SourceFormatDescription;
                        TerminalSrcText.Text = string.IsNullOrWhiteSpace(srcFmt)
                            ? (hifi ? "（解析中）" : "系统解码")
                            : srcFmt;
                    }
                }

                if (TerminalOutText != null)
                {
                    if (chain?.DeviceOutput is AudioFormat dev)
                    {
                        TerminalOutText.Text = dev.Describe() + " · " + chain.VerdictNote();
                    }
                    else
                    {
                        string? outFmt = _audioEngine?.ActualOutputFormat;
                        TerminalOutText.Text = string.IsNullOrWhiteSpace(outFmt)
                            ? (hifi ? "（解析中）" : "系统混音器")
                            : outFmt;
                    }
                }

                if (TerminalOutputText != null)
                {
                    TerminalOutputText.Text = hifi ? "WASAPI（独占）" : "系统混音器（共享）";
                }

                if (TerminalModeText != null)
                {
                    // 播放路径：引擎直出还是系统 MediaPlayer —— 排障时第一眼要看的就是这个
                    TerminalModeText.Text = _usingEnginePlayback ? "ENGINE" : "MEDIAPLAYER";
                }

                bool eqOn = EqCurveStore.Load().HasEffect();
                if (TerminalDspText != null)
                {
                    TerminalDspText.Text = hifi ? "无（bit-perfect）" : (eqOn ? "EQ=on" : "EQ=off");
                }

                string deviceId = AppSettingsStore.Load().OutputDeviceId;
                if (TerminalDeviceText != null)
                {
                    TerminalDeviceText.Text = string.IsNullOrWhiteSpace(deviceId) ? "系统默认" : "指定设备";
                }

                _ = UpdateTerminalFormatAsync(path);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateTerminalInfo", caught);
            }
        }

        /// <summary>格式/码率走现成的探测（跟经典布局那行同一套数据），后台线程解码 + 缓存。</summary>
        private async System.Threading.Tasks.Task UpdateTerminalFormatAsync(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    if (TerminalFormatText != null)
                    {
                        TerminalFormatText.Text = "—";
                    }

                    return;
                }

                string? info = await System.Threading.Tasks.Task.Run(() => AudioInfoFormatter.Format(path));
                if (!_terminalStageVisible || TerminalFormatText == null)
                {
                    return;
                }

                if (string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    TerminalFormatText.Text = string.IsNullOrWhiteSpace(info) ? "—" : info;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateTerminalFormatAsync", caught);
            }
        }

        // =====================================================================
        // 中栏：方块频谱 + 电平 + 峰值
        // =====================================================================

        /// <summary>
        /// 频谱自动量程的参考值：取最近一段时间里最高的那一段当"满格"。
        /// 慢慢往下掉（每帧 ×0.99），所以安静的段落柱子会自己缩回去，不会永远顶天。
        /// 下限 0.10 保证极安静的曲子也不会只剩一格。
        /// </summary>
        private double _terminalSpecRef = 0.35;

        /// <summary>32 段缓存，避免每帧重复算区间最大值。</summary>
        private readonly double[] _terminalBandLevels = new double[TerminalBarCount];

        /// <summary>
        /// 方块段频谱：柱高取整到"段"上，形成 ▁▂▃▄ 那种一格一格的数码感。
        /// 数据直接用 _waveLevels（波形定时器已经按真 FFT 算好了，不重复取一次）。
        /// </summary>
        private void DrawTerminalSpectrum()
        {
            if (!_terminalStageVisible || TerminalSpectrumCanvas == null)
            {
                return;
            }

            double width = TerminalSpectrumCanvas.ActualWidth;
            double height = TerminalSpectrumCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            // 1) 40 个频段 → 32 段：取区间最大值，保证轮廓不碎，顺便找本帧峰值
            double peak = 0;
            for (int i = 0; i < TerminalBarCount; i++)
            {
                int a = (int)Math.Floor(i * WaveBarCount / (double)TerminalBarCount);
                int b = Math.Min(WaveBarCount - 1, (int)Math.Ceiling((i + 1) * WaveBarCount / (double)TerminalBarCount) - 1);
                double level = 0;
                for (int k = a; k <= b; k++)
                {
                    if (_waveLevels[k] > level)
                    {
                        level = _waveLevels[k];
                    }
                }

                _terminalBandLevels[i] = level;
                if (level > peak)
                {
                    peak = level;
                }
            }

            // 2) 自动量程：让最近最响的那段顶到满格。
            //    不做这一步的话，实际音乐每根柱子只有 0.3~0.7 的量，
            //    画出来就是"只有三四格、上面一大片空"，看着像没接上信号。
            _terminalSpecRef = Math.Max(peak, _terminalSpecRef * 0.99);
            _terminalSpecRef = Math.Clamp(_terminalSpecRef, 0.10, 1.0);

            // 3) 分段：16 段（原来只有 8 段，一格太高，看不出层次）
            const int segments = 16;
            double gap = 3;
            double barWidth = Math.Max(3, (width - gap * (TerminalBarCount - 1)) / TerminalBarCount);
            double segH = Math.Max(2.5, Math.Round(height / segments));
            double maxSegH = Math.Round(height / segH) * segH;

            for (int i = 0; i < TerminalBarCount; i++)
            {
                Border? bar = _terminalBars[i];
                if (bar == null)
                {
                    bar = new Border
                    {
                        CornerRadius = new CornerRadius(1),
                        IsHitTestVisible = false
                    };
                    _terminalBars[i] = bar;
                    TerminalSpectrumCanvas.Children.Add(bar);
                }

                // 归一化后再做一点感知提升（0.8 次幂），中间的段更容易看出起伏
                double norm = Math.Clamp(_terminalBandLevels[i] / _terminalSpecRef, 0.0, 1.0);
                double shaped = Math.Pow(norm, 0.8);

                // 高度取整到段：这就是"方块段"的来源
                int segCount = (int)Math.Round(shaped * segments);
                if (segCount < 1)
                {
                    segCount = 1; // 静音时留一格底，别整排消失
                }

                double barHeight = Math.Min(segCount * segH, maxSegH);

                Color accent = _waveAccentColor;
                byte alpha = (byte)(110 + Math.Round(norm * 145));
                bar.Background = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
                bar.Width = barWidth;
                bar.Height = barHeight;
                Canvas.SetLeft(bar, i * (barWidth + gap));
                Canvas.SetTop(bar, height - barHeight);
            }
        }

        /// <summary>电平条：填充用 RMS，右边数字也是 RMS 的 dB；PEAK 那行是峰值 dB。</summary>
        private void UpdateTerminalLevels()
        {
            if (!_terminalStageVisible)
            {
                return;
            }

            try
            {
                int channels = _audioEngine?.LevelMeterChannels ?? 0;
                bool got = channels > 0 && _audioEngine != null
                           && _audioEngine.TryGetLevels(_terminalPeakBuf, _terminalRmsBuf);

                float rmsL = got && channels > 0 ? _terminalRmsBuf[0] : 0f;
                float rmsR = got && channels > 1 ? _terminalRmsBuf[1] : rmsL;
                float peakL = got && channels > 0 ? _terminalPeakBuf[0] : 0f;
                float peakR = got && channels > 1 ? _terminalPeakBuf[1] : peakL;

                SetLevelBar(TerminalLevelLTrack, TerminalLevelLFill, rmsL);
                SetLevelBar(TerminalLevelRTrack, TerminalLevelRFill, rmsR);

                if (TerminalLevelLText != null)
                {
                    TerminalLevelLText.Text = FormatDb(rmsL);
                }

                if (TerminalLevelRText != null)
                {
                    TerminalLevelRText.Text = FormatDb(rmsR);
                }

                if (TerminalPeakText != null)
                {
                    TerminalPeakText.Text = "L " + FormatDb(peakL) + " / R " + FormatDb(peakR);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateTerminalLevels", caught);
            }
        }

        private static void SetLevelBar(Border? track, Border? fill, float value)
        {
            if (track == null || fill == null)
            {
                return;
            }

            double frac = DbToFraction(ToDb(value));
            fill.Width = Math.Max(0, track.ActualWidth * frac);
        }

        private static double ToDb(float linear)
            => linear <= 0.0001f ? -120.0 : 20.0 * Math.Log10(linear);

        /// <summary>-60dB ~ 0dB 映射到 0 ~ 1（-60 以下当静音）。</summary>
        private static double DbToFraction(double db)
            => Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);

        private static string FormatDb(float linear)
        {
            double db = ToDb(linear);
            return db <= -119.5 ? "-inf dB" : db.ToString("0.0") + " dB";
        }

        // =====================================================================
        // 右栏：相位图（L/R 李萨如）
        // =====================================================================

        /// <summary>
        /// 相位图：x = 左声道、y = 右声道。终端页有 150×150 的方形画布，
        /// 比波形槽那个 320×110 的扁条宽裕得多 —— 这就是当初把李萨如留给这一页的原因。
        /// </summary>
        private void DrawTerminalPhase()
        {
            if (!_terminalStageVisible || TerminalPhaseCanvas == null)
            {
                return;
            }

            double size = Math.Min(TerminalPhaseCanvas.ActualWidth, TerminalPhaseCanvas.ActualHeight);
            if (size <= 1)
            {
                return;
            }

            // 十字准星（一次性）：极客面板的"刻度"，也让无信号时能看出这块画布是活的。
            // 坐标必须按画布实际尺寸算 —— 画布从 150 放大到 220 后，写死的 75/150 会让准星
            // 偏到左上、和居中的李萨如曲线错位（用户实测反馈）。这里用 size 居中重画。
            if (TerminalPhaseCanvas.Children.Count == 0)
            {
                double half = size / 2;
                var dim = new SolidColorBrush(Color.FromArgb(255, 0x2A, 0x2D, 0x34));
                TerminalPhaseCanvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = half, X2 = size, Y2 = half,
                    Stroke = dim, StrokeThickness = 1, IsHitTestVisible = false
                });
                TerminalPhaseCanvas.Children.Add(new Line
                {
                    X1 = half, Y1 = 0, X2 = half, Y2 = size,
                    Stroke = dim, StrokeThickness = 1, IsHitTestVisible = false
                });
            }

            if (_terminalPhaseLine == null)
            {
                // 描边必须在这里就给上：这条线是懒创建的，ApplyTerminalAccent 那时它还不存在，
                // 少了这一行就是 Stroke=null —— 线画出来了但完全透明，看着就像"李萨如没生效"。
                _terminalPhaseLine = new Polyline
                {
                    Stroke = new SolidColorBrush(_waveAccentColor),
                    StrokeThickness = 1.2,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                TerminalPhaseCanvas.Children.Add(_terminalPhaseLine);
            }

            PointCollection points = new();
            // 自愈：启动时第一首歌的 SetStereoCapture(true) 可能打在尚未就绪的引擎上被空掉，
            // 导致"当前歌李萨如不动、切一首才有"（用户实测反馈）。这里每帧（终端可见时）再确保一次，
            // SpectrumAnalyzer.SetStereoCapture 幂等（值相同直接返回），开销可忽略。
            _audioEngine?.SetStereoCapture(true);
            // 去掉「必须正在播放」的硬门槛：只要终端页可见、且引擎真的在供立体声样本就画李萨如；
            // 否则画一条对角虚线表示「无相位数据」——多半是当前走系统 MediaPlayer（未走 HiFi 引擎），
            // 没有 L/R 样本可取。要看真实李萨如请切到 HiFi 引擎播放模式（终端页 STATE 显示 ENGINE）。
            bool got = _audioEngine != null
                       && _audioEngine.TryGetStereoSamples(_terminalLeft, _terminalRight);

            if (got)
            {
                double cx = size / 2;
                double cy = size / 2;
                // 自动增益：直接用原始样本幅度画，正常听音量（峰值 -10~-20 dBFS）下
                // 李萨如缩在中心只有一小团（用户实测反馈）。按本帧峰值归一化到接近满幅，
                // 平滑系数 0.25 防止音量波动时忽大忽小；增益上限 6 倍（≈+15.6dB），
                // 避免接近静音时把底噪放大成满屏乱线。
                double peak = 0;
                for (int i = 0; i < _terminalLeft.Length; i++)
                {
                    double a = Math.Abs(_terminalLeft[i]);
                    if (a > peak)
                    {
                        peak = a;
                    }

                    a = Math.Abs(_terminalRight[i]);
                    if (a > peak)
                    {
                        peak = a;
                    }
                }

                double gain = peak > 0.001 ? Math.Min(1.0 / peak, 6.0) : 1.0;
                _terminalPhaseGain = _terminalPhaseGain <= 0
                    ? gain
                    : _terminalPhaseGain + (gain - _terminalPhaseGain) * 0.25;

                double amp = size * 0.44 * _terminalPhaseGain;
                int step = _terminalLeft.Length / 256;
                for (int i = 0; i < _terminalLeft.Length; i += step)
                {
                    points.Add(new Point(cx + _terminalLeft[i] * amp, cy - _terminalRight[i] * amp));
                }
            }
            else
            {
                // 无信号：一条对角线（单声道/静音时李萨如本来就是这个形状，不是画错）
                points.Add(new Point(size * 0.25, size * 0.75));
                points.Add(new Point(size * 0.75, size * 0.25));
            }

            _terminalPhaseLine.Points = points;
        }

        // =====================================================================
        // 底部：播放头 + 队列 + 状态行
        // =====================================================================

        /// <summary>播放头：一排小方块，走过的点亮（代替普通进度条）。</summary>
        private void UpdateTerminalPlayhead()
        {
            if (!_terminalStageVisible || TerminalPlayheadCanvas == null)
            {
                return;
            }

            double width = TerminalPlayheadCanvas.ActualWidth;
            double height = TerminalPlayheadCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            TimeSpan position = TerminalCurrentPosition();
            TimeSpan duration = TerminalCurrentDuration();
            double ratio = duration > TimeSpan.Zero
                ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0)
                : 0.0;
            int filled = (int)Math.Round(ratio * TerminalPlayheadBlocks);

            double gap = 2;
            double blockWidth = Math.Max(2, (width - gap * (TerminalPlayheadBlocks - 1)) / TerminalPlayheadBlocks);
            Color accent = _waveAccentColor;

            for (int i = 0; i < TerminalPlayheadBlocks; i++)
            {
                Border? block = _terminalBlocks[i];
                if (block == null)
                {
                    block = new Border
                    {
                        CornerRadius = new CornerRadius(1),
                        IsHitTestVisible = false
                    };
                    _terminalBlocks[i] = block;
                    TerminalPlayheadCanvas.Children.Add(block);
                }

                bool on = i < filled;
                block.Background = on
                    ? new SolidColorBrush(accent)
                    : new SolidColorBrush(Color.FromArgb(255, 0x22, 0x25, 0x2B));
                block.Width = blockWidth;
                block.Height = height;
                Canvas.SetLeft(block, i * (blockWidth + gap));
                Canvas.SetTop(block, 0);
            }

            UpdateTerminalStatus(position, duration);
        }

        /// <summary>表头右侧：播放状态 + 时间码。</summary>
        private void UpdateTerminalStatus(TimeSpan position, TimeSpan duration)
        {
            try
            {
                bool playing = IsEnginePlayingNow || (_usingEnginePlayback && _audioEngine?.IsPlaying == true);
                string state = playing ? "PLAYING" : (_nowPlayingPath == null ? "STOPPED" : "PAUSED");

                if (TerminalPlaybackText != null)
                {
                    TerminalPlaybackText.Text = state;
                }

                if (TerminalStatusText != null)
                {
                    TerminalStatusText.Text = state + "   " + FormatTime(position) + " / " + FormatTime(duration);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateTerminalStatus", caught);
            }
        }

        /// <summary>
        /// 队列预览：当前曲目高亮，后面跟几首。只在签名变化时重建。
        /// 数据源优先级：真播放队列 _userPlaylist（右键"加入播放队列"或双击整张专辑形成的那个）→
        /// 队列为空时退回媒体库当前列表 _playlist。
        /// 注意不能直接用 _playlist：那是"当前分类视图里的曲目"（例如全部歌曲按字母排序），
        /// 不是播放队列 —— 之前就是错用这个，导致这里显示成一整库按字母排的歌。
        /// </summary>
        private void UpdateTerminalQueue(bool force = false)
        {
            if (!_terminalStageVisible || TerminalQueuePanel == null)
            {
                return;
            }

            try
            {
                bool fromQueue = _userPlaylist.Count > 0;
                var list = fromQueue ? _userPlaylist : _playlist;
                int index = fromQueue ? _userPlaylistIndex : _currentIndex;

                int count = list.Count;
                string signature = (fromQueue ? "Q" : "L") + count + ":" + index;
                if (!force && signature == _terminalQueueSignature)
                {
                    return;
                }

                _terminalQueueSignature = signature;
                TerminalQueuePanel.Children.Clear();

                if (TerminalQueueHeader != null)
                {
                    TerminalQueueHeader.Text = fromQueue
                        ? "QUEUE  ·  播放队列 " + count + " 首"
                        : "QUEUE  ·  媒体库顺序（未建队列）";
                }

                if (count == 0)
                {
                    TerminalQueuePanel.Children.Add(MakeQueueLine("—", "（队列为空）", false));
                    return;
                }

                // 从当前这首开始往后列 5 行；到了末尾就从 0 折回（跟播放顺序一致）
                int start = index >= 0 ? index : 0;
                const int rows = 5;
                for (int r = 0; r < Math.Min(rows, count); r++)
                {
                    int i = start + r;
                    if (i >= count)
                    {
                        i -= count;
                    }

                    PlaylistItem item = list[i];
                    string no = (i + 1).ToString("00");
                    string title = string.IsNullOrWhiteSpace(item.DisplayTitle)
                        ? System.IO.Path.GetFileNameWithoutExtension(item.FilePath)
                        : item.DisplayTitle;
                    TerminalQueuePanel.Children.Add(MakeQueueLine(no, title, i == index));
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateTerminalQueue", caught);
            }
        }

        private TextBlock MakeQueueLine(string number, string title, bool current)
        {
            Color accent = _waveAccentColor;
            return new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = current
                    ? new SolidColorBrush(accent)
                    : new SolidColorBrush(Color.FromArgb(255, 0x9A, 0xA2, 0xAE)),
                Text = number + "  " + title,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private TimeSpan TerminalCurrentPosition()
            => _usingEnginePlayback && _audioEngine != null
                ? _audioEngine.Position
                : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);

        private TimeSpan TerminalCurrentDuration()
        {
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                TimeSpan d = _playlist[_currentIndex].Duration;
                if (d > TimeSpan.Zero)
                {
                    return d;
                }
            }

            return TimeSpan.Zero;
        }

        /// <summary>每帧刷新（由 WaveformTimer_Tick 调用，非终端布局直接返回）。</summary>
        internal void UpdateTerminalTick()
        {
            if (!_terminalStageVisible)
            {
                return;
            }

            DrawTerminalSpectrum();
            UpdateTerminalLevels();
            DrawTerminalPhase();
            UpdateTerminalPlayhead();
            UpdateTerminalQueue();
        }

        /// <summary>画布尺寸变了要按新宽度重排柱子/方块（否则缩放窗口后会留一条空白）。</summary>
        private void TerminalCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_terminalStageVisible)
            {
                return;
            }

            DrawTerminalSpectrum();
            UpdateTerminalPlayhead();
        }
    }
}