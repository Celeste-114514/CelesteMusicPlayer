using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.Linq;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：曲库详情页收尾：关闭专辑/艺术家详情、排序按钮状态、左侧导航高亮。</summary>
public sealed partial class MainWindow
{

        // W13 第三十四刀：详情页收尾 / 导航高亮

        private void CloseArtistDetailUi()
        {
            ExitMultiSelectMode();
            _openedArtist = null;
            _artistDetailUsesAlbumArtist = false;
            _artistTracks.Clear();
            _artistAlbums.Clear();
            _albumOpenedFromArtist = false;
        }

        private void CloseAlbumDetailUi()
        {
            _openedAlbum = null;
            _albumOpenedFromArtist = false;
            _albumTracks.Clear();
            ApplyCoverFrame(AlbumDetailCoverBorder, AlbumDetailCoverImage);
        }

        private void UpdateAlbumSortButtonsUi()
        {
            // 关键：下拉按钮曾在切到歌曲库 / 播放列表时被单独隐藏（那时整个 AlbumSortPanel 已折叠），
            // 但切回专辑分类时没人把它恢复，结果面板只有升序/降序一个按钮、排序字段选不了。
            // 这里统一按面板状态复位，避免按钮的可见性被别处改坏后残留。
            if (AlbumSortButton != null)
            {
            }

            if (AlbumSortOrderButton != null)
            {
            }
        }

        private static string GetAlbumSortFieldName(AlbumSortMode mode) => mode switch
        {
            AlbumSortMode.Artist => "按艺术家",
            AlbumSortMode.Year => "按发行年份",
            AlbumSortMode.Added => "按添加时间",
            AlbumSortMode.Random => "随机",
            AlbumSortMode.TrackCount => "按专辑曲目数",
            AlbumSortMode.TotalDuration => "按专辑总时长",
            _ => "按专辑名称"
        };

        /// <summary>某个 tag 是不是当前分类（流派/年份各有"分类页"和"分类下的歌曲列表"两种取值）。</summary>
        /// <summary>
        /// 清掉历史版本给"胶囊按钮"设过的固定高度。
        /// 音效 / 播放列表这 2 个按钮早先是胶囊样式（Height=40、CornerRadius=20），
        /// 现在与分类同款；不清干净的话，切过一次界面风格之后旧值会残留，看着就跟别的不一样高。
        /// </summary>
        private static void ResetNavCapsuleChrome(Button button)
        {
            if (!double.IsNaN(button.Height) || !double.IsNaN(button.MinHeight))
            {
                button.ClearValue(FrameworkElement.HeightProperty);
                button.ClearValue(FrameworkElement.MinHeightProperty);
            }
        }

        private bool IsNavCategoryActive(string tag)
            => !string.IsNullOrEmpty(tag)
               && (string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                   || (tag == "Genres" && _currentCategory is "Genres" or "GenreSongs")
                   || (tag == "Years" && _currentCategory is "Years" or "YearSongs"));
    }
