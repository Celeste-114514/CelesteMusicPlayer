using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSD 预加载缓存：提前把整首 DSF 装箱成 DoP(24bit 紧凑) 的 PCM WAV 文件，播放时直接从文件顺序读，
    /// 绕开"边播边装箱"的实时流水线（DsdBitstream 读源 → 装箱线程 → 32MB 环 → render）。
    ///
    /// 立项依据（2026-09-24 实测，FiiO KA13）：
    ///   · 实时装箱播 DSF：独占/ASIO 都在固定位置卡顿
    ///   · 同一首预生成 WAV 交给 foobar 播：绿灯且不卡（字节内容 × 设备 DoP 接收 = 无罪）
    ///   · 同一首预生成 WAV 用我们自己的独占出口播：绿灯且不卡 → 真凶在实时装箱流水线
    ///
    /// 缓存文件是标准 PCM WAV（如 352800Hz/24bit/2ch，随 DSD 倍率变），但内容就是 DoP 帧流，
    /// 必须经独占/ASIO 原样直通；任何 DSP、重采样、软音量都会毁掉 DoP 标记（播放侧已有守卫）。
    /// 未命中缓存时行为完全不变（仍走实时装箱），所以这个功能随时可以关掉，零风险回退。
    /// </summary>
    internal static class DsdPreloadService
    {
        public const string CacheExtension = ".dop24.wav";
        private const int BpF = 6; // 24bit 紧凑容器：每立体声帧 6 字节

        /// <summary>默认缓存根目录（D 盘存在就用 D 盘，避免占系统盘；用户可在设置里改）。</summary>
        public static string DefaultCacheRoot =>
            Directory.Exists(@"D:\")
                ? @"D:\CelesteDsdCache"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "DsdCache");

        public static string CacheRoot
        {
            get
            {
                string p = AppSettingsStore.Load().DsdCachePath ?? string.Empty;
                p = p.Trim();
                return string.IsNullOrEmpty(p) ? DefaultCacheRoot : p;
            }
        }

        public static bool Enabled => AppSettingsStore.Load().DsdPreloadEnabled;

        /// <summary>缓存文件名带源文件指纹（路径+大小+修改时间），源文件一变旧缓存自动失效。</summary>
        private static string Fingerprint(string dsf)
        {
            try
            {
                var fi = new FileInfo(dsf);
                string key = dsf.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                using var md5 = MD5.Create();
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                return h[0].ToString("x2") + h[1].ToString("x2") + h[2].ToString("x2") + h[3].ToString("x2");
            }
            catch (Exception)
            {
                return "00000000";
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return "未分类";
            }

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }

            string r = sb.ToString().Trim();
            return r.Length == 0 ? "未分类" : r;
        }

        /// <summary>该 DSF 对应的缓存文件路径（不保证存在）。布局：&lt;缓存根&gt;\&lt;专辑目录名&gt;\&lt;曲名&gt;.&lt;指纹&gt;.dop24.wav</summary>
        public static string CachePathFor(string dsf)
        {
            string album = Sanitize(Path.GetFileName(Path.GetDirectoryName(dsf) ?? string.Empty));
            string name = Sanitize(Path.GetFileNameWithoutExtension(dsf));
            return Path.Combine(CacheRoot, album, name + "." + Fingerprint(dsf) + CacheExtension);
        }

        /// <summary>缓存是否存在且完整（&gt;44B 头）。播放侧用这个决定是否走旁路。</summary>
        public static bool TryGetCached(string dsf, out string wav)
        {
            wav = CachePathFor(dsf);
            try
            {
                return File.Exists(wav) && new FileInfo(wav).Length > 44;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>生成缓存。走真实链路（DsdBitstream → DoP24LeSource）全流读一遍并落盘，
        /// 只写"真实帧"（尾部补的 0x69 静音不进缓存），先写 .part 再改名，避免半截文件被当成有效缓存。</summary>
        public static bool Build(string dsf, out string wav, out string? error)
        {
            wav = CachePathFor(dsf);
            error = null;
            FileStream? fs = null;
            DsdBitstream? bs = null;
            DoP24LeSource? dop = null;
            string tmp = wav + ".part";
            try
            {
                string? dir = Path.GetDirectoryName(wav);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                bs = DsdBitstream.Open(dsf);
                dop = new DoP24LeSource(bs);
                long total = dop.TotalFrames;
                if (total <= 0)
                {
                    error = "无法解析该 DSD 的长度";
                    return false;
                }

                fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                fs.Write(new byte[44], 0, 44); // 占位头，末尾回填

                byte[] buf = new byte[1 << 20];
                long wroteFrames = 0;
                while (wroteFrames < total)
                {
                    long pfBefore = dop.PrefillFrames;
                    int got = dop.Read(buf, 0, buf.Length - (buf.Length % BpF));
                    if (got <= 0)
                    {
                        break;
                    }

                    long pad = dop.PrefillFrames - pfBefore; // 本次补的静音帧
                    long realFrames = got / BpF - pad;
                    if (realFrames > 0)
                    {
                        int realBytes = (int)realFrames * BpF;
                        fs.Write(buf, 0, realBytes);
                        wroteFrames += realFrames;
                    }

                    if (pad > 0)
                    {
                        break; // 源已尽
                    }
                }

                WriteWavHeader(fs, dop.WaveFormat.SampleRate, 24, 2, BpF, wroteFrames * BpF);
                fs.Dispose();
                fs = null;
                File.Move(tmp, wav, true);
                StartupLog.Write($"[DSD预载] 已生成 {wav} = {wroteFrames * BpF / 1024 / 1024}MB / {wroteFrames}帧 @ {dop.WaveFormat.SampleRate}Hz");
                return true;
            }
            catch (Exception caught)
            {
                error = caught.Message;
                StartupLog.WriteException("DsdPreloadService.cs", caught);
                return false;
            }
            finally
            {
                try { fs?.Dispose(); } catch (Exception) { /* 忽略：清理路径不抛 */ }
                try { if (fs != null && File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* 忽略 */ }
                try { dop?.Dispose(); } catch (Exception) { /* 忽略 */ }
                try { bs?.Dispose(); } catch (Exception) { /* 忽略 */ }
            }
        }

        /// <summary>删除某一首的缓存（返回是否真的删掉了）。</summary>
        public static bool DeleteFor(string dsf)
        {
            try
            {
                string p = CachePathFor(dsf);
                if (File.Exists(p))
                {
                    File.Delete(p);
                    return true;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("DsdPreloadService.cs", caught);
            }

            return false;
        }

        /// <summary>缓存总字节数与文件数。</summary>
        public static (long Bytes, int Files) Stat()
        {
            long bytes = 0;
            int files = 0;
            try
            {
                string root = CacheRoot;
                if (!Directory.Exists(root))
                {
                    return (0, 0);
                }

                foreach (string f in Directory.EnumerateFiles(root, "*" + CacheExtension, SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(f).Length; files++; } catch (Exception) { /* 忽略单文件 */ }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("DsdPreloadService.cs", caught);
            }

            return (bytes, files);
        }

        /// <summary>清空全部缓存（返回删除文件数）。分批删除，避免一次删太多触发安全确认。</summary>
        public static int ClearAll()
        {
            int n = 0;
            try
            {
                string root = CacheRoot;
                if (!Directory.Exists(root))
                {
                    return 0;
                }

                var list = new System.Collections.Generic.List<string>();
                foreach (string f in Directory.EnumerateFiles(root, "*" + CacheExtension, SearchOption.AllDirectories))
                {
                    list.Add(f);
                }

                foreach (string f in list)
                {
                    try { File.Delete(f); n++; } catch (Exception) { /* 忽略单文件 */ }
                }

                // 顺手清掉空目录
                foreach (string d in Directory.EnumerateDirectories(root))
                {
                    try { if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch (Exception) { /* 忽略 */ }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("DsdPreloadService.cs", caught);
            }

            return n;
        }

        /// <summary>标准 44 字节 PCM WAV 头（回填到已写好的流：先 seek 到 0 写头再回到原位置）。</summary>
        private static void WriteWavHeader(FileStream fs, int sampleRate, int bits, int channels, int blockAlign, long dataBytes)
        {
            long saved = fs.Position;
            fs.Position = 0;
            var w = new BinaryWriter(fs, Encoding.UTF8, true);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)(36 + dataBytes));
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write((uint)16);
            w.Write((ushort)1);              // PCM
            w.Write((ushort)channels);
            w.Write((uint)sampleRate);
            w.Write((uint)(sampleRate * blockAlign)); // byteRate
            w.Write((ushort)blockAlign);
            w.Write((ushort)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((uint)dataBytes);
            w.Flush();
            fs.Position = Math.Max(saved, 44);
        }
    }
}
