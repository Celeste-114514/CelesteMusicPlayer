using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>音频文件标签编辑窗口。</summary>
    public sealed partial class TagEditorWindow : Window
    {
        private readonly List<string> _filePaths;
        private readonly bool _isBatch;
        private readonly string _singlePath;

        public static event Action<string>? TagsSaved;

        public TagEditorWindow(string filePath)
            : this(filePath == null ? Array.Empty<string>() : new[] { filePath })
        {
        }

        public TagEditorWindow(IReadOnlyList<string> filePaths)
        {
            _filePaths = (filePaths ?? Array.Empty<string>()).ToList();
            _isBatch = _filePaths.Count > 1;
            _singlePath = _filePaths.Count > 0 ? _filePaths[0] : string.Empty;
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = _isBatch ? "批量编辑标签" : "编辑标签";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(_isBatch ? new SizeInt32(600, 720) : new SizeInt32(560, 680));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            LoadTags();
        }

        public static void Show(string filePath)
        {
            var window = new TagEditorWindow(filePath);
            window.Activate();
        }

        public static void ShowBatch(IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
            {
                return;
            }

            var window = new TagEditorWindow(filePaths);
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

        private void LoadTags()
        {
            if (_isBatch)
            {
                FilePathText.Text = "已选 " + _filePaths.Count + " 首歌曲";
                BatchHintText.Visibility = Visibility.Visible;
                FillFromFileNameButton.Visibility = Visibility.Collapsed;
                return;
            }

            FilePathText.Text = _singlePath;
            TagEditModel model = TagEditorService.ReadTag(_singlePath);
            TitleBox.Text = model.Title;
            ArtistBox.Text = model.Artist;
            AlbumBox.Text = model.Album;
            AlbumArtistBox.Text = model.AlbumArtist;
            YearBox.Text = model.Year > 0 ? model.Year.ToString() : string.Empty;
            TrackBox.Text = model.Track > 0 ? model.Track.ToString() : string.Empty;
            GenreBox.Text = model.Genre;
            CommentBox.Text = model.Comment;
            LyricsBox.Text = model.Lyrics;
        }

        private TagEditModel BuildModelFromUi()
        {
            uint.TryParse(YearBox.Text.Trim(), out uint year);
            uint.TryParse(TrackBox.Text.Trim(), out uint track);

            return new TagEditModel
            {
                Title = TitleBox.Text.Trim(),
                Artist = ArtistBox.Text.Trim(),
                Album = AlbumBox.Text.Trim(),
                AlbumArtist = AlbumArtistBox.Text.Trim(),
                Year = year,
                Track = track,
                Genre = GenreBox.Text.Trim(),
                Comment = CommentBox.Text.Trim(),
                Lyrics = LyricsBox.Text.Trim()
            };
        }

        private void FillFromFileNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatch)
            {
                return;
            }

            TagEditModel fromName = TagEditorService.TryParseTagsFromFileName(Path.GetFileName(_singlePath));
            if (!string.IsNullOrWhiteSpace(fromName.Title))
            {
                TitleBox.Text = fromName.Title;
            }

            if (!string.IsNullOrWhiteSpace(fromName.Artist))
            {
                ArtistBox.Text = fromName.Artist;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatch)
            {
                SaveBatch();
                return;
            }

            TagEditModel model = BuildModelFromUi();
            TagEditorService.SaveTag(_singlePath, model);
            try
            {
                TagsSaved?.Invoke(_singlePath);
            }
            catch
            {
            }

            Close();
        }

        /// <summary>批量保存：只把填写的字段应用到所有选中歌曲(留空不修改)。</summary>
        private void SaveBatch()
        {
            TagEditModel model = BuildModelFromUi();
            int ok = 0;
            foreach (string path in _filePaths)
            {
                try
                {
                    TagEditModel merged = TagEditorService.ReadTag(path);
                    if (!string.IsNullOrWhiteSpace(model.Title))
                    {
                        merged.Title = model.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Artist))
                    {
                        merged.Artist = model.Artist;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Album))
                    {
                        merged.Album = model.Album;
                    }

                    if (!string.IsNullOrWhiteSpace(model.AlbumArtist))
                    {
                        merged.AlbumArtist = model.AlbumArtist;
                    }

                    if (model.Year > 0)
                    {
                        merged.Year = model.Year;
                    }

                    if (model.Track > 0)
                    {
                        merged.Track = model.Track;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Genre))
                    {
                        merged.Genre = model.Genre;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Comment))
                    {
                        merged.Comment = model.Comment;
                    }

                    if (!string.IsNullOrWhiteSpace(model.Lyrics))
                    {
                        merged.Lyrics = model.Lyrics;
                    }

                    TagEditorService.SaveTag(path, merged);
                    ok++;
                    try
                    {
                        TagsSaved?.Invoke(path);
                    }
                    catch
                    {
                    }
                }
                catch
                {
                }
            }

            if (ok == _filePaths.Count)
            {
                FilePathText.Text = "已成功更新 " + ok + " 首歌曲";
            }
            else
            {
                FilePathText.Text = "更新 " + ok + "/" + _filePaths.Count + " 首(部分失败)";
            }

            SaveButton.IsEnabled = false;
            CancelButton.Content = "关闭";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
