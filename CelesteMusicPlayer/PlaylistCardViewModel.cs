using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace CelesteMusicPlayer
{
    /// <summary>命中单详情页歌曲行（序号/标题/艺术家/路径）。</summary>
    public sealed class PlaylistDetailRow
    {
        public int Index { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = "未知艺术家";
        public string FilePath { get; set; } = string.Empty;
    }

    /// <summary>播放列表墙卡片数据（Name / 歌曲数 / 封面），供 XAML x:Bind（需 INotifyPropertyChanged）。</summary>
    public sealed class PlaylistCardViewModel : INotifyPropertyChanged
    {
        private ImageSource? _cover;

        public string Name { get; set; } = string.Empty;
        public string SongCountText { get; set; } = "0 首";

        public event PropertyChangedEventHandler? PropertyChanged;

        public ImageSource? Cover
        {
            get => _cover;
            set
            {
                if (ReferenceEquals(_cover, value)) return;
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            }
        }
    }
}
