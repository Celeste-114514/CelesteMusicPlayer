namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    // One editing session for web DSP, device profiles and the legacy presentation adapter.
    private readonly EqEditSession EqState = new();
    private readonly DspApplyService DspApply = new();
}
