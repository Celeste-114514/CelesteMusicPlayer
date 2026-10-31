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
        }

        private void UpdateAlbumSortButtonsUi()
        {
            // 关键：下拉按钮曾在切到歌曲库 / 播放列表时被单独隐藏（那时整个 AlbumSortPanel 已折叠），
            // 但切回专辑分类时没人把它恢复，结果面板只有升序/降序一个按钮、排序字段选不了。
            // 这里统一按面板状态复位，避免按钮的可见性被别处改坏后残留。

        }

    }
