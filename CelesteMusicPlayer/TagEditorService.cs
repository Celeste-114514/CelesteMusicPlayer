using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    /// <summary>标签字段枚举。脏标记（Dirty）的最小粒度。</summary>
    public enum TagField
    {
        Title,
        Performers,
        Album,
        AlbumArtists,
        Genres,
        Composers,
        Year,
        Track,
        TrackCount,
        Disc,
        DiscCount,
        Comment,
        /// <summary>非同步歌词（ID3v2 的 USLT、Vorbis 的 UNSYNCEDLYRICS）—— mp3tag 里叫 UNSYNCEDLYRICS。</summary>
        Lyrics,
        /// <summary>同步歌词（ID3v2 的 SYLT、Vorbis 的 LYRICS）—— mp3tag 里就叫 LYRICS。</summary>
        SyncedLyrics,
        /// <summary>速度 BPM（ID3v2 TBPM / Vorbis BPM / MP4 tmpo）。没有标准属性，各格式各自存。</summary>
        Bpm,
    }

    /// <summary>
    /// 标签编辑模型（Kid3 式多值 + mp3tag 式 "; " 显示约定的合体）。
    /// 多值字段（Performers/Genres/AlbumArtists/Composers）是真数组，不再压扁；
    /// <see cref="Artist"/>/<see cref="Genre"/>/<see cref="AlbumArtist"/>/<see cref="Composer"/>
    /// 是给 UI 的兼容视图：读 = "; " 连接，写 = 按 ";" 拆分。
    /// 保存只写 <see cref="Dirty"/> 里标记过的字段（ReadTag/ReadTagRaw 读出来的模型默认不脏，
    /// 直接存回去 = 不写盘、不抖动 mtime）。
    /// </summary>
    public sealed class TagEditModel
    {
        public static readonly TagField[] AllFields =
        {
            TagField.Title, TagField.Performers, TagField.Album, TagField.AlbumArtists,
            TagField.Genres, TagField.Composers, TagField.Year, TagField.Track,
            TagField.TrackCount, TagField.Disc, TagField.DiscCount, TagField.Comment,
            TagField.Lyrics, TagField.SyncedLyrics, TagField.Bpm,
        };

        /// <summary>多值字段的显示分隔符（mp3tag 约定）。</summary>
        public const string Separator = "; ";

        public string Title { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        /// <summary>非同步歌词（mp3tag：UNSYNCEDLYRICS；ID3v2 = USLT）。</summary>
        public string Lyrics { get; set; } = string.Empty;

        /// <summary>同步歌词（mp3tag：LYRICS；ID3v2 = SYLT）。带时间轴的那种。</summary>
        public string SyncedLyrics { get; set; } = string.Empty;

        /// <summary>
        /// 速度 BPM。各格式存法不统一（ID3v2=TBPM、Vorbis=BPM、MP4=tmpo），
        /// 而且有人在里面写非数字，所以用文本存，写盘时按各格式自己的口味转换。
        /// </summary>
        public string Bpm { get; set; } = string.Empty;

        /// <summary>艺术家（多值）。ID3=TPE1 多帧值 / Vorbis=多 ARTIST / MP4=多 ©ART。</summary>
        public string[] Performers { get; set; } = Array.Empty<string>();

        /// <summary>流派（多值）。</summary>
        public string[] Genres { get; set; } = Array.Empty<string>();

        /// <summary>专辑艺术家（多值）。</summary>
        public string[] AlbumArtists { get; set; } = Array.Empty<string>();

        /// <summary>作曲者（多值）。</summary>
        public string[] Composers { get; set; } = Array.Empty<string>();

        public uint Year { get; set; }

        public uint Track { get; set; }

        /// <summary>曲目总数（"5/10" 的 10；0=未设置）。</summary>
        public uint TrackCount { get; set; }

        public uint Disc { get; set; }

        /// <summary>碟片总数（"2/3" 的 3；0=未设置）。</summary>
        public uint DiscCount { get; set; }

        // ---------- UI 兼容视图（mp3tag 语义：显示 "; " 连接，保存按 ";" 拆分） ----------

        public string Artist
        {
            get => Join(Performers);
            set => Performers = Split(value);
        }

        public string Genre
        {
            get => Join(Genres);
            set => Genres = Split(value);
        }

        public string AlbumArtist
        {
            get => Join(AlbumArtists);
            set => AlbumArtists = Split(value);
        }

        public string Composer
        {
            get => Join(Composers);
            set => Composers = Split(value);
        }

        // ---------- 脏标记 ----------

        /// <summary>自打开/读取后被改过的字段。空 = 没有需要写盘的东西。</summary>
        public HashSet<TagField> Dirty { get; } = new();

        public bool IsDirty(TagField field) => Dirty.Contains(field);

        public void MarkDirty(TagField field) => Dirty.Add(field);

        /// <summary>整体回滚/表单整体保存用：把全部字段标记为待写。</summary>
        public void MarkAllDirty()
        {
            foreach (TagField field in AllFields)
            {
                Dirty.Add(field);
            }
        }

        // ---------- 连接/拆分约定 ----------

        public static string Join(System.Collections.Generic.IEnumerable<string>? values)
        {
            if (values == null)
            {
                return string.Empty;
            }
            return string.Join(Separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        public static string[] Split(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }
            return text
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }
    }

    /// <summary>保存结果。Ok=成功（含 Skipped 的「无改动未写盘」）；失败时 Error 带可直接展示的原因。</summary>
    public sealed class TagSaveResult
    {
        public bool Ok { get; init; }

        /// <summary>true = 没有任何脏字段，文件没动。</summary>
        public bool Skipped { get; init; }

        /// <summary>失败原因（可直接显示给用户；成功/跳过为 null）。</summary>
        public string? Error { get; init; }

        public static TagSaveResult Success(bool skipped) => new() { Ok = true, Skipped = skipped };

        public static TagSaveResult Fail(string error) => new() { Ok = false, Error = error };
    }

    public static class TagEditorService
    {
        private static readonly Regex ArtistTitleRegex = new(
            @"^(?<artist>.+?)\s*[-–—]\s*(?<title>.+)$",
            RegexOptions.Compiled);

        private static readonly Regex TitleByArtistRegex = new(
            @"^(?<title>.+?)\s*[-–—]\s*(?<artist>.+)$",
            RegexOptions.Compiled);

        public static TagEditModel ReadTag(string path)
        {
            var model = ReadTagRaw(path);
            if (string.IsNullOrWhiteSpace(model.Title) && string.IsNullOrWhiteSpace(model.Artist))
            {
                TagEditModel fromName = TryParseTagsFromFileName(Path.GetFileName(path));
                if (!string.IsNullOrWhiteSpace(fromName.Title))
                {
                    model.Title = fromName.Title;
                }

                if (!string.IsNullOrWhiteSpace(fromName.Artist))
                {
                    model.Performers = fromName.Performers;
                }
            }

            return model;
        }

        /// <summary>
        /// 写标签（只写 <see cref="TagEditModel.Dirty"/> 里的字段；一个脏字段都没有 = 不写盘）。
        /// 返回 false = 没写成（路径/模型为空，或写盘抛异常，异常已进日志）。
        /// </summary>
        public static bool SaveTag(string path, TagEditModel model)
        {
            return TrySaveTag(path, model).Ok;
        }

        /// <summary>
        /// 写标签并带回可直接展示的失败原因（TTA 等 TagLib 不支持的格式在这里给出人话）。
        /// 标签板块的快照整体回滚靠 Ok 判断成败；旧窗口调用方忽略 Error 即可。
        /// </summary>
        public static TagSaveResult TrySaveTag(string path, TagEditModel model)
        {
            if (string.IsNullOrWhiteSpace(path) || model == null)
            {
                return TagSaveResult.Fail("路径或标签数据为空，未写入。");
            }

            if (model.Dirty.Count == 0)
            {
                // 一个字段都没改：不碰文件（避免 mtime 抖动让批量工具误判「已改动」）
                return TagSaveResult.Success(skipped: true);
            }

            // TTA：整份写 APEv2（重写文件 + 原子替换），失败带原因返回
            if (TtaTagAccess.IsTtaPath(path))
            {
                return TtaTagAccess.Write(path, model)
                    ? TagSaveResult.Success(false)
                    : TagSaveResult.Fail("TTA 标签写入失败（文件可能被占用或磁盘满），详见日志。");
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                TagLib.Tag tag = file.Tag;

                if (model.IsDirty(TagField.Title))
                {
                    tag.Title = model.Title ?? string.Empty;
                }

                if (model.IsDirty(TagField.Album))
                {
                    tag.Album = model.Album ?? string.Empty;
                }

                if (model.IsDirty(TagField.Performers))
                {
                    tag.Performers = model.Performers ?? Array.Empty<string>();
                }

                if (model.IsDirty(TagField.AlbumArtists))
                {
                    tag.AlbumArtists = model.AlbumArtists ?? Array.Empty<string>();
                }

                if (model.IsDirty(TagField.Genres))
                {
                    tag.Genres = model.Genres ?? Array.Empty<string>();
                }

                if (model.IsDirty(TagField.Composers))
                {
                    tag.Composers = model.Composers ?? Array.Empty<string>();
                }

                if (model.IsDirty(TagField.Year))
                {
                    tag.Year = model.Year;
                }

                if (model.IsDirty(TagField.Track))
                {
                    tag.Track = model.Track;
                }

                if (model.IsDirty(TagField.TrackCount))
                {
                    tag.TrackCount = model.TrackCount;
                }

                if (model.IsDirty(TagField.Disc))
                {
                    tag.Disc = model.Disc;
                }

                if (model.IsDirty(TagField.DiscCount))
                {
                    tag.DiscCount = model.DiscCount;
                }

                if (model.IsDirty(TagField.Comment))
                {
                    tag.Comment = model.Comment ?? string.Empty;
                }

                if (model.IsDirty(TagField.Lyrics))
                {
                    tag.Lyrics = model.Lyrics ?? string.Empty;
                }

                if (model.IsDirty(TagField.Bpm))
                {
                    WriteBpm(file, model.Bpm ?? string.Empty);
                }

                // 同步歌词（带时间轴）只做显示，不做写盘：写它需要按帧重建时间轴事件，
                // 风险大又极少用到 —— 面板里那个框标了只读，避免改了不生效的错觉。
                // if (model.IsDirty(TagField.SyncedLyrics)) { ... }

                // 按设置把 ID3v2.4 降成 2.3（用户设置，默认关；只在真写盘时执行）
                if (AppSettingsStore.Load().WriteId3v23
                    && (file.TagTypes & TagLib.TagTypes.Id3v2) != 0
                    && file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    id3.Version = 3;
                }

                file.Save();
                return TagSaveResult.Success(skipped: false);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.cs", caught);
                return TagSaveResult.Fail(DescribeSaveFailure(path, caught));
            }
        }

        /// <summary>把 TagLib 的异常翻译成用户能看懂的原因。</summary>
        private static string DescribeSaveFailure(string path, Exception caught)
        {
            string ext = string.IsNullOrWhiteSpace(path)
                ? ""
                : Path.GetExtension(path).ToLowerInvariant();
            if (caught is TagLib.UnsupportedFormatException)
            {
                return $"播放器使用的标签引擎不支持 {ext} 格式的标签读写，这个文件改不了。";
            }
            if (caught is IOException)
            {
                return "文件正被其它程序占用或无法写入，请关闭其它使用该文件的程序后重试。";
            }
            return $"写入标签失败：{caught.Message}";
        }

        public static void SaveEmbeddedLyrics(string path, string lyrics)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                file.Tag.Lyrics = lyrics ?? string.Empty;
                file.Save();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.cs", caught); }
        }

        public static void ClearEmbeddedLyrics(string path)
        {
            SaveEmbeddedLyrics(path, string.Empty);
        }

        /// <summary>读取文件内嵌封面图片数据（无封面返回 null）。</summary>
        public static byte[]? ReadCoverBytes(string path)
        {
            if (TtaTagAccess.IsTtaPath(path))
            {
                return TtaTagAccess.ReadCoverBytes(path);
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                foreach (TagLib.IPicture pic in file.Tag.Pictures ?? Array.Empty<TagLib.IPicture>())
                {
                    if (pic?.Type == TagLib.PictureType.FrontCover || pic?.Data?.Count > 0)
                    {
                        return pic.Data?.Data;
                    }
                }

                return (file.Tag.Pictures?[0]?.Data?.Count > 0)
                    ? file.Tag.Pictures[0].Data.Data
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>替换（或写入）前置封面图片字节。保留文件里的其它图片（如后封）。返回是否成功。</summary>
        public static bool SetCoverBytes(string path, byte[] bytes)
        {
            if (TtaTagAccess.IsTtaPath(path))
            {
                return TtaTagAccess.SetCoverBytes(path, bytes);
            }

            if (string.IsNullOrWhiteSpace(path) || bytes == null || bytes.Length == 0)
            {
                return false;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                var others = (file.Tag.Pictures ?? Array.Empty<TagLib.IPicture>())
                    .Where(p => p != null && p.Type != TagLib.PictureType.FrontCover)
                    .ToList();
                var front = new TagLib.Picture(new TagLib.ByteVector(bytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    // MIME 按文件头猜，不再硬编码 image/jpeg（PNG 封面被标成 JPEG 会裂图）
                    MimeType = SniffImageMime(bytes),
                    Description = "Cover",
                };
                others.Add(front);
                file.Tag.Pictures = others.ToArray();
                file.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>清除文件内嵌封面。返回是否成功。</summary>
        public static bool ClearCover(string path)
        {
            if (TtaTagAccess.IsTtaPath(path))
            {
                return TtaTagAccess.ClearCover(path);
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                file.Tag.Pictures = Array.Empty<TagLib.IPicture>();
                file.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把文件内嵌封面提取保存到指定路径。返回是否成功。</summary>
        public static bool ExtractCover(string path, string destPath)
        {
            byte[]? bytes = ReadCoverBytes(path);
            if (bytes == null || bytes.Length == 0 || string.IsNullOrWhiteSpace(destPath))
            {
                return false;
            }

            try
            {
                File.WriteAllBytes(destPath, bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按魔术字节猜图片 MIME（PNG/JPEG/GIF/BMP/WEBP）；猜不出按 image/jpeg。</summary>
        public static string SniffImageMime(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12)
            {
                return "image/jpeg";
            }

            if (bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            {
                return "image/png";
            }

            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                return "image/gif";
            }

            if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                return "image/bmp";
            }

            if (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "image/webp";
            }

            return "image/jpeg";
        }

        /// <summary>
        /// 读磁盘上的真实标签（**不做文件名兜底**）。批量工具的快照/回滚和
        /// 「只填空」判断必须用真值——ReadTag 在标题+艺术家都空时会用文件名
        /// 解析结果补上，那对编辑面板是方便，对批量工具会把「没改动」误判成
        /// 「已改过」从而整个跳过。读取失败返回全空模型（调用方按无标签处理）。
        /// </summary>
        public static TagEditModel ReadTagRaw(string path)
        {
            // TTA：TagLib 没有容器解析器（File.Create 直接抛 UnsupportedFormatException），
            // 走 TtaTagAccess 直接读文件头尾的 ID3v2 / APEv2 标签（2026-10-14 方案 1）。
            if (TtaTagAccess.IsTtaPath(path))
            {
                return TtaTagAccess.Read(path);
            }

            var model = new TagEditModel();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return model;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                TagLib.Tag tag = file.Tag;
                model.Title = tag.Title ?? string.Empty;
                model.Performers = CleanArray(tag.Performers);
                model.Album = tag.Album ?? string.Empty;
                model.AlbumArtists = CleanArray(tag.AlbumArtists);
                model.Year = tag.Year;
                model.Track = tag.Track;
                model.TrackCount = tag.TrackCount;
                model.Disc = tag.Disc;
                model.DiscCount = tag.DiscCount;
                model.Genres = CleanArray(tag.Genres);
                model.Composers = CleanArray(tag.Composers);
                model.Comment = tag.Comment ?? string.Empty;
                model.Lyrics = tag.Lyrics ?? string.Empty;          // 非同步歌词（USLT）
                model.SyncedLyrics = ReadSyncedLyrics(file);        // 同步歌词（SYLT / LYRICS）
                model.Bpm = ReadBpm(file);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.cs", caught); }

            return model;
        }

        // =====================================================================
        // 两个"没有统一属性"的字段：BPM 和同步歌词
        //
        // TagLib 的 Tag 上没有 BeatsPerMinute / SyncedLyrics 这种跨格式属性，
        // 只能各自去格式自己的帧里取（ID3v2 帧 / Vorbis 注释）。读不到就是空，
        // 不猜、不报错打断整个读取流程。
        // =====================================================================

        /// <summary>读 BPM：ID3v2 的 TBPM 帧，或 Vorbis 注释里的 BPM。读不到返回空串。</summary>
        private static string ReadBpm(TagLib.File file)
        {
            try
            {
                if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    foreach (TagLib.Id3v2.Frame frame in id3.GetFrames("TBPM"))
                    {
                        if (frame is TagLib.Id3v2.TextInformationFrame text && text.Text.Length > 0)
                        {
                            return string.Join("; ", text.Text);
                        }
                    }
                }

                if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    string[] values = xiph.GetField("BPM");
                    if (values.Length > 0)
                    {
                        return values[0];
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.ReadBpm", caught);
            }

            return string.Empty;
        }

        /// <summary>
        /// 写 BPM：ID3v2 写 TBPM 帧、Vorbis 写 BPM 注释。空串 = 把这个字段删掉
        /// （而不是写个空帧进去，免得每次打开都看到一个空壳）。
        /// </summary>
        private static void WriteBpm(TagLib.File file, string bpm)
        {
            try
            {
                if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    id3.RemoveFrames("TBPM");
                    if (!string.IsNullOrWhiteSpace(bpm))
                    {
                        id3.SetTextFrame("TBPM", bpm);
                    }
                }

                if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    xiph.SetField("BPM", bpm ?? string.Empty);
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.WriteBpm", caught);
            }
        }

        /// <summary>
        /// 读同步歌词（mp3tag 里叫 LYRICS）：ID3v2 的 SYLT 帧（带时间轴）或 Vorbis 的
        /// LYRICS 注释。SYLT 里的每个事件带一个时间戳，拼成 [mm:ss.xx] 歌词 的形式给人看。
        /// </summary>
        private static string ReadSyncedLyrics(TagLib.File file)
        {
            try
            {
                if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    foreach (TagLib.Id3v2.Frame frame in id3.GetFrames("SYLT"))
                    {
                        if (frame is TagLib.Id3v2.SynchronisedLyricsFrame sylt)
                        {
                            System.Text.StringBuilder sb = new();
                            foreach (TagLib.Id3v2.SynchedText ev in sylt.Text)
                            {
                                sb.Append('[').Append(FormatSyncedTime(ev.Time)).Append(']')
                                  .AppendLine(ev.Text);
                            }

                            return sb.ToString().TrimEnd();
                        }
                    }
                }

                if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    string[] values = xiph.GetField("LYRICS");
                    if (values.Length > 0)
                    {
                        return string.Join("\n", values);
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("TagEditorService.ReadSyncedLyrics", caught);
            }

            return string.Empty;
        }

        /// <summary>毫秒 → [mm:ss.xx]（同步歌词时间戳的显示格式）。</summary>
        private static string FormatSyncedTime(long milliseconds)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(milliseconds);
            return t.ToString(@"mm\:ss\.ff", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>去掉空元素（TagLib 偶尔返回含空串的数组， Join 显示时会出现 "; ;"）。</summary>
        private static string[] CleanArray(string[]? values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<string>();
            }
            return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        }

        public static TagEditModel TryParseTagsFromFileName(string fileName)
        {
            var model = new TagEditModel();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return model;
            }

            string name = Path.GetFileNameWithoutExtension(fileName.Trim());
            Match match = ArtistTitleRegex.Match(name);
            if (match.Success)
            {
                model.Performers = TagEditModel.Split(match.Groups["artist"].Value.Trim());
                model.Title = match.Groups["title"].Value.Trim();
                return model;
            }

            match = TitleByArtistRegex.Match(name);
            if (match.Success)
            {
                model.Title = match.Groups["title"].Value.Trim();
                model.Performers = TagEditModel.Split(match.Groups["artist"].Value.Trim());
                return model;
            }

            model.Title = name;
            return model;
        }
    }
}
