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

                using IDsDStream dsd = decoder.Open(dsdPath);
                using var dop = new DoPWaveSource(dsd);
                int rate = dop.WaveFormat.SampleRate;
                int channels = dop.WaveFormat.Channels;
                int bits = dop.WaveFormat.BitsPerSample;
                int bytesPerFrame = (bits / 8) * channels; // 6
                long totalFrames = dop.TotalTime.TotalSeconds > 0
                    ? (long)(dop.TotalTime.TotalSeconds * rate)
                    : 0;
                long totalBytes = totalFrames * bytesPerFrame;

                string dir = Path.GetDirectoryName(wavPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
                WriteWaveHeader(fs, rate, channels, bits, totalBytes);

                byte[] buf = new byte[128 * 1024];
                long written = 0;
                int lastPct = -1;
                while (true)
                {
                    int n = dop.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    fs.Write(buf, 0, n);
                    written += n;
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

                fs.Seek(4, SeekOrigin.Begin);
                var bw = new BinaryWriter(fs);
                bw.Write((int)(fs.Length - 8));
                fs.Seek(0, SeekOrigin.End);
                return (true, rate, rate + "Hz / " + bits + "bit / " + channels + "ch");
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
