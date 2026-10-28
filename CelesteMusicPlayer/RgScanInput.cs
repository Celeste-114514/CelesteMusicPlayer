namespace CelesteMusicPlayer
{
    /// <summary>扫描输入的一首曲目（含用于专辑分组的专辑信息）。</summary>
    /// <remarks>
    /// W6 服务迁出：原先定义在 ReplayGainScanner.cs（依赖 TagLib# 与进程调用）里，
    /// 而 LibraryDb.GetAllTracksForScan 返回它，导致曲库层被拖进扫描器依赖。
    /// 挪成独立文件后两边各取所需：曲库层只需这个纯数据类。
    /// 命名空间未变，所有调用点无需改动。
    /// </remarks>
    public sealed class RgScanInput
    {
        public string FilePath = string.Empty;
        public string Album = string.Empty;
        public string AlbumArtist = string.Empty;
    }
}
