using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>声道平衡状态（对齐 ECHO 音效处理「声道平衡」模块的核心子集）。</summary>
    public sealed class ChannelBalanceState
    {
        /// <summary>总开关。关闭时不改变声道（bit-perfect 直通）。</summary>
        public bool Enabled { get; set; }

        /// <summary>左右平衡 -1(全左)..0(居中)..1(全右)。</summary>
        public double Balance { get; set; }

        /// <summary>左声道增益 dB（-12..12）。</summary>
        public double LeftGainDb { get; set; }

        /// <summary>右声道增益 dB（-12..12）。</summary>
        public double RightGainDb { get; set; }

        /// <summary>左声道反相。</summary>
        public bool InvertLeft { get; set; }

        /// <summary>右声道反相。</summary>
        public bool InvertRight { get; set; }

        /// <summary>交换左右声道。</summary>
        public bool SwapChannels { get; set; }

        /// <summary>单声道模式摘要合并：off=保持立体声 / left=只用左 / right=只用右 / sum=左右求和。</summary>
        public string MonoMode { get; set; } = "off";

        public ChannelBalanceState Clone() => new()
        {
            Enabled = Enabled,
            Balance = Balance,
            LeftGainDb = LeftGainDb,
            RightGainDb = RightGainDb,
            InvertLeft = InvertLeft,
            InvertRight = InvertRight,
            SwapChannels = SwapChannels,
            MonoMode = MonoMode
        };

        public static ChannelBalanceState Default() => new() { MonoMode = "off" };

        public void Normalize()
        {
            Balance = Math.Clamp(Balance, -1.0, 1.0);
            LeftGainDb = Math.Clamp(LeftGainDb, -12.0, 12.0);
            RightGainDb = Math.Clamp(RightGainDb, -12.0, 12.0);
            if (MonoMode is not ("off" or "left" or "right" or "sum"))
            {
                MonoMode = "off";
            }
        }

        public bool IsActive => Enabled;
    }

    /// <summary>安全限幅 / 余量状态（对齐 ECHO「headroom + safety limiter」模块）。</summary>
    public sealed class DspSafetyState
    {
        /// <summary>余量(dB)：负值 = 全局预衰减，为防削波预留 headroom（-12..0）。0 = 不加余量。</summary>
        public double HeadroomDb { get; set; }

        /// <summary>安全限幅器：对超 0dBFS 的样本做软削波（soft clip / brickwall ±1.0）。
        /// 注意：此开关只影响「已有其它 DSP（EQ/声道/headroom）」时的削波保护；
        /// 单独开限幅、无其它 DSP 时不做任何逐样本处理（源 PCM 不会超 ±1，无需削波），保持 bit-perfect 直通。</summary>
        public bool EnableLimiter { get; set; } = true;

        public DspSafetyState Clone() => new()
        {
            HeadroomDb = HeadroomDb,
            EnableLimiter = EnableLimiter
        };

        public static DspSafetyState Default() => new() { HeadroomDb = 0, EnableLimiter = true };

        public void Normalize()
        {
            HeadroomDb = Math.Clamp(HeadroomDb, -12.0, 0.0);
        }

        /// <summary>是否因本模块让输出「必经过 DSP」：设了余量(负增益)或明确关闭软限幅保护时视为激活；
        /// 默认软限幅保护不算，避免让无其它 DSP 的播放也走逐样本链而拖慢（此时源 PCM 不会超 ±1，软限幅无需处理）。</summary>
        public bool AffectsBits => Math.Abs(HeadroomDb) > 0.001 || !EnableLimiter;
    }

    /// <summary>DSP 附加状态（声道平衡 + 安全限幅）持久化。</summary>
    public static class DspExtraStore
    {
        private const string FileName = "dsp-extra.json";
        private static DspExtraState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static DspExtraState Load()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return Clone(_cache);
                }

                try
                {
                    string path = GetFilePath();
                    if (File.Exists(path))
                    {
                        _cache = Normalize(JsonSerializer.Deserialize<DspExtraState>(File.ReadAllText(path)) ?? new DspExtraState());
                    }
                    else
                    {
                        _cache = new DspExtraState();
                    }
                }
                catch
                {
                    _cache = new DspExtraState();
                }

                return Clone(_cache);
            }
        }

        public static void Save(DspExtraState state)
        {
            lock (Gate)
            {
                _cache = Normalize(state ?? new DspExtraState());
                try
                {
                    string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(GetFilePath(), json);
                }
                catch
                {
                }
            }
        }

        private static DspExtraState Normalize(DspExtraState s)
        {
            s.ChannelBalance?.Normalize();
            s.Safety?.Normalize();
            return s;
        }

        private static DspExtraState Clone(DspExtraState s) => new()
        {
            ChannelBalance = s.ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
            Safety = s.Safety?.Clone() ?? DspSafetyState.Default()
        };
    }

    /// <summary>DSP 附加状态联合体。</summary>
    public sealed class DspExtraState
    {
        public ChannelBalanceState ChannelBalance { get; set; } = ChannelBalanceState.Default();
        public DspSafetyState Safety { get; set; } = DspSafetyState.Default();
    }
}
