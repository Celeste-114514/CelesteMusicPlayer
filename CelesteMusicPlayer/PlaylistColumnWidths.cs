using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放列表各列像素宽度；拖动分割线时更新，列表行通过 Binding 同步。
    /// </summary>
    public sealed class PlaylistColumnWidths : INotifyPropertyChanged
    {
        public static PlaylistColumnWidths Instance { get; } = new();

        private double _title = 140;
        private double _artist = 110;
        private double _album = 110;
        private double _year = 52;
        private double _duration = 60;

        public double Title
        {
            get => _title;
            set { if (Set(ref _title, value)) OnPropertyChanged(nameof(TitleLength)); }
        }

        public double Artist
        {
            get => _artist;
            set { if (Set(ref _artist, value)) OnPropertyChanged(nameof(ArtistLength)); }
        }

        public double Album
        {
            get => _album;
            set { if (Set(ref _album, value)) OnPropertyChanged(nameof(AlbumLength)); }
        }

        public double Year
        {
            get => _year;
            set { if (Set(ref _year, value)) OnPropertyChanged(nameof(YearLength)); }
        }

        public double Duration
        {
            get => _duration;
            set { if (Set(ref _duration, value)) OnPropertyChanged(nameof(DurationLength)); }
        }

        public GridLength TitleLength => new(_title);
        public GridLength ArtistLength => new(_artist);
        public GridLength AlbumLength => new(_album);
        public GridLength YearLength => new(_year);
        public GridLength DurationLength => new(_duration);

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set(ref double field, double value, [CallerMemberName] string? name = null)
        {
            if (System.Math.Abs(field - value) < 0.5)
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
