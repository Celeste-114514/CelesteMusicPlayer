using System;

namespace CelesteMusicPlayer;

/// <summary>
/// 实时变速引擎（2026-10-15 用户拍板「拖动即时变声、音调变/不变可选」）。
///
/// 为什么放在托管侧而不是原生内核：播放音频（共享 / WASAPI 独占 / ASIO）全部经过
/// <see cref="ManagedDspSourceProvider"/> 这一个卡口，在它里面做变速天然全覆盖；
/// 同时不必重编 celeste_dsp_core.dll（本机构建工具链状态不明），也不碰
/// native/dsp-core 那批与 ECHO 同源（AGPL）的代码，不加重许可证问题。
///
/// 两种模式（用户可选）：
///  · 保调（默认）：WSOLA 时域拉伸。窗口 ≈46ms、50% 重叠、±6ms 搜索窗；
///    粗定位用 4 倍抽取（便宜），再在全速率上 ±4 样本细定位。总成本约几百万乘加/秒，
///    渲染线程无压力。音调不变、速度变。
///  · 变调：三次 Hermite 插值重采样。音调随倍率升降（rate&gt;1 变尖、rate&lt;1 变闷），
///    即 nightcore / daycore 效果，零伪影、成本可忽略。
///
/// 数据结构：输入用 FIFO（交错 float 线性缓冲 + 头部前移 + 定期压实）。
/// 倍率变化时不需要重载、不需要重新转码——下一块直接按新倍率生成，拖动即生效。
///
/// 线程约定：所有方法只在音频渲染线程调用（倍率/模式由 UI 线程经 volatile 字段写入，
/// 由 <see cref="ManagedDspSourceProvider.SetPlaybackSpeed"/> 在锁外原子替换）。
/// 全部缓冲预分配，热路径零分配。
/// </summary>
internal sealed class SpeedStretch
{
    /// <summary>保调（WSOLA）还是变调（重采样）。默认保调。UI 线程写、音频线程读。</summary>
    public bool PreservePitch
    {
        get => _preservePitch;
        set => _preservePitch = value;
    }

    private readonly int _channels;
    // 不用 volatile：C# 不允许 volatile double（只支持 int/float/bool/引用等）。
    // x64 上 double 读写本身原子、不会撕裂；且倍率按"下一个音频块"生效，
    // 拖动滑块时几毫秒的过渡完全无感。UI 线程写、音频线程读的约定不变。
    private double _rate = 1.0;
    private volatile bool _preservePitch = true;

    // ---------- 输入 FIFO（交错 float） ----------
    private float[] _fifo = Array.Empty<float>();
    private int _fifoStart;   // 第一个有效帧在 _fifo 中的下标
    private int _fifoFrames;  // 有效帧数
    private int _fifoCap;     // 容量（帧）

    /// <summary>FIFO 中待处理的输入帧数。</summary>
    public int FramesAvailable => _fifoFrames;

    // ---------- WSOLA 参数与状态 ----------
    private int _win;     // 分析/合成窗长（样本/帧）
    private int _hop;     // 合成跳 = win/2
    private int _search;  // 搜索半径（帧）
    private float[] _window = Array.Empty<float>();  // Hann 窗（win）
    private float[] _hist = Array.Empty<float>();    // 上一合成帧输出后半段（hop*ch）＝相关模板
    private float[] _ola = Array.Empty<float>();     // 重叠相加累加器（hop*ch）
    private float[] _frame = Array.Empty<float>();   // 当前合成帧（win*ch）
    private float[] _pending = Array.Empty<float>(); // 半成品输出（hop*ch）
    private int _pendingCount;
    private double _anaPos;       // 下一合成帧的分析位置（FIFO 相对，帧）
    private bool _wsolaPrimed;    // 是否已产出过一帧（有模板才能做相关）

    // ---------- 重采样状态 ----------
    private double _readPos;      // 读位置（FIFO 相对，帧，带小数）

    private volatile bool _prepared;

    /// <summary>是否已按当前采样率准备好（未就绪时音频线程先 Prepare 再干活）。</summary>
    public bool Prepared => _prepared;

    public SpeedStretch(int channels)
    {
        _channels = Math.Max(1, channels);
    }

    /// <summary>
    /// 按采样率定参数。只在音频线程调（UI 线程的 SetPlaybackSpeed 只在本对象尚未就绪时
    /// 打标，真正的 Prepare 推迟到第一个音频块，避免与渲染线程抢数组）。
    /// </summary>
    public void Prepare(double sampleRate)
    {
        double sr = sampleRate > 8000 ? sampleRate : 44100.0;
        int win = (int)Math.Round(sr * 0.046);
        win = Math.Clamp(win, 1024, 8192);
        if (win % 2 != 0) win += 1;
        _win = win;
        _hop = win / 2;
        _search = Math.Clamp((int)Math.Round(sr * 0.006), 32, 2048);

        _window = new float[win];
        for (int n = 0; n < win; n++)
        {
            _window[n] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * n / (win - 1)));
        }

        int ch = _channels;
        _hist = new float[_hop * ch];
        _ola = new float[_hop * ch];
        _frame = new float[win * ch];
        _pending = new float[_hop * ch];
        Reset();
        _prepared = true;
    }

    /// <summary>设倍率（0.5~2.0）。UI 线程调；渲染线程下一块自然读到新值。</summary>
    public void SetRate(double rate)
    {
        _rate = double.IsFinite(rate) ? Math.Clamp(rate, 0.5, 2.0) : 1.0;
    }

    public double Rate => _rate;

    /// <summary>生成一个输出块需要多少输入帧（消费量 + 前瞻窗）。供 provider 决定拉多少源数据。</summary>
    public int RequiredInputFrames(int outFrames)
    {
        double consume = outFrames * _rate;
        return (int)Math.Ceiling(consume) + _win + _search + 8;
    }

    /// <summary>清空全部状态（seek / 换曲 / 停播）。FIFO 内容丢弃——调用方保证此时不在续播。</summary>
    public void Reset()
    {
        _fifoStart = 0;
        _fifoFrames = 0;
        _anaPos = 0.0;
        _readPos = 0.0;
        _wsolaPrimed = false;
        _pendingCount = 0;
        Array.Clear(_hist, 0, _hist.Length);
        Array.Clear(_ola, 0, _ola.Length);
    }

    /// <summary>把解码后的源帧追加进 FIFO（provider 拉源后调用）。</summary>
    public void PushFrames(float[] src, int frames)
    {
        if (frames <= 0) return;
        int ch = _channels;
        int samples = frames * ch;
        EnsureCapacity(samples);
        Array.Copy(src, 0, _fifo, (_fifoStart + _fifoFrames) * ch, samples);
        _fifoFrames += frames;
    }

    /// <summary>丢弃 FIFO 前 n 帧（已消费）。</summary>
    public void Consume(int frames)
    {
        if (frames <= 0) return;
        if (frames > _fifoFrames) frames = _fifoFrames;
        _fifoStart += frames;
        _fifoFrames -= frames;
    }

    /// <summary>
    /// 生成 outFrames 帧到 dest（交错）。FIFO 不足的位置按 0 补（曲尾自然淡出到静音）。
    /// 返回实际生成帧数（恒等于 outFrames，除非 outFrames&lt;=0）。
    /// </summary>
    public int Generate(float[] dest, int outFrames)
    {
        if (outFrames <= 0) return 0;
        return PreservePitch ? GenerateWsola(dest, outFrames) : GenerateResample(dest, outFrames);
    }

    // ==================== 变调：三次 Hermite 重采样 ====================

    private int GenerateResample(float[] dest, int outFrames)
    {
        int ch = _channels;
        for (int f = 0; f < outFrames; f++)
        {
            int i0 = (int)_readPos;
            double t = _readPos - i0;
            float tf = (float)t;
            for (int c = 0; c < ch; c++)
            {
                float x0 = Fifo(i0 - 1, c);
                float x1 = Fifo(i0, c);
                float x2 = Fifo(i0 + 1, c);
                float x3 = Fifo(i0 + 2, c);
                // Catmull-Rom：四次插值，比重采样里的线性插值干净得多（高频不塌）
                float a0 = -0.5f * x0 + 1.5f * x1 - 1.5f * x2 + 0.5f * x3;
                float a1 = x0 - 2.5f * x1 + 2f * x2 - 0.5f * x3;
                float a2 = -0.5f * x0 + 0.5f * x2;
                dest[f * ch + c] = ((a0 * tf + a1) * tf + a2) * tf + x1;
            }

            _readPos += _rate;
        }

        // 消费：读过的不再需要，但保留 i0-1 帧供下一块第一样本做插值
        int advance = (int)Math.Floor(_readPos) - 1;
        if (advance > 0)
        {
            _readPos -= advance;
            Consume(advance);
        }
        return outFrames;
    }

    // ==================== 保调：WSOLA ====================

    private int GenerateWsola(float[] dest, int outFrames)
    {
        int ch = _channels;
        int produced = 0;
        while (produced < outFrames)
        {
            if (_pendingCount <= 0)
            {
                SynthesizeFrame();
                _pendingCount = _hop;
            }

            int take = Math.Min(_pendingCount, outFrames - produced);
            Array.Copy(_pending, (_hop - _pendingCount) * ch, dest, produced * ch, take * ch);
            produced += take;
            _pendingCount -= take;
        }
        return outFrames;
    }

    /// <summary>合成一个 hop 帧的段：选最佳对齐 → 加窗分析帧 → 重叠相加 → 更新模板/分析位置。</summary>
    private void SynthesizeFrame()
    {
        int ch = _channels;

        // 1) 最佳对齐偏移：上一帧输出后半段 与 候选位置输入 的相关最大处
        int delta = 0;
        if (_wsolaPrimed)
        {
            delta = BestDelta();
        }

        // 2) 加窗分析帧 frame[n] = win[n] * x[anaPos+delta+n]
        int baseIdx = (int)Math.Floor(_anaPos) + delta;
        for (int n = 0; n < _win; n++)
        {
            float w = _window[n];
            int src = baseIdx + n;
            for (int c = 0; c < ch; c++)
            {
                _frame[n * ch + c] = w * Fifo(src, c);
            }
        }

        // 3) 输出段 = 上一帧残留(_ola) + 本帧前半；新残留 = 本帧后半
        for (int i = 0; i < _hop; i++)
        {
            for (int c = 0; c < ch; c++)
            {
                float head = _frame[i * ch + c];
                float tail = _frame[(i + _hop) * ch + c];
                float o = _ola[i * ch + c] + head;
                _pending[i * ch + c] = o;
                _ola[i * ch + c] = tail;
                // 模板 = 本帧实际输出的后半段（自然延续），供下一帧相关
                _hist[i * ch + c] = o + tail;
            }
        }

        // 4) 分析位置前进 hop*rate，FIFO 同步消费
        _anaPos += _hop * _rate;
        int consume = (int)Math.Floor(_anaPos);
        if (consume > 0)
        {
            _anaPos -= consume;
            Consume(consume);
        }
        _wsolaPrimed = true;
    }

    /// <summary>
    /// 在 ±_search 内找最佳对齐偏移。粗定位步长 4（抽取 4 倍，成本 1/16），
    /// 再在粗最优 ±4 内做全速率细定位。相关用点积（未归一化）——WSOLA 标准做法。
    /// </summary>
    private int BestDelta()
    {
        int ch = _channels;
        int anaInt = (int)Math.Floor(_anaPos);

        int bestDelta = 0;
        double bestScore = double.NegativeInfinity;
        for (int d = -_search; d <= _search; d += 4)
        {
            double score = 0.0;
            int baseIdx = anaInt + d;
            for (int i = 0; i < _hop; i += 4)
            {
                for (int c = 0; c < ch; c++)
                {
                    score += _hist[i * ch + c] * Fifo(baseIdx + i, c);
                }
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestDelta = d;
            }
        }

        // 细定位：全速率、±4
        double fineBest = double.NegativeInfinity;
        int fineDelta = bestDelta;
        for (int d = bestDelta - 4; d <= bestDelta + 4; d++)
        {
            if (d < -_search || d > _search) continue;
            double score = 0.0;
            int baseIdx = anaInt + d;
            for (int i = 0; i < _hop; i++)
            {
                for (int c = 0; c < ch; c++)
                {
                    score += _hist[i * ch + c] * Fifo(baseIdx + i, c);
                }
            }
            if (score > fineBest)
            {
                fineBest = score;
                fineDelta = d;
            }
        }
        return fineDelta;
    }

    // ==================== FIFO 工具 ====================

    /// <summary>取 FIFO 中第 frameIdx 帧第 c 声道；越界按 0（曲尾/启动初期）。</summary>
    private float Fifo(int frameIdx, int c)
    {
        if (frameIdx < 0 || frameIdx >= _fifoFrames) return 0f;
        return _fifo[(_fifoStart + frameIdx) * _channels + c];
    }

    /// <summary>保证 FIFO 还能再收 samples 个样本；不够就先压实（前移），再不够就扩容。</summary>
    private void EnsureCapacity(int samples)
    {
        int ch = _channels;
        int needFrames = (samples + ch - 1) / ch;
        if (_fifoStart + _fifoFrames + needFrames <= _fifoCap) return;

        if (_fifoFrames + needFrames <= _fifoCap)
        {
            // 前面消费掉的空间够用：把剩余整体搬到数组头部
            if (_fifoFrames > 0 && _fifoStart > 0)
            {
                Array.Copy(_fifo, _fifoStart * ch, _fifo, 0, _fifoFrames * ch);
            }
            _fifoStart = 0;
            return;
        }

        int newCap = Math.Max(_fifoCap * 2, _fifoFrames + needFrames + _win + _search + 4096);
        var next = new float[newCap * ch];
        if (_fifoFrames > 0)
        {
            Array.Copy(_fifo, _fifoStart * ch, next, 0, _fifoFrames * ch);
        }
        _fifo = next;
        _fifoCap = newCap;
        _fifoStart = 0;
    }
}
