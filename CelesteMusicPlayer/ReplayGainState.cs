using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>ReplayGain 模式（对齐 ECHO ReplayGainConfig.mode）。</summary>
    public enum ReplayGainMode
    {
        Off = 0,
        Track = 1,
        Album = 2
    }

    /// <summary>响度归一化（ReplayGain）状态与持久化。</summary>
    public sealed class ReplayGainState
    {
        public ReplayGainMode Mode { get; set; } = ReplayGainMode.Off;

        /// <summary>额外增益（dB），在曲目/专辑增益基础上叠加。</summary>
        public double PreampDb { get; set; }

        /// <summary>防削波：若 peak×gain&gt;1 则将增益压到不削波的最大值。</summary>
        public bool PreventClipping { get; set; } = true;

        public ReplayGainState Clone() => new()
        {
            Mode = Mode,
            PreampDb = PreampDb,
            PreventClipping = PreventClipping
        };

        public void Normalize()
        {
            PreampDb = Math.Clamp(PreampDb, -24, 24);
        }
    }

    public static class ReplayGainStore
    {
        private const string FileName = "replaygain.json";
        private static ReplayGainState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            // 与主设置(AppSettingsStore)同源：固定路径，规避 packaged 下 ApplicationData 路径漂浮导致重启读回默认。
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static ReplayGainState Load()
        {
            lock (Gate)
            {
                if (_cache == null)
                {
                    _cache = JsonFile.Read(GetFilePath(), new ReplayGainState());
                    _cache.Normalize();
                }

                return JsonFile.DeepClone(_cache);
            }
        }

        public static void Save(ReplayGainState state)
        {
            lock (Gate)
            {
                _cache = state ?? new ReplayGainState();
                _cache.Normalize();
                JsonFile.Write(GetFilePath(), _cache);
            }
        }
    }

    /// <summary>从各种音源读取的 ReplayGain 元数据（dB 与 peak 均为线性/对数原值）。</summary>
    public static class ReplayGainReader
    {
        /// <summary>读取源文件的 ReplayGain（track/album 增益 dB 与 peak 线性标量）。失败返回 null。</summary>
        public static (double TrackGainDb, double AlbumGainDb, double Peak)? ReadForAudio(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(path);
                double trackGain = 0, albumGain = 0, peak = 1.0;
                bool any = false;

                // FLAC/AAC/OGG/Opus：Vorbis 注释（Xiph）
                try
                {
                    if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment x) 
                    {
                        string? g = x.GetFirstField("REPLAYGAIN_TRACK_GAIN");
                        if (ParseDb(g, out double tg)) { trackGain = tg; any = true; }
                        string? ag = x.GetFirstField("REPLAYGAIN_ALBUM_GAIN");
                        if (ParseDb(ag, out double al)) { albumGain = al; any = true; }
                        string? pk = x.GetFirstField("REPLAYGAIN_TRACK_PEAK");
                        if (ParsePeak(pk, out double p)) peak = p;
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ReplayGainState.cs", caught); }

                // MP3/AAC(ID3v2)：TXXX:REPLAYGAIN_*
                try
                {
                    if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id) 
                    {
                        foreach (var f in id.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                        {
                            string desc = f.Description ?? string.Empty;
                            string val = f.Text?.Length > 0 ? f.Text[0] : string.Empty;
                            if (desc.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase)) { if (ParseDb(val, out double tg)) { trackGain = tg; any = true; } }
                            else if (desc.Equals("REPLAYGAIN_ALBUM_GAIN", StringComparison.OrdinalIgnoreCase)) { if (ParseDb(val, out double ag)) { albumGain = ag; any = true; } }
                            else if (desc.Equals("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase)) { if (ParsePeak(val, out double p)) peak = p; }
                        }
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ReplayGainState.cs", caught); }

                return any ? (trackGain, albumGain, peak) : default((double, double, double)?);
            }
            catch
            {
                return null;
            }
        }

        private static bool ParseDb(string? s, out double db)
        {
            db = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            int i = s.IndexOfAny(new[] { ' ', '\t' });
            string num = i >= 0 ? s[..i] : s;
            num = num.TrimEnd('d', 'B', 'D', 'b');
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out db);
        }

        private static bool ParsePeak(string? s, out double peak)
        {
            peak = 1.0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out peak);
        }
    }
}
