using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;
using WinRT.Interop;

namespace CelesteMusicPlayer
{
    /// <summary>头像候选结果（供 x:Bind 使用）。</summary>
    public sealed class ArtistAvatarSearchItem
    {
        public OnlineSongResult Song { get; }

        public string CoverUrl { get; }

        public string Platform { get; }

        public string Name { get; }

        public BitmapImage? Cover { get; }

        public ArtistAvatarSearchItem(OnlineSongResult song)
        {
            Song = song;
            CoverUrl = song.CoverUrl ?? string.Empty;
            Name = song.Name ?? string.Empty;
            Platform = song.Source switch
            {
                "QQ" => "QQ音乐",
                "iTunes" => "Apple Music",
                "MusicBrainz" => "MusicBrainz",
                _ => "网易云"
            };
            if (!string.IsNullOrWhiteSpace(song.CoverUrl))
            {
                try
                {
                    Cover = new BitmapImage(new Uri(song.CoverUrl));
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ArtistAvatarSearchWindow.xaml.cs", caught); }
            }
        }
    }

    /// <summary>
    /// 艺术家头像在线搜索窗口：按歌手名聚合各平台（网易云 / QQ / Apple Music / MusicBrainz）
    /// 搜索结果的封面作为头像候选，供用户点选；选定后进入头像裁切编辑器确认。
    /// </summary>
    public sealed partial class ArtistAvatarSearchWindow : Window
    {
        private static ArtistAvatarSearchWindow? _instance;
        private readonly string _artistName;
        private readonly string _storeKey;
        private readonly List<OnlineSongResult> _hits = new();
        private string _source = "All";
        private CancellationTokenSource? _cts;
        private static readonly SemaphoreSlim SearchGate = new(1, 1);

        /// <summary>用户确认了头像（图片已保存到自定义头像存储）。</summary>
        public event Action<BitmapImage>? AvatarConfirmed;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hWnd);

        public ArtistAvatarSearchWindow(string artistName, string storeKey)
        {
            _artistName = artistName;
            _storeKey = storeKey;

            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "搜索网络头像";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;
            AppWindow.Resize(new SizeInt32((int)Math.Round(980.0 * scale), (int)Math.Round(720.0 * scale)));
            if (AppWindow.Presenter is OverlappedPresenter ov)
            {
                ov.IsResizable = true;
                ov.IsMaximizable = true;
            }

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            SourceCombo.Items.Add("全部平台");
            SourceCombo.Items.Add("网易云音乐");
            SourceCombo.Items.Add("QQ音乐");
            SourceCombo.Items.Add("Apple Music");
            SourceCombo.Items.Add("MusicBrainz");
            SourceCombo.SelectedIndex = 0;

            SearchBox.Text = _artistName;

            Closed += (_, _) =>
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static ArtistAvatarSearchWindow OpenForArtist(string artistName, string storeKey)
        {
            _instance?.Close();
            _instance = new ArtistAvatarSearchWindow(artistName, storeKey);
            _instance.Activate();
            return _instance;
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
                FrostedGlass.ApplyWindowTheme(this);
            }
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string previous = _source;
            _source = SourceCombo.SelectedIndex switch
            {
                1 => "NetEase",
                2 => "QQ",
                3 => "iTunes",
                4 => "MusicBrainz",
                _ => "All"
            };

            if (!string.Equals(previous, _source, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = SearchAsync();
            }
        }

        private static string DisplayPlatform(string source) => source switch
        {
            "QQ" => "QQ音乐",
            "iTunes" => "Apple Music",
            "MusicBrainz" => "MusicBrainz",
            _ => "网易云"
        };

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _ = SearchAsync();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
            => _ = SearchAsync();

        private async Task SearchAsync()
        {
            string query = SearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                StatusText.Text = "请输入搜索内容";
                return;
            }

            await SearchGate.WaitAsync();
            try
            {
                SearchButton.IsEnabled = false;
                StatusText.Text = "正在搜索…";
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _hits.Clear();
                var sources = _source == "All"
                    ? new[] { "NetEase", "QQ", "iTunes", "MusicBrainz" }
                    : new[] { _source };

                var all = new List<OnlineSongResult>();
                foreach (string src in sources)
                {
                    try
                    {
                        // 歌手搜索：直接命中歌手本人头像，不再用歌曲名搜索接口搜歌手名
                        var results = await OnlineMusicApi.SearchArtistsAsync(src, query, _cts.Token);
                        all.AddRange(results);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 单个平台失败不影响其它平台
                    }
                }

                // 去重：优先按头像 URL 去重；仅保留有头像的候选（MusicBrainz 无头像来源，天然被过滤）。
                // 同名歌手不同头像（各平台图可能不同）按头像 URL 去重，同名同图跨平台只留一条。
                _hits.AddRange(all.Where(r => !string.IsNullOrWhiteSpace(r.CoverUrl))
                    .GroupBy(r => r.CoverUrl)
                    .Select(g => g.First())
                    .ToList());

                ResultGrid.ItemsSource = _hits.Select(h => new ArtistAvatarSearchItem(h)).ToList();
                StatusText.Text = _hits.Count == 0
                    ? "未找到结果，请换个平台或关键词再试"
                    : $"找到 {_hits.Count} 个头像候选";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "搜索已取消";
            }
            catch
            {
                StatusText.Text = "搜索失败，请检查网络";
            }
            finally
            {
                SearchButton.IsEnabled = true;
                SearchGate.Release();
            }
        }

        private async void ResultGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not ArtistAvatarSearchItem item
                || string.IsNullOrWhiteSpace(item.CoverUrl))
            {
                return;
            }

            try
            {
                StatusText.Text = "正在下载所选头像…";

                string tmp = Path.Combine(Path.GetTempPath(), "celeste-avatar-" + Guid.NewGuid().ToString("N") + ".jpg");
                string? saved = await OnlineMusicApi.DownloadCoverAsync(item.CoverUrl, tmp);
                if (saved == null || !File.Exists(saved))
                {
                    StatusText.Text = "下载头像失败，请重试";
                    return;
                }

                // 打开头像裁切编辑器（圆内裁切），确认后回传
                var editor = new ArtistAvatarEditorWindow(_storeKey, saved);
                editor.AvatarConfirmed += image =>
                {
                    AvatarConfirmed?.Invoke(image);
                    Close();
                };
                editor.Activate();
            }
            catch (Exception ex)
            {
                StatusText.Text = "下载头像失败：" + ex.Message;
            }
        }
    }
}
