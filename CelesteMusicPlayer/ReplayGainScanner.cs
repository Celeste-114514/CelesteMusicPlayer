using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;

namespace CelesteMusicPlayer
{
    /// <summary>扫描输入的一首曲目（含用于专辑分组的专辑信息）。</summary>
    public sealed class RgScanInput
    {
        public string FilePath = string.Empty;
        public string Album = string.Empty;
        public string AlbumArtist = string.Empty;
    }

    /// <summary>
    /// ReplayGain 2.0 (EBU R128) 扫描与写标签。
    /// 仅做响度测量（读音频算 LUFS / True Peak）与写标签，绝不改动播放输出字节流；
    /// 默认 target = -18 LUFS，REFERENCE_LOUDNESS = 89 dB SPL（RG 2.0 标准）。
    /// 测量用完整版 ffmpeg 的 ebur128 滤镜（复用 AudioPlaybackEngine.FindFfmpeg）。
    /// </summary>
    public sealed class ReplayGainScanner
    {
        /// <summary>ReplayGain 2.0 目标响度（LUFS）。</summary>
        public const double TargetLufs = -18.0;

        /// <summary>RG 2.0 参考响度（dB SPL），写入 REFERENCE_LOUDNESS 标签。</summary>
        public const double ReferenceLoudnessDb = 89.0;

        // ebur128 Summary 块里的两项：
        //   Integrated loudness:
        //     I:         -27.1 LUFS
        //   True peak:
        //     Peak:      -24.1 dBFS
        private static readonly Regex IntegratedRegex =
            new("Integrated loudness:\\s*I:\\s*([-\\d.]+)\\s*LUFS", RegexOptions.Singleline);
        private static readonly Regex TruePeakRegex =
            new("True peak:\\s*Peak:\\s*([-\\d.]+)\\s*dBFS", RegexOptions.Singleline);

        /// <summary>单曲目测量结果。</summary>
        public sealed class TrackResult
        {
            public string FilePath = string.Empty;
            public bool Unsupported;
            public string? Error;
            public double IntegratedLufs;
            public double TruePeakDb;

            /// <summary>Track gain (dB) = TargetLufs − 测得集成响度。</summary>
            public double TrackGainDb => TargetLufs - IntegratedLufs;

            /// <summary>Track peak 线性标量 = 10^(TP/20)。</summary>
            public double TrackPeakLinear => Math.Pow(10, TruePeakDb / 20.0);
        }

        /// <summary>专辑（整张一起测）测量结果。</summary>
        public sealed class AlbumResult
        {
            public bool Unsupported;
            public string? Error;
            public double IntegratedLufs;
            public double TruePeakDb;

            public double AlbumGainDb => TargetLufs - IntegratedLufs;
            public double AlbumPeakLinear => Math.Pow(10, TruePeakDb / 20.0);
        }

        /// <summary>DSD 比特流无法直接做响度测量，v1 跳过。</summary>
        public static bool IsDsd(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        /// <summary>测量单首曲目的集成响度与真峰。</summary>
        public async Task<TrackResult> MeasureTrackAsync(string path, CancellationToken ct = default)
        {
            var r = new TrackResult { FilePath = path };
            if (IsDsd(path)) { r.Unsupported = true; r.Error = "DSD 暂不支持扫描"; return r; }
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
            if (ffmpeg == null) { r.Error = "找不到 ffmpeg.exe"; return r; }

            string args = $"-hide_banner -nostats -i \"{path}\" -af ebur128 -f null -";
            string? output = await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false);
            if (output == null) { r.Error = "ffmpeg 执行失败"; return r; }
            if (!TryParseSummary(output, out double i, out double tp))
            {
                r.Unsupported = true;
                r.Error = "无法解析响度（可能格式不支持）";
                return r;
            }

            r.IntegratedLufs = i;
            r.TruePeakDb = tp;
            return r;
        }

        /// <summary>专辑模式：把所有曲目 resample 到 48k 单声道后拼接，整体测一次集成响度与真峰。</summary>
        public async Task<AlbumResult> MeasureAlbumAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            var ar = new AlbumResult();
            if (paths == null || paths.Count == 0) { ar.Error = "无曲目"; return ar; }
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
            if (ffmpeg == null) { ar.Error = "找不到 ffmpeg.exe"; return ar; }

            var inputs = new StringBuilder();
            var parts = new List<string>();
            for (int k = 0; k < paths.Count; k++)
            {
                inputs.Append(" -i \"").Append(paths[k]).Append('"');
                parts.Add($"[{k}:a]aresample=48000,aformat=channel_layouts=mono[{k}a]");
            }

            var sbIn = new StringBuilder();
            foreach (string p in parts) sbIn.Append(p);
            for (int k = 0; k < paths.Count; k++) sbIn.Append($"[{k}a]");
            string filter = $"-filter_complex \"{sbIn}concat=n={paths.Count}:v=0:a=1,ebur128\"";

            string args = $"-hide_banner -nostats{inputs} {filter} -f null -";
            string? output = await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false);
            if (output == null) { ar.Error = "ffmpeg 执行失败"; return ar; }
            if (!TryParseSummary(output, out double i, out double tp))
            {
                ar.Unsupported = true;
                ar.Error = "无法解析专辑响度";
                return ar;
            }

            ar.IntegratedLufs = i;
            ar.TruePeakDb = tp;
            return ar;
        }

        /// <summary>
        /// 把扫描结果写回标签。对照播放端 ReplayGainReader 读取的字段：
        /// REPLAYGAIN_TRACK_GAIN / ALBUM_GAIN / TRACK_PEAK / ALBUM_PEAK / REFERENCE_LOUDNESS。
        /// 按容器写入 Xiph(FLAC/OGG/OPUS) / ID3v2(MP3/AAC/M4A/WAV) / APE(ape/wv/tak/mpc/tta)。
        /// 仅改标签区，不动音频帧。
        /// </summary>
        public static void WriteTags(string path, double trackGainDb, double trackPeakLinear,
            double albumGainDb, double albumPeakLinear)
        {
            using TagLib.File file = TagLib.File.Create(path);
            string tg = $"{trackGainDb:F2} dB";
            string ag = $"{albumGainDb:F2} dB";
            string tp = trackPeakLinear.ToString("F6", CultureInfo.InvariantCulture);
            string ap = albumPeakLinear.ToString("F6", CultureInfo.InvariantCulture);
            string rl = $"{ReferenceLoudnessDb:F1} dB";

            // Xiph（FLAC / OGG / OPUS）
            try
            {
                if (file.GetTag(TagTypes.Xiph, true) is TagLib.Ogg.XiphComment x)
                {
                    x.SetField("REPLAYGAIN_TRACK_GAIN", tg);
                    x.SetField("REPLAYGAIN_ALBUM_GAIN", ag);
                    x.SetField("REPLAYGAIN_TRACK_PEAK", tp);
                    x.SetField("REPLAYGAIN_ALBUM_PEAK", ap);
                    x.SetField("REPLAYGAIN_REFERENCE_LOUDNESS", rl);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("ReplayGainScanner.WriteTags(Xiph)", caught); }

            // ID3v2（MP3 / AAC / M4A / WAV）
            try
            {
                if (file.GetTag(TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
                {
                    SetTxxx(id3, "REPLAYGAIN_TRACK_GAIN", tg);
                    SetTxxx(id3, "REPLAYGAIN_ALBUM_GAIN", ag);
                    SetTxxx(id3, "REPLAYGAIN_TRACK_PEAK", tp);
                    SetTxxx(id3, "REPLAYGAIN_ALBUM_PEAK", ap);
                    SetTxxx(id3, "REPLAYGAIN_REFERENCE_LOUDNESS", rl);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("ReplayGainScanner.WriteTags(Id3v2)", caught); }

            // APE（ape / wv / tak / mpc / tta）
            try
            {
                if (file.GetTag(TagTypes.Ape, true) is TagLib.Ape.Tag ape)
                {
                    ape.SetValue("REPLAYGAIN_TRACK_GAIN", tg);
                    ape.SetValue("REPLAYGAIN_ALBUM_GAIN", ag);
                    ape.SetValue("REPLAYGAIN_TRACK_PEAK", tp);
                    ape.SetValue("REPLAYGAIN_ALBUM_PEAK", ap);
                    ape.SetValue("REPLAYGAIN_REFERENCE_LOUDNESS", rl);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("ReplayGainScanner.WriteTags(Ape)", caught); }

            file.Save();
        }

        private static void SetTxxx(TagLib.Id3v2.Tag id3, string desc, string value)
        {
            TagLib.Id3v2.UserTextInformationFrame frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, false);
            frame.Text = new[] { value };
        }

        private static bool TryParseSummary(string output, out double integrated, out double truePeak)
        {
            integrated = 0;
            truePeak = 0;
            Match mi = IntegratedRegex.Match(output);
            Match mp = TruePeakRegex.Match(output);
            if (!mi.Success || !mp.Success) return false;
            if (!double.TryParse(mi.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out integrated))
                return false;
            if (!double.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out truePeak))
                return false;
            return true;
        }

        private static async Task<string?> RunFfmpegAsync(string ffmpeg, string args, CancellationToken ct)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            Task<string> tErr = proc.StandardError.ReadToEndAsync();
            Task<string> tOut = proc.StandardOutput.ReadToEndAsync();

            using (ct.Register(() =>
                   {
                       try { proc.Kill(); }
                       catch { /* 已退出 */ }
                   }))
            {
                try
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); }
                    catch { /* 已退出 */ }
                    try { await proc.WaitForExitAsync().ConfigureAwait(false); }
                    catch { /* ignore */ }
                    throw;
                }
            }

            string err = await tErr.ConfigureAwait(false);
            string stdout = await tOut.ConfigureAwait(false);
            return err + "\n" + stdout;
        }
    }
}
