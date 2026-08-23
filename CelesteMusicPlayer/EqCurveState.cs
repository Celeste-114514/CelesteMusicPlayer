using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>EQ 滤波器类型（对齐 ECHO eq types）。</summary>
    public enum EqFilterType
    {
        Peaking,     // 峰值（带通）
        LowShelf,    // 低频搁架
        HighShelf,   // 高频搁架
        LowPass,     // 低通
        HighPass,    // 高通
        Notch        // 切除
    }

    /// <summary>单条 EQ band（段）。</summary>
    public sealed class EqBand
    {
        public bool Enabled { get; set; } = true;
        public double FrequencyHz { get; set; } = 1000;
        public double GainDb { get; set; }
        public double Q { get; set; } = 1.0;
        public EqFilterType FilterType { get; set; } = EqFilterType.Peaking;

        public EqBand Clone() => new()
        {
            Enabled = Enabled,
            FrequencyHz = FrequencyHz,
            GainDb = GainDb,
            Q = Q,
            FilterType = FilterType
        };
    }

    /// <summary>EQ 曲线状态：可编辑的 band 列表 + preamp（对齐 ECHO EqState）。</summary>
    public sealed class EqCurveState
    {
        /// <summary>EQ 总开关（快速旁路）。关闭时不做 EQ DSP。</summary>
        public bool Enabled { get; set; }

        /// <summary>全局预增益 dB（-24..24），用于补偿 band 增益导致的削波/响度差异。</summary>
        public double PreampDb { get; set; }

        /// <summary>band 列表。</summary>
        public List<EqBand> Bands { get; set; } = new();

        public string PresetId { get; set; } = "flat";
        public string PresetName { get; set; } = "平坦";

        public EqCurveState Clone()
        {
            return new EqCurveState
            {
                Enabled = Enabled,
                PreampDb = PreampDb,
                Bands = Bands.Select(b => b.Clone()).ToList(),
                PresetId = PresetId,
                PresetName = PresetName
            };
        }

        public static EqCurveState Default() => CreatePreset("flat");

        public void Normalize()
        {
            PreampDb = Math.Clamp(PreampDb, -24.0, 24.0);
            if (Bands == null) Bands = new List<EqBand>();
            // 空列表默认给一条峰值（1000Hz,0dB）作为起点，便于用户在图里加段
            if (Bands.Count == 0)
            {
                Bands = new List<EqBand> { new EqBand() };
            }

            for (int i = 0; i < Bands.Count; i++)
            {
                if (Bands[i] == null) Bands[i] = new EqBand();
                var b = Bands[i];
                b.FrequencyHz = Math.Clamp(b.FrequencyHz, 20, 20000);
                b.GainDb = Math.Clamp(b.GainDb, -24, 24);
                b.Q = Math.Clamp(b.Q, 0.1, 24);
            }
        }

        /// <summary>任意 band 是否实际产生增益/滤波（决定 EQ 是否激活）。</summary>
        public bool HasEffect()
        {
            if (!Enabled) return false;
            if (Math.Abs(PreampDb) > 0.01) return true;
            return Bands.Any(b => b is { Enabled: true } && (Math.Abs(b.GainDb) > 0.01 || b.FilterType is EqFilterType.LowPass or EqFilterType.HighPass or EqFilterType.Notch));
        }

        /// <summary>预设。对齐 ECHO/常见 EQ，band 频率按音乐常用档。</summary>
        public static EqCurveState CreatePreset(string id)
        {
            return id switch
            {
                "classical" => Build(new[] { (0.0, 31.0), (0.0, 62.0), (0.0, 125.0), (0.0, 250.0), (0.0, 500.0), (-2.0, 1000.0), (-3.0, 2000.0), (-4.0, 4000.0), (-4.0, 8000.0), (-5.0, 16000.0) }),
                "pop" => Build(new[] { (-1.0, 31.0), (2.0, 62.0), (4.0, 125.0), (4.0, 250.0), (2.0, 500.0), (0.0, 1000.0), (-1.0, 2000.0), (-1.0, 4000.0), (-1.0, 8000.0), (-1.0, 16000.0) }),
                "jazz" => Build(new[] { (0.0, 31.0), (0.0, 62.0), (1.0, 125.0), (3.0, 250.0), (3.0, 500.0), (3.0, 1000.0), (2.0, 2000.0), (1.0, 4000.0), (1.0, 8000.0), (2.0, 16000.0) }),
                "rock" => Build(new[] { (4.0, 31.0), (3.0, 62.0), (2.0, 125.0), (1.0, 250.0), (0.0, 500.0), (0.0, 1000.0), (1.0, 2000.0), (2.0, 4000.0), (3.0, 8000.0), (4.0, 16000.0) }),
                "soft" => Build(new[] { (2.0, 31.0), (1.0, 62.0), (0.0, 125.0), (0.0, 250.0), (0.0, 500.0), (0.0, 1000.0), (-2.0, 2000.0), (-2.0, 4000.0), (1.0, 8000.0), (2.0, 16000.0) }),
                "bass" => Build(new[] { (6.0, 31.0), (5.0, 62.0), (4.0, 125.0), (2.0, 250.0), (1.0, 500.0), (0.0, 1000.0), (0.0, 2000.0), (0.0, 4000.0), (0.0, 8000.0), (0.0, 16000.0) }),
                // 简单模式预设（一键调音）
                "simple_bass" => SimpleTone(bass: 1.0),
                "simple_vocal" => SimpleTone(vocal: 1.0),
                "simple_air" => SimpleTone(air: 1.0),
                "simple_warm" => SimpleTone(warm: 1.0),
                _ => Build(new[] { (0.0, 31.0), (0.0, 62.0), (0.0, 125.0), (0.0, 250.0), (0.0, 500.0), (0.0, 1000.0), (0.0, 2000.0), (0.0, 4000.0), (0.0, 8000.0), (0.0, 16000.0) }) // flat
            };
        }

        private static EqCurveState Build((double gain, double freq)[] gains)
        {
            var s = new EqCurveState { Enabled = true, PreampDb = 0, PresetId = "custom", PresetName = "自定义" };
            foreach (var (g, f) in gains)
            {
                s.Bands.Add(new EqBand { Enabled = true, FrequencyHz = f, GainDb = g, Q = 1.0, FilterType = EqFilterType.Peaking });
            }

            return s;
        }

        /// <summary>简单模式一键调音：按强度生成低频/人声/高频/温暖曲线（对齐 ECHO simpleTone 思路）。</summary>
        private static EqCurveState SimpleTone(double bass = 0, double vocal = 0, double air = 0, double warm = 0)
        {
            var s = new EqCurveState { Enabled = true, PreampDb = 0, PresetId = "custom", PresetName = "自定义" };
            // 10 固定支点频率带
            double[] fr = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            foreach (double f in fr)
            {
                double g = 0;
                if (bass > 0.01)
                {
                    if (f <= 80) g += 2.5 * bass;
                    else if (f <= 160) g += 1.6 * bass;
                    else if (f <= 315) g += 0.7 * bass;
                    else if (f >= 10000) g += -0.4 * bass;
                }

                if (vocal > 0.01)
                {
                    if (f >= 800 && f <= 2500) g += 1.7 * vocal;
                    else if (f >= 315 && f < 800) g += 0.7 * vocal;
                    else if (f >= 5000 && f <= 8000) g += -0.8 * vocal;
                    else if (f <= 80) g += -0.4 * vocal;
                }

                if (air > 0.01)
                {
                    if (f >= 10000) g += 2.0 * air;
                    else if (f >= 5000) g += 1.1 * air;
                    else if (f <= 160) g += -0.5 * air;
                }

                if (warm > 0.01)
                {
                    if (f <= 125) g += 1.4 * warm;
                    else if (f >= 4000) g += -0.9 * warm;
                    else if (f >= 250 && f <= 1000) g += 0.4 * warm;
                }

                g = Math.Round(Math.Clamp(g, -12, 12) * 10) / 10;
                s.Bands.Add(new EqBand { Enabled = Math.Abs(g) > 0.01, FrequencyHz = f, GainDb = g, Q = 1.0, FilterType = EqFilterType.Peaking });
            }

            return s;
        }
    }

    /// <summary>EQ 曲线状态持久化（eq-curve.json）。</summary>
    public static class EqCurveStore
    {
        private const string FileName = "eq-curve.json";
        private static EqCurveState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            // 与主设置(AppSettingsStore)同源：固定路径，规避 packaged 下 ApplicationData 路径漂浮导致重启读回默认。
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static EqCurveState Load()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache.Clone();
                try
                {
                    string p = GetFilePath();
                    if (File.Exists(p))
                    {
                        var s = JsonSerializer.Deserialize<EqCurveState>(File.ReadAllText(p));
                        s ??= EqCurveState.Default();
                        s.Normalize();
                        _cache = s;
                    }
                    else
                    {
                        _cache = EqCurveState.Default();
                    }
                }
                catch
                {
                    _cache = EqCurveState.Default();
                }

                return _cache.Clone();
            }
        }

        public static void Save(EqCurveState state)
        {
            lock (Gate)
            {
                var s = state?.Clone() ?? EqCurveState.Default();
                s.Normalize();
                _cache = s;
                try
                {
                    File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>用户自定义 EQ 预设：列表 + 增删查（eq-user-presets.json）。每个预设含完整曲线状态。</summary>
    public static class EqUserPresetStore
    {
        private const string FileName = "eq-user-presets.json";
        private static List<EqCurveState>? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            // 与主设置(AppSettingsStore)同源：固定路径，规避 packaged 下 ApplicationData 路径漂浮导致重启读回默认。
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        /// <summary>加载用户预设列表（深拷贝，供 UI 编辑前隔离）。</summary>
        public static List<EqCurveState> Load()
        {
            lock (Gate)
            {
                if (_cache != null) return CloneList(_cache);
                try
                {
                    string p = GetFilePath();
                    if (File.Exists(p))
                    {
                        var list = JsonSerializer.Deserialize<List<EqCurveState>>(File.ReadAllText(p)) ?? new List<EqCurveState>();
                        foreach (var s in list) s?.Normalize();
                        _cache = list;
                    }
                    else
                    {
                        _cache = new List<EqCurveState>();
                    }
                }
                catch
                {
                    _cache = new List<EqCurveState>();
                }

                return CloneList(_cache);
            }
        }

        public static void Save(List<EqCurveState> presets)
        {
            lock (Gate)
            {
                _cache = presets?.Where(s => s != null).Select(s => { s.Normalize(); return s; }).ToList() ?? new List<EqCurveState>();
                try
                {
                    File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                }
            }
        }

        /// <summary>添加/更新用户预设（id 已存在则覆盖；否则分配新 id）。返回预设 id。</summary>
        public static string Upsert(EqCurveState preset)
        {
            lock (Gate)
            {
                var list = Load();
                if (string.IsNullOrWhiteSpace(preset.PresetId) || !preset.PresetId.StartsWith("user:", StringComparison.Ordinal))
                {
                    preset.PresetId = "user:" + Guid.NewGuid().ToString("N").Substring(0, 8);
                }

                int idx = list.FindIndex(s => s != null && string.Equals(s.PresetId, preset.PresetId, StringComparison.Ordinal));
                var copy = preset.Clone();
                copy.PresetId = preset.PresetId;
                if (idx >= 0) list[idx] = copy;
                else list.Add(copy);
                Save(list);
                return preset.PresetId;
            }
        }

        public static void Delete(string presetId)
        {
            lock (Gate)
            {
                var list = Load();
                list.RemoveAll(s => s != null && string.Equals(s.PresetId, presetId, StringComparison.Ordinal));
                Save(list);
            }
        }

        public static EqCurveState? FindById(string presetId)
        {
            lock (Gate)
            {
                var list = Load();
                var hit = list.FirstOrDefault(s => s != null && string.Equals(s.PresetId, presetId, StringComparison.Ordinal));
                return hit?.Clone();
            }
        }

        private static List<EqCurveState> CloneList(List<EqCurveState> list) => list.Where(s => s != null).Select(s => s.Clone()).ToList();
    }
}
