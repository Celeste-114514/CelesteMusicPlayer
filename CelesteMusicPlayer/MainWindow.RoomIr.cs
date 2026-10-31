using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 房间校正（卷积 FIR）的 IR 导入（2026-10-12 阶段 2）：
    /// 原先要点「打开房间校正」弹独立窗口 RoomCorrectionWindow，现在就在
    /// 「音效处理 → FIR / 房间校正」页里选文件 / 开关 / 应用。
    /// 校验与应用口径照搬原窗口：加载成功才允许启用，应用后立刻对播放生效。
    /// 下面那块「IR 信息与微调」管的是 trim（防削波余量），与本页的输出增益不是一回事。
    ///
    /// 2026-10-30 W13：参数搬到 <see cref="DspFirParameters"/>，本文件只剩三件事 ——
    /// 选文件（写模型）、把模型投影到控件、落盘并下发。面板自己不再保存任何业务值。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 面板就绪守卫：XAML 初始化阶段 ToggleSwitch/Slider 会就地触发事件，
        // 那时后面的 x:Name 还是 null（0xC000027B 秒崩的老坑）
        private bool _roomIrUiReady;
        private float[][]? _roomIrLoaded;
        private int _roomIrLoadedRate;

        /// <summary>进入 FIR 页时把存档装进模型并投影到面板（幂等，可反复调用）。</summary>
        internal void LoadRoomCorrectionIntoPanel()
        {

            FirParameters.Load(RoomCorrectionStore.Load());
            _roomIrUiReady = true;
            ProjectFirParameters();

            if (FirParameters.HasIr)
            {
                _ = PreloadRoomIrAsync(FirParameters.Current.IrPath);
            }
        }

        /// <summary>模型 → 控件（路径框 / 增益滑杆 / 模块开关 / trim 滑杆 + 两条读数）。</summary>
        private void ProjectFirParameters()
        {
            bool loading = _audioFxLoading;
            _audioFxLoading = true;
            try
            {



            }
            finally
            {
                _audioFxLoading = loading;
            }

            UpdateRoomIrGainText();
            UpdateRoomTrimLabel();
        }


        /// <summary>把模型里的房间校正参数落盘并实时下发（网页命令 / 页头电源开关 / 面板「应用」共用）。</summary>
        private void ApplyFirParameters()
        {
            // IR 元数据（taps / 源采样率 / 声道数）沿用最近一次成功加载的结果：
            // 没重新加载过时保留盘中旧值 —— 与旧版「保存时才回写」的口径一致
            if (_roomIrLoaded is { Length: > 0 })
            {
                int rate = _roomIrLoadedRate > 0 ? _roomIrLoadedRate : FirParameters.Current.IrSampleRate;
                FirParameters.SetIrMetadata(_roomIrLoaded[0].Length, rate, _roomIrLoaded.Length);
            }

            RoomCorrectionState state = FirParameters.Snapshot();
            RoomCorrectionStore.Save(state);
            ApplyRoomCorrection(state);
        }





        private void UpdateRoomIrGainText()
        {

            double g = FirParameters.Current.GainDb;
        }

        private void UpdateRoomTrimLabel()
        {

        }


        private async Task PreloadRoomIrAsync(string path)
        {
            RoomIrStatus("正在读取/校验脉冲响应…");
            float[][]? ir = null;
            int rate = 0;
            await Task.Run(() =>
            {
                // 用 48000 预检（实际运行时用播放采样率，差异仅显示用）
                ir = ConvolutionIr.Load(path, 48000);
                try
                {
                    using NAudio.Wave.WaveFileReader r = new(path);
                    rate = r.WaveFormat.SampleRate;
                }
                catch
                {
                    // 读不出采样率只是少显示一项，不影响启用
                }
            });

            if (ir == null)
            {
                _roomIrLoaded = null;
                _roomIrLoadedRate = 0;
                RoomIrStatus("⚠ 无法读取该文件（需要有效的 PCM WAV，16/24/32-bit 或 Float）。");
                return;
            }

            _roomIrLoaded = ir;
            _roomIrLoadedRate = rate;

            // 读成功才把这份 IR 的路径写进模型（taps 等元数据等落盘时再回写，见 ApplyFirParameters）
            FirParameters.SetIr(path);
            ProjectFirParameters();

            RoomIrStatus($"✓ 读取成功：{ir.Length} 声道，脉冲 {ir[0].Length} 采样"
                + (rate > 0 ? $"（源 {rate} Hz，播放时自动重采样）" : "") + "。");
        }

        private void RoomIrStatus(string text)
        {
        }
    }
}
