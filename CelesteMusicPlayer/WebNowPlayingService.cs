using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「正在播放」详情页需要的宿主事实。服务不碰引擎、不碰歌词解析，
    /// 只从这里取当前曲目、播放状态、频谱/采样、歌词行。
    /// </summary>
    internal interface INowPlayingHost
    {
        bool IsPlaying { get; }
        PlaylistItem? CurrentItem { get; }
        bool TryGetSpectrum(float[] values, out int sampleRate);
        bool TryGetSamples(float[] samples);
        bool TryGetStereoSamples(float[] left, float[] right);
        void SetStereoCapture(bool enabled);
        IReadOnlyList<LyricLine> LyricLines { get; }
        int CurrentLyricIndex { get; }

        /// <summary>
        /// 当前这份歌词属于哪个文件。歌词是异步读的，换歌后会有几百毫秒"曲目已经换了、
        /// 歌词还是上一首"的窗口；取歌词时必须拿它对一下，否则网页会把上一首的词显示在新歌下面。
        /// </summary>
        string LyricsTrackPath { get; }
    }

    /// <summary>
    /// 网页「正在播放」的两个只读投影：
    /// ① <see cref="Snapshot"/> —— 曲目身份 + 音频参数 + 整首歌词；
    /// ② <see cref="Visual"/> —— 频谱 / 波形 / 立体声快照（终端详情页与李萨如图用）。
    ///
    /// W13：从 MainWindow.WebNowPlaying.cs 搬出来。这里只有计算，取数据全部走宿主口子。
    ///
    /// 为什么快照不带播放进度：网页每 250ms 收一次 celeste.state，进度和时间都在里面，
    /// 当前句由前端按时间自己算。每帧跨宿主取一次会把这条消息链打爆。
    /// </summary>
    internal sealed class WebNowPlayingService(INowPlayingHost host)
    {
        // —— 立体声采样的订阅：引用计数 ——
        // 采集开关是全引擎一把的（SetStereoCapture 只是 bool），
        // 所以这里把「谁要看」记成一组订阅者：有人在场才开，一个页面关闭不能把
        // 另一个还在看的消费者一起关掉。
        private static readonly object StereoGate = new();
        private static readonly HashSet<string> StereoOwners = new(StringComparer.Ordinal);

        // —— 快照的代次：曲目一换就 +1，网页据此丢弃旧响应，避免上一首的采样串到这一首 ——
        private string _visualTrackId = "";
        private long _visualGeneration, _visualSequence;
        private readonly float[] _visualLeft = new float[512];
        private readonly float[] _visualRight = new float[512];

        /// <summary>网页可视化订阅/退订立体声采样。owner 用调用方的名字（如 "web.nowPlaying"）。</summary>
        internal void SetStereoSampling(string owner, bool wanted)
        {
            bool desired;
            lock (StereoGate)
            {
                if (wanted) StereoOwners.Add(owner);
                else StereoOwners.Remove(owner);
                desired = StereoOwners.Count > 0;
            }

            try { host.SetStereoCapture(desired); }
            catch (Exception ex) { StartupLog.WriteException("WebNowPlaying.SetStereoSampling", ex); }
        }

        /// <summary>
        /// 网页详情页的频谱/采样快照。
        /// 歌词为空不区分「还没解析完」和「这首歌确实没有」——网页那边看到空会重试两次。
        /// </summary>
        internal object Visual()
        {
            var values = new float[140];
            int sampleRate = 0;
            bool playing = host.IsPlaying;
            bool available = playing && host.TryGetSpectrum(values, out sampleRate);
            var samples = new float[256];
            bool samplesAvailable = playing && host.TryGetSamples(samples);

            string trackId = host.CurrentItem is { } current ? WebTrackIdentity.Of(current) : "";
            // 换曲就把代次 +1：网页那边用 (trackId, generation) 判断手里的采样是不是这一首的，
            // 旧响应不能盖到新的选择上。
            if (!string.Equals(trackId, _visualTrackId, StringComparison.Ordinal))
            {
                _visualTrackId = trackId;
                _visualGeneration++;
            }

            // 真实的左右声道：必须同一时刻、同一处理阶段的一对样本。
            // 这里取 DSP 之后的输出抽头（"post-dsp"），不拿单声道副本冒充右声道。
            // 没采到就全零 + available=false，网页据此显示空网格，不许画假对角线。
            bool stereo = playing && host.TryGetStereoSamples(_visualLeft, _visualRight);
            double sumLL = 0, sumRR = 0, sumLR = 0;
            float peakLeft = 0, peakRight = 0;
            if (stereo)
            {
                for (int i = 0; i < _visualLeft.Length; i++)
                {
                    float l = _visualLeft[i], r = _visualRight[i];
                    sumLL += l * l; sumRR += r * r; sumLR += l * r;
                    float absL = Math.Abs(l), absR = Math.Abs(r);
                    if (absL > peakLeft) peakLeft = absL;
                    if (absR > peakRight) peakRight = absR;
                }
            }

            int count = _visualLeft.Length;
            double rmsLeft = stereo ? Math.Sqrt(sumLL / count) : 0;
            double rmsRight = stereo ? Math.Sqrt(sumRR / count) : 0;
            // 相关系数 rho：零能量时给不出值，前端显示"—"，不要编一个 0 冒充
            double denominator = Math.Sqrt(sumLL * sumRR);
            double? rho = stereo && sumLL > 1e-12 && sumRR > 1e-12 ? sumLR / denominator : null;

            return new
            {
                sampleRate,
                minFrequency = 20, maxFrequency = Math.Min(20000, sampleRate / 2), floorDb = -100,
                samplesAvailable,
                samples = samples.Select(v => samplesAvailable && float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f).ToArray(),
                trackId,
                available,
                bands = values.Select(v => available && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f).ToArray(),
                // —— 立体声快照（终端详情页和李萨如用）——
                stereo = new
                {
                    available = stereo,
                    trackId,
                    generation = _visualGeneration,
                    sequence = ++_visualSequence,
                    sampleRate,
                    count,
                    // 512 对样本在常见采样率下约 10ms 左右，够画一帧又不至于把消息撑爆；
                    // 具体时间窗由前端按 sampleRate 自己算，宿主不替它拍板。
                    stage = "post-dsp",
                    left = stereo ? _visualLeft.Select(v => float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f).ToArray() : Array.Empty<float>(),
                    right = stereo ? _visualRight.Select(v => float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f).ToArray() : Array.Empty<float>(),
                    rmsLeft, rmsRight,
                    peakLeft, peakRight,
                    correlation = rho
                }
            };
        }

        /// <summary>曲目身份 + 音频参数 + 整首歌词。</summary>
        internal object Snapshot()
        {
            var item = host.CurrentItem;
            string trackId = item == null ? string.Empty : WebTrackIdentity.Of(item);
            // 歌词是异步读的：刚换歌时 _lyricLines 还是上一首的。只有它确实属于当前曲目才交出去，
            // 否则网页只能凭 trackId 判断，就会把上一首的词挂在新歌下面。
            string currentPath = item?.FilePath ?? "";
            string lyricsPath = host.LyricsTrackPath ?? "";
            bool lyricsBelongHere = item != null && currentPath.Length > 0 && lyricsPath.Length > 0
                && string.Equals(Path.GetFullPath(lyricsPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase);
            int rate = 0, bits = 0, kbps = 0, channels = 0;
            long fileSize = 0;
            if (item != null)
            {
                AudioInfoFormatter.TryGetFacts(item.FilePath, out rate, out bits, out kbps, out channels);
                try { if (File.Exists(item.FilePath)) fileSize = new FileInfo(item.FilePath).Length; }
                catch (IOException) { }
            }
            var lines = lyricsBelongHere ? host.LyricLines : Array.Empty<LyricLine>();
            var rows = lines
                .Select(line => new
                {
                    t = Math.Round(Math.Max(0, line.Time.TotalSeconds), 3),
                    text = line.Text ?? string.Empty,
                    tr = line.IsTranslation
                })
                .ToArray();
            return new
            {
                trackId,
                audio = new { sampleRate = rate, bitDepth = bits, kbps, channels, fileSize },
                // 还没轮到这份歌词：告诉前端"在等"，让它多试几次，而不是当成"这首没歌词"。
                lyricState = lyricsBelongHere ? (rows.Length > 0 ? "ready" : "none") : (item == null ? "none" : "loading"),
                currentIndex = lyricsBelongHere ? host.CurrentLyricIndex : -1,
                lines = rows
            };
        }
    }
}
