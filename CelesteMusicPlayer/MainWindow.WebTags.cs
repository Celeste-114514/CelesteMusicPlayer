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
        //   list  — explicitly added workspace rows (file-only sort fields read metadata), sorted and
        //           returned as one complete workspace when all=true; legacy paging stays compatible
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
            {
                var snapshot = new Dictionary<string, PlaylistItem>(tracks, StringComparer.Ordinal);
                return await Task.Run(() => WebTagsList(msg, snapshot));
            }

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
                    : WebTagFileService.ReadOne(job.Id, job.Path, job.Cover))
                .ToArray()).ConfigureAwait(true);

            return new { tracks = rows };
        }

        // ---- write --------------------------------------------------------------------------

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
