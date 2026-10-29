using System;
using System.IO;
using System.Linq;
namespace CelesteMusicPlayer
{
    public partial class PlaylistItem
    {
        /// <summary>行序号（0 = 未编号）。virtual 是为了标签板块的 TagFileItem 能在赋值时通知界面重排。</summary>
        public virtual int Index { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = "未知艺术家";

        /// <summary>专辑艺术家（Tag AlbumArtist）；缺省时与 Artist 相同</summary>
        public string AlbumArtist { get; set; } = "未知艺术家";

        public string Album { get; set; } = "未知专辑";

        /// <summary>音轨号（Tag.Track）；0 表示未知</summary>
        public uint Track { get; set; }

        /// <summary>网络音乐（WebDAV）：这一首在服务器上的相对路径；空 = 本地文件。</summary>
        public string RemotePath { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string SourceTrackId { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public bool IsCueSegment { get; set; }
        // Persist references separately from the playback/cache path. Local ordinary files keep legacy keys.
        public string PersistenceKey => SourceTrackId.Length > 0 ? NetworkIdentity.Reference(SourceTrackId) : FilePath;

        /// <summary>是否来自网络音乐库。</summary>
        public bool IsRemote => SourceId.Length > 0 || !string.IsNullOrEmpty(RemotePath);

        /// <summary>
        /// 网络音乐右侧那列小字（文件大小 / 缓存状态）。
        /// 本地曲目靠内嵌标签拿时长，网络曲目没下载前读不到，就用这个顶上。
        /// </summary>
        public string RemoteHint { get; set; } = string.Empty;

        /// <summary>碟片号（Tag.Disc）；0 表示未知</summary>
        public uint Disc { get; set; }


        /// <summary>年份数值；0 表示未知</summary>
        public uint Year { get; set; }

        /// <summary>流派（Tag.Genre）；空视为未知流派</summary>
        public string Genre { get; set; } = "未知流派";

        /// <summary>列表上显示的年份文字（由 Year 推导，避免复制条目时漏设）</summary>
        public string YearText => Year > 0 ? Year.ToString() : "-";

        /// <summary>时长文本（由 Duration 只读推导，保证恒有值、不空白）。</summary>
        public string DurationText
        {
            get
            {
                // 网络曲目没下载前读不到时长 —— 那列直接显示大小/缓存状态，比一排 00:00 有用。
                if (IsRemote && Duration <= TimeSpan.Zero && !string.IsNullOrEmpty(RemoteHint))
                {
                    return RemoteHint;
                }

                var t = Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration;
                return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            }
        }

        public TimeSpan Duration { get; set; }

        public string FilePath { get; set; } = string.Empty;

        /// <summary>用户评分 0..5（0 = 未评分）。</summary>
        public int Rating { get; set; }

        /// <summary>仅文件名（媒体库/文件夹详情列表显示用）。
        /// 网络曲目取服务器上的名字 —— 本地缓存文件名带路径哈希前缀，直接取会露出一串乱码似的前缀。</summary>
        public string FileName =>
            System.IO.Path.GetFileName(IsRemote ? RemotePath : FilePath);

        /// <summary>CUE 分轨起始秒；0 表示整曲。</summary>
        public double StartTimeSeconds { get; set; }

        /// <summary>浅拷贝（CUE 分轨展开用：同一文件派生出多轨，除分轨字段外沿用文件级元数据）。</summary>
        public PlaylistItem Clone() => (PlaylistItem)MemberwiseClone();

        public string DisplayName => Title;

        /// <summary>
        /// 播放歌曲状态条第三行显示的短格式信息："ALAC · 24bit/44kHz · 1411kbps"（由路径实时读取，读失败为空）。
        /// </summary>
        public string FormatInfoLine =>
            string.IsNullOrWhiteSpace(FilePath) ? string.Empty : (IsCueSegment ? "CUE/PCM · " + string.Join(" · ", AudioInfoFormatter.FormatShortLine(FilePath).Split(" · ").Skip(1)) : AudioInfoFormatter.FormatShortLine(FilePath));

        /// <summary>
        /// 歌曲面板显示的标题：一律使用音频内嵌标题标签（Tag.Title），
        /// 缺失时回退文件名。不再受"播放列表显示格式"设置影响。
        /// </summary>
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title)
                ? Path.GetFileNameWithoutExtension(IsRemote ? RemotePath : FilePath)
                : Title.Trim();

        /// <summary>歌曲面板第二行："艺术家 - 专辑"（用曲目/演出艺术家，而非专辑艺术家）。</summary>
        public string ArtistAlbumText
        {
            get
            {
                string artist = string.IsNullOrWhiteSpace(Artist) || string.Equals(Artist, "未知艺术家", StringComparison.Ordinal)
                    ? "未知艺术家"
                    : Artist;
                string album = string.IsNullOrWhiteSpace(Album) || string.Equals(Album, "未知专辑", StringComparison.Ordinal)
                    ? "未知专辑"
                    : Album;
                return artist + " - " + album;
            }
        }

        /// <summary>专辑详情列表显示的音轨号（纯音轨号；碟片号由独立标题行表达）</summary>
        public string TrackText => Track > 0 ? Track.ToString() : "-";
    }

}
