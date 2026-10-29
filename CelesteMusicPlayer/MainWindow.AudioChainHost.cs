using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对「音频链路」服务的构造点。
    /// W13：序列化逻辑在 <see cref="WebAudioChainService"/>，这里只接一支取快照的委托。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebAudioChainService? _audioChainService;

        internal WebAudioChainService AudioChainService =>
            LazyInitializer.EnsureInitialized(
                ref _audioChainService,
                () => new WebAudioChainService(() => AudioSignalPath.Capture(_audioEngine, DspRackGlobalBypassed)));
    }
}
