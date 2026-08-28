using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// PCM 样本的「字节 ↔ float」编解码助手（支持 16 / 24 / 32bit 整数与 32bit 浮点）。
    /// 供需要在字节层做样本级处理的场景使用（目前是交叉淡化的两曲混合）。
    /// 约定：float 域为 -1.0 .. 1.0；编码时做钳位，避免混合后溢出产生爆音。
    /// </summary>
    internal static class PcmSampleCodec
    {
        /// <summary>把一帧（channels 个样本）从字节解码为 float。</summary>
        public static void DecodeFrame(byte[] src, int srcOffset, WaveFormat fmt, float[] dst, int dstOffset)
        {
            int channels = fmt.Channels;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                int bi = srcOffset;
                for (int c = 0; c < channels; c++, bi += 4)
                {
                    dst[dstOffset + c] = System.BitConverter.ToSingle(src, bi);
                }
            }
            else if (fmt.BitsPerSample == 32)
            {
                int bi = srcOffset;
                for (int c = 0; c < channels; c++, bi += 4)
                {
                    int v = src[bi] | (src[bi + 1] << 8) | (src[bi + 2] << 16) | (src[bi + 3] << 24);
                    dst[dstOffset + c] = v / 2147483648f;
                }
            }
            else if (fmt.BitsPerSample == 24)
            {
                int bi = srcOffset;
                for (int c = 0; c < channels; c++, bi += 3)
                {
                    int v = src[bi] | (src[bi + 1] << 8) | (src[bi + 2] << 16);
                    if ((src[bi + 2] & 0x80) != 0) v |= unchecked((int)0xFF000000);
                    dst[dstOffset + c] = v / 8388608f;
                }
            }
            else // 16bit（含其它位深的兜底）
            {
                int bi = srcOffset;
                for (int c = 0; c < channels; c++, bi += 2)
                {
                    dst[dstOffset + c] = (short)(src[bi] | (src[bi + 1] << 8)) / 32768f;
                }
            }
        }

        /// <summary>把一帧 float 编码回字节（钳位到 -1..1，防溢出爆音）。</summary>
        public static void EncodeFrame(byte[] dst, int dstOffset, WaveFormat fmt, float[] src, int srcOffset)
        {
            int channels = fmt.Channels;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                int bo = dstOffset;
                for (int c = 0; c < channels; c++, bo += 4)
                {
                    System.BitConverter.GetBytes(Clamp(src[srcOffset + c])).CopyTo(dst, bo);
                }
            }
            else if (fmt.BitsPerSample == 32)
            {
                int bo = dstOffset;
                for (int c = 0; c < channels; c++, bo += 4)
                {
                    int v = (int)(Clamp(src[srcOffset + c]) * 2147483647f);
                    dst[bo] = (byte)(v & 0xFF);
                    dst[bo + 1] = (byte)((v >> 8) & 0xFF);
                    dst[bo + 2] = (byte)((v >> 16) & 0xFF);
                    dst[bo + 3] = (byte)((v >> 24) & 0xFF);
                }
            }
            else if (fmt.BitsPerSample == 24)
            {
                int bo = dstOffset;
                for (int c = 0; c < channels; c++, bo += 3)
                {
                    int v = (int)(Clamp(src[srcOffset + c]) * 8388607f);
                    dst[bo] = (byte)(v & 0xFF);
                    dst[bo + 1] = (byte)((v >> 8) & 0xFF);
                    dst[bo + 2] = (byte)((v >> 16) & 0xFF);
                }
            }
            else // 16bit
            {
                int bo = dstOffset;
                for (int c = 0; c < channels; c++, bo += 2)
                {
                    short s = (short)(Clamp(src[srcOffset + c]) * 32767f);
                    dst[bo] = (byte)(s & 0xFF);
                    dst[bo + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        private static float Clamp(float v)
        {
            if (!float.IsFinite(v)) return 0f;
            if (v > 1f) return 1f;
            if (v < -1f) return -1f;
            return v;
        }
    }
}
