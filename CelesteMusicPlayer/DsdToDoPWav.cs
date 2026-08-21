using System;
using System.IO;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 把 DSD（DSF/DFF）一次性完整解码/封装为 DoP 容器 WAV（176.4k/24bit/2ch 系列）后落盘，
    /// 再交给现有 WAV 播放通道（WASAPI 独占原生直通）播放。
    /// 这样实时播放只是在顺序读一个规整 WAV，彻底消除"边播边解 DSD"导致的电流音/卡顿；
    /// 且 DoP 容器字节原样直通 DAC（标记 0x05/0xFA 保留）→ 仍 bit-perfect。
    /// </summary>
    internal static class DsdToDoPWav
    {
        // 8-bit 位反转表（DSF MSB-first → DoP LSB-first）
        private static readonly byte[] Rev8 = BuildRevTable();
        private static byte[] BuildRevTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                byte b = (byte)i, r = 0;
                for (int k = 0; k < 8; k++)
                {
                    r = (byte)((r << 1) | (b & 1));
                    b >>= 1;
                }

                t[i] = r;
            }

            return t;
        }
        /// <summary>把 dsf/dfF 一次性解析并写出 DoP PCM 容器 WAV。返回 (成功, 采样率, 说明)。</summary>
        public static (bool Ok, int Rate, string Msg) Convert(string dsdPath, string wavPath, Action<int>? progress = null)
        {
            try
            {
                IDsDDecoder decoder = DsdDecoderRegistry.Resolve(dsdPath);
                if (decoder == null)
                {
                    return (false, 0, "没有可用的 DSD 解码器。");
                }

                IDsDStream dsd = decoder.Open(dsdPath);
                using (dsd)
                {
                // DoP 容器采样率：DSD64→176.4k DSD128→352.8k DSD256→705.6k DSD512→1411.2k
                int rate = dsd.Rate switch
                {
                    DsdRate.Dsd128 => 352800,
                    DsdRate.Dsd256 => 705600,
                    DsdRate.Dsd512 => 1411200,
                    _ => 176400,
                };
                int channels = dsd.Channels;
                int bits = 24;
                int bytesPerFrame = (bits / 8) * channels; // 6
                long totalFrames = channels > 0 ? dsd.TotalSamples / 16 : 0; // TotalSamples 已是"每声道 1-bit 样本数"
                long totalBytes = totalFrames * bytesPerFrame;

                string dir = Path.GetDirectoryName(wavPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
                WriteWaveHeader(fs, rate, channels, bits, totalBytes);

                // 单线程顺序读 DSD(L,R,L,R 交织) 并封 DoP 容器帧(低16bit DSD + 高8bit 0x05/0xFA 交替)，避免预读线程与写并发。
                byte[] src = new byte[4];   // L,R,L,R
                byte[] frame = new byte[6];
                long frameIndex = 0;
                long written = 0;
                int lastPct = -1;
                using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                while (true)
                {
                    int got = 0;
                    while (got < 4)
                    {
                        int k = dsd.Read(src, got, 4 - got);
                        if (k <= 0)
                        {
                            break;
                        }

                        got += k;
                    }

                    if (got < 4)
                    {
                        break; // 源尽/残缺
                    }

                    // 位序：DSF MSB-first → DoP 容器需 LSB-first → 逐字节位反转(与 DoPWaveSource 一致)
                    byte l0 = Rev8[src[0]], r0 = Rev8[src[1]], l1 = Rev8[src[2]], r1 = Rev8[src[3]];
                    byte marker = (frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                    frame[0] = l0; frame[1] = l1; frame[2] = marker; // L: 低16=DSD, 高8=标记
                    frame[3] = r0; frame[4] = r1; frame[5] = marker; // R
                    bw.Write(frame);
                    frameIndex++;
                    written += 6;
                    if (totalBytes > 0)
                    {
                        int pct = (int)(written * 100 / totalBytes);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Invoke(pct);
                        }
                    }
                }
                }

                // 用实际写入长度修正 RIFF/data 头
                fs.Flush();
                fs.Seek(4, SeekOrigin.Begin);
                var w = new BinaryWriter(fs);
                w.Write((int)(fs.Length - 8));
                fs.Seek(0, SeekOrigin.End);
                return (true, rate, rate + "Hz / " + bits + "bit / " + channels + "ch");
                }
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>写标准 PCM RIFF/WAVE 头（24bit 用 WAVE_FORMAT_PCM）。</summary>
        private static void WriteWaveHeader(Stream s, int rate, int channels, int bits, long dataBytes)
        {
            int blockAlign = (bits / 8) * channels;
            int fmtSize = 16;
            int riffSize = 4 + (8 + fmtSize) + (8 + (int)dataBytes);
            var w = new BinaryWriter(s);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(riffSize);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(fmtSize);
            w.Write((short)1);           // WAVE_FORMAT_PCM
            w.Write((short)channels);
            w.Write(rate);
            w.Write(rate * blockAlign);  // byte rate
            w.Write((short)blockAlign);
            w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((int)dataBytes);
        }
    }
}
