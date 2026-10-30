using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>单文件写结果。刻意不用 record：事务回滚和曲库同步写完后还要读字段。</summary>
    public sealed class WebTagWriteResult
    {
        public string Id = "";
        public bool Ok;
        public bool Skipped;
        public string Error = "";
        public Dictionary<string, string>? Fields;
    }

    /// <summary>
    /// 网页标签编辑的**文件 I/O 核心**（2026-10-30 从 MainWindow 搬出）：读单文件（含封面）、
    /// 批量写事务（先快照、校验全部通过才落盘、失败整组回滚）。
    /// 只有脱离界面，"写一半失败要还原"这套语义才能用 fixture 文件直接测。
    /// 零 UI、零播放；所有会写盘的入口（标签写、歌词保存、重命名、封面）共用同一把写闸门。
    /// </summary>
    public static class WebTagFileService
    {
        private static readonly SemaphoreSlim WriteGate = new(1, 1);

        /// <summary>批量写标签：串行（同一时间只有一批在写），事务语义见 <see cref="WriteTransaction"/>。</summary>
        public static async Task<WebTagWriteResult[]> WriteAsync(
            List<(string Id, string? Path, Dictionary<string, string> Fields)> jobs)
        {
            await WriteGate.WaitAsync().ConfigureAwait(true);
            try { return await Task.Run(() => WriteTransaction(jobs)).ConfigureAwait(true); }
            finally { WriteGate.Release(); }
        }

        /// <summary>歌词保存 / 重命名 / 封面操作与批量写共用这把闸门，别绕开。</summary>
        public static Task WaitWriteGateAsync() => WriteGate.WaitAsync();

        /// <summary>释放写闸门。必须放在 finally 里。</summary>
        public static void ReleaseWriteGate() => WriteGate.Release();

        /// <summary>
        /// 整组写事务（原 MainWindow.WebTagWriteTransaction，逐行一致）：先给每个文件拍快照、
        /// 把所有数值字段先校验一遍（坏值在任何文件落盘前就被拒），然后逐个写；
        /// 任何一步失败，已写入的文件用快照整体还原 —— 与原生标签面板的组写行为一致。
        /// 只快照可写元数据，不替换、不截断音频数据。
        /// </summary>
        public static WebTagWriteResult[] WriteTransaction(
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
                    WebTagEditRules.Apply(TagEditorService.ReadTagRaw(job.Path), job.Fields);
                }
                foreach (var job in jobs)
                {
                    attempted.Add(job.Path!);
                    var result = WriteOne(job.Id, job.Path!, job.Fields);
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

        /// <summary>读一个文件的完整标签字段（可选带封面 base64）。读不了返回 ok=false 与原因。</summary>
        public static object ReadOne(string id, string path, bool withCover)
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
                        cover = "data:" + SniffImageMime(bytes) + ";base64," + Convert.ToBase64String(bytes);
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

        public static string SniffImageMime(byte[] bytes) => TagEditorService.SniffImageMime(bytes);

        public static string? ResolvePath(IReadOnlyDictionary<string, PlaylistItem> tracks, string id)
        {
            if (!tracks.TryGetValue(id, out var item) || item.IsRemote)
                return null;
            return item.FilePath;
        }

        /// <summary>把 { "title": "x", "year": "1999" } 摊平成普通字典 —— JSON 元素不许跨线程。</summary>
        public static Dictionary<string, string> ReadFieldMap(System.Text.Json.JsonElement container)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!container.TryGetProperty("fields", out var fields) || fields.ValueKind != System.Text.Json.JsonValueKind.Object)
                return map;

            foreach (var property in fields.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => property.Value.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Number => property.Value.ToString(),
                    System.Text.Json.JsonValueKind.Null => "",
                    _ => throw new ArgumentException("标签值必须是文字或数字：" + property.Name)
                };
            }

            return map;
        }

        public static WebTagWriteResult WriteOne(string id, string path, Dictionary<string, string> fields)
        {
            var result = new WebTagWriteResult { Id = id, Fields = fields };
            try
            {
                var model = TagEditorService.ReadTag(path);
                if (!WebTagEditRules.Apply(model, fields))
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
    }
}
