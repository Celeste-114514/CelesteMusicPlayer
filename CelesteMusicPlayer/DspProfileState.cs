// DspProfileState.cs
// 听音方案持久化（dsp-profiles.json）—— 2026-09-30 新增。
//
// 边界：一个方案 = 下列既有 store 当前值的一份快照（不另造一套参数格式）：
//   dsp-rack.json        机架顺序 + 压缩器 + 立体声场 + 声道矩阵
//   eq-curve.json        EQ 曲线（OPRA 耳机校正结果也落在这里）
//   dsp-extra.json       声道平衡 / Crossfeed + 余量 / 限幅
//   replaygain.json      ReplayGain 模式 + 额外增益 + 防削波
//   room-correction.json 房间校正（卷积 FIR）
//   simple-eq.json       简单模式 EQ 四滑杆
//   app-settings.json    的 SrcTargetHz / SrcQuality / SrcDither（升频三件套）
// 应用方案 = 快照写回各 store → 重载面板 UI → 既有下发路径推进引擎，
// 与用户手动改每个模块走的是同一条链路，不新开下发通道。
// 回退方式：删除 dsp-profiles.json，各 store 原样生效（方案只是快照，删不掉设置本身）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>一个听音方案：整套 DSP 链路设置的命名快照。</summary>
    public sealed class DspProfile
    {
        public string Name { get; set; } = string.Empty;

        public string CreatedUtc { get; set; } = string.Empty;

        public string ModifiedUtc { get; set; } = string.Empty;

        public RackState Rack { get; set; } = new();

        public EqCurveState Eq { get; set; } = new();

        public DspExtraState Extra { get; set; } = new();

        public ReplayGainState ReplayGain { get; set; } = new();

        public RoomCorrectionState Room { get; set; } = new();

        public SimpleEqState SimpleEq { get; set; } = new();

        /// <summary>升频目标采样率 Hz（0 = 不升频）。与 SRC 下拉框同一口径。</summary>
        public int SrcTargetHz { get; set; }

        /// <summary>升频质量键（lowlatency / balanced / transparent）。</summary>
        public string SrcQuality { get; set; } = "balanced";

        /// <summary>抖动 / 噪声整形键（off / tpdf / highpass / ns5）。</summary>
        public string SrcDither { get; set; } = "off";

        public DspProfile Clone() => new()
        {
            Name = Name,
            CreatedUtc = CreatedUtc,
            ModifiedUtc = ModifiedUtc,
            Rack = Rack.Clone(),
            Eq = Eq.Clone(),
            Extra = new DspExtraState
            {
                ChannelBalance = Extra.ChannelBalance.Clone(),
                Safety = Extra.Safety.Clone()
            },
            ReplayGain = ReplayGain.Clone(),
            Room = Room.Clone(),
            SimpleEq = SimpleEq == null
                ? new SimpleEqState()
                : new SimpleEqState { Bass = SimpleEq.Bass, Vocal = SimpleEq.Vocal, Air = SimpleEq.Air, Warm = SimpleEq.Warm },
            SrcTargetHz = SrcTargetHz,
            SrcQuality = SrcQuality,
            SrcDither = SrcDither
        };
    }

    /// <summary>方案簿根（dsp-profiles.json）。</summary>
    public sealed class DspProfileBook
    {
        public int SchemaVersion { get; set; } = 1;

        public List<DspProfile> Profiles { get; set; } = new();

        /// <summary>最近应用/保存的方案名；空 = 当前是手动拼的设置，不属于任何方案。</summary>
        public string ActiveProfile { get; set; } = string.Empty;
    }

    /// <summary>听音方案持久化：固定 %LOCALAPPDATA%\CelesteMusicPlayer\dsp-profiles.json。
    /// 与其它 store 同源同模式（JsonFile + 缓存 + 损坏备份降级）。</summary>
    public static class DspProfileStore
    {
        private const string FileName = "dsp-profiles.json";
        private const int MaxProfiles = 50;
        private static DspProfileBook? _cache;
        private static readonly object Gate = new();

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static DspProfileBook Load()
        {
            lock (Gate)
            {
                if (_cache == null)
                {
                    _cache = JsonFile.Read(GetFilePath(), new DspProfileBook());
                    _cache.Profiles ??= new List<DspProfile>();
                    _cache.ActiveProfile ??= string.Empty;
                }

                return Clone(_cache);
            }
        }

        public static void Save(DspProfileBook book)
        {
            lock (Gate)
            {
                book ??= new DspProfileBook();
                book.Profiles ??= new List<DspProfile>();
                book.ActiveProfile ??= string.Empty;
                if (book.Profiles.Count > MaxProfiles)
                {
                    book.Profiles.RemoveRange(0, book.Profiles.Count - MaxProfiles);
                }

                _cache = Clone(book);
                try
                {
                    JsonFile.Write(GetFilePath(), _cache);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DspProfileStore.Save", caught); }
            }
        }

        public static void Update(Action<DspProfileBook> mutator)
        {
            DspProfileBook book = Load();
            mutator(book);
            Save(book);
        }

        /// <summary>记住当前生效方案名（应用 / 保存时调用）。</summary>
        public static void SetActive(string name)
        {
            Update(b => b.ActiveProfile = name ?? string.Empty);
        }

        /// <summary>用户手动改过任一 DSP 设置：当前设置不再"属于"某个方案。
        /// 已是空名时不再写盘（一次手动改动最多落一次盘）。</summary>
        public static void MarkCustom()
        {
            // 快路径：缓存里已经是「手动」就直接返回。Update 的 Save 无论如何都会写文件，
            // 而本方法会被"改一个设置"这类高频路径调用（滑杆每次 ValueChanged 都会到），
            // 没有这道判断就会变成每次改动都重写一遍 dsp-profiles.json。
            lock (Gate)
            {
                if (_cache != null && string.IsNullOrEmpty(_cache.ActiveProfile))
                {
                    return;
                }
            }

            Update(b => b.ActiveProfile = string.Empty);
        }

        private static DspProfileBook Clone(DspProfileBook b) => new()
        {
            SchemaVersion = b.SchemaVersion,
            ActiveProfile = b.ActiveProfile,
            Profiles = b.Profiles.ConvertAll(p => p.Clone())
        };
    }
}
