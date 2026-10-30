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
            if (operation.StartsWith("opra", StringComparison.Ordinal)) return await WebDspOpraAsync(operation, msg);
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
                ApplyFirParameters();
                return new { ok = true };
            }
            if (operation == "firClear")
            {
                FirParameters.ClearIr();
                _roomIrLoaded = null; _roomIrLoadedRate = 0;
                ApplyFirParameters();
                return new { ok = true };
            }
            if (operation == "firApply") { ApplyFirParameters(); return new { ok = true }; }
            return WebDsp(msg);
        }

        private object WebDspModuleInfo(string module)
        {
            var room = FirParameters.Snapshot();
            return new
            {
                module,
                session = _audioEngine?.LevelMeterChannels > 0,
                src = SrcSessionStateText?.Text ?? "",
                srcTarget = AppSettingsStore.Load().SrcTargetHz,
                headroom = ExtraParameters.Safety.HeadroomDb,
                limiter = ExtraParameters.Safety.EnableLimiter,
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
