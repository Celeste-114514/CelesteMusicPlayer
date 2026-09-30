// DspRackState.cs
// DSP 机架状态持久化（dsp-rack.json）—— Stage B 新增。
//
// 边界（与既有 store 的分工）：
//   - 本文件管「新机架相关内容」：8 模块处理顺序 + 压缩器 + 立体声场 + 声道矩阵；
//   - EQ 曲线（eq-curve.json）/ 声道平衡与限幅（dsp-extra.json）/ ReplayGain
//     （replaygain.json）/ 房间校正（room-correction.json）继续由各自 store 保管，
//     UI 既有链路照旧推送，经 ManagedDspSourceProvider 的映射在新内核上生效 —— 老用户
//     盘上旧设置行为不变，无需迁移数据文件。
//   - 首次生成本文件即视为「迁移完成」（MigratedFromLegacyStores=true，仅诊断标记）。
//     回退方式：删除本文件 + 回退 Stage B 提交，旧 store 原样生效。

using System;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>压缩器参数（对齐内核 echo::CompressorState；默认值同 ECHO）。</summary>
    public sealed class CompressorSettings
    {
        public bool Enabled { get; set; }

        /// <summary>阈值 dB（-60..0）。</summary>
        public double ThresholdDb { get; set; } = -18.0;

        /// <summary>压缩比（1..20）。</summary>
        public double Ratio { get; set; } = 4.0;

        /// <summary>启动时间 ms（0.1..200）。</summary>
        public double AttackMs { get; set; } = 10.0;

        /// <summary>释放时间 ms（5..2000）。</summary>
        public double ReleaseMs { get; set; } = 120.0;

        /// <summary>软拐点宽度 dB（0..24）。</summary>
        public double KneeDb { get; set; } = 6.0;

        /// <summary>补偿增益 dB（-12..24）。</summary>
        public double MakeupDb { get; set; }

        /// <summary>干湿混合（0..1）。</summary>
        public double Mix { get; set; } = 1.0;

        public CompressorSettings Clone() => new()
        {
            Enabled = Enabled,
            ThresholdDb = ThresholdDb,
            Ratio = Ratio,
            AttackMs = AttackMs,
            ReleaseMs = ReleaseMs,
            KneeDb = KneeDb,
            MakeupDb = MakeupDb,
            Mix = Mix
        };

        public void Normalize()
        {
            ThresholdDb = Math.Clamp(ThresholdDb, -60.0, 0.0);
            Ratio = Math.Clamp(Ratio, 1.0, 20.0);
            AttackMs = Math.Clamp(AttackMs, 0.1, 200.0);
            ReleaseMs = Math.Clamp(ReleaseMs, 5.0, 2000.0);
            KneeDb = Math.Clamp(KneeDb, 0.0, 24.0);
            MakeupDb = Math.Clamp(MakeupDb, -12.0, 24.0);
            Mix = Math.Clamp(Mix, 0.0, 1.0);
        }

        /// <summary>是否产生实际处理（全默认旁路值 = 不激活）。</summary>
        public bool IsActive => Enabled && Mix > 0.001;
    }

    /// <summary>立体声场参数（M/S 域；对齐内核 echo::StereoFieldState）。</summary>
    public sealed class StereoFieldSettings
    {
        public bool Enabled { get; set; }

        /// <summary>声场宽度（0..2，1=原始）。</summary>
        public double Width { get; set; } = 1.0;

        /// <summary>中心（中置）增益 dB（-18..18）。</summary>
        public double CenterGainDb { get; set; }

        /// <summary>侧向（差）增益 dB（-18..18）。</summary>
        public double SideGainDb { get; set; }

        public StereoFieldSettings Clone() => new()
        {
            Enabled = Enabled,
            Width = Width,
            CenterGainDb = CenterGainDb,
            SideGainDb = SideGainDb
        };

        public void Normalize()
        {
            Width = Math.Clamp(Width, 0.0, 2.0);
            CenterGainDb = Math.Clamp(CenterGainDb, -18.0, 18.0);
            SideGainDb = Math.Clamp(SideGainDb, -18.0, 18.0);
        }

        /// <summary>是否产生实际处理。</summary>
        public bool IsActive => Enabled
            && (Math.Abs(Width - 1.0) > 0.001 || Math.Abs(CenterGainDb) > 0.01 || Math.Abs(SideGainDb) > 0.01);
    }

    /// <summary>声道矩阵参数（L/R 2×2 混合，±2；对齐内核 echo::ChannelMatrixState）。</summary>
    public sealed class ChannelMatrixSettings
    {
        public bool Enabled { get; set; }

        /// <summary>左→左（默认 1）。</summary>
        public double LeftToLeft { get; set; } = 1.0;

        /// <summary>右→左（默认 0）。</summary>
        public double RightToLeft { get; set; }

        /// <summary>左→右（默认 0）。</summary>
        public double LeftToRight { get; set; }

        /// <summary>右→右（默认 1）。</summary>
        public double RightToRight { get; set; } = 1.0;

        public ChannelMatrixSettings Clone() => new()
        {
            Enabled = Enabled,
            LeftToLeft = LeftToLeft,
            RightToLeft = RightToLeft,
            LeftToRight = LeftToRight,
            RightToRight = RightToRight
        };

        public void Normalize()
        {
            LeftToLeft = Math.Clamp(LeftToLeft, -2.0, 2.0);
            RightToLeft = Math.Clamp(RightToLeft, -2.0, 2.0);
            LeftToRight = Math.Clamp(LeftToRight, -2.0, 2.0);
            RightToRight = Math.Clamp(RightToRight, -2.0, 2.0);
        }

        /// <summary>是否产生实际处理（单位矩阵 = 不激活）。</summary>
        public bool IsActive => Enabled
            && (Math.Abs(LeftToLeft - 1.0) > 0.001 || Math.Abs(RightToLeft) > 0.001
                || Math.Abs(LeftToRight) > 0.001 || Math.Abs(RightToRight - 1.0) > 0.001);
    }

    /// <summary>DSP 机架状态联合体（dsp-rack.json 根）。</summary>
    public sealed class RackState
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>机架处理顺序：8 个模块 ID（<see cref="DspCoreInterop.RackEqualizer"/> 等）。
        /// 默认 ECHO 顺序 EQ→卷积→RG→压缩→Crossfeed→立体声场→矩阵→平衡。</summary>
        public int[] RackOrder { get; set; } =
        {
            DspCoreInterop.RackEqualizer, DspCoreInterop.RackConvolution, DspCoreInterop.RackReplayGain,
            DspCoreInterop.RackCompressor, DspCoreInterop.RackCrossfeed, DspCoreInterop.RackStereoField,
            DspCoreInterop.RackChannelMatrix, DspCoreInterop.RackChannelBalance
        };

        public CompressorSettings Compressor { get; set; } = new();

        public StereoFieldSettings StereoField { get; set; } = new();

        public ChannelMatrixSettings Matrix { get; set; } = new();

        /// <summary>旧设置迁移标记：首次生成 dsp-rack.json 时置 true。仅诊断用，
        /// 旧设置（EQ/声道/限幅/RG/卷积）始终由各自 store 保管，不迁移数据。</summary>
        public bool MigratedFromLegacyStores { get; set; }

        public RackState Clone() => new()
        {
            SchemaVersion = SchemaVersion,
            RackOrder = (int[])RackOrder.Clone(),
            Compressor = Compressor.Clone(),
            StereoField = StereoField.Clone(),
            Matrix = Matrix.Clone(),
            MigratedFromLegacyStores = MigratedFromLegacyStores
        };

        public void Normalize()
        {
            SchemaVersion = SchemaVersion <= 0 ? 1 : SchemaVersion;
            Compressor ??= new CompressorSettings();
            StereoField ??= new StereoFieldSettings();
            Matrix ??= new ChannelMatrixSettings();
            Compressor.Normalize();
            StereoField.Normalize();
            Matrix.Normalize();

            // 机架顺序必须是 0..7 的一个排列，否则回退默认（内核同样会拒绝非法顺序）
            if (RackOrder == null || RackOrder.Length != DspCoreInterop.RackModuleCount)
            {
                RackOrder = DefaultOrder();
                return;
            }

            bool[] seen = new bool[DspCoreInterop.RackModuleCount];
            bool ok = true;
            foreach (int id in RackOrder)
            {
                if (id < 0 || id >= DspCoreInterop.RackModuleCount || seen[id])
                {
                    ok = false;
                    break;
                }
                seen[id] = true;
            }

            if (!ok)
            {
                RackOrder = DefaultOrder();
            }
        }

        private static int[] DefaultOrder() => new[]
        {
            DspCoreInterop.RackEqualizer, DspCoreInterop.RackConvolution, DspCoreInterop.RackReplayGain,
            DspCoreInterop.RackCompressor, DspCoreInterop.RackCrossfeed, DspCoreInterop.RackStereoField,
            DspCoreInterop.RackChannelMatrix, DspCoreInterop.RackChannelBalance
        };
    }

    /// <summary>DSP 机架状态持久化：固定 %LOCALAPPDATA%\CelesteMusicPlayer\dsp-rack.json。
    /// 与其它 store 同源同模式（JsonFile + DeepClone + Normalize）。</summary>
    public static class DspRackStore
    {
        private const string FileName = "dsp-rack.json";
        private static RackState? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        /// <summary>加载机架状态。文件不存在 = 首次运行：写默认机架 + 迁移标记（旧 store 不动）。
        /// 文件损坏：备份 .corrupt 时间戳副本后用默认（与 AppSettingsStore 同策略）。</summary>
        public static RackState Load()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return JsonFile.DeepClone(_cache);
                }

                string path = GetFilePath();
                if (File.Exists(path))
                {
                    try
                    {
                        _cache = JsonFile.Read(path, new RackState());
                        _cache.Normalize();
                    }
                    catch (Exception caught)
                    {
                        try
                        {
                            string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                            File.Copy(path, backup, overwrite: true);
                            StartupLog.Write("dsp-rack.json 损坏，已备份到 " + backup + " 并用默认机架");
                        }
                        catch (Exception inner) { global::CelesteMusicPlayer.StartupLog.WriteException("DspRackStore.Load", inner); }

                        _cache = new RackState();
                    }
                }
                else
                {
                    // 首次：默认机架（ECHO 顺序）+ 迁移完成标记。
                    // 旧设置继续由 eq-curve.json / dsp-extra.json / replaygain.json /
                    // room-correction.json 保管（UI 既有推送链路不变），行为零变化。
                    _cache = new RackState { MigratedFromLegacyStores = true };
                    try
                    {
                        JsonFile.Write(path, _cache);
                        StartupLog.Write("[DSP] dsp-rack.json 首次生成（默认机架顺序；旧设置沿用既有 store，行为不变）");
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DspRackStore.Load", caught); }
                }

                return JsonFile.DeepClone(_cache);
            }
        }

        public static void Save(RackState state)
        {
            lock (Gate)
            {
                _cache = (state ?? new RackState()).Clone();
                _cache.Normalize();
                try
                {
                    JsonFile.Write(GetFilePath(), _cache);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DspRackStore.Save", caught); }
            }
        }

        public static void Update(Action<RackState> mutator)
        {
            RackState state = Load();
            mutator(state);
            Save(state);
        }
    }
}
