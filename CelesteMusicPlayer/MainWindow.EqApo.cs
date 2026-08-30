using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// Equalizer APO 配置（config.txt）的导入 / 导出，挂在「音效处理」面板的曲线 EQ 工具栏。
    /// 目标是曲线 EQ（任意频段 / 增益 / Q / 滤波类型），不是固定 10 段那个均衡器窗口——
    /// APO 的任意滤波段只有曲线 EQ 表达得了。
    /// </summary>
    public sealed partial class MainWindow
    {
        private async void AudioFxEqImportApo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".txt");

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                string text = await FileIO.ReadTextAsync(file);
                if (!EqualizerApoConverter.TryImport(text, out EqCurveState? curve, out int imported, out int skipped, out string error)
                    || curve == null)
                {
                    await ShowErrorAsync("导入 APO 配置", error);
                    return;
                }

                ApplyEqCurveToPlayer(curve);

                string msg = "已导入 APO：" + imported + " 段"
                    + (skipped > 0 ? "（" + skipped + " 段类型不支持已跳过）" : string.Empty);
                NowPlayingText.Text = msg;
                StartupLog.Write("APO 导入成功: " + msg);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导入 APO 配置失败", ex.Message);
            }
        }

        private async void AudioFxEqExportApo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 优先导出面板里正在编辑的曲线（可能有未落盘的改动）
                EqCurveState curve = (_audioFxEqBuilt && _audioFxEq != null)
                    ? _audioFxEq.Clone()
                    : EqCurveStore.Load();

                string text = EqualizerApoConverter.Export(curve);

                var picker = new FileSavePicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = "config";
                picker.FileTypeChoices.Add("Equalizer APO 配置", new List<string> { ".txt" });

                StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                await FileIO.WriteTextAsync(file, text);
                NowPlayingText.Text = "已导出 APO 配置：" + curve.Bands.Count + " 段";
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导出 APO 配置失败", ex.Message);
            }
        }
    }
}
