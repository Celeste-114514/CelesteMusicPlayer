using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // —— 立体声采样的订阅：引用计数 ——
        // 采集开关是全引擎一把的（SetStereoCapture 只是 bool），
        // 所以这里把「谁要看」记成一组订阅者：有人在场才开，一个页面关闭不能把
        // 另一个还在看的消费者一起关掉。
        private static readonly object StereoGate = new();
        private static readonly HashSet<string> StereoOwners = new(StringComparer.Ordinal);

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

            try { _audioEngine?.SetStereoCapture(desired); }
            catch (Exception ex) { StartupLog.WriteException("WebNowPlaying.SetStereoSampling", ex); }
        }

        // —— 快照的代次：曲目一换就 +1，网页据此丢弃旧响应，避免上一首的采样串到这一首 ——
        private string _visualTrackId = "";
        private long _visualGeneration, _visualSequence;
        private readonly float[] _visualLeft = new float[512];
        private readonly float[] _visualRight = new float[512];
        /// <summary>
        /// 网页「正在播放」详情页要的一份快照：曲目身份 + 整首歌词 + 当前句。
        ///
        /// 为什么这里不带播放进度：网页每 250ms 收一次 celeste.state，进度和时间都在里面，
        /// 当前句由前端按时间自己算。每帧跨宿主取一次会把这条消息链打爆，
        /// 也没必要为一个高亮位置多开一条 RPC。
        ///
        /// 歌词为空不区分「还没解析完」和「这首歌确实没有」——网页那边看到空会重试两次，
        /// 之后才显示「没有找到歌词」，因为解析是异步的。
        /// </summary>
        internal object WebNowPlayingVisual()
        {
            var values = new float[140];
            int sampleRate = 0;
            bool playing = IsPlaybackActuallyPlaying();
            bool available = playing && (_audioEngine?.TryGetDetailedSpectrum(values, out sampleRate) ?? false);
            var samples = new float[256];
            bool samplesAvailable = playing && (_audioEngine?.TryGetSamples(samples) ?? false);

            string trackId = GetCurrentPlayingItem() is { } current ? WebTrackIdentity.Of(current) : "";
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
            bool stereo = playing && (_audioEngine?.TryGetStereoSamples(_visualLeft, _visualRight) ?? false);
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

        internal object WebNowPlaying()
        {
            var item = GetCurrentPlayingItem();
            string trackId = item == null ? string.Empty : WebTrackIdentity.Of(item);
            int rate = 0, bits = 0, kbps = 0, channels = 0;
            long fileSize = 0;
            if (item != null)
            {
                AudioInfoFormatter.TryGetFacts(item.FilePath, out rate, out bits, out kbps, out channels);
                try { if (System.IO.File.Exists(item.FilePath)) fileSize = new System.IO.FileInfo(item.FilePath).Length; }
                catch (System.IO.IOException) { }
            }
            var lines = GetLyricLinesPublic();
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
                lyricState = rows.Length > 0 ? "ready" : "none",
                currentIndex = GetCurrentLyricIndexPublic(),
                lines = rows
            };
        }
    }
}
