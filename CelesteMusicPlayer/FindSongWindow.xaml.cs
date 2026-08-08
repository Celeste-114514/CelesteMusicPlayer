using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    public sealed class FindSongHit
    {
        public string Title { get; init; } = string.Empty;

        public string Artist { get; init; } = string.Empty;

        public string Album { get; init; } = string.Empty;

        public string FilePath { get; init; } = string.Empty;
    }

    /// <summary>全库搜索并播放/加入列表。</summary>
    public sealed partial class FindSongWindow : Window
    {
        private readonly IReadOnlyList<FindSongHit> _allTracks;
        private readonly Action<string>? _onPlay;
        private readonly Action<string>? _onAddToPlaylist;
        private readonly Action<string>? _onPlayNext;
        private List<FindSongHit> _filtered = new();

        public FindSongWindow(
            IEnumerable<(string Title, string Artist, string Album, string FilePath)> allTracks,
            Action<string>? onPlay,
            Action<string>? onAddToPlaylist,
            Action<string>? onPlayNext)
        {
            _allTracks = allTracks?
                .Select(t => new FindSongHit
                {
                    Title = t.Title ?? string.Empty,
                    Artist = t.Artist ?? string.Empty,
                    Album = t.Album ?? string.Empty,
                    FilePath = t.FilePath ?? string.Empty
                })
                .ToList()
                ?? new List<FindSongHit>();
            _onPlay = onPlay;
            _onAddToPlaylist = onAddToPlaylist;
            _onPlayNext = onPlayNext;

            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "查找歌曲";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(900, 560));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            ApplyFilter(string.Empty);
        }

        public static void Show(
            IEnumerable<(string Title, string Artist, string Album, string FilePath)> allTracks,
            Action<string>? onPlay,
            Action<string>? onAddToPlaylist,
            Action<string>? onPlayNext)
        {
            var window = new FindSongWindow(allTracks, onPlay, onAddToPlaylist, onPlayNext);
            window.Activate();
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(SearchBox.Text);
        }

        private void ApplyFilter(string query)
        {
            string q = query?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                _filtered = _allTracks.ToList();
            }
            else
            {
                _filtered = _allTracks
                    .Where(t => Matches(t, q))
                    .ToList();
            }

            ResultsList.ItemsSource = _filtered;
            StatusText.Text = _filtered.Count == 0
                ? "没有匹配的歌曲"
                : $"共 {_filtered.Count} 首匹配";
        }

        private static bool Matches(FindSongHit hit, string query)
        {
            return Contains(hit.Title, query)
                || Contains(hit.Artist, query)
                || Contains(hit.Album, query)
                || Contains(hit.FilePath, query);
        }

        private static bool Contains(string value, string query)
        {
            return value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }

        private FindSongHit? GetSelectedHit()
        {
            if (ResultsList.SelectedItem is FindSongHit selected)
            {
                return selected;
            }

            return _filtered.Count > 0 ? _filtered[0] : null;
        }

        private void PlaySelected()
        {
            FindSongHit? hit = GetSelectedHit();
            if (hit == null || string.IsNullOrWhiteSpace(hit.FilePath))
            {
                return;
            }

            _onPlay?.Invoke(hit.FilePath);
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            PlaySelected();
        }

        private void ResultsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            PlaySelected();
        }

        private void ResultsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (ResultsList.SelectedItem is not FindSongHit hit || string.IsNullOrWhiteSpace(hit.FilePath))
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "播放",
                Tag = hit.FilePath
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "下一首播放",
                Tag = hit.FilePath
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "添加到播放列表",
                Tag = hit.FilePath
            });

            foreach (MenuFlyoutItem item in flyout.Items.OfType<MenuFlyoutItem>())
            {
                item.Click += ContextMenuItem_Click;
            }

            flyout.ShowAt(ResultsList, e.GetPosition(ResultsList));
        }

        private void ContextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem { Tag: string path } || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            switch (sender is MenuFlyoutItem item ? item.Text : string.Empty)
            {
                case "播放":
                    _onPlay?.Invoke(path);
                    break;
                case "下一首播放":
                    _onPlayNext?.Invoke(path);
                    break;
                case "添加到播放列表":
                    _onAddToPlaylist?.Invoke(path);
                    break;
            }
        }
    }
}
