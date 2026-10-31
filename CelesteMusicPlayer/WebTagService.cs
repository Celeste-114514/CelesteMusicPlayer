using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer;

/// <summary>
/// 网页标签编辑（<c>tags</c> 命令）需要宿主提供的能力。
///
/// W13 的规矩：**只放服务自己造不出来的**。这里只剩三样东西——
/// 播放队列（模型在 <see cref="PlaybackQueue"/>，宿主把它整个交出来）、
/// 界面上正在显示的行集合（重命名后要跟着改路径）、以及两条 UI 通知
/// （刷新元数据 / 落盘队列）。文件读写、在线搜索、歌词下载全在服务里。
/// </summary>
internal interface IWebTagHost
{
    /// <summary>播放队列：重命名后要就地改路径，改完落盘。</summary>
    PlaybackQueue Queue { get; }

    /// <summary>
    /// 界面上当前绑定的行（<c>_playlist</c>）。重命名要顺带改这里的路径，
    /// 否则标签页和原生列表会指着已经不存在的文件名。服务造不出这份引用。
    /// </summary>
    IReadOnlyList<PlaylistItem> VisibleRows { get; }

    /// <summary>正在播放的文件：不允许重命名（播放中改文件名会打断读取）。</summary>
    string? NowPlayingPath { get; }

    /// <summary>重命名 / 歌词回写之后把队列落到磁盘。</summary>
    void SavePlayQueue();

    /// <summary>标签写入完成后刷新界面上的那份元数据（回到 UI 线程）。</summary>
    void OnTagsSaved(string path);
}

/// <summary>
/// 网页标签页的全部业务（<c>tags</c> 命令）。
///
/// W13：从 <c>MainWindow.WebTags.cs</c> + <c>MainWindow.WebTagTools.cs</c> 搬出。
/// 文件 I/O 细分三层：本类负责命令分发与会话态（搜索 id、候选缓存、批量任务进度），
/// <see cref="WebTagFileService"/> 负责单文件读写与整组事务，<see cref="TagEditorService"/>
/// 负责容器格式。三者都没有界面依赖。
///
/// 会话态留在实例里是刻意的：在线搜索有「结果已过期」判定（searchId 对不上就拒绝），
/// 批量歌词下载要能查进度 —— 这些都跨请求，不能塞进方法局部变量。
/// </summary>
internal sealed class WebTagService(IWebTagHost host)
{
    private IReadOnlyList<OnlineTagCandidate> _candidates = Array.Empty<OnlineTagCandidate>();
    private IReadOnlyList<LyricCandidate> _lyricCandidates = Array.Empty<LyricCandidate>();
    private readonly Dictionary<int, OnlineTagCandidate> _onlineDetails = new();
    private string _onlineSearchId = "", _lyricSearchId = "";
    private InMemoryRandomAccessStream? _coverClipboardStream;
    private readonly Dictionary<int, (string Text, bool Translation)> _lyricTexts = new();
    private CancellationTokenSource? _lyricBatchCancel;
    private LyricBatchProgress? _lyricBatchProgress;
    private LyricBatchSummary? _lyricBatchSummary;
    private string _lyricBatchError = "";
    private bool _lyricBatchRunning;

    /// <summary>命令总入口：list / read / write 在本地，其余交给在线与批量工具。</summary>
    internal async Task<object> ExecuteAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        string operation = "list";
        if (msg.TryGetProperty("operation", out var opElement) && opElement.ValueKind == JsonValueKind.String)
            operation = opElement.GetString() ?? "list";

        if (operation == "list")
        {
            var snapshot = new Dictionary<string, PlaylistItem>(tracks, StringComparer.Ordinal);
            return await Task.Run(() => ListRows(msg, snapshot)).ConfigureAwait(true);
        }

        if (operation == "read")
            return await ReadAsync(msg, tracks).ConfigureAwait(true);

        if (operation == "write")
            return await WriteAsync(msg, tracks).ConfigureAwait(true);

        return await ToolsAsync(operation, msg, tracks).ConfigureAwait(true);
    }

    // ---- 工作集 -------------------------------------------------------------------------

    /// <summary>挑文件 / 挑文件夹，给标签页加工作集。返回新曲目（宿主登记）。</summary>
    internal async Task<(PlaylistItem[] Tracks, string Directory, bool Canceled)> AddFilesAsync(bool folder, object window)
    {
        nint handle = NativeMainWebWindow.HandleOf(window);
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
                .Where(path => LibraryFolderScan.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).Take(20001).ToArray());
        }
        else
        {
            var picker = new FileOpenPicker();
            foreach (string ext in LibraryFolderScan.AudioExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            var files = await picker.PickMultipleFilesAsync();
            paths = files.Select(f => f.Path).ToArray();
            if (paths.Length == 0) return (Array.Empty<PlaylistItem>(), "", true);
        }
        if (paths.Length > 20000) throw new ArgumentException("每个标签工作集最多 20,000 个文件，请分开工作目录。原工作集已保留。");
        return (await Task.Run(() => paths.Select(MainWindow.CreatePlaylistItemFromPath).ToArray()), directory, false);
    }

    /// <summary>把一个路径变成曲目（网页拖文件进来时用）。</summary>
    internal static PlaylistItem FromPath(string path) => MainWindow.CreatePlaylistItemFromPath(path);

    /// <summary>把 {"ids": [...]} 解析成 (id, 绝对路径)，远程/失效曲目直接拒。</summary>
    private static List<(string Id, string Path)> Targets(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        if (!msg.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("请先选择文件。");
        if (ids.GetArrayLength() > 2000) throw new ArgumentException("每次最多处理 2000 个文件。");
        var result = ids.EnumerateArray().Select(value => value.GetString() ?? "").Distinct()
            .Select(id => (Id: id, Path: WebTagFileService.ResolvePath(tracks, id) ?? throw new ArgumentException("网络曲目或失效曲目不能编辑标签。"))).ToList();
        if (result.Count == 0) throw new ArgumentException("请先选择文件。");
        return result;
    }

    // ---- list ---------------------------------------------------------------------------

    private static object ListRows(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        string query = ReadString(msg, "query").Trim();
        int offset = Math.Max(0, ReadInt(msg, "offset", 0));
        bool all = msg.TryGetProperty("all", out var allRows) && allRows.ValueKind == JsonValueKind.True;
        if (all) offset = 0;
        int limit = all ? 20000 : Math.Clamp(ReadInt(msg, "limit", 300), 1, 2000);

        IEnumerable<KeyValuePair<string, PlaylistItem>> items = Array.Empty<KeyValuePair<string, PlaylistItem>>();
        if (msg.TryGetProperty("scopeIds", out var scope) && scope.ValueKind == JsonValueKind.Array && scope.GetArrayLength() > 0)
        {
            if (scope.GetArrayLength() > 20000) throw new ArgumentException("每个标签工作集最多 20,000 个文件，请分开工作目录。");
            var wanted = scope.EnumerateArray().Select(id => id.GetString() ?? "").Distinct();
            items = wanted.Where(tracks.ContainsKey).Select(id => new KeyValuePair<string, PlaylistItem>(id, tracks[id]));
        }
        if (query.Length > 0)
        {
            items = items.Where(pair =>
                Contains(pair.Value.Title, query) ||
                Contains(pair.Value.Artist, query) ||
                Contains(pair.Value.Album, query) ||
                Contains(Path.GetFileName(pair.Value.FilePath), query));
        }

        var filtered = items.ToArray();
        var rows = WebTagSortRules.Sort(filtered, msg, (id, path) => WebTagFileService.ReadOne(id, path, false))
            .Skip(offset)
            .Take(limit)
            .Select(pair => new
            {
                id = pair.Key,
                file = Path.GetFileName(pair.Value.FilePath),
                folder = Path.GetDirectoryName(pair.Value.FilePath) ?? "",
                duration = pair.Value.DurationText,
                format = Path.GetExtension(pair.Value.FilePath).TrimStart('.').ToUpperInvariant(),
                title = pair.Value.Title,
                artist = pair.Value.Artist,
                album = pair.Value.Album,
                albumArtist = pair.Value.AlbumArtist,
                year = pair.Value.Year == 0 ? "" : pair.Value.Year.ToString(CultureInfo.InvariantCulture),
                track = pair.Value.Track == 0 ? "" : pair.Value.Track.ToString(CultureInfo.InvariantCulture),
                disc = pair.Value.Disc == 0 ? "" : pair.Value.Disc.ToString(CultureInfo.InvariantCulture),
                genre = pair.Value.Genre,
                remote = pair.Value.IsRemote
            })
            .ToArray();

        return new { total = filtered.Length, offset, rows };
    }

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;

    // ---- read ---------------------------------------------------------------------------

    private static async Task<object> ReadAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        bool wantCover = msg.TryGetProperty("cover", out var coverElement) && coverElement.ValueKind == JsonValueKind.True;

        var jobs = new List<(string Id, string? Path, bool Cover)>();
        if (msg.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var idElement in idsElement.EnumerateArray().Take(60))
            {
                string id = idElement.GetString() ?? "";
                if (tracks.TryGetValue(id, out var item) && !item.IsRemote)
                    jobs.Add((id, item.FilePath, wantCover && jobs.Count == 0));
                else
                    jobs.Add((id, null, false));
            }
        }

        if (jobs.Count == 0)
            return new { tracks = Array.Empty<object>() };

        var rows = await Task.Run(() => jobs
            .Select(job => job.Path == null
                ? (object)new { id = job.Id, ok = false, error = "网络曲目暂不支持标签编辑。" }
                : WebTagFileService.ReadOne(job.Id, job.Path, job.Cover))
            .ToArray()).ConfigureAwait(true);

        return new { tracks = rows };
    }

    // ---- write --------------------------------------------------------------------------

    private async Task<object> WriteAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        // 两种入参：{ ids, fields } 同一组字段写多首（快速编辑表单），
        // { rows: [{ id, fields }] } 每行一套（表格里逐格改）。
        var jobs = new List<(string Id, string? Path, Dictionary<string, string> Fields)>();

        if (msg.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            if (rowsElement.GetArrayLength() > 2000) throw new ArgumentException("每次最多写入 2000 个文件，请缩小选择范围。");
            foreach (var rowElement in rowsElement.EnumerateArray().Take(2000))
            {
                string id = ReadString(rowElement, "id");
                var fields = WebTagFileService.ReadFieldMap(rowElement);
                if (id.Length > 0 && fields.Count > 0)
                    jobs.Add((id, WebTagFileService.ResolvePath(tracks, id), fields));
            }
        }
        else
        {
            var fields = WebTagFileService.ReadFieldMap(msg);
            if (fields.Count == 0)
                throw new ArgumentException("缺少要写入的字段。");
            if (!msg.TryGetProperty("ids", out var idsElement) || idsElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("缺少曲目编号。");
            if (idsElement.GetArrayLength() > 2000) throw new ArgumentException("每次最多写入 2000 个文件，请缩小选择范围。");

            foreach (var idElement in idsElement.EnumerateArray().Take(2000))
            {
                string id = idElement.GetString() ?? "";
                if (id.Length > 0)
                    jobs.Add((id, WebTagFileService.ResolvePath(tracks, id), fields));
            }
        }

        if (jobs.Count == 0)
            return new { results = Array.Empty<object>() };

        WebTagWriteResult[] results = await WebTagFileService.WriteAsync(jobs).ConfigureAwait(true);

        // 曲库行要跟着改，否则表格继续显示旧文字。
        foreach (var result in results)
        {
            if (!result.Ok || result.Skipped || result.Fields == null)
                continue;
            if (!tracks.TryGetValue(result.Id, out var item))
                continue;
            if (result.Fields.TryGetValue("title", out var title)) item.Title = title;
            if (result.Fields.TryGetValue("artist", out var artist)) item.Artist = artist;
            if (result.Fields.TryGetValue("album", out var album)) item.Album = album;
            if (result.Fields.TryGetValue("albumArtist", out var albumArtist)) item.AlbumArtist = albumArtist;
            if (result.Fields.TryGetValue("genre", out var genre)) item.Genre = genre;
            if (result.Fields.TryGetValue("year", out var year)) item.Year = uint.TryParse(year, out var y) ? y : 0;
            if (result.Fields.TryGetValue("track", out var track)) item.Track = uint.TryParse(track, out var t) ? t : 0;
            if (result.Fields.TryGetValue("disc", out var disc)) item.Disc = uint.TryParse(disc, out var d) ? d : 0;
            host.OnTagsSaved(item.FilePath);
        }

        var payload = results.Select(result => new
        {
            id = result.Id,
            ok = result.Ok,
            skipped = result.Skipped,
            error = result.Error
        }).ToArray();

        return new { results = payload };
    }

    // ---- 在线搜索 / 歌词 / 重命名 / 封面 / 批量工具 -------------------------------------

    private async Task<object> ToolsAsync(string operation, JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        if (operation == "searchOnline")
        {
            string searchId = Guid.NewGuid().ToString("N"); _onlineSearchId = searchId;
            string source = ReadString(msg, "source");
            string title = ReadString(msg, "title"), artist = ReadString(msg, "artist"), album = ReadString(msg, "album");
            if (string.IsNullOrWhiteSpace(title + artist + album)) throw new ArgumentException("请填写搜索内容。");
            var candidates = await OnlineTagFill.SearchAsync(source, title, artist, album);
            RequireSearch(msg, _onlineSearchId, searchId);
            _candidates = candidates; _onlineDetails.Clear();
            return new { searchId, candidates = _candidates.Select((c, i) => OnlineCandidate(c, i)).ToArray(), note = OnlineTagFill.CheckKeyAvailability(source) ?? "" };
        }
        if (operation == "onlineDetail")
        {
            string searchId = ReadString(msg, "searchId"); RequireSearch(msg, _onlineSearchId);
            int index = ReadInt(msg, "index", -1);
            if (index < 0 || index >= _candidates.Count) throw new ArgumentException("搜索结果已变化，请重新选择。");
            var candidate = _candidates[index];
            candidate = candidate.Source == OnlineTagFill.SourceDiscogs
                ? await OnlineTagFill.LoadDiscogsDetailAsync(candidate) ?? candidate
                : await OnlineTagFill.LoadMusicBrainzDetailAsync(candidate, ReadString(msg, "title")) ?? candidate;
            var bytes = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl);
            string lyric = await OnlineTagFill.FetchLyricAsync(candidate.Artist, candidate.Title, candidate.Album, candidate.DurationMs / 1000.0);
            RequireSearch(msg, _onlineSearchId, searchId);
            _onlineDetails[index] = candidate;
            return new { candidate = OnlineCandidate(candidate, index), cover = bytes is { Length: > 0 } ? "data:" + TagEditorService.SniffImageMime(bytes) + ";base64," + Convert.ToBase64String(bytes) : "", lyrics = lyric };
        }
        if (operation == "searchLyrics")
        {
            string searchId = Guid.NewGuid().ToString("N"); _lyricSearchId = searchId;
            string keyword = (ReadString(msg, "artist") + " " + ReadString(msg, "title")).Trim();
            if (keyword.Length == 0) throw new ArgumentException("请填写标题或艺术家。");
            var candidates = await OnlineLyricDownload.SearchAsync(ReadString(msg, "source"), keyword);
            RequireSearch(msg, _lyricSearchId, searchId); _lyricCandidates = candidates;
            _lyricTexts.Clear();
            return new { searchId, candidates = _lyricCandidates.Select((c, i) => new { index = i, c.Title, c.Artist, c.Album, c.DurationText, c.Source }).ToArray() };
        }
        if (operation is "lyricPreview" or "lyricSave")
        {
            string searchId = ReadString(msg, "searchId"); RequireSearch(msg, _lyricSearchId);
            int index = ReadInt(msg, "index", -1);
            if (index < 0 || index >= _lyricCandidates.Count) throw new ArgumentException("请重新选择歌词候选。");
            bool translation = msg.TryGetProperty("translation", out var trans) && trans.ValueKind == JsonValueKind.True;
            if (!_lyricTexts.TryGetValue(index, out var cached) || cached.Translation != translation)
            {
                cached = (await OnlineLyricDownload.FetchLyricAsync(_lyricCandidates[index], translation), translation);
                RequireSearch(msg, _lyricSearchId, searchId);
                _lyricTexts[index] = cached;
            }
            if (operation == "lyricPreview") return new { text = cached.Text };
            if (string.IsNullOrWhiteSpace(cached.Text)) throw new ArgumentException("该候选没有可保存的歌词。");
            var targets = Targets(msg, tracks);
            var settings = AppSettingsStore.Load();
            bool songFolder = ReadString(msg, "location") != "lyricFolder";
            if (!songFolder && string.IsNullOrWhiteSpace(settings.LyricFolder)) throw new ArgumentException("请先在设置中配置歌词文件夹。");
            var target = OnlineLyricDownload.ParseWriteTarget(ReadString(msg, "target"));
            var results = new List<object>();
            await WebTagFileService.WaitWriteGateAsync();
            try
            {
                foreach (var item in targets)
                {
                    var saved = await OnlineLyricDownload.WriteLyricAsync(item.Path, cached.Text, target, songFolder, settings.LyricFolder ?? "");
                    results.Add(new { id = item.Id, ok = saved.Ok, error = saved.Ok ? "" : saved.Message });
                    if (saved.Ok) host.OnTagsSaved(item.Path);
                }
            }
            finally { WebTagFileService.ReleaseWriteGate(); }
            return new { results };
        }
        if (operation == "lyricBatchStatus") return new { running = _lyricBatchRunning, progress = _lyricBatchProgress, summary = _lyricBatchSummary, error = _lyricBatchError };
        if (operation == "rename") return await RenameAsync(msg, tracks);
        if (operation == "lyricBatchStop") { _lyricBatchCancel?.Cancel(); return new { ok = true }; }
        if (operation == "lyricBatchStart")
        {
            if (_lyricBatchRunning) throw new InvalidOperationException("批量歌词下载正在进行。");
            var targets = Targets(msg, tracks);
            var songs = targets.Select(t => { var p = tracks[t.Id]; return new LyricBatchSong(t.Path, p.Title, p.Artist, p.Album, Path.GetFileNameWithoutExtension(t.Path)); }).ToList();
            var options = new LyricBatchOptions(ReadString(msg, "source"), msg.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.True,
                !msg.TryGetProperty("skipExisting", out var skip) || skip.ValueKind != JsonValueKind.False, ReadString(msg, "location") != "lyricFolder", AppSettingsStore.Load().LyricFolder ?? "");
            if (!options.SaveToSongFolder && string.IsNullOrWhiteSpace(options.LyricFolder)) throw new ArgumentException("请先在设置中配置歌词文件夹。");
            _lyricBatchCancel?.Dispose();
            _lyricBatchCancel = new CancellationTokenSource();
            _lyricBatchProgress = new LyricBatchProgress(0, 0, songs.Count, "准备下载");
            _lyricBatchSummary = null; _lyricBatchError = ""; _lyricBatchRunning = true;
            var progress = new Progress<LyricBatchProgress>(p => _lyricBatchProgress = p);
            _ = RunLyricBatchAsync(songs, options, progress, _lyricBatchCancel.Token);
            return new { ok = true };
        }
        if (operation.StartsWith("cover", StringComparison.Ordinal))
            return await CoverAsync(operation, msg, tracks);
        if (operation == "batchTransform")
        {
            var targets = Targets(msg, tracks);
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
                    string text = ModelField(model, field);
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
            // 只回预览；真正落盘走 write，与其它标签写入同一个事务。
            return new { rows };
        }
        throw new ArgumentException("未知的标签操作。");
    }

    private async Task RunLyricBatchAsync(List<LyricBatchSong> songs, LyricBatchOptions options, IProgress<LyricBatchProgress> progress, CancellationToken token)
    {
        try { _lyricBatchSummary = await OnlineLyricDownload.BatchDownloadAsync(songs, options, progress, token); }
        catch (Exception error) { _lyricBatchError = error.Message; }
        finally { _lyricBatchRunning = false; }
    }

    private static object OnlineCandidate(OnlineTagCandidate c, int index) => new
    {
        index, title = c.Title, artist = c.Artist, album = c.Album, albumArtist = c.AlbumArtist, year = c.Year,
        track = c.Track, trackCount = c.TrackCount, disc = c.Disc, discCount = c.DiscCount, genre = c.Genre,
        source = c.SourceLabel, duration = c.DurationText
    };

    private static void RequireSearch(JsonElement msg, string active, string? expected = null)
    {
        if (string.IsNullOrEmpty(active) || !string.Equals(active, expected ?? ReadString(msg, "searchId"), StringComparison.Ordinal))
            throw new InvalidOperationException("搜索结果已更新，请重新搜索并选择候选。");
    }

    private static string ModelField(TagEditModel m, string field) => field switch
    {
        "title" => m.Title, "artist" => m.Artist, "album" => m.Album, "albumArtist" => m.AlbumArtist,
        "genre" => m.Genre, "composer" => m.Composer, "comment" => m.Comment, "bpm" => m.Bpm,
        "lyrics" => m.Lyrics, "year" => m.Year.ToString(CultureInfo.InvariantCulture),
        "track" => m.Track.ToString(CultureInfo.InvariantCulture), "disc" => m.Disc.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("不支持该字段。")
    };

    /// <summary>批量改名：先校验全部，再逐个 move；中途失败按逆序回滚，然后同步所有引用。</summary>
    private async Task<object> RenameAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        var rows = msg.GetProperty("rows").EnumerateArray().ToArray();
        if (rows.Length is < 1 or > 2000) throw new ArgumentException("每次可重命名 1–2000 个文件。");
        var jobs = rows.Select(row =>
        {
            string id = ReadString(row, "id");
            string oldPath = WebTagFileService.ResolvePath(tracks, id) ?? throw new ArgumentException("曲目已失效。");
            string name = ReadString(row, "file").Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name)) throw new ArgumentException("文件名无效。");
            if (!name.EndsWith(Path.GetExtension(oldPath), StringComparison.OrdinalIgnoreCase)) name += Path.GetExtension(oldPath);
            return (Old: oldPath, New: Path.Combine(Path.GetDirectoryName(oldPath)!, name));
        }).Distinct().Where(j => !string.Equals(j.Old, j.New, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (jobs.Select(j => j.New).Distinct(StringComparer.OrdinalIgnoreCase).Count() != jobs.Length) throw new ArgumentException("多个文件的新名称重复。");
        if (jobs.Any(j => string.Equals(j.Old, host.NowPlayingPath, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("请先切换到其他曲目，再重命名当前播放文件。");
        await WebTagFileService.WaitWriteGateAsync();
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
                foreach (var item in tracks.Values.Concat(host.VisibleRows).Concat(host.Queue.Items).Distinct())
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
                MainWindow.InvalidateCoverCache(job.Old); host.OnTagsSaved(job.New);
            }
            host.SavePlayQueue();
            return new { ok = true, count = jobs.Length, remap = tracks.Where(pair => pair.Key != WebTrackIdentity.Of(pair.Value)).ToDictionary(pair => pair.Key, pair => WebTrackIdentity.Of(pair.Value)) };
        }
        finally { WebTagFileService.ReleaseWriteGate(); }
    }

    /// <summary>封面：换 / 复制 / 粘贴 / 在线取 / 导出 / 删除。整组失败按快照还原。</summary>
    private async Task<object> CoverAsync(string operation, JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
    {
        var targets = Targets(msg, tracks);
        byte[]? bytes = null;
        if (operation == "coverReplace")
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.PickerHostHandle());
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
            _coverClipboardStream?.Dispose(); _coverClipboardStream = new InMemoryRandomAccessStream();
            using (var output = _coverClipboardStream.GetOutputStreamAt(0)) await output.WriteAsync(bytes.AsBuffer());
            var package = new DataPackage(); package.SetBitmap(RandomAccessStreamReference.CreateFromStream(_coverClipboardStream));
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
            RequireSearch(msg, _onlineSearchId);
            int index = ReadInt(msg, "index", -1);
            if (index < 0 || index >= _candidates.Count) throw new ArgumentException("搜索结果已变化。");
            var candidate = _onlineDetails.TryGetValue(index, out var detail) ? detail : _candidates[index];
            bytes = await OnlineTagFill.DownloadCoverBytesAsync(candidate.CoverUrl)
                ?? throw new IOException("封面下载失败。");
            RequireSearch(msg, _onlineSearchId);
        }
        else if (operation == "coverExport")
        {
            bytes = await Task.Run(() => TagEditorService.ReadCoverBytes(targets[0].Path));
            if (bytes == null) throw new ArgumentException("文件没有内嵌封面。");
            var picker = new FileSavePicker { SuggestedFileName = "cover" };
            picker.FileTypeChoices.Add("封面图片", new List<string> { TagEditorService.SniffImageMime(bytes) == "image/png" ? ".png" : ".jpg" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.PickerHostHandle());
            var file = await picker.PickSaveFileAsync();
            if (file == null) return new { canceled = true };
            await File.WriteAllBytesAsync(file.Path, bytes);
            return new { ok = true };
        }
        else if (operation != "coverRemove") throw new ArgumentException("未知的封面操作。");

        if (bytes != null) { if (bytes.Length > 25_000_000) throw new ArgumentException("封面图片过大。"); using var stream = new MemoryStream(bytes); using var image = System.Drawing.Image.FromStream(stream); }

        await WebTagFileService.WaitWriteGateAsync();
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
            foreach (var t in targets) { MainWindow.InvalidateCoverCache(t.Path); host.OnTagsSaved(t.Path); }
        }
        finally { WebTagFileService.ReleaseWriteGate(); }
        return new { ok = true, count = targets.Count };
    }

    // ---- JSON 小工具 --------------------------------------------------------------------

    private static string ReadString(JsonElement msg, string property) =>
        msg.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement msg, string property, int fallback)
    {
        if (!msg.TryGetProperty(property, out var element))
            return fallback;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number))
            return number;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int parsed))
            return parsed;
        return fallback;
    }
}
