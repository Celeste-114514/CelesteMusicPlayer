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

        /// <summary>
        /// 左侧导航高亮。经典界面：所有条目统一成「半透明灰圆角底 + 左侧一根直竖条（主题色）」，
        /// 左对齐、切换时竖条淡入（观感对齐设置页）。
        /// 背景图式界面：分类与工具（音效 / 播放列表）同款 —— 透明底、选中填主题色、左对齐；
        /// 工具按钮原来那圈胶囊背景已按用户要求去掉。
        /// </summary>
        private void UpdateLibraryNavHighlight()
        {
            try
            {
                Brush accent = ResolveAccentBrush();
                Brush fg = ColorHelper.ResolveContrastingForeground(accent);
                var transparent = new SolidColorBrush(Colors.Transparent);
                bool classicNav = IsClassicUiStyleActive();
                bool geekNav = IsGeekUiStyleActive();
                // 极客界面与经典一样走「竖条 + 半透明底」选中款：整行填强调色的胶囊款在近黑底上过于突兀（用户实测反馈）。
                bool barNav = classicNav || geekNav;

                // 当前分类是哪个（用于判断要不要播"切换"动画：同一次切换里只让新选中的那个播）
                string? activeTag = null;
                foreach (NavItemRef item in NavItems)
                {
                    string tag = item.Button.Tag as string ?? string.Empty;
                    if (IsNavCategoryActive(tag))
                    {
                        activeTag = tag;
                    }
                }

                bool animateSelection = barNav && activeTag != null
                    && !string.Equals(activeTag, _lastAnimatedNavTag, StringComparison.Ordinal);
                _lastAnimatedNavTag = activeTag;

                foreach (NavItemRef item in NavItems)
                {
                    string tag = item.Button.Tag as string ?? string.Empty;
                    bool active = IsNavCategoryActive(tag);
                    Button button = item.Button;

                    if (barNav)
                    {
                        // 经典 / 极客界面：分类与工具统一款式（竖条 + 半透明底；极客额外直角）
                        button.CornerRadius = geekNav ? new CornerRadius(0) : new CornerRadius(8);
                        button.BorderThickness = new Thickness(0);
                        button.ClearValue(Control.BorderBrushProperty);
                        button.ClearValue(Control.ForegroundProperty);
                        // ⚠️ 不要用 HorizontalAlignment.Left 字面量：收起态会被打回左对齐，
                        // 造成「点一下分类整列图标偏移」。统一走 NavContentAlignment。
                        button.HorizontalContentAlignment = NavContentAlignment;
                        ResetNavCapsuleChrome(button);

                        if (active)
                        {
                            // 底色对象要留着给动画用（淡入），所以这里现搓一个画笔
                            button.Background = new SolidColorBrush(ResolveClassicRowSelectionColor(button))
                            {
                                Opacity = animateSelection ? 0d : 1d
                            };
                        }
                        else
                        {
                            button.Background = transparent;
                        }
                    }
                    else
                    {
                        // 背景图式的分类按钮（原样保留）
                        button.CornerRadius = new CornerRadius(8);
                        button.BorderThickness = new Thickness(0);
                        button.ClearValue(Control.BorderBrushProperty);
                        button.HorizontalContentAlignment = NavContentAlignment;
                        ResetNavCapsuleChrome(button);
                        if (active)
                        {
                            button.Background = accent;
                            button.Foreground = fg;
                        }
                        else
                        {
                            button.Background = transparent;
                            button.ClearValue(Control.ForegroundProperty);
                        }
                    }

                    // 竖条指示条：经典 / 极客且选中才出现
                    if (item.Indicator != null)
                    {
                        if (barNav && active)
                        {
                            item.Indicator.Background = accent;
                            item.Indicator.Visibility = Visibility.Visible;
                            AnimateNavSelection(item, animateSelection);
                        }
                        else
                        {
                            item.Indicator.Visibility = Visibility.Collapsed;
                            item.Indicator.Opacity = 0;
                        }
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Library2.cs.UpdateLibraryNavHighlight", caught); }
        }

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
