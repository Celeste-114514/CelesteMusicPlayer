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

        internal async Task<object> WebBrowseAsync(JsonElement msg, object host)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            if (operation is "get" or "createPlaylist" or "favorite" or "rating") return WebBrowse(msg);
            if (_webBrowseBusy) throw new InvalidOperationException("请先完成当前操作。");
            _webBrowseBusy = true;
            nint hwnd = NativeMainWebWindow.HandleOf(host);
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
                        bool portable = paths.Any(path => SourceReferenceId(path).Length > 0);
                        picker.FileTypeChoices.Add(portable ? "Celeste 混合播放列表（保留来源与 CUE）" : "M3U8 播放列表", new List<string> { portable ? ".json" : ".m3u8" });
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSaveFileAsync();
                        if (file != null)
                        {
                            if (portable)
                            {
                                var records = paths.Select(SourceFromReference).Where(item => item != null).Select(item => NetworkLibraryStore.Track(item!.SourceTrackId)).Where(track => track != null).ToArray();
                                var sourceIds = records.Select(track => track!.SourceId).ToHashSet();
                                var sources = NetworkLibraryStore.Sources().Where(source => sourceIds.Contains(source.Id)).Select(source => new { source.Id, source.Provider, source.Name, source.Endpoint, source.Root }).ToArray();
                                await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(new { format = "celeste-playlist", version = 1, references = paths, tracks = records, sources }, new JsonSerializerOptions { WriteIndented = true }));
                            }
                            else M3uPlaylistIO.WriteM3u8(file.Path, paths);
                        }
                        break;
                    }
                    case "importM3u":
                    {
                        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".m3u"); picker.FileTypeFilter.Add(".m3u8"); picker.FileTypeFilter.Add(".json");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        var file = await picker.PickSingleFileAsync();
                        if (file == null) break;
                        string[] paths;
                        if (Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            if (new FileInfo(file.Path).Length > 20_000_000) throw new ArgumentException("播放列表文件过大。");
                            using var portable = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
                            var root = portable.RootElement;
                            if (root.GetProperty("format").GetString() != "celeste-playlist" || root.GetProperty("version").GetInt32() != 1) throw new ArgumentException("不支持的播放列表格式版本。");
                            var knownSources = NetworkLibraryStore.Sources().Select(source => source.Id).ToHashSet();
                            foreach (var source in root.GetProperty("sources").EnumerateArray())
                            {
                                string sourceId = source.GetProperty("Id").GetString() ?? "";
                                if (!System.Text.RegularExpressions.Regex.IsMatch(sourceId, "^[a-zA-Z0-9-]{1,64}$")) throw new ArgumentException("来源编号无效。");
                                if (knownSources.Contains(sourceId)) continue;
                                // Imported sources contain no credentials and stay disabled until explicitly configured.
                                NetworkLibraryStore.SaveSource(new NetworkSource { Id = sourceId, Provider = source.GetProperty("Provider").GetString() ?? "webdav", Name = (source.GetProperty("Name").GetString() ?? "导入来源") + "（请配置连接）", Endpoint = source.GetProperty("Endpoint").GetString() ?? "", Root = source.GetProperty("Root").GetString() ?? "", Enabled = false });
                            }
                            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var raw in root.GetProperty("tracks").EnumerateArray())
                            {
                                var track = raw.Deserialize<NetworkTrack>() ?? throw new ArgumentException("无效曲目。");
                                if (!System.Text.RegularExpressions.Regex.IsMatch(track.Id, "^(source-|cue-)[a-f0-9]{64}$")) throw new ArgumentException("曲目编号无效。");
                                if (NetworkLibraryStore.Track(track.Id) == null) { track.Indexed = false; track.Pinned = true; NetworkLibraryStore.Put(new[] { track }); }
                                remap[track.Id] = NetworkIdentity.Reference(track.Id);
                            }
                            paths = root.GetProperty("references").EnumerateArray().Select(entry => entry.GetString() ?? "").Where(path => path.Length > 0)
                                .Select(path => remap.TryGetValue(Path.GetFileNameWithoutExtension(path), out var reference) ? reference : path).ToArray();
                        }
                        else paths = M3uPlaylistIO.Parse(file.Path, existingOnly: false).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

        private async Task<string?> WebPickPlaylistAsync(object host)
        {
            using var dialog = new System.Windows.Forms.Form { Text = "添加到播放列表", Width = 420, Height = 160, StartPosition = System.Windows.Forms.FormStartPosition.CenterParent, FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            var choice = new System.Windows.Forms.ComboBox { Dock = System.Windows.Forms.DockStyle.Top, MaxLength = 120 };
            choice.Items.AddRange(NamedPlaylistStore.List().Cast<object>().ToArray());
            var add = new System.Windows.Forms.Button { Text = "添加", Dock = System.Windows.Forms.DockStyle.Bottom, DialogResult = System.Windows.Forms.DialogResult.OK };
            dialog.Controls.Add(choice); dialog.Controls.Add(add); dialog.AcceptButton = add;
            if (dialog.ShowDialog(host as System.Windows.Forms.IWin32Window) != System.Windows.Forms.DialogResult.OK) return null;
            string name = choice.Text.Trim();
            if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("请输入有效的播放列表名称。");
            if (!NamedPlaylistStore.List().Contains(name)) NamedPlaylistStore.Create(name);
            return name;
        }

        // Context operations resolve the clicked row, never the currently playing track.
        internal async Task<object> WebTrackActionAsync(JsonElement msg, object host)
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
                var map = WebAllTracks().GroupBy(WebTrackId).ToDictionary(group => group.Key, group => group.First());
                foreach (var entry in _webFolderTracks) if (!map.ContainsKey(entry.Key) && File.Exists(entry.Value.FilePath)) map[entry.Key] = entry.Value;
                songs = ids.Distinct().Select(id => id != null && map.TryGetValue(id, out var song) ? song : throw new InvalidOperationException("歌曲已不在曲库中，请刷新。")).ToArray();
            }
            if (songs.Length is < 1 or > 50000) throw new ArgumentException("请选择歌曲。");
            switch (operation)
            {
                case "get": return new { favorite = songs.All(song => TrackStatsStore.Get(song.PersistenceKey)?.IsFavorite == true),
                    canDelete = !AppSettingsStore.Load().DisableDeleteFromDisk && songs.All(song => !song.IsRemote), count = songs.Length };
                case "play": if (fromQueue) PlayUserPlaylistAt(indices[0]); else if (LibraryTracks.Contains(songs[0])) WebPlayTrack(songs[0]); else PlayPlaylistItem(songs[0]); break;
                case "next": WebInsertQueue(songs, next: true); break;
                case "addQueue": WebInsertQueue(songs, next: false); break;
                case "addPlaylist":
                {
                    string? target = await WebPickPlaylistAsync(host);
                    if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(songs.Select(song => song.PersistenceKey)).Distinct(StringComparer.OrdinalIgnoreCase));
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
                case "favorite": foreach (var song in songs) TrackStatsStore.SetFavorite(song.PersistenceKey, msg.GetProperty("value").GetBoolean()); NamedPlaylistStore.SyncFavoritesPlaylist(); UpdateFavoriteButtonUi(); break;
                case "rating":
                    int rating = msg.GetProperty("value").GetInt32();
                    if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。");
                    foreach (var song in songs) TrackStatsStore.SetRating(song.PersistenceKey, rating);
                    break;
                case "tags": OpenTagEditForFiles(songs.Select(song => song.PersistenceKey)); Activate(); break;
                case "reveal": if (songs[0].IsRemote) throw new InvalidOperationException("远程歌曲没有本地文件夹。"); OpenFileLocationInExplorer(songs[0].FilePath); break;
                case "copy": var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, songs.Select(song => song.Title + " - " + song.Artist))); Clipboard.SetContent(data); break;
                case "delete":
                    if (AppSettingsStore.Load().DisableDeleteFromDisk || songs.Any(song => song.IsRemote)) throw new InvalidOperationException("当前歌曲不允许从磁盘删除。");
                    foreach (var song in songs.DistinctBy(song => song.FilePath, StringComparer.OrdinalIgnoreCase)) await DeleteSongFromDiskAsync(song, Content.XamlRoot);
                    break;
                default: throw new ArgumentException("未知的歌曲操作。");
            }
            return new { message = "操作已完成" };
        }

        private void WebInsertQueue(PlaylistItem[] songs, bool next)
        {
            foreach (var song in songs.Where(s => s.SourceTrackId.Length > 0)) NetworkLibraryStore.Pin(song.SourceTrackId);
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
