namespace CelesteMusicPlayer
{
    /// <summary>
    /// ReplayGain 扫描范围。原定义在 ReplayGainScanWindow（旧 XAML 窗口）里；
    /// W13 把 XAML 窗口下线后枚举搬到此处，扫描逻辑本身不受影响。
    /// </summary>
    public enum ReplayGainScanScope
    {
        Library,
        Playlist,
        Selection
    }
}
