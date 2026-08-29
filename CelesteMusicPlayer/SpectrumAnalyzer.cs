using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 实时频谱分析器：渲染线程把 post-DSP 的浮点样本 <see cref="Push"/> 进单声道环形缓冲，
    /// UI 线程按帧调用 <see cref="TryCompute"/> 做 Hann 加窗 FFT + 对数分频，
    /// 输出每根柱子的 0..1 电平。
    /// <para>
    /// 设计要点：FFT 只在 UI 线程执行（2048 点约几十微秒，30fps 下可忽略），
    /// 渲染线程只做一次声道降混 + 环形缓冲写入，不分配、不阻塞音频。
    /// 这样即使把频谱挂在 bit-perfect 直通路径上，也只是多一次解码，输出字节仍然原样不变。
    /// </para>
    /// </summary>
    public sealed class SpectrumAnalyzer
    {
        private const int DefaultFftSize = 4096;
        private const double FloorDb = -72.0;   // 显示下限：低于此电平视为 0
        private const double TiltDbPerOctave = 2.5; // 高频提升：音乐本身高频能量衰减，不补一点会只看到低频在跳
        private const double TiltRefHz = 500.0;
        private const double MinHz = 32.0;      // 分频下限（低于 32Hz 人耳基本只剩"轰"，显示意义不大）

        private readonly object _gate = new();
        private readonly int _fftSize;
        private readonly float[] _ring;         // 单声道环形缓冲
        private int _writePos;
        private bool _hasData;

        private int _sampleRate;
        private bool _enabled;

        // ↓↓↓ 以下仅供 UI 线程访问（TryCompute 内），无需加锁
        private readonly float[] _work;         // 取出的最近 N 个样本
        private readonly CF[] _fft;             // FFT 工作区（就地变换，复用不重新分配）
        private readonly float[] _window;       // Hann 窗
        private readonly float[] _mag;          // 每个频点的幅度
        private double[] _smooth;               // 每根柱子的平滑值（快起慢落；柱子数变化时重建）
        private int _bandCount;
        private int[] _bandLo = Array.Empty<int>();  // 每根柱子覆盖的频点区间（左闭）
        private int[] _bandHi = Array.Empty<int>();  // 右闭
        private float[] _tilt = Array.Empty<float>(); // 每根柱子的高频补偿 dB

        public SpectrumAnalyzer(int bandCount, int fftSize = DefaultFftSize)
        {
            // FFT 长度必须是 2 的幂，这里向上取到最近的合法值
            int n = 2;
            while (n < fftSize && n < 1 << 20)
            {
                n <<= 1;
            }

            _fftSize = n;

            _ring = new float[n * 2];
            _work = new float[n];
            _fft = new CF[n];
            _window = new float[n];
            _mag = new float[n / 2];

            for (int i = 0; i < n; i++)
            {
                // Hann 窗：抑制非整周期截断造成的频谱泄漏
                _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1))));
            }

            _bandCount = Math.Max(1, bandCount);
            _smooth = new double[_bandCount];
        }

        /// <summary>当前柱子数量。</summary>
        public int BandCount => _bandCount;

        /// <summary>是否已有足够的样本可算频谱。</summary>
        public bool HasData
        {
            get { lock (_gate) { return _hasData; } }
        }

        /// <summary>
        /// 开始一段播放时调用：按声道数/采样率/柱子数重建内部表。
        /// 采样率取 DSP 链入口（SRC 之前）的源采样率，保证频率映射准确。
        /// </summary>
        public void Reset(int channels, int sampleRate, int bandCount)
        {
            if (bandCount > 0 && bandCount != _bandCount)
            {
                _bandCount = bandCount;
                _smooth = new double[_bandCount];
            }

            _sampleRate = sampleRate;
            _enabled = channels > 0 && sampleRate > 0;

            lock (_gate)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _writePos = 0;
                _hasData = false;
            }

            Array.Clear(_smooth, 0, _smooth.Length);
            BuildBandTable();
        }

        /// <summary>开关采样写入。关闭后渲染线程不再做任何频谱相关工作（零开销）。</summary>
        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                lock (_gate)
                {
                    _hasData = false;
                    _writePos = 0;
                }

                Array.Clear(_smooth, 0, _smooth.Length);
            }
        }

        /// <summary>停止播放时清空历史样本，避免残留画面。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _writePos = 0;
                _hasData = false;
            }

            Array.Clear(_smooth, 0, _smooth.Length);
        }

        /// <summary>渲染线程：喂一段交错排列的浮点样本（长度 n = frames * channels）。</summary>
        public void Push(float[] samples, int n, int channels)
        {
            if (!_enabled || channels <= 0 || n <= 0 || samples == null)
            {
                return;
            }

            int frames = n / channels;
            if (frames <= 0)
            {
                return;
            }

            float[] ring = _ring;
            int cap = ring.Length;

            lock (_gate)
            {
                int pos = _writePos;
                int i = 0;

                while (i < frames)
                {
                    int chunk = frames - i;
                    int room = cap - pos;
                    if (chunk > room)
                    {
                        chunk = room;
                    }

                    if (channels == 1)
                    {
                        Array.Copy(samples, i, ring, pos, chunk);
                    }
                    else
                    {
                        float inv = 1.0f / channels;
                        int s = i * channels;
                        for (int k = 0; k < chunk; k++, s += channels)
                        {
                            float acc = 0f;
                            for (int c = 0; c < channels; c++)
                            {
                                acc += samples[s + c];
                            }

                            ring[pos + k] = acc * inv;
                        }
                    }

                    i += chunk;
                    pos += chunk;
                    if (pos >= cap)
                    {
                        pos = 0;
                    }
                }

                _writePos = pos;
                _hasData = true;
            }
        }

        /// <summary>
        /// UI 线程：算出每根柱子的电平（0..1）写入 <paramref name="bandsOut"/>，返回是否取到有效频谱。
        /// 返回 false 时调用方应回退到装饰性动画（例如 DSD 直出、未播放）。
        /// </summary>
        public bool TryCompute(float[] bandsOut)
        {
            if (bandsOut == null || bandsOut.Length == 0)
            {
                return false;
            }

            if (_sampleRate <= 0 || _bandLo.Length != _bandCount)
            {
                return false;
            }

            int n = _fftSize;
            int cap = _ring.Length;

            // 1) 取出最近 N 个单声道样本（这段锁很短，只是两次 Array.Copy）
            lock (_gate)
            {
                if (!_hasData)
                {
                    return false;
                }

                int end = _writePos;
                int start = end - n;
                if (start < 0)
                {
                    start += cap;
                }

                int first = n;
                if (first > cap - start)
                {
                    first = cap - start;
                }

                Array.Copy(_ring, start, _work, 0, first);
                if (first < n)
                {
                    Array.Copy(_ring, 0, _work, first, n - first);
                }
            }

            // 2) 加窗 + FFT
            CF[] fft = _fft;
            float[] win = _window;
            float[] work = _work;
            for (int i = 0; i < n; i++)
            {
                fft[i].Re = work[i] * win[i];
                fft[i].Im = 0.0;
            }

            FftProcessor.Transform(fft, false);

            // 3) 幅度谱（归一化到满幅正弦 = 1.0）
            int bins = n / 2;
            float[] mag = _mag;
            float norm = 2.0f / n;
            for (int i = 0; i < bins; i++)
            {
                double re = fft[i].Re;
                double im = fft[i].Im;
                mag[i] = (float)Math.Sqrt(re * re + im * im) * norm;
            }

            // 4) 对数分频 → 每根柱子取区间内最大值，转 dB 并做高频补偿
            int count = Math.Min(_bandCount, bandsOut.Length);
            double scale = 1.0 / -FloorDb;
            for (int b = 0; b < count; b++)
            {
                int lo = _bandLo[b];
                int hi = _bandHi[b];
                float m = 0f;
                for (int i = lo; i <= hi && i < bins; i++)
                {
                    if (mag[i] > m)
                    {
                        m = mag[i];
                    }
                }

                double db;
                if (m <= 1e-9f)
                {
                    db = FloorDb;
                }
                else
                {
                    db = 20.0 * Math.Log10(m);
                    if (double.IsNaN(db) || db < FloorDb)
                    {
                        db = FloorDb;
                    }
                }

                // 先按线性电平铺满 0..1，再用高频补偿做"倍率"补抬。
                // 注意不能把 tilt 直接加到 dB 上：那样静音时（db 已被压到 FloorDb）
                // 高频柱会被凭空抬到 tilt/72 ≈ 17%，柱子永远落不到底。
                double v0 = (db - FloorDb) * scale;
                if (v0 < 0.0) v0 = 0.0; else if (v0 > 1.0) v0 = 1.0;

                double v = v0 * (1.0 + _tilt[b] * scale);
                if (v < 0.0) v = 0.0; else if (v > 1.0) v = 1.0;

                // 快起慢落：瞬间冲上去，慢慢落下来，比纯线性平滑更像真实频谱仪
                double prev = _smooth[b];
                if (v > prev)
                {
                    _smooth[b] = v;
                }
                else
                {
                    _smooth[b] = prev * 0.70 + v * 0.30;
                }

                bandsOut[b] = (float)_smooth[b];
            }

            for (int b = count; b < bandsOut.Length; b++)
            {
                bandsOut[b] = 0f;
            }

            return true;
        }

        /// <summary>按当前采样率/柱子数预计算每根柱子的频点区间与高频补偿（Reset 时调用一次）。</summary>
        private void BuildBandTable()
        {
            int count = _bandCount;
            int bins = _fftSize / 2;
            if (_bandLo.Length != count)
            {
                _bandLo = new int[count];
                _bandHi = new int[count];
                _tilt = new float[count];
            }

            double nyquist = _sampleRate > 0 ? _sampleRate / 2.0 : 22050.0;
            double fMax = Math.Min(18000.0, nyquist * 0.95);
            double binHz = (_sampleRate > 0 ? _sampleRate : 44100.0) / (double)_fftSize;

            // 分频下限随分辨率上浮：高采样率下单个频点跨度很大（384k/4096 → 93.75Hz/点），
            // 若死守 32Hz，底部十几根柱子会各自占一个频点，整个低频段被"拉偏"两三个柱。
            // 这里保证最低那根柱的带宽不小于一个频点，低频只损失小半级音程，换回准确映射。
            double fMin = Math.Max(MinHz, binHz * 4.0);
            fMin = Math.Min(fMin, fMax * 0.25);

            // 低频段的对数带宽往往窄于一个 FFT 频点（44.1k/4096 → 10.8Hz/点），
            // 若各自独立取整，相邻柱子会共用同一个频点 —— 一份能量被两根柱重复计入，
            // 低频峰值就会往右漂。这里强制区间单调推进：每根柱子从上根柱子结束的
            // 下一个频点开始，保证互不重叠且连续覆盖（低频若干柱会共用频点，呈平台状，
            // 这是分辨率的物理上限，比重复计数导致的漂移要好）。
            int next = 1;
            for (int b = 0; b < count; b++)
            {
                // 对数分频：低频段窄、高频段宽，符合人耳对频率的感知
                double f0 = fMin * Math.Pow(fMax / fMin, b / (double)count);
                double f1 = fMin * Math.Pow(fMax / fMin, (b + 1) / (double)count);

                int lo = next;
                int hi = (int)Math.Ceiling(f1 / binHz) - 1;

                if (hi < lo)
                {
                    hi = lo;
                }

                if (hi > bins - 1)
                {
                    hi = bins - 1;
                }

                if (lo > bins - 1)
                {
                    lo = bins - 1;
                    hi = bins - 1;
                }
                else if (lo < 1)
                {
                    lo = 1;
                }

                _bandLo[b] = lo;
                _bandHi[b] = hi;
                next = hi + 1;

                // 高频补偿：以 500Hz 为基准，每升高一个八度补 TiltDbPerOctave dB
                double fc = Math.Sqrt(f0 * f1);
                _tilt[b] = (float)(TiltDbPerOctave * Math.Log2(fc / TiltRefHz));
            }
        }
    }
}
