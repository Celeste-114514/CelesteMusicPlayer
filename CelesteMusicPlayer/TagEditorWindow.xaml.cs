using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>音频文件标签编辑窗口（800×600 固定；左侧封面+在线搜索，右侧双选项卡）。</summary>
    public sealed partial class TagEditorWindow : Window
    {
        private readonly List<string> _filePaths;
        private readonly bool _isBatch;
        private readonly string _singlePath;
        private byte[]? _coverBytes;

        public static event Action<string>? TagsSaved;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hWnd);

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
            // 800×600 DIP → 物理像素换算（兼容高 DPI 缩放屏）
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

            double scale = dpi / 96.0;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(900.0 * scale),
                (int)Math.Round(850.0 * scale)));
            if (AppWindow.Presenter is OverlappedPresenter ov)
            {
                ov.IsResizable = false; // 固定大小
                ov.IsMaximizable = false; // 固定大小：不响应 Windows 拖拽到边缘自动最大化
            }

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            // 默认选中“当前音频标签”（用代码设 IsChecked，避免 XAML 解析期控件未就绪触发 NRE）
            TabCurrentRadio.IsChecked = true;
            TabOnlineRadio.IsChecked = false;
            LoadTags();
            LoadCover();
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
                LeftColumnScroll.Visibility = Visibility.Collapsed;
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
            CurrentTitleText.Text = model.Title;
            OnlineTitleTitle.Text = string.Empty;

            OnlineSourceCombo.Items.Clear();
            OnlineSourceCombo.Items.Add("网易云音乐");
            OnlineSourceCombo.Items.Add("QQ音乐");
            OnlineSourceCombo.Items.Add("Apple Music");
            OnlineSourceCombo.SelectedIndex = 0;
        }

        private void LoadCover()
        {
            if (_isBatch || string.IsNullOrWhiteSpace(_singlePath))
            {
                return;
            }

            _coverBytes = TagEditorService.ReadCoverBytes(_singlePath);
            ApplyCoverToImage(_coverBytes);
            if (_coverBytes == null || _coverBytes.Length == 0)
            {
                NoCoverPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void ApplyCoverToImage(byte[]? bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0)
                {
                    CurrentCoverImage.Source = null;
                    CurrentSmallCover.Source = null;
                    NoCoverPlaceholder.Visibility = Visibility.Visible;
                    return;
                }

                NoCoverPlaceholder.Visibility = Visibility.Collapsed;
                BitmapImage bmp = new();
                using (var ms = new MemoryStream(bytes))
                using (var stream = ms.AsRandomAccessStream())
                {
                    bmp.SetSource(stream);
                }

                CurrentCoverImage.Source = bmp;
                CurrentSmallCover.Source = bmp;
                _coverBytes = bytes;
            }
            catch
            {
            }
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
            if (_coverBytes != null && _coverBytes.Length > 0)
            {
                TagEditorService.SetCoverBytes(_singlePath, _coverBytes);
            }

            try
            {
                TagsSaved?.Invoke(_singlePath);
            }
            catch
            {
            }

            Close();
        }

        private void SaveBatch()
        {
            TagEditModel model = BuildModelFromUi();
            int ok = 0;
            foreach (string path in _filePaths)
            {
                try
                {
                    TagEditModel merged = TagEditorService.ReadTag(path);
                    if (!string.IsNullOrWhiteSpace(model.Title)) merged.Title = model.Title;
                    if (!string.IsNullOrWhiteSpace(model.Artist)) merged.Artist = model.Artist;
                    if (!string.IsNullOrWhiteSpace(model.Album)) merged.Album = model.Album;
                    if (!string.IsNullOrWhiteSpace(model.AlbumArtist)) merged.AlbumArtist = model.AlbumArtist;
                    if (model.Year > 0) merged.Year = model.Year;
                    if (model.Track > 0) merged.Track = model.Track;
                    if (!string.IsNullOrWhiteSpace(model.Genre)) merged.Genre = model.Genre;
                    if (!string.IsNullOrWhiteSpace(model.Comment)) merged.Comment = model.Comment;
                    if (!string.IsNullOrWhiteSpace(model.Lyrics)) merged.Lyrics = model.Lyrics;
                    TagEditorService.SaveTag(path, merged);
                    ok++;
                    try { TagsSaved?.Invoke(path); } catch { }
                }
                catch
                {
                }
            }

            FilePathText.Text = ok == _filePaths.Count
                ? "已成功更新 " + ok + " 首歌曲"
                : "更新 " + ok + "/" + _filePaths.Count + " 首(部分失败)";
            SaveButton.IsEnabled = false;
            CancelButton.Content = "关闭";
        }

        // ============================= 封面编辑 =============================
        private async void AddCoverButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_singlePath))
            {
                return;
            }

            var picker = new FileOpenPicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                byte[] bytes = await System.IO.File.ReadAllBytesAsync(file.Path);
                ApplyCoverToImage(bytes);
                CoverStatusText.Text = "封面已就绪（保存时写入标签）";
            }
            catch
            {
                CoverStatusText.Text = "读取图片失败";
            }
        }

        private void ClearCoverButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_singlePath))
            {
                return;
            }

            _coverBytes = Array.Empty<byte>();
            ApplyCoverToImage(null);
            TagEditorService.ClearCover(_singlePath);
            CoverStatusText.Text = "封面已清除";
        }

        private async void ExtractCoverButton_Click(object sender, RoutedEventArgs e)
        {
            byte[]? bytes = TagEditorService.ReadCoverBytes(_singlePath);
            if (bytes == null || bytes.Length == 0)
            {
                CoverStatusText.Text = "当前无封面可提取";
                return;
            }

            var picker = new FileSavePicker();
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("图片", new List<string> { ".jpg" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_singlePath) + "-cover";
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            bool ok = TagEditorService.ExtractCover(_singlePath, file.Path);
            CoverStatusText.Text = ok ? "封面已提取到 " + file.Name : "提取失败";
        }

        // ============================= 在线搜索 =============================
        private void OnlineSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isItunes = OnlineSourceCombo.SelectedIndex == 3;
            OnlineStatusText.Text = isItunes
                ? "Apple Music 歌词需登录个人账号加载；未登录可保存后用播放器「下载歌词」从其他平台获取。"
                : string.Empty;
        }

        private async void OnlineSearchButton_Click(object sender, RoutedEventArgs e)
        {
            string q = OnlineQueryBox.Text?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                OnlineStatusText.Text = "请输入关键词";
                return;
            }

            string source = OnlineSourceCombo.SelectedIndex switch
            {
                1 => "QQ",
                2 => "iTunes",
                _ => "NetEase"
            };
            OnlineStatusText.Text = "搜索中…";
            try
            {
                var list = await OnlineMusicApi.SearchSongsAsync(source, q, string.Empty);
                var items = list.Select(s => new TagEditorSearchItem(s)).ToList();
                OnlineResultList.ItemsSource = items;
                OnlineStatusText.Text = items.Count == 0 ? "无结果" : items.Count + " 条结果（点击选择）";
            }
            catch
            {
                OnlineStatusText.Text = "搜索失败";
            }
        }

        private void SearchThisSongButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatch || string.IsNullOrWhiteSpace(_singlePath))
            {
                return;
            }

            TagEditModel m = TagEditorService.ReadTag(_singlePath);
            if (string.IsNullOrWhiteSpace(m.Title))
            {
                OnlineStatusText.Text = "当前歌曲无标题，无法自动搜索";
                return;
            }

            OnlineQueryBox.Text = m.Title;
            OnlineSearchButton_Click(sender, e);
        }

        private async void OnlineResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OnlineResultList.SelectedItem is not TagEditorSearchItem it)
            {
                return;
            }

            // 切到右侧“在线音频标签”选项卡
            TabOnlineRadio.IsEnabled = true;
            TabCurrentRadio.IsChecked = false;
            TabOnlineRadio.IsChecked = true;
            FillOnlineTab(it);

            // 在线小封面
            if (!string.IsNullOrWhiteSpace(it.CoverUrl))
            {
                try
                {
                    using (var client = new System.Net.Http.HttpClient())
                    using (var response = await client.GetAsync(it.CoverUrl))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                            var bmp = new BitmapImage();
                            using (var ms = new MemoryStream(bytes))
                            using (var stream = ms.AsRandomAccessStream())
                            {
                                bmp.SetSource(stream);
                            }

                            OnlineSmallCover.Source = bmp;
                        }
                    }
                }
                catch
                {
                }
            }
            else
            {
                OnlineSmallCover.Source = null;
            }
        }

        private void FillOnlineTab(TagEditorSearchItem it)
        {
            OnlineTitleBox.Text = it.Name;
            OnlineArtistBox.Text = it.Artist;
            OnlineAlbumBox.Text = it.Album;
            OnlineYearBox.Text = string.Empty;
            OnlineGenreBox.Text = string.Empty;
            OnlineTitleTitle.Text = it.Name;
            OnlineDetailText.Text = it.Detail + "  ·  " + it.SourceLabel;
            OnlineApplyStatus.Text = string.Empty;
        }

        private void TagTab_Checked(object sender, RoutedEventArgs e)
        {
            if (TabCurrentRadio == null || CurrentTabPanel == null || OnlineTabPanel == null)
            {
                return;
            }

            bool current = TabCurrentRadio.IsChecked == true;
            CurrentTabPanel.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            OnlineTabPanel.Visibility = current ? Visibility.Collapsed : Visibility.Visible;
        }

        // ============================= 一键应用到当前歌曲 =============================
        private async void ApplyOnlineButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_singlePath))
            {
                return;
            }

            if (OnlineResultList.SelectedItem is not TagEditorSearchItem sel)
            {
                OnlineApplyStatus.Text = "请先在左侧选择一条在线结果";
                return;
            }

            var model = new TagEditModel
            {
                Title = OnlineTitleBox.Text.Trim(),
                Artist = OnlineArtistBox.Text.Trim(),
                Album = OnlineAlbumBox.Text.Trim(),
                AlbumArtist = OnlineArtistBox.Text.Trim(),
                Comment = CommentBox.Text.Trim(),
                Lyrics = LyricsBox.Text.Trim()
            };
            uint.TryParse(OnlineYearBox.Text.Trim(), out uint year);
            model.Year = year;
            TagEditorService.SaveTag(_singlePath, model);

            bool coverOk = false;
            if (!string.IsNullOrWhiteSpace(sel.CoverUrl))
            {
                coverOk = await OnlineMusicApi.EmbedCoverUrlAsync(_singlePath, sel.CoverUrl);
            }

            try { TagsSaved?.Invoke(_singlePath); } catch { }

            // 同步左侧当前标签与封面
            TagEditModel fresh = TagEditorService.ReadTag(_singlePath);
            TitleBox.Text = fresh.Title;
            ArtistBox.Text = fresh.Artist;
            AlbumBox.Text = fresh.Album;
            AlbumArtistBox.Text = fresh.AlbumArtist;
            if (coverOk)
            {
                _coverBytes = TagEditorService.ReadCoverBytes(_singlePath);
                ApplyCoverToImage(_coverBytes);
            }

            OnlineApplyStatus.Text = "已应用：标签" + (coverOk ? " + 封面" : "（封面未嵌入）") + " 已更新到当前歌曲";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>标签编辑器在线搜索结果包装（含封面，供 x:Bind）。</summary>
    public sealed class TagEditorSearchItem
    {
        public TagEditorSearchItem(OnlineSongResult raw)
        {
            Raw = raw;
            string url = (raw?.CoverUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Cover = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
                }
                catch
                {
                }
            }
        }

        public OnlineSongResult Raw { get; }

        public string Name => Raw?.Name ?? string.Empty;
        public string Artist => Raw?.Artist ?? string.Empty;
        public string Album => Raw?.Album ?? string.Empty;
        public string CoverUrl => Raw?.CoverUrl ?? string.Empty;

        public string SourceLabel => (Raw?.Source) switch
        {
            "QQ" => "QQ音乐",
            "iTunes" => "Apple Music",
            _ => "网易云"
        };

        public string Detail
        {
            get
            {
                string a = Artist, b = Album;
                if (string.IsNullOrWhiteSpace(a)) return b;
                if (string.IsNullOrWhiteSpace(b)) return a;
                return a + " · " + b;
            }
        }

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Cover { get; }
    }
}
