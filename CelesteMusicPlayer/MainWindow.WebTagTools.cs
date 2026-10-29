using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private readonly SemaphoreSlim _webTagWriteGate = new(1, 1);
        private IReadOnlyList<OnlineTagCandidate> _webTagCandidates = Array.Empty<OnlineTagCandidate>();
        private IReadOnlyList<LyricCandidate> _webLyricCandidates = Array.Empty<LyricCandidate>();
        private readonly Dictionary<int, OnlineTagCandidate> _webOnlineDetails = new();
        private string _webOnlineSearchId = "", _webLyricSearchId = "";
        private InMemoryRandomAccessStream? _webCoverClipboardStream;
        private readonly Dictionary<int, (string Text, bool Translation)> _webLyricTexts = new();
        private CancellationTokenSource? _webLyricBatchCancel;
        private LyricBatchProgress? _webLyricBatchProgress;
        private LyricBatchSummary? _webLyricBatchSummary;
        private string _webLyricBatchError = "";
        private bool _webLyricBatchRunning;

        // The native panel also snapshots tags before a group write and rolls the group back on
        // failure. Snapshot only the writable metadata; do not replace or truncate audio files.
        private static WebTagWriteResult[] WebTagWriteTransaction(
            List<(string Id, string? Path, Dictionary<string, string> Fields)> jobs)
        {
            var snapshots = new Dictionary<string, TagEditModel>(StringComparer.OrdinalIgnoreCase);
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<WebTagWriteResult>();
            try
            {
                foreach (var job in jobs)
                {
                    if (job.Path == null || !File.Exists(job.Path))
                        throw new IOException("文件不存在或为网络曲目：" + job.Id);
                    if (!snapshots.ContainsKey(job.Path)) snapshots[job.Path] = TagEditorService.ReadTagRaw(job.Path);
                    // Validate all numeric fields before writing the first file.
                    ApplyWebTagFields(TagEditorService.ReadTagRaw(job.Path), job.Fields);
                }
                foreach (var job in jobs)
                {
                    attempted.Add(job.Path!);
                    var result = WebTagWriteOne(job.Id, job.Path!, job.Fields);
                    results.Add(result);
                    if (!result.Ok) throw new IOException(result.Error);
                }
                return results.ToArray();
            }
            catch (Exception error)
            {
                var rollbackErrors = new List<string>();
                foreach (string path in attempted)
                {
                    try
                    {
                        var original = snapshots[path];
                        original.MarkAllDirty();
                        var restored = TagEditorService.TrySaveTag(path, original);
                        if (!restored.Ok) rollbackErrors.Add(Path.GetFileName(path) + ": " + restored.Error);
                    }
                    catch (Exception restoreError) { rollbackErrors.Add(Path.GetFileName(path) + ": " + restoreError.Message); }
                }
                string message = error.Message + (rollbackErrors.Count == 0
                    ? "；本次已写入的标签已还原。" : "；部分标签还原失败：" + string.Join("；", rollbackErrors));
                return jobs.Select(job => new WebTagWriteResult { Id = job.Id, Error = message }).ToArray();
            }
        }

        private static List<(string Id, string Path)> WebTagTargets(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            if (!msg.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("请先选择文件。");
            if (ids.GetArrayLength() > 2000) throw new ArgumentException("每次最多处理 2000 个文件。");
            var result = ids.EnumerateArray().Select(value => value.GetString() ?? "").Distinct()
                .Select(id => (Id: id, Path: ResolveTagPath(tracks, id) ?? throw new ArgumentException("网络曲目或失效曲目不能编辑标签。"))).ToList();
            if (result.Count == 0) throw new ArgumentException("请先选择文件。");
            return result;
        }

        private async Task<object> WebTagToolsAsync(string operation, JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            if (operation == "searchOnline")
            {
                string searchId = Guid.NewGuid().ToString("N"); _webOnlineSearchId = searchId;
                string source = ReadString(msg, "source");
                string title = ReadString(msg, "title"), artist = ReadString(msg, "artist"), album = ReadString(msg, "album");
                if (string.IsNullOrWhiteSpace(title + artist + album)) throw new ArgumentException("请填写搜索内容。");
                var candidates = await OnlineTagFill.SearchAsync(source, title, artist, album);
                WebTagRequireSearch(msg, _webOnlineSearchId, searchId);
                _webTagCandidates = candidates; _webOnlineDetails.Clear();
                return new { searchId, candidates = _webTagCandidates.Select((c, i) => WebOnlineCandidate(c, i)).ToArray(), note = OnlineTagFill.CheckKeyAvailability(source) ?? "" };
            }
            if (operation == "onlineDetail")
            {
                string searchId = ReadString(msg, "searchId"); WebTagRequireSearch(msg, _webOnlineSearchId);
                int index = ReadInt(msg, "index", -1);
                if (index < 0 || index >= _webTagCandidates.Count) throw new ArgumentException("搜索结果已变化，请重新选择。");
                var candidate = _webTagCandidates[index];
                candidate = candidate.Source == OnlineTagFill.SourceDiscogs
                    ? await OnlineTagFill.LoadDiscogsDetailAsync(candidate) ?? candidate
                    : await OnlineTagFill.LoadMusicBrainzDetailAsync(candidate, ReadString(msg, "title")) ?? candidate;
                var bytes = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl);
                string lyric = await OnlineTagFill.FetchLyricAsync(candidate.Artist, candidate.Title, candidate.Album, candidate.DurationMs / 1000.0);
                WebTagRequireSearch(msg, _webOnlineSearchId, searchId);
                _webOnlineDetails[index] = candidate;
                return new { candidate = WebOnlineCandidate(candidate, index), cover = bytes is { Length: > 0 } ? "data:" + TagEditorService.SniffImageMime(bytes) + ";base64," + Convert.ToBase64String(bytes) : "", lyrics = lyric };
            }
            if (operation == "searchLyrics")
            {
                string searchId = Guid.NewGuid().ToString("N"); _webLyricSearchId = searchId;
                string keyword = (ReadString(msg, "artist") + " " + ReadString(msg, "title")).Trim();
                if (keyword.Length == 0) throw new ArgumentException("请填写标题或艺术家。");
                var candidates = await OnlineLyricDownload.SearchAsync(ReadString(msg, "source"), keyword);
                WebTagRequireSearch(msg, _webLyricSearchId, searchId); _webLyricCandidates = candidates;
                _webLyricTexts.Clear();
                return new { searchId, candidates = _webLyricCandidates.Select((c, i) => new { index = i, c.Title, c.Artist, c.Album, c.DurationText, c.Source }).ToArray() };
            }
            if (operation is "lyricPreview" or "lyricSave")
            {
                string searchId = ReadString(msg, "searchId"); WebTagRequireSearch(msg, _webLyricSearchId);
                int index = ReadInt(msg, "index", -1);
                if (index < 0 || index >= _webLyricCandidates.Count) throw new ArgumentException("请重新选择歌词候选。");
                bool translation = msg.TryGetProperty("translation", out var trans) && trans.ValueKind == JsonValueKind.True;
                if (!_webLyricTexts.TryGetValue(index, out var cached) || cached.Translation != translation)
                {
                    cached = (await OnlineLyricDownload.FetchLyricAsync(_webLyricCandidates[index], translation), translation);
                    WebTagRequireSearch(msg, _webLyricSearchId, searchId);
                    _webLyricTexts[index] = cached;
                }
                if (operation == "lyricPreview") return new { text = cached.Text };
                if (string.IsNullOrWhiteSpace(cached.Text)) throw new ArgumentException("该候选没有可保存的歌词。");
                var targets = WebTagTargets(msg, tracks);
                var settings = AppSettingsStore.Load();
                bool songFolder = ReadString(msg, "location") != "lyricFolder";
                if (!songFolder && string.IsNullOrWhiteSpace(settings.LyricFolder)) throw new ArgumentException("请先在设置中配置歌词文件夹。");
                var target = OnlineLyricDownload.ParseWriteTarget(ReadString(msg, "target"));
                var results = new List<object>();
                await _webTagWriteGate.WaitAsync();
                try
                {
                    foreach (var item in targets)
                    {
                        var saved = await OnlineLyricDownload.WriteLyricAsync(item.Path, cached.Text, target, songFolder, settings.LyricFolder ?? "");
                        results.Add(new { id = item.Id, ok = saved.Ok, error = saved.Ok ? "" : saved.Message });
                        if (saved.Ok) OnTagsSaved(item.Path);
                    }
                }
                finally { _webTagWriteGate.Release(); }
                return new { results };
            }
            if (operation == "lyricBatchStatus") return new { running = _webLyricBatchRunning, progress = _webLyricBatchProgress, summary = _webLyricBatchSummary, error = _webLyricBatchError };
            if (operation == "rename") return await WebTagRenameAsync(msg, tracks);
            if (operation == "lyricBatchStop") { _webLyricBatchCancel?.Cancel(); return new { ok = true }; }
            if (operation == "lyricBatchStart")
            {
                if (_webLyricBatchRunning) throw new InvalidOperationException("批量歌词下载正在进行。");
                var targets = WebTagTargets(msg, tracks);
                var songs = targets.Select(t => { var p = tracks[t.Id]; return new LyricBatchSong(t.Path, p.Title, p.Artist, p.Album, Path.GetFileNameWithoutExtension(t.Path)); }).ToList();
                var options = new LyricBatchOptions(ReadString(msg, "source"), msg.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.True,
                    !msg.TryGetProperty("skipExisting", out var skip) || skip.ValueKind != JsonValueKind.False, ReadString(msg, "location") != "lyricFolder", AppSettingsStore.Load().LyricFolder ?? "");
                if (!options.SaveToSongFolder && string.IsNullOrWhiteSpace(options.LyricFolder)) throw new ArgumentException("请先在设置中配置歌词文件夹。");
                _webLyricBatchCancel?.Dispose();
                _webLyricBatchCancel = new CancellationTokenSource();
                _webLyricBatchProgress = new LyricBatchProgress(0, 0, songs.Count, "准备下载");
                _webLyricBatchSummary = null; _webLyricBatchError = ""; _webLyricBatchRunning = true;
                var progress = new Progress<LyricBatchProgress>(p => _webLyricBatchProgress = p);
                _ = RunWebLyricBatchAsync(songs, options, progress, _webLyricBatchCancel.Token);
                return new { ok = true };
            }
            if (operation.StartsWith("cover", StringComparison.Ordinal))
                return await WebTagCoverAsync(operation, msg, tracks);
            if (operation == "batchTransform")
            {
                var targets = WebTagTargets(msg, tracks);
                string mode = ReadString(msg, "mode"), field = ReadString(msg, "field"), find = ReadString(msg, "find"), replacement = ReadString(msg, "replacement");
                bool useRegex = msg.TryGetProperty("regex", out var re) && re.ValueKind == JsonValueKind.True;
                var rows = await Task.Run(() => targets.Select(t =>
                {
                    var model = TagEditorService.ReadTagRaw(t.Path);
                    var fields = new Dictionary<string, string>();
                    if (mode == "clearAll")
                    {
                        foreach (string key in new[] { "title", "artist", "album", "albumArtist", "genre", "composer", "comment", "bpm", "lyrics", "year", "track", "trackCount", "disc", "discCount" }) fields[key] = "";
                    }
                    else if (mode == "fromFilename")
                    {
                        var parsed = TagEditorService.TryParseTagsFromFileName(Path.GetFileNameWithoutExtension(t.Path));
                        bool overwrite = msg.TryGetProperty("overwrite", out var over) && over.ValueKind == JsonValueKind.True;
                        if (overwrite || string.IsNullOrWhiteSpace(model.Title)) fields["title"] = parsed.Title;
                        if ((overwrite || string.IsNullOrWhiteSpace(model.Artist)) && !string.IsNullOrWhiteSpace(parsed.Artist)) fields["artist"] = parsed.Artist;
                    }
                    else
                    {
                        string text = WebModelField(model, field);
                        fields[field] = mode switch
                        {
                            "replace" => useRegex ? Regex.Replace(text, find, replacement, RegexOptions.None, TimeSpan.FromSeconds(1)) : text.Replace(find, replacement, StringComparison.Ordinal),
                            "upper" => text.ToUpperInvariant(),
                            "lower" => text.ToLowerInvariant(),
                            "titleCase" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture)),
                            "clear" => "",
                            _ => throw new ArgumentException("未知的批量工具。")
                        };
                    }
                    return new { id = t.Id, fields };
                }).ToArray());
                // Return a preview; applying uses the same transaction as all other tag writes.
                return new { rows };
            }
            throw new ArgumentException("未知的标签操作。");
        }

        private async Task RunWebLyricBatchAsync(List<LyricBatchSong> songs, LyricBatchOptions options, IProgress<LyricBatchProgress> progress, CancellationToken token)
        {
            try { _webLyricBatchSummary = await OnlineLyricDownload.BatchDownloadAsync(songs, options, progress, token); }
            catch (Exception error) { _webLyricBatchError = error.Message; }
            finally { _webLyricBatchRunning = false; }
        }

        private static object WebOnlineCandidate(OnlineTagCandidate c, int index) => new
        {
            index, title = c.Title, artist = c.Artist, album = c.Album, albumArtist = c.AlbumArtist, year = c.Year,
            track = c.Track, trackCount = c.TrackCount, disc = c.Disc, discCount = c.DiscCount, genre = c.Genre,
            source = c.SourceLabel, duration = c.DurationText
        };

        private static void WebTagRequireSearch(JsonElement msg, string active, string? expected = null)
        {
            if (string.IsNullOrEmpty(active) || !string.Equals(active, expected ?? ReadString(msg, "searchId"), StringComparison.Ordinal))
                throw new InvalidOperationException("搜索结果已更新，请重新搜索并选择候选。");
        }

        private static string WebModelField(TagEditModel m, string field) => field switch
        {
            "title" => m.Title, "artist" => m.Artist, "album" => m.Album, "albumArtist" => m.AlbumArtist,
            "genre" => m.Genre, "composer" => m.Composer, "comment" => m.Comment, "bpm" => m.Bpm,
            "lyrics" => m.Lyrics, "year" => m.Year.ToString(CultureInfo.InvariantCulture),
            "track" => m.Track.ToString(CultureInfo.InvariantCulture), "disc" => m.Disc.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("不支持该字段。")
        };

        internal async Task<(PlaylistItem[] Tracks, string Directory, bool Canceled)> WebTagAddFilesAsync(bool folder, object host)
        {
            nint handle = NativeMainWebWindow.HandleOf(host);
            string[] paths;
            string directory = "";
            if (folder)
            {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
                var selected = await picker.PickSingleFolderAsync();
                if (selected == null) return (Array.Empty<PlaylistItem>(), "", true);
                directory = selected.Path;
                paths = await Task.Run(() => Directory.EnumerateFiles(selected.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).Take(20001).ToArray());
            }
            else
            {
                var picker = new FileOpenPicker();
                foreach (string ext in AudioExtensions) picker.FileTypeFilter.Add(ext);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
                var files = await picker.PickMultipleFilesAsync();
                paths = files.Select(f => f.Path).ToArray();
                if (paths.Length == 0) return (Array.Empty<PlaylistItem>(), "", true);
            }
            if (paths.Length > 20000) throw new ArgumentException("每个标签工作集最多 20,000 个文件，请分开工作目录。原工作集已保留。");
            return (await Task.Run(() => paths.Select(CreatePlaylistItemFromPath).ToArray()), directory, false);
        }
        internal PlaylistItem WebTagFromPath(string path) => CreatePlaylistItemFromPath(path);

        private async Task<object> WebTagRenameAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            var rows = msg.GetProperty("rows").EnumerateArray().ToArray();
            if (rows.Length is < 1 or > 2000) throw new ArgumentException("每次可重命名 1–2000 个文件。");
            var jobs = rows.Select(row =>
            {
                string id = ReadString(row, "id");
                string oldPath = ResolveTagPath(tracks, id) ?? throw new ArgumentException("曲目已失效。");
                string name = ReadString(row, "file").Trim();
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name)) throw new ArgumentException("文件名无效。" );
                if (!name.EndsWith(Path.GetExtension(oldPath), StringComparison.OrdinalIgnoreCase)) name += Path.GetExtension(oldPath);
                return (Old: oldPath, New: Path.Combine(Path.GetDirectoryName(oldPath)!, name));
            }).Distinct().Where(j => !string.Equals(j.Old, j.New, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (jobs.Select(j => j.New).Distinct(StringComparer.OrdinalIgnoreCase).Count() != jobs.Length) throw new ArgumentException("多个文件的新名称重复。");
            if (jobs.Any(j => string.Equals(j.Old, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("请先切换到其他曲目，再重命名当前播放文件。");
            await _webTagWriteGate.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var job in jobs) if (File.Exists(job.New)) throw new IOException("目标文件已存在：" + Path.GetFileName(job.New));
                    var moved = new List<(string Old, string New)>();
                    try { foreach (var job in jobs) { File.Move(job.Old, job.New); moved.Add(job); } }
                    catch (Exception error)
                    {
                        var failed = new List<string>();
                        foreach (var job in moved.AsEnumerable().Reverse()) try { File.Move(job.New, job.Old); } catch { failed.Add(job.New); }
                        throw new IOException(error.Message + (failed.Count == 0 ? "；已恢复原文件名。" : "；以下文件需手动恢复：" + string.Join("、", failed)), error);
                    }
                });
                foreach (var job in jobs)
                {
                    foreach (var item in tracks.Values.Concat(_playlist).Concat(_userPlaylist).Distinct())
                        if (string.Equals(item.FilePath, job.Old, StringComparison.OrdinalIgnoreCase)) item.FilePath = job.New;
                    foreach (string playlist in NamedPlaylistStore.List())
                    {
                        if (string.Equals(playlist, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal)) continue;
                        var paths = NamedPlaylistStore.LoadSongs(playlist);
                        if (paths.Any(p => string.Equals(p, job.Old, StringComparison.OrdinalIgnoreCase)))
                            NamedPlaylistStore.SaveSongs(playlist, paths.Select(p => string.Equals(p, job.Old, StringComparison.OrdinalIgnoreCase) ? job.New : p));
                    }
                    var session = LibraryDb.LoadSession();
                    if (session != null) LibraryDb.SaveSession(session.Mode, session.FolderPath, session.FilePaths.Select(p => string.Equals(p, job.Old, StringComparison.OrdinalIgnoreCase) ? job.New : p));
                    var stats = TrackStatsStore.Load();
                    if (stats.Remove(job.Old, out var entry)) { entry.FilePath = job.New; stats[job.New] = entry; TrackStatsStore.Save(stats); }
                    LibraryDb.DeleteTracks(new[] { job.Old });
                    InvalidateCoverCache(job.Old); OnTagsSaved(job.New);
                }
                SavePlayQueue();
                return new { ok = true, count = jobs.Length, remap = tracks.Where(pair => pair.Key != WebTrackIdentity.Of(pair.Value)).ToDictionary(pair => pair.Key, pair => WebTrackIdentity.Of(pair.Value)) };
            }
            finally { _webTagWriteGate.Release(); }
        }

        private async Task<object> WebTagCoverAsync(string operation, JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            var targets = WebTagTargets(msg, tracks);
            byte[]? bytes = null;
            if (operation == "coverReplace")
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerHostHandle());
                foreach (string extension in new[] { ".jpg", ".jpeg", ".png" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return new { canceled = true };
                bytes = await File.ReadAllBytesAsync(file.Path);
                if (bytes.Length > 25_000_000) throw new ArgumentException("封面图片过大，请使用小于 25 MB 的图片。");
            }
            else if (operation == "coverCopy")
            {
                bytes = await Task.Run(() => TagEditorService.ReadCoverBytes(targets[0].Path));
                if (bytes == null) throw new ArgumentException("文件没有内嵌封面。");
                _webCoverClipboardStream?.Dispose(); _webCoverClipboardStream = new InMemoryRandomAccessStream();
                using (var output = _webCoverClipboardStream.GetOutputStreamAt(0)) await output.WriteAsync(bytes.AsBuffer());
                var package = new DataPackage(); package.SetBitmap(RandomAccessStreamReference.CreateFromStream(_webCoverClipboardStream));
                Clipboard.SetContent(package); return new { ok = true };
            }
            else if (operation == "coverPaste")
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Bitmap)) throw new ArgumentException("剪贴板中没有图片。");
                using var stream = await (await content.GetBitmapAsync()).OpenReadAsync();
                using var memory = new MemoryStream(); await stream.AsStreamForRead().CopyToAsync(memory); bytes = memory.ToArray();
                if (bytes.Length > 25_000_000) throw new ArgumentException("剪贴板图片过大。");
            }
            else if (operation == "coverOnline")
            {
                WebTagRequireSearch(msg, _webOnlineSearchId);
                int index = ReadInt(msg, "index", -1);
                if (index < 0 || index >= _webTagCandidates.Count) throw new ArgumentException("搜索结果已变化。");
                var candidate = _webOnlineDetails.TryGetValue(index, out var detail) ? detail : _webTagCandidates[index];
                bytes = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl)
                    ?? throw new IOException("封面下载失败。");
                WebTagRequireSearch(msg, _webOnlineSearchId);
            }
            else if (operation == "coverExport")
            {
                bytes = await Task.Run(() => TagEditorService.ReadCoverBytes(targets[0].Path));
                if (bytes == null) throw new ArgumentException("文件没有内嵌封面。");
                var picker = new FileSavePicker { SuggestedFileName = "cover" };
                picker.FileTypeChoices.Add("封面图片", new List<string> { TagEditorService.SniffImageMime(bytes) == "image/png" ? ".png" : ".jpg" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerHostHandle());
                var file = await picker.PickSaveFileAsync();
                if (file == null) return new { canceled = true };
                await File.WriteAllBytesAsync(file.Path, bytes);
                return new { ok = true };
            }
            else if (operation != "coverRemove") throw new ArgumentException("未知的封面操作。");

            if (bytes != null) { if (bytes.Length > 25_000_000) throw new ArgumentException("封面图片过大。"); using var stream = new MemoryStream(bytes); using var image = System.Drawing.Image.FromStream(stream); }

            await _webTagWriteGate.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    var originals = targets.ToDictionary(t => t.Path, t => TagEditorService.ReadCoverBytes(t.Path), StringComparer.OrdinalIgnoreCase);
                    var attempted = new List<string>();
                    try
                    {
                        foreach (var t in targets)
                        {
                            attempted.Add(t.Path);
                            bool ok = operation == "coverRemove" ? TagEditorService.ClearCover(t.Path) : TagEditorService.SetCoverBytes(t.Path, bytes!);
                            if (!ok) throw new IOException("封面写入失败：" + Path.GetFileName(t.Path));
                        }
                    }
                    catch (Exception error)
                    {
                        var failures = new List<string>();
                        foreach (string path in attempted)
                        {
                            try
                            {
                                bool ok = originals[path] is byte[] original ? TagEditorService.SetCoverBytes(path, original) : TagEditorService.ClearCover(path);
                                if (!ok) failures.Add(path);
                            }
                            catch { failures.Add(path); }
                        }
                        throw new IOException(error.Message + (failures.Count == 0 ? "；封面已还原。" : "；有封面还原失败：" + string.Join("、", failures)), error);
                    }
                });
                foreach (var t in targets) { InvalidateCoverCache(t.Path); OnTagsSaved(t.Path); }
            }
            finally { _webTagWriteGate.Release(); }
            return new { ok = true, count = targets.Count };
        }
    }
}
