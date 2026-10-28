using System;
using System.Collections.Generic;
namespace CelesteMusicPlayer
{
    public partial class PlaylistItem
    {
        /// <summary>行首序号文本：极客界面下补零成 003 式（等宽字下对齐成一张数据表），其它界面原样数字。</summary>
        public string IndexLabel => MainWindow.GeekUiStyleCached
            ? Index.ToString("000", System.Globalization.CultureInfo.InvariantCulture)
            : Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>歌曲面板第三行的格式胶囊：格式 / 位深·采样率 / 比特率（如 ["FLAC","16bit/44kHz","1411kbps"]）。
        /// 懒计算 + AudioInfoFormatter 按路径缓存，避免启动/建条目时同步解析。
        /// 极客界面下包一层方括号（[FLAC] [16bit/44kHz]…），配合等宽字读起来像终端元数据行。</summary>
        private IReadOnlyList<string>? _formatChips;
        private IReadOnlyList<string>? _geekFormatChips;
        public IReadOnlyList<string> FormatChips
        {
            get
            {
                _formatChips ??= string.IsNullOrWhiteSpace(FilePath)
                    ? System.Array.Empty<string>()
                    : AudioInfoFormatter.FormatChips(FilePath);

                if (!MainWindow.GeekUiStyleCached)
                {
                    return _formatChips;
                }

                if (_geekFormatChips == null)
                {
                    var wrapped = new string[_formatChips.Count];
                    for (int i = 0; i < _formatChips.Count; i++)
                    {
                        wrapped[i] = "[" + _formatChips[i] + "]";
                    }

                    _geekFormatChips = wrapped;
                }

                return _geekFormatChips;
            }
        }

        /// <summary>歌曲小封面（延迟异步加载，空则显示占位图标）。</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? CoverImage { get; set; }

    }
}
