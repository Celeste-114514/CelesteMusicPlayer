using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 10 段峰值均衡波提供器：把源 PCM 逐采样过 10 段串联 PeakingEQ（BiQuadFilter），
    /// 输出与源相同的 WaveFormat。数据被 DSP 改变 —— 开启即非 bit-perfect（对齐 ECHO 的界定）。
    /// 供 ASIO / 共享（NAudio 输出路径）使用；WASAPI 原生独占保持 bit-perfect 直通不经过这里。
    /// 全 0 增益时各段 0 dB ≈ 数值直通。
    /// </summary>
    internal sealed class Eq10WaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        private readonly BiQuadFilter[] _filters = new BiQuadFilter[10];
        private readonly WaveFormat _format;
        private readonly int _channels;
        private readonly int _bytesPerSample;
        private readonly bool _isFloat;

        private static readonly float[] Centers = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

        public Eq10WaveProvider(IWaveProvider source, double[]? gainsDb)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _format = source.WaveFormat;
            _channels = _format.Channels;
            _isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat;
            _bytesPerSample = _format.BitsPerSample / 8;
            if (_bytesPerSample < 3 && !_isFloat)
            {
                _bytesPerSample = 2; // 最小 16bit；24/32 由 WaveFormat 决定
            }

            for (int i = 0; i < 10; i++)
            {
                double g = (gainsDb != null && i < gainsDb.Length) ? gainsDb[i] : 0.0;
                _filters[i] = BiQuadFilter.PeakingEQ(_format.SampleRate, Centers[i], 1.0f, (float)Math.Clamp(g, -12.0, 12.0));
            }
        }

        public WaveFormat WaveFormat => _format;

        /// <summary>播放中实时更新各段增益（无需重建 provider，下一次 Read 即生效）。null/全0 表示直通。</summary>
        public void UpdateGains(double[]? gainsDb)
        {
            for (int i = 0; i < _filters.Length; i++)
            {
                double g = (gainsDb != null && i < gainsDb.Length) ? gainsDb[i] : 0.0;
                _filters[i].SetPeakingEq(_format.SampleRate, Centers[i], 1.0f, (float)Math.Clamp(g, -12.0, 12.0));
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0)
            {
                return read;
            }

            int block = _format.BlockAlign;
            int frames = block > 0 ? read / block : 0;
            for (int f = 0; f < frames; f++)
            {
                int baseP = offset + f * block;
                for (int c = 0; c < _channels; c++)
                {
                    int p = baseP + c * BytesPerChannel(f);
                    float x = Decode(buffer, p);
                    float y = x;
                    for (int k = 0; k < _filters.Length; k++)
                    {
                        y = _filters[k].Transform(y);
                    }

                    if (y > 1f) y = 1f;
                    else if (y < -1f) y = -1f;
                    Encode(buffer, p, y);
                }
            }

            return read;
        }

        /// <summary>声道在当前帧内的字节跨距（帧内每个声道的样本字节数）。</summary>
        private int BytesPerChannel(int _) => _format.BitsPerSample / 8;

        private float Decode(byte[] b, int p)
        {
            if (_isFloat)
            {
                return BitConverter.ToSingle(b, p);
            }

            int bits = _format.BitsPerSample;
            if (bits == 32)
            {
                int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
                return v / 2147483648f;
            }

            if (bits == 24)
            {
                int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                if ((b[p + 2] & 0x80) != 0)
                {
                    v |= unchecked((int)0xFF000000);
                }

                return v / 8388608f;
            }

            short s = (short)(b[p] | (b[p + 1] << 8));
            return s / 32768f;
        }

        private void Encode(byte[] b, int p, float y)
        {
            if (_isFloat)
            {
                BitConverter.GetBytes(y).CopyTo(b, p);
                return;
            }

            int bits = _format.BitsPerSample;
            if (bits == 32)
            {
                int v = (int)(y * 2147483647f);
                b[p] = (byte)(v & 0xFF);
                b[p + 1] = (byte)((v >> 8) & 0xFF);
                b[p + 2] = (byte)((v >> 16) & 0xFF);
                b[p + 3] = (byte)((v >> 24) & 0xFF);
                return;
            }

            if (bits == 24)
            {
                int v = (int)(y * 8388607f);
                b[p] = (byte)(v & 0xFF);
                b[p + 1] = (byte)((v >> 8) & 0xFF);
                b[p + 2] = (byte)((v >> 16) & 0xFF);
                return;
            }

            short s = (short)(y * 32767f);
            b[p] = (byte)(s & 0xFF);
            b[p + 1] = (byte)((s >> 8) & 0xFF);
        }
    }
}
