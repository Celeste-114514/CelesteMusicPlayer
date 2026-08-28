using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.Graphics;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 房间校正（卷积 FIR）独立窗口：导入脉冲响应 WAV（IR）做真卷积处理，
    /// 与参数 EQ（OPRA 耳机校正）区分。设置持久化到 RoomCorrectionStore。
    /// </summary>
    public sealed partial class RoomCorrectionWindow : Window
    {
        private static RoomCorrectionWindow? _instance;
        private float[][]? _loadedIr;         // 预加载的 IR（用于信息展示 + 确认可启用）
        private int _loadedIrRate;            // 加载 IR 时用的源采样率（读取用）

        public RoomCorrectionWindow()
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "房间校正 · 卷积 FIR";
            ExtendsContentIntoTitleBar = true;
            AppWindow.Resize(new SizeInt32(780, 620));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            LoadFromStore();

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void OpenOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }

            _instance = new RoomCorrectionWindow();
            _instance.Activate();
        }

        private void LoadFromStore()
        {
            RoomCorrectionState s = RoomCorrectionStore.Load();
            IrPathTextBox.Text = s.IrPath ?? string.Empty;
            EnabledToggle.IsOn = s.Enabled;
            GainSlider.Value = Math.Clamp(s.GainDb, -12, 12);
            UpdateGainText();
            UpdateIrInfo();

            // 预加载 IR 供信息展示并校验可用性
            if (!string.IsNullOrWhiteSpace(s.IrPath))
            {
                _ = PreloadIrPathAsync(s.IrPath);
            }
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        // ---------------------------------------------------------------- 文件选择

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary
            };
            picker.FileTypeFilter.Add(".wav");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            IrPathTextBox.Text = file.Path;
            ClearButton.IsEnabled = true;
            await PreloadIrPathAsync(file.Path);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            IrPathTextBox.Text = string.Empty;
            _loadedIr = null;
            _loadedIrRate = 0;
            IrInfoText.Text = string.Empty;
            IrLoadStatusText.Text = "已清除。";
            EnabledToggle.IsOn = false;
        }

        private async Task PreloadIrPathAsync(string path)
        {
            IrLoadStatusText.Text = "正在读取/校验脉冲响应…";
            float[][]? ir = null;
            int rate = 0;
            await Task.Run(() =>
            {
                // 用 48000 预检（实际运行时用播放采样率，差异仅显示用）
                ir = ConvolutionIr.Load(path, 48000);
                try
                {
                    using var r = new NAudio.Wave.WaveFileReader(path);
                    rate = r.WaveFormat.SampleRate;
                }
                catch { }
            });

            if (ir == null)
            {
                IrLoadStatusText.Text = "⚠ 无法读取该文件（需要有效的 PCM WAV，16/24/32-bit 或 Float）。";
                _loadedIr = null;
                _loadedIrRate = 0;
                return;
            }

            _loadedIr = ir;
            _loadedIrRate = rate;
            IrLoadStatusText.Text =
                $"✓ 读取成功：{ir.Length} 声道，脉冲 {ir[0].Length} 采样" +
                (rate > 0 ? $"（源 {rate} Hz，播放时自动重采样）" : "") + "。";
            UpdateIrInfo();
        }

        private void UpdateIrInfo()
        {
            string irPath = IrPathTextBox.Text ?? string.Empty;
            IrInfoText.Text = string.IsNullOrWhiteSpace(irPath)
                ? "未选择 IR"
                : "已选：" + irPath;
            ApplyButton.IsEnabled = !string.IsNullOrWhiteSpace(irPath);
        }

        // ---------------------------------------------------------------- 状态

        private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (EnabledToggle.IsOn && string.IsNullOrWhiteSpace(IrPathTextBox.Text))
            {
                // 未选 IR 时不允许启用
                EnabledToggle.IsOn = false;
                StatusHint("请先选择脉冲响应文件");
                return;
            }
        }

        private void GainSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateGainText();
        }

        private void UpdateGainText()
        {
            double g = GainSlider.Value;
            GainValueText.Text = (g >= 0 ? "+" : "") + g.ToString("0.0") + " dB";
        }

        private void StatusHint(string text)
        {
            IrLoadStatusText.Text = text;
        }

        // ---------------------------------------------------------------- 应用 / 关闭

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var state = new RoomCorrectionState
                {
                    Enabled = EnabledToggle.IsOn,
                    IrPath = IrPathTextBox.Text?.Trim() ?? string.Empty,
                    GainDb = Math.Round(GainSlider.Value, 1)
                };

                RoomCorrectionStore.Save(state);

                // 应用到播放器（播放中实时生效 + 刷新 bit-perfect 提示）
                MainWindow.Instance?.ApplyRoomCorrection(state);

                StatusHint(state.Enabled
                    ? "✓ 已应用并启用卷积。换歌后仍会保留该设置。"
                    : "已保存，卷积已关闭。");
            }
            catch (Exception ex)
            {
                StatusHint("应用失败：" + ex.Message);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
