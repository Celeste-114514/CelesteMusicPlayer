using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // Web bridge for the tag editor.
        //
        // Same rule as the DSP bridge: reuse the native service instead of reimplementing tag I/O.
        // TagEditorService already handles every container format the player supports, so going
        // through it keeps the web page byte-identical in behaviour to the native tag panel.
        //
        // Operations:
        //   list  — rows straight from the in-memory library (no disk access at all), sorted and
        //           paged on this side because 4000+ rows do not fit in one reply
        //   read  — full tag fields for the ids on screen, read lazily (only visible rows ask)
        //   write — apply only the fields the caller sent, per file
        //
        // read/write touch the disk, so they run on a worker thread: a 40-file batch would
        // otherwise freeze the shell for a second on a slow drive. Everything the worker needs is
        // copied into plain tuples on the UI thread first — the JsonDocument and the library
        // dictionary must not be touched from that thread.
        internal async Task<object> WebTagsAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            string operation = "list";
            if (msg.TryGetProperty("operation", out var opElement) && opElement.ValueKind == JsonValueKind.String)
                operation = opElement.GetString() ?? "list";

            if (operation == "list")
                return WebTagsList(msg, tracks);

            if (operation == "read")
                return await WebTagsReadAsync(msg, tracks).ConfigureAwait(true);

            if (operation == "write")
                return await WebTagsWriteAsync(msg, tracks).ConfigureAwait(true);

            return await WebTagToolsAsync(operation, msg, tracks);
        }

        private object WebTagsList(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            string query = ReadString(msg, "query").Trim();
            int offset = Math.Max(0, ReadInt(msg, "offset", 0));
            int limit = Math.Clamp(ReadInt(msg, "limit", 300), 1, 2000);
            string sort = ReadString(msg, "sort");
            bool asc = !msg.TryGetProperty("asc", out var ascElement) || ascElement.ValueKind != JsonValueKind.False;

            IEnumerable<KeyValuePair<string, PlaylistItem>> items = tracks;
            if (msg.TryGetProperty("scopeIds", out var scope) && scope.ValueKind == JsonValueKind.Array && scope.GetArrayLength() > 0)
            {
                var wanted = scope.EnumerateArray().Take(2000).Select(id => id.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
                items = items.Where(pair => wanted.Contains(pair.Key));
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
            var rows = SortTagRows(filtered, sort, asc)
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

        /// <summary>
        /// Sorts by a library-backed column. Columns that only exist inside the file (BPM, composer,
        /// comment, lyrics…) are not sortable here — the page sorts those over what it has loaded.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, PlaylistItem>> SortTagRows(
            IEnumerable<KeyValuePair<string, PlaylistItem>> items, string sort, bool asc)
        {
            IOrderedEnumerable<KeyValuePair<string, PlaylistItem>> ordered;
            switch (sort)
            {
                case "title":
                    ordered = items.OrderBy(p => p.Value.Title, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "artist":
                    ordered = items.OrderBy(p => p.Value.Artist, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "albumArtist":
                    ordered = items.OrderBy(p => p.Value.AlbumArtist, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "album":
                    ordered = items.OrderBy(p => p.Value.Album, StringComparer.CurrentCultureIgnoreCase)
                                   .ThenBy(p => p.Value.Disc).ThenBy(p => p.Value.Track);
                    break;
                case "year":
                    ordered = items.OrderBy(p => p.Value.Year);
                    break;
                case "track":
                    ordered = items.OrderBy(p => p.Value.Track);
                    break;
                case "disc":
                    ordered = items.OrderBy(p => p.Value.Disc).ThenBy(p => p.Value.Track);
                    break;
                case "genre":
                    ordered = items.OrderBy(p => p.Value.Genre, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "file":
                    ordered = items.OrderBy(p => Path.GetFileName(p.Value.FilePath), StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "duration": ordered = items.OrderBy(p => p.Value.Duration); break;
                case "format": ordered = items.OrderBy(p => Path.GetExtension(p.Value.FilePath), StringComparer.CurrentCultureIgnoreCase); break;
                default:
                    ordered = items
                        .OrderBy(p => p.Value.Artist, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Value.Album, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Value.Disc)
                        .ThenBy(p => p.Value.Track)
                        .ThenBy(p => p.Value.Title, StringComparer.CurrentCultureIgnoreCase);
                    return asc ? ordered : ordered.ToArray().Reverse();
            }

            return asc ? ordered : ordered.Reverse();
        }

        private static bool Contains(string? text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;

        // ---- read ---------------------------------------------------------------------------

        private async Task<object> WebTagsReadAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
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
                    : WebTagReadOne(job.Id, job.Path, job.Cover))
                .ToArray()).ConfigureAwait(true);

            return new { tracks = rows };
        }

        private static object WebTagReadOne(string id, string path, bool withCover)
        {
            try
            {
                var model = TagEditorService.ReadTag(path);
                int bitDepth = 0, sampleRate = 0, bitrate = 0;
                try { using var file = TagLib.File.Create(path); bitDepth = file.Properties.BitsPerSample; sampleRate = file.Properties.AudioSampleRate; bitrate = file.Properties.AudioBitrate; } catch { }
                string cover = "";
                if (withCover)
                {
                    byte[]? bytes = TagEditorService.ReadCoverBytes(path);
                    if (bytes != null && bytes.Length > 0)
                        cover = "data:" + WebCoverMime(bytes) + ";base64," + Convert.ToBase64String(bytes);
                }

                return new
                {
                    id,
                    ok = true,
                    title = model.Title,
                    artist = model.Artist,
                    album = model.Album,
                    albumArtist = model.AlbumArtist,
                    year = model.Year == 0 ? "" : model.Year.ToString(CultureInfo.InvariantCulture),
                    track = model.Track == 0 ? "" : model.Track.ToString(CultureInfo.InvariantCulture),
                    trackCount = model.TrackCount == 0 ? "" : model.TrackCount.ToString(CultureInfo.InvariantCulture),
                    disc = model.Disc == 0 ? "" : model.Disc.ToString(CultureInfo.InvariantCulture),
                    discCount = model.DiscCount == 0 ? "" : model.DiscCount.ToString(CultureInfo.InvariantCulture),
                    bpm = model.Bpm,
                    genre = model.Genre,
                    composer = model.Composer,
                    comment = model.Comment,
                    lyrics = model.Lyrics,
                    syncedLyrics = model.SyncedLyrics,
                    bitDepth = bitDepth > 0 ? bitDepth + " bit" : "",
                    sampleRate = sampleRate > 0 ? (sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : "",
                    bitrate = bitrate > 0 ? bitrate + " kbps" : "",
                    fileSize = Math.Round(new FileInfo(path).Length / 1024.0 / 1024.0, 2),
                    cover
                };
            }
            catch (Exception error)
            {
                return new { id, ok = false, error = error.Message };
            }
        }

        private static string WebCoverMime(byte[] bytes)
            => TagEditorService.SniffImageMime(bytes);

        // ---- write --------------------------------------------------------------------------

        /// <summary>One file's write result, kept as a class so the caller can still read the fields afterwards.</summary>
        private sealed class WebTagWriteResult
        {
            public string Id = "";
            public bool Ok;
            public bool Skipped;
            public string Error = "";
            public Dictionary<string, string>? Fields;
        }

        private async Task<object> WebTagsWriteAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
        {
            // Two accepted shapes: { ids, fields } applies one field set to every id (the quick-edit
            // form), while { rows: [{ id, fields }] } gives each row its own set (inline cell edits).
            var jobs = new List<(string Id, string? Path, Dictionary<string, string> Fields)>();

            if (msg.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
            {
                if (rowsElement.GetArrayLength() > 2000) throw new ArgumentException("每次最多写入 2000 个文件，请缩小选择范围。");
                foreach (var rowElement in rowsElement.EnumerateArray().Take(2000))
                {
                    string id = ReadString(rowElement, "id");
                    var fields = ReadFieldMap(rowElement);
                    if (id.Length > 0 && fields.Count > 0)
                        jobs.Add((id, ResolveTagPath(tracks, id), fields));
                }
            }
            else
            {
                var fields = ReadFieldMap(msg);
                if (fields.Count == 0)
                    throw new ArgumentException("缺少要写入的字段。");
                if (!msg.TryGetProperty("ids", out var idsElement) || idsElement.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("缺少曲目编号。");
                if (idsElement.GetArrayLength() > 2000) throw new ArgumentException("每次最多写入 2000 个文件，请缩小选择范围。");

                foreach (var idElement in idsElement.EnumerateArray().Take(2000))
                {
                    string id = idElement.GetString() ?? "";
                    if (id.Length > 0)
                        jobs.Add((id, ResolveTagPath(tracks, id), fields));
                }
            }

            if (jobs.Count == 0)
                return new { results = Array.Empty<object>() };

            await _webTagWriteGate.WaitAsync();
            WebTagWriteResult[] results;
            try
            {
                results = await Task.Run(() => WebTagWriteTransaction(jobs));
            }
            finally { _webTagWriteGate.Release(); }

            // Keep the library rows in step, otherwise the table keeps showing the old text.
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
                OnTagsSaved(item.FilePath);
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

        private static string? ResolveTagPath(IReadOnlyDictionary<string, PlaylistItem> tracks, string id)
        {
            if (!tracks.TryGetValue(id, out var item) || item.IsRemote)
                return null;
            return item.FilePath;
        }

        /// <summary>Flattens { "title": "x", "year": "1999" } into a plain dictionary — JSON elements must not cross threads.</summary>
        private static Dictionary<string, string> ReadFieldMap(JsonElement container)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!container.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                return map;

            foreach (var property in fields.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.Null => "",
                    _ => throw new ArgumentException("标签值必须是文字或数字：" + property.Name)
                };
            }

            return map;
        }

        private static WebTagWriteResult WebTagWriteOne(string id, string path, Dictionary<string, string> fields)
        {
            var result = new WebTagWriteResult { Id = id, Fields = fields };
            try
            {
                var model = TagEditorService.ReadTag(path);
                if (!ApplyWebTagFields(model, fields))
                {
                    result.Ok = true;
                    result.Skipped = true;
                    return result;
                }

                var save = TagEditorService.TrySaveTag(path, model);
                result.Ok = save.Ok;
                result.Skipped = save.Skipped;
                result.Error = save.Error ?? "";
            }
            catch (Exception error)
            {
                result.Ok = false;
                result.Error = error.Message;
            }

            return result;
        }

        /// <summary>Copies only the fields present in the payload; anything missing is left untouched.</summary>
        private static bool ApplyWebTagFields(TagEditModel model, Dictionary<string, string> fields)
            => WebTagEditRules.Apply(model, fields);

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
}
