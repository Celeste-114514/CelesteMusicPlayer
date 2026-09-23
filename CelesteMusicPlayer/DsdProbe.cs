using System;
using System.IO;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>DSD 容器类型。</summary>
    internal enum DsdContainerKind
    {
        Unknown = 0,
        Dsf,
        Dff,
    }

    /// <summary>
    /// DSD 容器探测结果（只读文件头，不解码任何音频数据）。
    /// 对应 ECHO 的 DsdProbe：起播前一次性判定，不满足即带原因回退 PCM，绝不静默。
    /// </summary>
    internal sealed class DsdProbeResult
    {
        /// <summary>文件头是否识别为 DSD 容器（DSF 或 DFF）。</summary>
        public bool IsDsdContainer;

        /// <summary>容器类型。</summary>
        public DsdContainerKind Kind = DsdContainerKind.Unknown;

        /// <summary>DSD 位时钟频率（Hz），如 2822400。</summary>
        public uint NativeRateHz;

        /// <summary>声道数。</summary>
        public int Channels;

        /// <summary>每声道 1-bit 样本数（DSF=fmt 块 sampleCount；DFF=0 表示未知）。</summary>
        public long SampleCount;

        /// <summary>DSF 块交错粒度（bytes/声道/块）。</summary>
        public int BlockSize;

        /// <summary>DoP 传输速率（native/16）：176400/352800/705600；不支持=0。</summary>
        public int DopRateHz;

        /// <summary>不允许 DoP 直出的原因（写回退日志用）；null=允许。</summary>
        public string? RejectReason;

        /// <summary>守卫综合判决：是否可走 DoP 直出。</summary>
        public bool DopSupported => DopRateHz > 0 && RejectReason == null;
    }

    /// <summary>
    /// DSD 容器探头 + DoP 守卫（照搬 ECHO 方案，替换原 BuiltInDsdDecoder/DsdDecoderRegistry 插件机制）：
    ///   · 仅 DSF 参与 DoP 直出；DFF（含 DST 压缩）一律回退 PCM——ECHO 的 DsdProbe 同样只认本地 .dsf；
    ///   · 声道 1-2；
    ///   · DSD64/128/256（native/16 = 176400/352800/705600Hz）；DSD512 的 1411.2k 传输率非标准，回退 PCM。
    /// </summary>
    internal static class DsdProbe
    {
        /// <summary>native/16 = DoP 传输速率。仅 DSD64/128/256。</summary>
        public static bool TryDopRate(uint nativeHz, out int dopHz)
        {
            switch (nativeHz)
            {
                case 2822400: dopHz = 176400; return true;   // DSD64
                case 5644800: dopHz = 352800; return true;   // DSD128
                case 11289600: dopHz = 705600; return true;  // DSD256
                default: dopHz = 0; return false;
            }
        }

        /// <summary>DSD 级别名（DSD64/128/256/512），未知返回 "DSD"。仅用于展示。</summary>
        public static string LevelName(uint nativeHz) => nativeHz switch
        {
            2822400 => "DSD64",
            5644800 => "DSD128",
            11289600 => "DSD256",
            22579200 => "DSD512",
            _ => "DSD",
        };

        /// <summary>只读头部取 DSD 位时钟频率（Hz，如 2822400 / 5644800），失败返回 0。
        /// 顶替原 BuiltInDsdDecoder.ProbeFreqHz：供「按 DSD 级别选 PCM 回退采样率」用（不解码、不开数据）。</summary>
        public static uint ProbeFreqHz(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return 0;
                }

                string ext = Path.GetExtension(path).ToLowerInvariant();
                using var fs = File.OpenRead(path);
                if (ext == ".dsf")
                {
                    if (ReadTag(fs, 4) != "DSD ")
                    {
                        return 0;
                    }

                    ReadI64(fs);          // header size
                    fs.Position += 16;    // fileTotalSize(8) + metadataPtr(8)
                    if (ReadTag(fs, 4) != "fmt ")
                    {
                        return 0;
                    }

                    ReadI64(fs);          // fmt chunk size
                    fs.Position += 8;     // version(4) + format id(4)
                    ReadU32(fs);          // channel type
                    ReadU32(fs);          // channel count
                    return ReadU32(fs);   // sampling frequency，如 2822400
                }

                if (ext == ".dff")
                {
                    if (ReadTag(fs, 4) != "FRM8")
                    {
                        return 0;
                    }

                    ReadI64(fs);          // FRM8 size
                    if (ReadTag(fs, 4) != "DSD ")
                    {
                        return 0;
                    }

                    while (fs.Position + 12 <= fs.Length)
                    {
                        string id = ReadTag(fs, 4);
                        long size = ReadI64(fs);
                        if (id == "PROP")
                        {
                            long propEnd = fs.Position + size;
                            ReadTag(fs, 4); // "SND "
                            while (fs.Position + 8 <= propEnd && fs.Position < fs.Length)
                            {
                                string sid = ReadTag(fs, 4);
                                long ssize = ReadI64(fs);
                                if (sid == "FS  ")
                                {
                                    return ReadU32(fs);
                                }

                                fs.Position = Math.Min(fs.Position + ssize, fs.Length);
                            }

                            return 0;
                        }

                        if (id == "DSD ")
                        {
                            return 0; // 数据块到了还没找到 FS：无采样率信息
                        }

                        fs.Position = Math.Min(fs.Position + size, fs.Length);
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("DsdProbe.cs", caught);
            }

            return 0;
        }

        /// <summary>探测 DSD 容器并做 DoP 守卫判定。任何异常/不识别的头都变成带原因的拒绝，不抛给调用方。</summary>
        public static DsdProbeResult Probe(string path)
        {
            var r = new DsdProbeResult();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    r.RejectReason = "文件不存在";
                    return r;
                }

                using var fs = File.OpenRead(path);
                string tag = ReadTag(fs, 4);
                if (tag == "DSD ")
                {
                    ProbeDsf(fs, r);
                }
                else if (tag == "FRM8")
                {
                    ProbeDff(fs, r);
                }
                else
                {
                    r.RejectReason = "无法识别的 DSD 容器（既非 DSF 也非 DFF）";
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("DsdProbe.cs", caught);
                r.RejectReason = "探测异常：" + caught.Message;
            }

            return r;
        }

        // ---------- DSF ----------
        private static void ProbeDsf(Stream fs, DsdProbeResult r)
        {
            ReadI64(fs);          // header size（DSD chunk 的 8 字节长度）
            fs.Position += 16;    // fileTotalSize(8) + metadataPtr(8)
            if (ReadTag(fs, 4) != "fmt ")
            {
                r.RejectReason = "DSF 缺 fmt 块";
                return;
            }

            ReadI64(fs);          // fmt size
            fs.Position += 4 + 4; // version + format id
            ReadU32(fs);          // channel type(0=stereo)
            uint ch = ReadU32(fs);
            uint freq = ReadU32(fs); // e.g. 2822400
            ReadU32(fs);          // bits per sample(=1)
            ulong sampleCount = ReadU64(fs);
            uint blockSize = ReadU32(fs);

            r.IsDsdContainer = true;
            r.Kind = DsdContainerKind.Dsf;
            r.NativeRateHz = freq;
            r.Channels = (int)ch;
            r.SampleCount = (long)sampleCount;
            r.BlockSize = (int)blockSize;

            if (!TryDopRate(freq, out int dop))
            {
                r.RejectReason = "DSD 倍率不在 DoP 直出支持范围（仅 DSD64/128/256，native=" + freq + "Hz）";
                return;
            }

            if (ch < 1 || ch > 2)
            {
                r.RejectReason = "声道数 " + ch + " 不在 DoP 直出支持范围（仅 1-2 声道）";
                return;
            }

            r.DopRateHz = dop;
        }

        // ---------- DFF ----------
        private static void ProbeDff(Stream fs, DsdProbeResult r)
        {
            ReadI64(fs); // FRM8 size
            if (ReadTag(fs, 4) != "DSD ")
            {
                r.RejectReason = "DFF 类型非 DSD";
                return;
            }

            uint fsFreq = 0;
            uint ch = 0;
            bool isDst = false;
            while (fs.Position + 12 <= fs.Length)
            {
                string id = ReadTag(fs, 4);
                long size = ReadI64(fs);
                if (id == "PROP")
                {
                    long propEnd = fs.Position + size;
                    ReadTag(fs, 4); // "SND "
                    while (fs.Position + 8 <= propEnd && fs.Position < fs.Length)
                    {
                        string sid = ReadTag(fs, 4);
                        long ssize = ReadI64(fs);
                        if (sid == "FS  ")
                        {
                            fsFreq = ReadU32(fs);
                        }
                        else if (sid == "CHNL")
                        {
                            ch = ReadU32(fs);
                        }
                        else if (sid == "CMPR")
                        {
                            string comp = Encoding.ASCII.GetString(ReadN(fs, 4));
                            if (comp.Contains("NDST", StringComparison.Ordinal))
                            {
                                isDst = true;
                            }

                            fs.Position = Math.Min(fs.Position + ssize - 4, fs.Length);
                        }
                        else
                        {
                            fs.Position = Math.Min(fs.Position + ssize, fs.Length);
                        }
                    }

                    break;
                }

                if (id == "DSD ")
                {
                    break; // 数据块到了还没看到 PROP：容器标记即可（DoP 一律回退，与频率无关）
                }

                fs.Position = Math.Min(fs.Position + size, fs.Length);
            }

            r.IsDsdContainer = true;
            r.Kind = DsdContainerKind.Dff;
            r.NativeRateHz = fsFreq;
            r.Channels = (int)(ch > 0 ? ch : 2);
            r.RejectReason = isDst
                ? "DST 压缩 DFF 不在 DoP 直出支持范围（走 PCM 回退）"
                : "DFF 容器不在 DoP 直出支持范围（ECHO 方案仅 DSF，走 PCM 回退）";
        }

        // ---------- 字节工具（DSF/DFF 字段均小端） ----------
        private static string ReadTag(Stream s, int n) => Encoding.ASCII.GetString(ReadN(s, n), 0, n);

        private static byte[] ReadN(Stream s, int n)
        {
            var b = new byte[n];
            int r = 0;
            while (r < n)
            {
                int k = s.Read(b, r, n - r);
                if (k <= 0)
                {
                    break;
                }

                r += k;
            }

            return b;
        }

        private static long ReadI64(Stream s) => BitConverter.ToInt64(ReadN(s, 8), 0);

        private static ulong ReadU64(Stream s) => BitConverter.ToUInt64(ReadN(s, 8), 0);

        private static uint ReadU32(Stream s) => BitConverter.ToUInt32(ReadN(s, 4), 0);
    }
}
