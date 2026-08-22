﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Color = Windows.UI.Color;
using WinRT.Interop;

namespace CelesteMusicPlayer
{
    /// <summary>搜索结果列表项包装（供 x:Bind 使用）。</summary>
    public sealed class OnlineSearchItem
    {
        public OnlineSongResult Song { get; }

        public string Display { get; }

        public string Subtitle { get; }

        public string Platform { get; }

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Cover { get; }

        public OnlineSearchItem(OnlineSongResult song)
        {
            Song = song;
            Display = string.IsNullOrWhiteSpace(song.Name) ? "未知歌曲" : song.Name;
            Subtitle = string.IsNullOrWhiteSpace(song.Album)
                ? song.Artist
                : string.IsNullOrWhiteSpace(song.Artist) ? song.Album : song.Artist + " · " + song.Album;
            Platform = song.Source switch
            {
                "QQ" => "QQ音乐",
                "iTunes" => "Apple Music",
                _ => "网易云"
            };
            if (!string.IsNullOrWhiteSpace(song.CoverUrl))
            {
                try
                {
                    Cover = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(song.CoverUrl));
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// 在线搜索窗口（类似洛雪音乐）：按平台搜索歌曲，可预览歌词 / 封面，
    /// 可把歌词、封面下载到本地，可通过浏览器在平台网页播放。
    /// 说明：平台匿名试听接口不可用（需登录态），故试听以「在网页播放」替代。
    /// </summary>
    public sealed partial class OnlineSearchWindow : Window
    {
        private static OnlineSearchWindow? _instance;
        private readonly List<OnlineSongResult> _hits = new();
        private OnlineSongResult? _selected;
        private string _source = "NetEase";
        private CancellationTokenSource? _cts;
        private static readonly SemaphoreSlim SearchGate = new(1, 1);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hWnd);

        public OnlineSearchWindow()
        {
            InitializeComponent();
            Title = "在线搜索";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            // 与标签编辑器一致：DIP → 物理像素换算（视觉 920×870）固定窗口
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;
            AppWindow.Resize(new SizeInt32((int)Math.Round(920.0 * scale), (int)Math.Round(870.0 * scale)));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter ov)
            {
                ov.IsResizable = false;
                ov.IsMaximizable = false;
            }

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            SourceCombo.Items.Add("网易云音乐");
            SourceCombo.Items.Add("QQ音乐");
            SourceCombo.Items.Add("Apple Music");
            SourceCombo.SelectedIndex = AppSettingsStore.Load().OnlineSearchDefaultSource switch
            {
                "QQ" => 1,
                "iTunes" => 2,
                _ => 0
            };

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

        public static void ShowOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }

            _instance = new OnlineSearchWindow();
            _instance.Activate();
        }

        /// <summary>打开（或激活）在线搜索窗口并按关键词立即搜索（供歌曲右键等入口调用）。</summary>
        public static void ShowOrActivate(string searchQuery)
        {
            if (_instance != null)
            {
                _instance.Activate();
                _instance.StartSearch(searchQuery);
                return;
            }

            _instance = new OnlineSearchWindow();
            _instance.StartSearch(searchQuery);
            _instance.Activate();
        }

        /// <summary>设置搜索框关键词并立即搜索。</summary>
        public void StartSearch(string query)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                SearchBox.Text = query.Trim();
            }

            _ = SearchAsync();
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

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string previous = _source;
            _source = SourceCombo.SelectedIndex switch
            {
                1 => "QQ",
                2 => "iTunes",
                _ => "NetEase"
            };

            // 切换平台时自动用当前关键词重新搜索，无需用户再点一次搜索
            if (!string.Equals(previous, _source, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = SearchAsync();
            }
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
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
                var results = await OnlineMusicApi.SearchSongsAsync(_source, query, string.Empty, _cts.Token);
                _hits.AddRange(results);
                ResultList.ItemsSource = _hits.Select(h => new OnlineSearchItem(h)).ToList();
                StatusText.Text = _hits.Count == 0
                    ? "未找到结果"
                    : $"找到 {_hits.Count} 条结果（{(_source == "QQ" ? "QQ音乐" : "网易云音乐")}）";
                if (_hits.Count == 0)
                {
                    ClearDetail();
                }
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

        private async void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultList.SelectedItem is OnlineSearchItem item)
            {
                _selected = item.Song;
                await ShowDetailAsync(item.Song);
            }
        }

        private async Task ShowDetailAsync(OnlineSongResult song)
        {
            DetailTitle.Text = song.Name;
            DetailArtist.Text = string.IsNullOrWhiteSpace(song.Artist) ? "未知艺术家" : song.Artist;
            DetailAlbum.Text = string.IsNullOrWhiteSpace(song.Album) ? string.Empty : "专辑：" + song.Album;
            LyricPreview.Text = "正在加载…";

            // 封面预览
            try
            {
                string? coverUrl = await OnlineMusicApi.GetCoverUrlAsync(song);
                if (!string.IsNullOrWhiteSpace(coverUrl))
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "celeste-search-" + Guid.NewGuid().ToString("N") + ".jpg");
                    string? saved = await OnlineMusicApi.DownloadCoverAsync(coverUrl, tmp);
                    if (saved != null && File.Exists(saved))
                    {
                        using FileStream fs = File.OpenRead(saved);
                        var bmp = new BitmapImage();
                        await bmp.SetSourceAsync(fs.AsRandomAccessStream());
                        CoverPreview.Source = bmp;
                    }
                }
            }
            catch
            {
            }

            // 歌词预览
            if (string.Equals(song.Source, "iTunes", StringComparison.OrdinalIgnoreCase))
            {
                // iTunes Search API 不返回歌词字段
                LyricPreview.Text = "Apple Music 歌词需登录个人账号加载；未登录可用播放器「歌曲信息→下载歌词」从其他平台获取。";
                return;
            }

            try
            {
                var settings = AppSettingsStore.Load();
                string lyric = await OnlineMusicApi.GetLyricAsync(song.Source, song, settings.ShowLyricTranslate);
                LyricPreview.Text = string.IsNullOrWhiteSpace(lyric) ? "（未获取到歌词）" : lyric;
            }
            catch
            {
                LyricPreview.Text = "（获取歌词失败）";
            }
        }

        private async void DownloadAudio_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            if (string.Equals(_selected.Source, "iTunes", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Apple Music 不提供音频下载，仅可下载歌词 / 封面。";
                return;
            }

            if (StreamingServiceClient.ResolveBase() == null)
            {
                StatusText.Text = "未配置流媒体插件服务（设置 → 流媒体），无法下载音频。";
                return;
            }

            DownloadAudioButton.IsEnabled = false;
            try
            {
                string? savePath = await PickSaveFileAsync(".mp3", "MP3 音频", PickerLocationId.Downloads);
                if (savePath == null)
                {
                    return;
                }

                var dl = await StreamingServiceClient.GetDownloadAsync(_selected.Source, _selected.SongId, "standard");
                if (dl == null || !dl.Ok || string.IsNullOrWhiteSpace(dl.Url))
                {
                    StatusText.Text = "获取下载链接失败：" + (dl?.Error ?? "服务未返回");
                    return;
                }

                StatusText.Text = "正在下载…";
                using var hc = new System.Net.Http.HttpClient();
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, dl.Url);
                if (dl.Headers != null)
                {
                    foreach (var kv in dl.Headers)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        {
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                    }
                }

                using var resp = await hc.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                await using (var fs = System.IO.File.Create(savePath))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                StatusText.Text = "已下载：" + savePath;
            }
            catch (Exception ex)
            {
                StatusText.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                DownloadAudioButton.IsEnabled = true;
            }
        }

        private void ClearDetail()
        {
            _selected = null;
            DetailTitle.Text = string.Empty;
            DetailArtist.Text = string.Empty;
            DetailAlbum.Text = string.Empty;
            LyricPreview.Text = string.Empty;
            CoverPreview.Source = null;
        }

        private void WebPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            string url = _selected.Source switch
            {
                "QQ" => $"https://y.qq.com/n/ryqq/songDetail/{_selected.SongId}",
                _ => $"https://music.163.com/#/song?id={_selected.SongId}"
            };
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                StatusText.Text = "无法打开浏览器";
            }
        }

        private async void DownloadLyric_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            try
            {
                DownloadLyricButton.IsEnabled = false;
                var settings = AppSettingsStore.Load();
                string? folder = settings.LyricFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    string? savePath = await PickSaveFileAsync(".lrc", "LRC 歌词", PickerLocationId.MusicLibrary);
                    if (savePath == null)
                    {
                        return;
                    }

                    var hits = await OnlineMusicApi.SearchSongsAsync(_selected.Source, _selected.Name, _selected.Artist);
                    if (hits.Count == 0)
                    {
                        StatusText.Text = "未找到对应歌曲";
                        return;
                    }

                    string lyric = await OnlineMusicApi.GetLyricAsync(_selected.Source, hits[0], settings.ShowLyricTranslate);
                    if (string.IsNullOrWhiteSpace(lyric))
                    {
                        StatusText.Text = "未获取到歌词";
                        return;
                    }

                    await File.WriteAllTextAsync(savePath, lyric);
                    StatusText.Text = "歌词已保存：" + savePath;
                }
                else
                {
                    string? path = await OnlineMusicApi.DownloadLyricToFolderAsync(
                        _selected.Source, _selected.Name, _selected.Artist, folder, settings.ShowLyricTranslate);
                    StatusText.Text = path != null ? "歌词已保存：" + path : "下载歌词失败";
                }
            }
            catch
            {
                StatusText.Text = "下载歌词失败";
            }
            finally
            {
                DownloadLyricButton.IsEnabled = true;
            }
        }

        private async void DownloadCover_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            try
            {
                DownloadCoverButton.IsEnabled = false;
                var settings = AppSettingsStore.Load();
                string? folder = settings.CoverFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    string? savePath = await PickSaveFileAsync(".jpg", "封面图片", PickerLocationId.PicturesLibrary);
                    if (savePath == null)
                    {
                        return;
                    }

                    string? coverUrl = await OnlineMusicApi.GetCoverUrlAsync(_selected);
                    if (string.IsNullOrWhiteSpace(coverUrl))
                    {
                        StatusText.Text = "未获取到封面";
                        return;
                    }

                    string? saved = await OnlineMusicApi.DownloadCoverAsync(coverUrl, savePath);
                    StatusText.Text = saved != null ? "封面已保存：" + saved : "下载封面失败";
                }
                else
                {
                    string? saved = await OnlineMusicApi.DownloadCoverToFolderAsync(
                        _selected.Source, _selected.Name, _selected.Artist, folder);
                    StatusText.Text = saved != null ? "封面已保存：" + saved : "下载封面失败";
                }
            }
            catch
            {
                StatusText.Text = "下载封面失败";
            }
            finally
            {
                DownloadCoverButton.IsEnabled = true;
            }
        }

        private async Task<string?> PickSaveFileAsync(string extension, string typeName, PickerLocationId location)
        {
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = location;
            picker.FileTypeChoices.Add(typeName, new List<string> { extension });
            picker.SuggestedFileName = OnlineMusicApi.SanitizeFileName(
                (_selected?.Name ?? "歌曲") + " - " + (_selected?.Artist ?? "未知")) + extension;
            StorageFile? file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
    }
}
