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

        /// <summary>左声道延迟（毫秒，0-10）。用于校正左右声道时序差（对齐 ECHO 声道工具）。</summary>
        public double LeftDelayMs { get; set; }

        /// <summary>右声道延迟（毫秒，0-10）。</summary>
        public double RightDelayMs { get; set; }

        /// <summary>耳机 Crossfeed（声场交叉馈送）：把对侧声道经低通后混入本侧，模拟扬声器串扰，
        /// 缓解耳机「头中定位」、让声场更自然开阔。属于声道平衡模块的子能力，需模块启用才生效。</summary>
        public bool CrossfeedEnabled { get; set; }

        /// <summary>Crossfeed 强度 0-100（%）。0 等同于关闭；内部映射到最高 70% 混音系数。</summary>
        public int CrossfeedLevel { get; set; }

        public ChannelBalanceState Clone() => new()
        {
            Enabled = Enabled,
            Balance = Balance,
            LeftGainDb = LeftGainDb,
            RightGainDb = RightGainDb,
            InvertLeft = InvertLeft,
            InvertRight = InvertRight,
            SwapChannels = SwapChannels,
            MonoMode = MonoMode,
            LeftDelayMs = LeftDelayMs,
            RightDelayMs = RightDelayMs,
            CrossfeedEnabled = CrossfeedEnabled,
            CrossfeedLevel = CrossfeedLevel
        };

        public static ChannelBalanceState Default() => new() { MonoMode = "off" };

        public void Normalize()
        {
            Balance = Math.Clamp(Balance, -1.0, 1.0);
            LeftGainDb = Math.Clamp(LeftGainDb, -12.0, 12.0);
            RightGainDb = Math.Clamp(RightGainDb, -12.0, 12.0);
            LeftDelayMs = Math.Clamp(LeftDelayMs, 0.0, 10.0);
            RightDelayMs = Math.Clamp(RightDelayMs, 0.0, 10.0);
            CrossfeedLevel = Math.Clamp(CrossfeedLevel, 0, 100);
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
            // 与主设置(AppSettingsStore)同源：固定 %LOCALAPPDATA%\CelesteMusicPlayer，
            // 规避 MSIX/packaged 下 ApplicationData.Current.LocalFolder 路径漂浮导致"保存正确却重启读回默认"。
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static DspExtraState Load()
        {
            lock (Gate)
            {
                if (_cache == null)
                {
                    _cache = JsonFile.Read(GetFilePath(), new DspExtraState());
                    Normalize(_cache);
                }

                return JsonFile.DeepClone(_cache);
            }
        }

        public static void Save(DspExtraState state)
        {
            lock (Gate)
            {
                _cache = Normalize(state ?? new DspExtraState());
                JsonFile.Write(GetFilePath(), _cache);
            }
        }

        private static DspExtraState Normalize(DspExtraState s)
        {
            s.ChannelBalance?.Normalize();
            s.Safety?.Normalize();
            return s;
        }
    }

    /// <summary>DSP 附加状态联合体。</summary>
    public sealed class DspExtraState
    {
        public ChannelBalanceState ChannelBalance { get; set; } = ChannelBalanceState.Default();
        public DspSafetyState Safety { get; set; } = DspSafetyState.Default();
    }

    /// <summary>简单模式 EQ 的滑杆值（低频/人声/通透/暖色）。独立持久化，避免叠加到曲线 band 无法反解。</summary>
    public sealed class SimpleEqState
    {
        public double Bass { get; set; }
        public double Vocal { get; set; }
        public double Air { get; set; }
        public double Warm { get; set; }
    }

    /// <summary>简单模式 EQ 滑杆值持久化。</summary>
    public static class SimpleEqStore
    {
        private const string FileName = "simple-eq.json";
        private static SimpleEqState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static SimpleEqState Load()
        {
            lock (Gate)
            {
                _cache ??= JsonFile.Read(GetFilePath(), new SimpleEqState());
                return JsonFile.DeepClone(_cache);
            }
        }

        public static void Save(SimpleEqState state)
        {
            if (state == null) return;
            lock (Gate)
            {
                _cache = state;
                JsonFile.Write(GetFilePath(), _cache);
            }
        }
    }

    /// <summary>房间校正（卷积 FIR / 脉冲响应）状态。</summary>
    public sealed class RoomCorrectionState
    {
        /// <summary>是否启用卷积处理。</summary>
        public bool Enabled { get; set; }

        /// <summary>脉冲响应 WAV 文件路径。</summary>
        public string IrPath { get; set; } = string.Empty;

        /// <summary>卷积输出增益（dB，默认 0）。</summary>
        public double GainDb { get; set; }

        public RoomCorrectionState Clone() => new()
        {
            Enabled = Enabled,
            IrPath = IrPath,
            GainDb = GainDb
        };
    }

    /// <summary>房间校正（卷积 FIR）持久化：固定 %LOCALAPPDATA%\CelesteMusicPlayer\room-correction.json。</summary>
    public static class RoomCorrectionStore
    {
        private const string FileName = "room-correction.json";
        private static RoomCorrectionState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static RoomCorrectionState Load()
        {
            lock (Gate)
            {
                _cache ??= JsonFile.Read(GetFilePath(), new RoomCorrectionState());
                return JsonFile.DeepClone(_cache);
            }
        }

        public static void Save(RoomCorrectionState state)
        {
            if (state == null) return;
            lock (Gate)
            {
                _cache = state;
                JsonFile.Write(GetFilePath(), _cache);
            }
        }
    }
}
