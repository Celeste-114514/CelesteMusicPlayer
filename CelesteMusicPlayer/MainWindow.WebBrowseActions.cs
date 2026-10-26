using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webBrowseBusy;

        private static string WebPlaylistName(JsonElement msg)
        {
            string name = msg.GetProperty("name").GetString() ?? "";
            if (!NamedPlaylistStore.List().Contains(name)) throw new InvalidOperationException("播放列表已不存在，请刷新。");
            return name;
        }

        internal async Task<object> WebBrowseAsync(JsonElement msg, Window host)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            if (operation is "get" or "createPlaylist" or "favorite" or "rating") return WebBrowse(msg);
            if (_webBrowseBusy) throw new InvalidOperationException("请先完成当前操作。");
            _webBrowseBusy = true;
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host);
            try
            {
                switch (operation)
                {
                    case "setCover":
                    {
                        string name = WebPlaylistName(msg);
                        var picker = new FileOpenPicker();
                        foreach (string ext in new[] { ".jpg", ".jpeg", ".png" }) picker.FileTypeFilter.Add(ext);
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSingleFileAsync();
                        if (file == null) break;
                        byte[] bytes = await File.ReadAllBytesAsync(file.Path);
                        if (bytes.Length > 20_000_000) throw new ArgumentException("请选择小于 20MB 的图片。");
                        using (var stream = new MemoryStream(bytes)) using (var image = System.Drawing.Image.FromStream(stream)) { }
                        WebPlaylistName(msg); // The picker may have been open while the native UI changed the list.
                        PlaylistLibraryService.WriteCustomCover(name, bytes);
                        break;
                    }
                    case "resetCover": PlaylistLibraryService.ClearCustomCover(WebPlaylistName(msg)); break;
                    case "searchCover":
                    {
                        string name = WebPlaylistName(msg);
                        string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(name);
                        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("未找到封面。");
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
                        byte[] bytes = await http.GetByteArrayAsync(url);
                        if (bytes.Length > 20_000_000) throw new ArgumentException("封面图片过大。");
                        using (var stream = new MemoryStream(bytes)) using (var image = System.Drawing.Image.FromStream(stream)) { }
                        WebPlaylistName(msg);
                        PlaylistLibraryService.WriteCustomCover(name, bytes);
                        break;
                    }
                    case "playPlaylist": await LoadNamedPlaylistToQueueAndPlayAsync(WebPlaylistName(msg)); break;
                    case "queuePlaylist": AddNamedPlaylistToQueue(WebPlaylistName(msg)); break;
                    case "copyPlaylist":
                    {
                        var paths = NamedPlaylistStore.LoadSongs(WebPlaylistName(msg));
                        string? target = await WebPickPlaylistAsync(host);
                        if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
                        break;
                    }
                    case "exportPlaylist":
                    {
                        string name = WebPlaylistName(msg);
                        var paths = NamedPlaylistStore.LoadSongs(name);
                        if (paths.Count == 0) throw new InvalidOperationException("播放列表为空。");
                        var picker = new FileSavePicker { SuggestedFileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) };
                        picker.FileTypeChoices.Add("M3U8 播放列表", new List<string> { ".m3u8" });
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSaveFileAsync();
                        if (file != null) M3uPlaylistIO.WriteM3u8(file.Path, paths);
                        break;
                    }
                    case "importM3u":
                    {
                        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".m3u"); picker.FileTypeFilter.Add(".m3u8");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSingleFileAsync();
                        if (file == null) break;
                        var paths = M3uPlaylistIO.Parse(file.Path, existingOnly: false).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (paths.Length == 0) throw new InvalidOperationException("文件中没有歌曲条目。");
                        string? target = await WebPickPlaylistAsync(host);
                        if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
                        break;
                    }
                    case "openCue":
                    {
                        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".cue");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSingleFileAsync();
                        if (file != null) await PlayCueFileAsync(file.Path);
                        break;
                    }
                    default: throw new ArgumentException("未知的曲库操作。");
                }
                PlaylistLibraryService.Refresh();
                return WebBrowse(JsonSerializer.SerializeToElement(new { operation = "get" }));
            }
            finally { _webBrowseBusy = false; }
        }

        private async Task<string?> WebPickPlaylistAsync(Window host)
        {
            var choice = new ComboBox { ItemsSource = NamedPlaylistStore.List().ToArray(), PlaceholderText = "选择已有播放列表", MinWidth = 300 };
            var nameBox = new TextBox { PlaceholderText = "或输入新播放列表名称", MaxLength = 120 };
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(choice); panel.Children.Add(nameBox);
            var dialog = new ContentDialog { XamlRoot = host.Content.XamlRoot, Title = "添加到播放列表", Content = panel,
                PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            ColorHelper.ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            string name = nameBox.Text.Trim();
            if (name.Length == 0) name = choice.SelectedItem as string ?? "";
            if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("请输入有效的播放列表名称。");
            if (!NamedPlaylistStore.List().Contains(name)) NamedPlaylistStore.Create(name);
            return name;
        }

        // Context operations resolve the clicked row, never the currently playing track.
        internal async Task<object> WebTrackActionAsync(JsonElement msg, Window host)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            bool fromQueue = msg.TryGetProperty("queue", out var q) && q.GetBoolean();
            PlaylistItem[] songs;
            int[] indices = Array.Empty<int>();
            if (fromQueue)
            {
                WebQueue(JsonSerializer.SerializeToElement(new { operation = "get" }));
                if (msg.GetProperty("revision").GetInt32() != _webQueueRevision) throw new InvalidOperationException("播放队列已变化，请重新打开菜单。");
                var refs = msg.GetProperty("entries").EnumerateArray().ToArray();
                indices = refs.Select(entry => entry.GetProperty("index").GetInt32()).Distinct().ToArray();
                foreach (var entry in refs)
                {
                    int index = entry.GetProperty("index").GetInt32();
                    if (index < 0 || index >= _userPlaylist.Count || WebTrackId(_userPlaylist[index]) != entry.GetProperty("trackId").GetString())
                        throw new InvalidOperationException("播放队列已变化，请重新打开菜单。");
                }
                songs = indices.Select(i => _userPlaylist[i]).ToArray();
            }
            else
            {
                var ids = msg.GetProperty("trackIds").EnumerateArray().Select(id => id.GetString()).ToArray();
                var map = LibraryTracks.GroupBy(WebTrackId).ToDictionary(group => group.Key, group => group.First());
                foreach (var entry in _webFolderTracks) if (!map.ContainsKey(entry.Key) && File.Exists(entry.Value.FilePath)) map[entry.Key] = entry.Value;
                songs = ids.Distinct().Select(id => id != null && map.TryGetValue(id, out var song) ? song : throw new InvalidOperationException("歌曲已不在曲库中，请刷新。")).ToArray();
            }
            if (songs.Length is < 1 or > 50000) throw new ArgumentException("请选择歌曲。");
            switch (operation)
            {
                case "get": return new { favorite = songs.All(song => TrackStatsStore.Get(song.FilePath)?.IsFavorite == true),
                    canDelete = !AppSettingsStore.Load().DisableDeleteFromDisk && songs.All(song => !song.IsRemote), count = songs.Length };
                case "play": if (fromQueue) PlayUserPlaylistAt(indices[0]); else if (LibraryTracks.Contains(songs[0])) WebPlayTrack(songs[0]); else PlayPlaylistItem(songs[0]); break;
                case "next": WebInsertQueue(songs, next: true); break;
                case "addQueue": WebInsertQueue(songs, next: false); break;
                case "addPlaylist":
                {
                    string? target = await WebPickPlaylistAsync(host);
                    if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(songs.Select(song => song.FilePath)).Distinct(StringComparer.OrdinalIgnoreCase));
                    PlaylistLibraryService.Refresh(); break;
                }
                case "pin":
                case "removeQueue":
                {
                    if (!fromQueue) throw new ArgumentException("请在播放队列中操作。");
                    var playing = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count ? _userPlaylist[_userPlaylistIndex] : null;
                    foreach (int index in indices.OrderByDescending(i => i)) _userPlaylist.RemoveAt(index);
                    if (operation == "pin") for (int i = songs.Length - 1; i >= 0; i--) _userPlaylist.Insert(0, songs[i]);
                    _userPlaylistIndex = playing == null ? -1 : _userPlaylist.IndexOf(playing);
                    RenumberCollection(_userPlaylist); NotifyCurrentPlaylistWindow();
                    if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
                    break;
                }
                case "favorite": foreach (var song in songs) TrackStatsStore.SetFavorite(song.FilePath, msg.GetProperty("value").GetBoolean()); NamedPlaylistStore.SyncFavoritesPlaylist(); UpdateFavoriteButtonUi(); break;
                case "rating":
                    int rating = msg.GetProperty("value").GetInt32();
                    if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。");
                    foreach (var song in songs) TrackStatsStore.SetRating(song.FilePath, rating);
                    break;
                case "tags": OpenTagEditForFiles(songs.Select(song => song.FilePath)); Activate(); break;
                case "reveal": if (songs[0].IsRemote) throw new InvalidOperationException("远程歌曲没有本地文件夹。"); OpenFileLocationInExplorer(songs[0].FilePath); break;
                case "copy": var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, songs.Select(song => song.Title + " - " + song.Artist))); Clipboard.SetContent(data); break;
                case "delete":
                    if (AppSettingsStore.Load().DisableDeleteFromDisk || songs.Any(song => song.IsRemote)) throw new InvalidOperationException("当前歌曲不允许从磁盘删除。");
                    foreach (var song in songs.DistinctBy(song => song.FilePath, StringComparer.OrdinalIgnoreCase)) await DeleteSongFromDiskAsync(song, host.Content.XamlRoot);
                    break;
                default: throw new ArgumentException("未知的歌曲操作。");
            }
            return new { message = "操作已完成" };
        }

        private void WebInsertQueue(PlaylistItem[] songs, bool next)
        {
            // CUE tracks share a file. Moving one must not remove its sibling tracks.
            var playing = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count ? _userPlaylist[_userPlaylistIndex] : null;
            var kept = WebBrowseRules.Insert(_userPlaylist.ToArray(), songs, playing, WebTrackId, ClonePlaylistItem, next, AppSettingsStore.Load().InsertPlaylistAtBegin);
            bool rebind = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist) || _currentCategory == "UserPlaylist";
            if (rebind) PlaylistView.ItemsSource = null;
            _userPlaylist = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(kept);
            if (rebind) PlaylistView.ItemsSource = _userPlaylist;
            _userPlaylistIndex = playing == null ? -1 : _userPlaylist.IndexOf(playing);
            if (_userPlaylistIndex < 0 && playing != null)
                _userPlaylistIndex = kept.FindIndex(song => WebTrackId(song) == WebTrackId(playing));
            RenumberCollection(_userPlaylist); NotifyCurrentPlaylistWindow();
            if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
        }
    }
}
