using System;

namespace CelesteMusicPlayer
{
    /// <summary>线程安全实时电平表：测量信号（post-DSP，即实际送往输出的信号）的每声道峰值与 RMS。
    /// 渲染线程在 Read 中调用 <see cref="Update"/> 写入，UI 线程在定时器里调用 <see cref="CopyTo"/> 读取，
    /// 两者通过锁隔离，互不阻塞。</summary>
    public sealed class LevelMeter
    {
        private readonly object _gate = new();
        private float[] _peak = Array.Empty<float>(); // 线性峰值（0..~1，限幅期间可能短暂 &gt;1）
        private float[] _rms = Array.Empty<float>();   // 线性 RMS

        /// <summary>声道数（即电平条数量）。</summary>
        public int Channels
        {
            get { lock (_gate) { return _peak.Length; } }
        }

        /// <summary>按声道数重建内部缓冲（新播放会话/声道数变化时调用）。</summary>
        public void Reset(int channels)
        {
            if (channels <= 0) channels = 0;
            lock (_gate)
            {
                _peak = new float[channels];
                _rms = new float[channels];
            }
        }

        /// <summary>用一段已解码为 float 的样本（交错排列，长度 n=frames*channels）更新每声道电平。</summary>
        public void Update(float[] samples, int n, int channels)
        {
            if (channels <= 0 || n <= 0) return;
            lock (_gate)
            {
                if (_peak.Length != channels) { _peak = new float[channels]; _rms = new float[channels]; }
                for (int c = 0; c < channels; c++)
                {
                    float peak = 0f;
                    double sumSq = 0.0;
                    int count = 0;
                    for (int i = c; i < n; i += channels)
                    {
                        float s = samples[i];
                        float a = s < 0 ? -s : s;
                        if (a > peak) peak = a;
                        sumSq += s * s;
                        count++;
                    }

                    _peak[c] = peak;
                    _rms[c] = count > 0 ? (float)Math.Sqrt(sumSq / count) : 0f;
                }
            }
        }

        /// <summary>把当前电平快照拷贝到调用方提供的数组（长度不足则只拷贝前 N 个声道）。</summary>
        public void CopyTo(float[] peakOut, float[] rmsOut)
        {
            lock (_gate)
            {
                int k = Math.Min(_peak.Length, peakOut.Length);
                if (k > 0) Array.Copy(_peak, 0, peakOut, 0, k);
                int m = Math.Min(_rms.Length, rmsOut.Length);
                if (m > 0) Array.Copy(_rms, 0, rmsOut, 0, m);
            }
        }
    }
}
