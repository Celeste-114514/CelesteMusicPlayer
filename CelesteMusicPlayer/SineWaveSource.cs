using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 内存正弦测试音源：1kHz / 48kHz / 16bit / 2ch / 3 秒。
    /// 用途：音频设置面板「内核自检」按钮——不走音乐库、不碰曲库索引，
    /// 用 <see cref="MainWindow.PlayExternalFileAsync"/> 同一播放路径试听当前选的
    /// 独占输出内核（自研 / ECHO / 原生），验证「起播即出声、无爆音、无卡顿」。
    /// 生成的临时 WAV 放在 %TEMP%，播完即止，不污染任何列表。
    /// </summary>
    internal sealed class SineWaveSource : IWaveSourceProvider
    {
        public const int TestRate = 48000;
        public const int TestBits = 16;
        public const int TestChannels = 2;
        public const double TestSeconds = 3.0;
        public const double TestFreqHz = 1000.0;

        private readonly WaveFormat _format = new(TestRate, TestBits, TestChannels);
        private readonly byte[] _pcm;
        private int _pos;

        public SineWaveSource()
        {
            int frames = (int)(TestRate * TestSeconds);
            _pcm = new byte[frames * TestChannels * (TestBits / 8)];
            double amp = 0.5; // -6dBFS，留足余量防削波（顺便验证限幅不在链路里作怪）
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / TestRate;
                double v = Math.Sin(2.0 * Math.PI * TestFreqHz * t) * amp;
                short s = (short)Math.Round(v * 32767.0);
                for (int c = 0; c < TestChannels; c++)
                {
                    int off = (i * TestChannels + c) * 2;
                    _pcm[off] = (byte)(s & 0xFF);
                    _pcm[off + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        public WaveFormat WaveFormat => _format;

        public TimeSpan TotalTime => TimeSpan.FromSeconds(TestSeconds);

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => (_pos, _pcm.Length, true);

        public bool NextMounted => false;

        public int Read(byte[] buffer, int offset, int count)
        {
            int avail = _pcm.Length - _pos;
            if (avail <= 0) return 0;
            int n = Math.Min(count, avail);
            Buffer.BlockCopy(_pcm, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public void Seek(TimeSpan position)
        {
            long target = (long)(position.TotalSeconds * TestRate) * TestChannels * (TestBits / 8);
            if (target < 0) target = 0;
            if (target > _pcm.Length) target = _pcm.Length;
            _pos = (int)target;
        }

        /// <summary>把测试音写成标准 16bit PCM WAV 文件（供自检按钮走完整播放链路用）。</summary>
        public void WriteWav(string path)
        {
            using var writer = new WaveFileWriter(path, _format);
            writer.Write(_pcm, 0, _pcm.Length);
            writer.Flush();
        }
    }
}
