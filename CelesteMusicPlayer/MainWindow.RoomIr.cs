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
    /// </summary>
    public sealed partial class MainWindow
    {
        // 面板就绪守卫：XAML 初始化阶段 ToggleSwitch/Slider 会就地触发事件，
        // 那时后面的 x:Name 还是 null（0xC000027B 秒崩的老坑）
        private bool _roomIrUiReady;
        private float[][]? _roomIrLoaded;
        private int _roomIrLoadedRate;

        /// <summary>进入 FIR 页时把已保存的设置填进面板（幂等，可反复调用）。</summary>
        internal void LoadRoomCorrectionIntoPanel()
        {
            if (RoomIrPathBox == null)
            {
                return;
            }

            RoomCorrectionState s = RoomCorrectionStore.Load();
            RoomIrPathBox.Text = s.IrPath ?? string.Empty;

            if (RoomIrGainSlider != null)
            {
                RoomIrGainSlider.Value = Math.Clamp(s.GainDb, -12, 12);
            }

            UpdateRoomIrGainText();

            if (RoomIrEnabledToggle != null)
            {
                RoomIrEnabledToggle.IsOn = s.Enabled;
            }

            _roomIrUiReady = true;

            if (!string.IsNullOrWhiteSpace(s.IrPath))
            {
                _ = PreloadRoomIrAsync(s.IrPath);
            }
        }

        private async void RoomIrBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FileOpenPicker picker = new()
                {
                    SuggestedStartLocation = PickerLocationId.MusicLibrary
                };
                picker.FileTypeFilter.Add(".wav");
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                if (RoomIrPathBox != null)
                {
                    RoomIrPathBox.Text = file.Path;
                }

                await PreloadRoomIrAsync(file.Path);
            }
            catch (Exception ex)
            {
                RoomIrStatus("选择文件失败：" + ex.Message);
                StartupLog.WriteException("RoomIr.Browse", ex);
            }
        }

        private void RoomIrClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (RoomIrPathBox != null)
            {
                RoomIrPathBox.Text = string.Empty;
            }

            _roomIrLoaded = null;
            _roomIrLoadedRate = 0;
            RoomIrStatus("已清除。");
            if (RoomIrEnabledToggle != null)
            {
                RoomIrEnabledToggle.IsOn = false;
            }
        }

        private void RoomIrEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_roomIrUiReady || RoomIrEnabledToggle == null)
            {
                return;
            }

            // 没有 IR 就不允许开：开了也是空卷积，白占算力还让人以为生效了
            if (RoomIrEnabledToggle.IsOn && string.IsNullOrWhiteSpace(RoomIrPathBox?.Text))
            {
                RoomIrEnabledToggle.IsOn = false;
                RoomIrStatus("请先选择脉冲响应文件");
            }
        }

        private void RoomIrGainSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateRoomIrGainText();
        }

        private void UpdateRoomIrGainText()
        {
            if (RoomIrGainValueText == null || RoomIrGainSlider == null)
            {
                return;
            }

            double g = RoomIrGainSlider.Value;
            RoomIrGainValueText.Text = (g >= 0 ? "+" : "") + g.ToString("0.0") + " dB";
        }

        private void RoomIrApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string irPath = RoomIrPathBox?.Text?.Trim() ?? string.Empty;
                RoomCorrectionState prev = RoomCorrectionStore.Load();
                RoomCorrectionState state = new()
                {
                    Enabled = RoomIrEnabledToggle?.IsOn == true,
                    IrPath = irPath,
                    GainDb = Math.Round(RoomIrGainSlider?.Value ?? 0, 1),
                    // trim 不在本卡片调整，沿用上次设置（下面「IR 信息与微调」那块管它）
                    TrimDb = prev.TrimDb,
                    IrName = string.IsNullOrWhiteSpace(irPath) ? string.Empty : System.IO.Path.GetFileName(irPath),
                    IrTapCount = _roomIrLoaded != null ? _roomIrLoaded[0].Length : prev.IrTapCount,
                    IrSampleRate = _roomIrLoadedRate > 0 ? _roomIrLoadedRate : prev.IrSampleRate,
                    IrChannels = _roomIrLoaded != null ? _roomIrLoaded.Length : prev.IrChannels
                };

                RoomCorrectionStore.Save(state);

                // 播放中实时生效 + 刷新 bit-perfect 提示
                ApplyRoomCorrection(state);
                RefreshRoomCorrectionInfo();

                RoomIrStatus(state.Enabled
                    ? "✓ 已应用并启用卷积。换歌后仍会保留该设置。"
                    : "已保存，卷积已关闭。");
            }
            catch (Exception ex)
            {
                RoomIrStatus("应用失败：" + ex.Message);
                StartupLog.WriteException("RoomIr.Apply", ex);
            }
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
            RoomIrStatus($"✓ 读取成功：{ir.Length} 声道，脉冲 {ir[0].Length} 采样"
                + (rate > 0 ? $"（源 {rate} Hz，播放时自动重采样）" : "") + "。");
        }

        private void RoomIrStatus(string text)
        {
            if (RoomIrLoadStatusText != null)
            {
                RoomIrLoadStatusText.Text = text;
            }
        }
    }
}
