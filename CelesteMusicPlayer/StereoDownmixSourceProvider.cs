using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 多声道（6ch/8ch 等）→ 立体声降混器。
    ///
    /// 背景：部分音源（如游戏 OST 的 5.1/7.1 AAC）经 ffmpeg 转码后是 6/8 声道 PCM。
    /// WASAPI 共享模式下，立体声设备的 IAudioClient::Initialize 会拒绝这种格式，
    /// 返回 E_INVALIDARG，.NET 侧表现为 ArgumentException
    /// "Value does not fall within the expected range."，整首歌直接播放失败。
    /// （日志特征：仅 8ch 曲目失败，2ch 曲目正常。）
    ///
    /// 处理：共享输出前把多于 2 声道的 PCM 合并为 2 声道，采样率/位深/编码与源一致，
    /// 只合并声道、不做重采样，避免额外音质损失。
    /// 独占/DoP 通道不经过本类（保持 bit-perfect 原样直通）。
    /// </summary>
    internal sealed class StereoDownmixSourceProvider : IWaveProvider, IWaveSourceProvider
    {
        private const float SurroundWeight = 0.7071f;

        private readonly IWaveSourceProvider _source;
        private readonly WaveFormat _outFormat;
        private readonly int _srcChannels;
        private readonly int _srcBlockAlign;
        private readonly int _outBlockAlign;
        private readonly bool _isFloat;
        private readonly int _bits;
        private readonly bool _mono;

        // 每个源声道归属：0=左, 1=右, -1=丢弃（LFE 不参与降混，避免低频过载）
        private readonly int[] _side;
        private readonly float[] _weight;
        private readonly float _normGain;

        private byte[] _readBuf = Array.Empty<byte>();
        private float[] _frame = Array.Empty<float>();

        public StereoDownmixSourceProvider(IWaveSourceProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat f = source.WaveFormat;
            _srcChannels = f.Channels;
            _bits = f.BitsPerSample;
            _isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            _srcBlockAlign = f.BlockAlign;

            _outFormat = _isFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, 2)
                : new WaveFormat(f.SampleRate, f.BitsPerSample, 2);
            _outBlockAlign = _outFormat.BlockAlign;

            _side = new int[_srcChannels];
            _weight = new float[_srcChannels];
            for (int c = 0; c < _srcChannels; c++)
            {
                _side[c] = -1;
                _weight[c] = 0f;
            }

            float sumL = 0f;
            float sumR = 0f;
            if (_srcChannels == 1)
            {
                // 单声道：左右同幅复制
                _side[0] = 0;
                _weight[0] = 1f;
                sumL = 1f;
                sumR = 1f;
                _mono = true;
            }
            else
            {
                _side[0] = 0;
                _weight[0] = 1f;
                _side[1] = 1;
                _weight[1] = 1f;
                sumL = 1f;
                sumR = 1f;

                for (int c = 2; c < _srcChannels; c++)
                {
                    // 标准 5.1/7.1 布局里索引 3 是 LFE（超低音），降混时丢弃
                    if (_srcChannels >= 6 && c == 3)
                    {
                        continue;
                    }

                    _side[c] = (c % 2 == 0) ? 0 : 1;
                    _weight[c] = SurroundWeight;
                    if (_side[c] == 0)
                    {
                        sumL += SurroundWeight;
                    }
                    else
                    {
                        sumR += SurroundWeight;
                    }
                }
            }

            // 按实际累加权重归一化，保证降混后不会因叠加而削波
            float peak = Math.Max(sumL, sumR);
            _normGain = peak > 0f ? 1f / peak : 1f;
        }

        public WaveFormat WaveFormat => _outFormat;

        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public void Seek(TimeSpan position) => _source.Seek(position);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_outBlockAlign <= 0 || _srcBlockAlign <= 0)
            {
                return 0;
            }

            int frames = count / _outBlockAlign;
            if (frames <= 0)
            {
                return 0;
            }

            int srcBytes = frames * _srcBlockAlign;
            if (_readBuf.Length < srcBytes)
            {
                _readBuf = new byte[srcBytes * 2];
            }

            int read = _source.Read(_readBuf, 0, srcBytes);
            if (read <= 0)
            {
                return 0;
            }

            frames = read / _srcBlockAlign;
            if (frames <= 0)
            {
                return 0;
            }

            if (_frame.Length < _srcChannels)
            {
                _frame = new float[_srcChannels];
            }

            int bi = 0;
            int bo = offset;
            int bytesPerSample = _bits / 8;
            for (int n = 0; n < frames; n++, bi += _srcBlockAlign, bo += _outBlockAlign)
            {
                for (int c = 0; c < _srcChannels; c++)
                {
                    _frame[c] = DecodeSample(_readBuf, bi + c * bytesPerSample);
                }

                float l;
                float r;
                if (_mono)
                {
                    l = _frame[0];
                    r = _frame[0];
                }
                else
                {
                    l = _frame[0];
                    r = _frame[1];
                    for (int c = 2; c < _srcChannels; c++)
                    {
                        int side = _side[c];
                        if (side < 0)
                        {
                            continue;
                        }

                        float v = _frame[c] * _weight[c];
                        if (side == 0)
                        {
                            l += v;
                        }
                        else
                        {
                            r += v;
                        }
                    }
                }

                l *= _normGain;
                r *= _normGain;
                if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
                if (r > 1f) r = 1f; else if (r < -1f) r = -1f;

                EncodeSample(buffer, bo, l);
                EncodeSample(buffer, bo + bytesPerSample, r);
            }

            return frames * _outBlockAlign;
        }

        private float DecodeSample(byte[] b, int i)
        {
            if (_isFloat)
            {
                return BitConverter.ToSingle(b, i);
            }

            if (_bits == 32)
            {
                return (b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)) / 2147483648f;
            }

            if (_bits == 24)
            {
                int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
                if ((b[i + 2] & 0x80) != 0)
                {
                    v |= unchecked((int)0xFF000000);
                }

                return v / 8388608f;
            }

            return (short)(b[i] | (b[i + 1] << 8)) / 32768f;
        }

        private void EncodeSample(byte[] b, int i, float s)
        {
            if (_isFloat)
            {
                byte[] t = BitConverter.GetBytes(s);
                b[i] = t[0]; b[i + 1] = t[1]; b[i + 2] = t[2]; b[i + 3] = t[3];
                return;
            }

            if (_bits == 32)
            {
                int v = (int)Math.Round(s * 2147483647.0);
                if (v > 2147483647) v = 2147483647; else if (v < -2147483648) v = -2147483648;
                b[i] = (byte)(v & 0xFF);
                b[i + 1] = (byte)((v >> 8) & 0xFF);
                b[i + 2] = (byte)((v >> 16) & 0xFF);
                b[i + 3] = (byte)((v >> 24) & 0xFF);
                return;
            }

            if (_bits == 24)
            {
                int v = (int)Math.Round(s * 8388607.0);
                if (v > 8388607) v = 8388607; else if (v < -8388608) v = -8388608;
                uint u = (uint)v;
                b[i] = (byte)(u & 0xFF);
                b[i + 1] = (byte)((u >> 8) & 0xFF);
                b[i + 2] = (byte)((u >> 16) & 0xFF);
                return;
            }

            short sv;
            if (_bits == 8)
            {
                // 8bit PCM 为无符号（128 为静音）
                int u8 = (int)Math.Round(s * 127.0) + 128;
                if (u8 > 255) u8 = 255; else if (u8 < 0) u8 = 0;
                b[i] = (byte)u8;
                return;
            }

            int v16 = (int)Math.Round(s * 32767.0);
            if (v16 > 32767) v16 = 32767; else if (v16 < -32768) v16 = -32768;
            sv = (short)v16;
            b[i] = (byte)(sv & 0xFF);
            b[i + 1] = (byte)((sv >> 8) & 0xFF);
        }
    }
}
