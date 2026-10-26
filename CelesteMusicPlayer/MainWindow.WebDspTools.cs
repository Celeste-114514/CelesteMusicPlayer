using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        internal async Task<object> WebDspAsync(JsonElement msg)
        {
            string operation = ReadString(msg, "operation");
            EnsureWebDspUiReady();
            if (operation == "importApo") { await ImportAudioFxEqApoCoreAsync(); return new { ok = true }; }
            if (operation == "exportApo") { await ExportAudioFxEqApoCoreAsync(); return new { ok = true }; }
            if (operation == "savePreset") return new { message = await SaveAudioFxEqPresetCoreAsync(ReadString(msg, "name")) };
            if (operation == "firImport")
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".wav");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerHostHandle());
                var file = await picker.PickSingleFileAsync();
                if (file == null) return new { canceled = true };
                await PreloadRoomIrAsync(file.Path);
                if (_roomIrLoaded == null) throw new InvalidOperationException("无法读取该脉冲响应，请选择 PCM WAV 文件。");
                RoomIrPathBox.Text = file.Path;
                WebDspSaveRoom();
                return new { ok = true };
            }
            if (operation == "firClear")
            {
                RoomIrPathBox.Text = ""; RoomIrEnabledToggle.IsOn = false;
                _roomIrLoaded = null; _roomIrLoadedRate = 0;
                WebDspSaveRoom();
                return new { ok = true };
            }
            if (operation == "firApply") { WebDspSaveRoom(); return new { ok = true }; }
            return WebDsp(msg);
        }

        private void WebDspSaveRoom()
        {
            string path = RoomIrPathBox?.Text?.Trim() ?? "";
            if (RoomIrEnabledToggle.IsOn && (path.Length == 0 || !File.Exists(path)))
                throw new InvalidOperationException("请先导入有效的脉冲响应文件。");
            var previous = RoomCorrectionStore.Load();
            var state = new RoomCorrectionState
            {
                Enabled = RoomIrEnabledToggle.IsOn,
                IrPath = path,
                GainDb = RoomIrGainSlider.Value,
                TrimDb = RoomTrimSlider.Value,
                IrName = Path.GetFileName(path),
                IrTapCount = _roomIrLoaded is { Length: > 0 } ? _roomIrLoaded[0].Length : previous.IrTapCount,
                IrSampleRate = _roomIrLoadedRate > 0 ? _roomIrLoadedRate : previous.IrSampleRate,
                IrChannels = _roomIrLoaded?.Length ?? previous.IrChannels
            };
            RoomCorrectionStore.Save(state);
            ApplyRoomCorrection(state);
            RefreshRoomCorrectionInfo();
            SyncDspPowerSwitches();
        }

        private object WebDspModuleInfo(string module)
        {
            var room = RoomCorrectionStore.Load();
            return new
            {
                module,
                session = _audioEngine?.LevelMeterChannels > 0,
                src = SrcSessionStateText?.Text ?? "",
                srcTarget = AppSettingsStore.Load().SrcTargetHz,
                headroom = AudioFxSafetyHeadroomSlider.Value,
                limiter = AudioFxSafetyLimiterToggle.IsOn,
                irName = room.IrName ?? "",
                irPath = room.IrPath ?? "",
                taps = room.IrTapCount,
                sampleRate = room.IrSampleRate,
                channels = room.IrChannels,
                enabled = room.Enabled,
                gain = room.GainDb,
                trim = room.TrimDb,
                clippingRisk = _audioEngine?.ConvolutionClippingRisk ?? false
            };
        }

        private static double WebDspFinite(JsonElement value, double min, double max)
        {
            double number = value.GetDouble();
            if (!double.IsFinite(number)) throw new ArgumentException("音效参数必须是有限数值。");
            return Math.Clamp(number, min, max);
        }
    }
}
