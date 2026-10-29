using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对输出模式服务的宿主实现。
    /// W13：映射与下发在 <see cref="OutputModeService"/>，这里只接两支动作。
    /// </summary>
    public sealed partial class MainWindow : IOutputModeHost
    {
        private OutputModeService? _outputMode;

        internal OutputModeService OutputMode =>
            LazyInitializer.EnsureInitialized(ref _outputMode, () => new OutputModeService(this));

        void IOutputModeHost.NoteChainMode(string? settingsMode) => NoteChainMode(settingsMode);

        void IOutputModeHost.SetEngineOutputMode(HiFiOutputBackend.OutputMode mode) => _audioEngine?.SetOutputMode(mode);
    }
}
