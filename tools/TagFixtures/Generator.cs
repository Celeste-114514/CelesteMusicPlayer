using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TagFixtures;

/// <summary>
/// 标签编辑测试样本生成器。
/// 产物：一个目录下的 100 个多格式微型音频（含毒样本标签组合）+ manifest.json
/// （逐文件记录「想写什么 / 实际落盘什么 / 格式特异说明」）。
/// 同一套逻辑被 tools/TagFixtures（控制台，手动跑）与 tools/TagEditTests
/// （xunit，跑测试时生成到临时目录）复用。
/// </summary>
public static class Generator
{
    public sealed class Options
    {
        public string OutDir = "_tagfixtures";
        public string? FfmpegPath;
        public bool Force = true;
    }

    private sealed record Fmt(string Ext, string Name, string[] CodecArgs);

    // 10 种格式 × 10 个毒样本档位 = 100 个文件。
    // 编码参数对齐 app 的 ffmpeg 可用清单（libmp3lame/flac/aac/libvorbis/libopus/wavpack/wmav2/tta/mp2/pcm）。
    private static readonly Fmt[] Formats =
    {
        new(".mp3",  "MP3 有损",    new[] { "-c:a", "libmp3lame", "-b:a", "32k" }),
        new(".flac", "FLAC 无损",   new[] { "-c:a", "flac", "-compression_level", "0" }),
        new(".wav",  "WAV 未压缩",  new[] { "-c:a", "pcm_s16le" }),
        new(".m4a",  "M4A/AAC",     new[] { "-c:a", "aac", "-b:a", "32k" }),
        new(".ogg",  "OGG Vorbis",  new[] { "-c:a", "libvorbis", "-b:a", "32k" }),
        new(".opus", "OPUS",        new[] { "-c:a", "libopus", "-b:a", "24k" }),
        new(".wma",  "WMA 有损",    new[] { "-c:a", "wmav2", "-b:a", "32k" }),
        new(".wv",   "WavPack",     new[] { "-c:a", "wavpack" }),
        new(".tta",  "TTA 无损",    new[] { "-c:a", "tta" }),
        new(".mp2",  "MP2 有损",    new[] { "-c:a", "mp2", "-b:a", "32k" }),
    };

    private const int ProfileCount = 10;

    /// <summary>8x8 纯红 PNG（zlib stored 块手搓，74 字节）——P4 前置封面。</summary>
    private const string CoverRedPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mM4ISeHFTEMLQkAkL9BAc9woTwAAAAASUVORK5CYII=";

    /// <summary>8x8 纯蓝 PNG——P4 后置封面。</summary>
    private const string CoverBluePng =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mOQkzuBFTEMLQkAO79BAQ68oXkAAAAASUVORK5CYII=";

    /// <summary>前置封面 PNG 字节（测试工程直接取用，与 fixtures 里嵌的完全一致）。</summary>
    public static byte[] CoverRedPngBytes => Convert.FromBase64String(CoverRedPng);

    /// <summary>后置封面 PNG 字节。</summary>
    public static byte[] CoverBluePngBytes => Convert.FromBase64String(CoverBluePng);

    private static readonly string[] ProfileNames =
    {
        "多值基本盘", "自定义字段", "ID3版本混合", "封面双图", "空缺失",
        "长文本特殊字符", "多语言歌词", "碟片曲目总数", "富字段", "边界值",
    };

    public sealed class ManifestFile
    {
        public string File { get; set; } = "";
        public int Index { get; set; }
        public string Format { get; set; } = "";
        public string FormatName { get; set; } = "";
        public int Profile { get; set; }
        public string ProfileName { get; set; } = "";
        public Dictionary<string, string> Expected { get; set; } = new();
        public Dictionary<string, string> Verified { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    public sealed class Manifest
    {
        public string GeneratedUtc { get; set; } = "";
        public string Ffmpeg { get; set; } = "";
        public int FileCount { get; set; }
        public List<ManifestFile> Files { get; set; } = new();
    }

    public static async Task<Manifest> GenerateAsync(Options options)
    {
        string? ffmpeg = ResolveFfmpeg(options.FfmpegPath);
        if (ffmpeg == null)
        {
            throw new InvalidOperationException(
                "找不到 ffmpeg：请用 --ffmpeg 指定，或设置 TAG_FIXTURES_FFMPEG，" +
                "或确保 PATH 里有 ffmpeg。仓库内位置：CelesteMusicPlayer/Assets/ffmpeg/ffmpeg.exe");
        }

        string outDir = Path.GetFullPath(options.OutDir);
        if (options.Force && Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);

        // 两张微型封面图（P4 用）。仓库内置 ffmpeg 是裁剪版（无 scale 滤镜，产不了图），
        // 这里直接内嵌两张 8x8 纯色 PNG（zlib stored 块手搓，74 字节）——
        // 封面毒样本要的是「两张图 + 不同角色 + 字节级 round-trip」，不需要真实画面。
        byte[] jpgBytes = Convert.FromBase64String(CoverRedPng);
        byte[] pngBytes = Convert.FromBase64String(CoverBluePng);

        var manifest = new Manifest
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Ffmpeg = ffmpeg,
        };

        int index = 0;
        foreach (Fmt fmt in Formats)
        {
            for (int p = 1; p <= ProfileCount; p++)
            {
                index++;
                var entry = await GenerateOneAsync(ffmpeg, outDir, fmt, p, index, jpgBytes, pngBytes);
                manifest.Files.Add(entry);
                Console.WriteLine($"[{index:000}] {entry.File}  {fmt.Name} P{p} {ProfileNames[p - 1]}  " +
                                  $"{(entry.Notes.Count == 0 ? "ok" : "notes:" + string.Join("|", entry.Notes))}");
            }
        }

        manifest.FileCount = manifest.Files.Count;
        string manifestPath = Path.Combine(outDir, "manifest.json");
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, json), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"完成：{manifest.FileCount} 个文件 + manifest.json → {outDir}");
        int withNotes = manifest.Files.Count(f => f.Notes.Count > 0);
        if (withNotes > 0)
        {
            Console.WriteLine($"其中 {withNotes} 个文件有格式特异说明（见 manifest.json 的 notes）。");
        }
        return manifest;
    }

    private static async Task<ManifestFile> GenerateOneAsync(
        string ffmpeg, string outDir, Fmt fmt, int profile, int index,
        byte[] jpgBytes, byte[] pngBytes)
    {
        string slug = ProfileNames[profile - 1];
        string stem = $"f{index:000}_{fmt.Ext.TrimStart('.')}_p{profile:00}_{slug}";
        string path = Path.Combine(outDir, stem + fmt.Ext);

        var entry = new ManifestFile
        {
            File = stem + fmt.Ext,
            Index = index,
            Format = fmt.Ext,
            FormatName = fmt.Name,
            Profile = profile,
            ProfileName = slug,
        };

        // 0.5 秒 22050Hz 单声道正弦——足够 TagLib 解析、体积足够小
        // 注意：ProcessStartInfo 不过 shell，lavfi 源串不要加引号
        string args =
            $"-y -hide_banner -loglevel error -f lavfi " +
            $"-i sine=frequency={300 + index * 7}:duration=0.5:sample_rate=22050 " +
            $"-ac 1 {string.Join(" ", fmt.CodecArgs)} \"{path}\"";
        int rc = await RunAsync(ffmpeg, args);
        if (rc != 0 || !File.Exists(path))
        {
            entry.Notes.Add($"ffmpeg 生成失败 rc={rc}");
            return entry;
        }

        // 写毒样本标签；按字段组 try/catch，落地情况全部记进 manifest
        try
        {
            using TagLib.File file = TagLib.File.Create(path);
            TagLib.Tag tag = file.Tag;
            TagLib.Id3v2.Tag? id3 = file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;

            switch (profile)
            {
                case 1: ApplyMultiValue(tag, id3, entry); break;
                case 2: ApplyCustomFields(tag, id3, file, entry); break;
                case 3: ApplyId3VersionMix(tag, id3, file, entry); break;
                case 4: ApplyCover(tag, jpgBytes, pngBytes, entry); break;
                case 5: ApplyEmpty(tag, entry); break;
                case 6: ApplyLongUnicode(tag, entry); break;
                case 7: ApplyLyricsMultiLang(tag, id3, entry); break;
                case 8: ApplyDiscTrackTotals(tag, entry); break;
                case 9: ApplyRichFields(tag, id3, entry); break;
                case 10: ApplyBoundary(tag, entry); break;
            }

            file.Save();
        }
        catch (Exception ex)
        {
            entry.Notes.Add($"TagLib 打开/保存失败：{ex.GetType().Name}: {ex.Message}");
            return entry;
        }

        // 回读校验：把 TagLib 自己都能 round-trip 的真值记进 manifest，
        // 测试工程以此为新后端「不许丢」的地面真值。
        try
        {
            using TagLib.File file = TagLib.File.Create(path);
            TagLib.Tag tag = file.Tag;
            entry.Verified["title"] = tag.Title ?? "";
            entry.Verified["performers"] = string.Join("; ", tag.Performers ?? Array.Empty<string>());
            entry.Verified["performers_count"] = (tag.Performers ?? Array.Empty<string>()).Length.ToString();
            entry.Verified["genres"] = string.Join("; ", tag.Genres ?? Array.Empty<string>());
            entry.Verified["genres_count"] = (tag.Genres ?? Array.Empty<string>()).Length.ToString();
            entry.Verified["album"] = tag.Album ?? "";
            entry.Verified["albumartists"] = string.Join("; ", tag.AlbumArtists ?? Array.Empty<string>());
            entry.Verified["composers"] = string.Join("; ", tag.Composers ?? Array.Empty<string>());
            entry.Verified["year"] = tag.Year.ToString();
            entry.Verified["track"] = tag.Track.ToString();
            entry.Verified["trackcount"] = tag.TrackCount.ToString();
            entry.Verified["disc"] = tag.Disc.ToString();
            entry.Verified["disccount"] = tag.DiscCount.ToString();
            entry.Verified["comment"] = tag.Comment ?? "";
            entry.Verified["lyrics_len"] = (tag.Lyrics ?? "").Length.ToString();
            entry.Verified["pictures"] = (tag.Pictures ?? Array.Empty<TagLib.IPicture>()).Length.ToString();
            entry.Verified["tagtypes"] = file.TagTypes.ToString();
        }
        catch (Exception ex)
        {
            entry.Notes.Add($"TagLib 回读失败：{ex.GetType().Name}: {ex.Message}");
        }

        return entry;
    }

    // ---------- 毒样本档位 ----------

    private static void ApplyMultiValue(TagLib.Tag tag, TagLib.Id3v2.Tag? id3, ManifestFile e)
    {
        tag.Title = "多值基本盘";
        tag.Album = "多值测试辑";
        tag.Performers = new[] { "谷冈音符", "星野铃" };
        tag.Genres = new[] { "摇滚", "电子" };
        tag.AlbumArtists = new[] { "合辑策划" };
        tag.Composers = new[] { "作曲者甲", "作曲者乙" };
        tag.Year = 2021;
        tag.Track = 7;
        tag.TrackCount = 12;
        tag.Comment = "第一条评论";
        tag.Lyrics = "第一行歌词\n第二行歌词";
        e.Expected["performers"] = "谷冈音符; 星野铃";
        e.Expected["genres"] = "摇滚; 电子";
    }

    private static void ApplyCustomFields(TagLib.Tag tag, TagLib.Id3v2.Tag? id3, TagLib.File file, ManifestFile e)
    {
        tag.Title = "自定义字段";
        tag.Album = "自定义字段辑";
        tag.Performers = new[] { "目录号乐队" };
        tag.Genres = new[] { "实验" };
        tag.Year = 2020;

        bool done = false;
        if (id3 != null)
        {
            try
            {
                id3.SetUserTextAsString("CATALOGNUMBER", "TL-2021-001");
                id3.SetUserTextAsString("测试目录号", "自定义值");
                done = true;
            }
            catch (Exception ex) { e.Notes.Add($"TXXX 写入失败：{ex.Message}"); }
        }

        if (!done)
        {
            // 非 ID3 容器：按各自的自定义字段机制写入
            try
            {
                if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
                {
                    ape.AddValue("CATALOGNUMBER", "TL-2021-001");
                    ape.AddValue("测试目录号", "自定义值");
                    done = true;
                }
                else if (file.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
                {
                    asf.SetDescriptorStrings(new[] { "测试目录号" }, new[] { "TL-2021-001" });
                    done = true;
                }
            }
            catch (Exception ex) { e.Notes.Add($"自定义字段写入失败：{ex.Message}"); }
        }

        if (!done)
        {
            e.Notes.Add("该格式无公开自定义字段 API（Xiph/RIFF/MP4 已知限制），仅写标准字段");
        }
        e.Expected["custom"] = "CATALOGNUMBER=TL-2021-001";
    }

    private static void ApplyId3VersionMix(TagLib.Tag tag, TagLib.Id3v2.Tag? id3, TagLib.File file, ManifestFile e)
    {
        tag.Title = "ID3版本混合超长标题测试一二三四五六七八九十";
        tag.Album = "版本混合辑";
        tag.Performers = new[] { "版本甲", "版本乙" };
        tag.Genres = new[] { "爵士", "蓝调" };
        tag.Year = 2019;

        if (id3 != null)
        {
            try
            {
                id3.Version = 3;
                // 同时保留一个 ID3v1：v1 字段 30 字节截断本身就是毒点
                if (file.GetTag(TagLib.TagTypes.Id3v1, true) is TagLib.Id3v1.Tag v1)
                {
                    v1.Title = tag.Title;
                    v1.Performers = tag.Performers;
                    v1.Album = tag.Album;
                    v1.Year = tag.Year;
                    v1.Track = tag.Track;
                    v1.Genres = tag.Genres;
                }
            }
            catch (Exception ex) { e.Notes.Add($"ID3v2.3/v1 设置失败：{ex.Message}"); }
        }
        else
        {
            e.Notes.Add("非 ID3 容器，无版本概念，退化为多值样本");
        }
        e.Expected["id3"] = "v2.3 + v1";
    }

    private static void ApplyCover(TagLib.Tag tag, byte[] pngFront, byte[] pngBack, ManifestFile e)
    {
        tag.Title = "封面双图";
        tag.Album = "封面测试辑";
        tag.Performers = new[] { "封面艺术家" };
        tag.Genres = new[] { "原声" };
        tag.Year = 2022;
        try
        {
            tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(pngFront))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = "image/png",
                    Description = "Front Cover",
                },
                new TagLib.Picture(new TagLib.ByteVector(pngBack))
                {
                    Type = TagLib.PictureType.BackCover,
                    MimeType = "image/png",
                    Description = "Back Cover",
                },
            };
        }
        catch (Exception ex)
        {
            e.Notes.Add($"该格式写封面失败（TagLib 限制）：{ex.GetType().Name}");
        }
        e.Expected["pictures"] = "2 (png front + png back)";
    }

    private static void ApplyEmpty(TagLib.Tag tag, ManifestFile e)
    {
        tag.Title = "";
        tag.Album = "";
        tag.Performers = Array.Empty<string>();
        tag.Genres = Array.Empty<string>();
        tag.AlbumArtists = Array.Empty<string>();
        tag.Composers = Array.Empty<string>();
        tag.Year = 0;
        tag.Track = 0;
        tag.TrackCount = 0;
        tag.Disc = 0;
        tag.DiscCount = 0;
        tag.Comment = "";
        tag.Lyrics = "";
        e.Expected["title"] = "";
        e.Expected["performers"] = "";
    }

    private static void ApplyLongUnicode(TagLib.Tag tag, ManifestFile e)
    {
        tag.Title = "超长标题" + new string('音', 120) + "🎵🎶 The Quick \"Brown\" Fox — 结尾";
        tag.Album = "特殊字符辑\t含制表符";
        tag.Performers = new[] { "عبد الله الموسيقى 🎵", "半角'引号'乐队" };
        tag.Genres = new[] { "世界音乐", "電子音楽" };
        tag.Year = 2023;
        tag.Comment = "第一行评论\r\n第二行评论\t带制表符";
        tag.Lyrics = "CRLF 歌词第一行\r\nCRLF 歌词第二行   ";
        e.Expected["title_len"] = tag.Title.Length.ToString();
        e.Expected["artist0"] = "عبد الله الموسيقى 🎵";
    }

    private static void ApplyLyricsMultiLang(TagLib.Tag tag, TagLib.Id3v2.Tag? id3, ManifestFile e)
    {
        tag.Title = "多语言歌词";
        tag.Album = "歌词测试辑";
        tag.Performers = new[] { "多语言合唱团" };
        tag.Genres = new[] { "流行" };
        tag.Year = 2024;
        string lyrics =
            "[00:01.00]中文第一行\n[00:02.00]中文第二行\n\n" +
            "[00:01.00]English line one\n[00:02.00]English line two\n\n" +
            "[00:01.00]日本語一行目\n[00:02.00]日本語二行目";
        tag.Lyrics = lyrics;
        e.Expected["lyrics_len"] = lyrics.Length.ToString();
    }

    private static void ApplyDiscTrackTotals(TagLib.Tag tag, ManifestFile e)
    {
        tag.Title = "碟片曲目总数";
        tag.Album = "合辑第三卷";
        tag.Performers = new[] { "多位作曲家" };
        tag.Genres = new[] { "古典" };
        tag.Year = 1999;
        tag.Disc = 2;
        tag.DiscCount = 3;
        tag.Track = 5;
        tag.TrackCount = 10;
        e.Expected["disc"] = "2/3";
        e.Expected["track"] = "5/10";
    }

    private static void ApplyRichFields(TagLib.Tag tag, TagLib.Id3v2.Tag? id3, ManifestFile e)
    {
        tag.Title = "富字段";
        tag.Album = "富字段辑";
        tag.Performers = new[] { "主唱", "和声" };
        tag.Genres = new[] { "摇滚" };
        tag.Year = 2018;
        try
        {
            tag.Copyright = "© 2018 测试唱片";
            tag.Publisher = "测试发行公司";
            tag.Grouping = "组别 A";
            tag.Conductor = "指挥家";
            tag.BeatsPerMinute = 128;
            tag.ISRC = "CN-T01-18-00001";
        }
        catch (Exception ex) { e.Notes.Add($"部分富字段不可用：{ex.Message}"); }

        if (id3 != null)
        {
            try
            {
                var c1 = new TagLib.Id3v2.CommentsFrame("中文评论", "chi", TagLib.StringType.UTF8);
                c1.Text = "这是中文评论帧";
                id3.AddFrame(c1);
                var c2 = new TagLib.Id3v2.CommentsFrame("description", "eng", TagLib.StringType.UTF8);
                c2.Text = "Second comment frame";
                id3.AddFrame(c2);
            }
            catch (Exception ex) { e.Notes.Add($"多评论帧失败：{ex.Message}"); }
        }
        e.Expected["comment_frames"] = "2 (中文评论 + description)";
    }

    private static void ApplyBoundary(TagLib.Tag tag, ManifestFile e)
    {
        tag.Title = "字";
        tag.Album = "\uFEFF"; // BOM 前缀
        tag.Performers = new[] { " " }; // 单空格
        tag.Genres = new[] { "a;b;" }; // 尾随分号
        tag.Year = 1;
        tag.Track = 1;
        tag.TrackCount = 1;
        tag.Disc = 1;
        tag.DiscCount = 1;
        tag.Comment = "";
        tag.Lyrics = "\n\n";
        e.Expected["title"] = "字";
        e.Expected["artist0"] = "(单空格)";
        e.Expected["genre0"] = "a;b;";
    }

    // ---------- 工具 ----------

    private static async Task<int> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null)
        {
            return -1;
        }
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>依次尝试：显式参数 → 环境变量 → 从当前目录/程序集目录向上找仓库标记 → PATH。</summary>
    public static string? ResolveFfmpeg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = Path.GetFullPath(explicitPath);
            if (File.Exists(full))
            {
                return full;
            }
        }
        string? fromEnv = Environment.GetEnvironmentVariable("TAG_FIXTURES_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            string fullEnv = Path.GetFullPath(fromEnv);
            if (File.Exists(fullEnv))
            {
                return fullEnv;
            }
        }
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CelesteMusicPlayer", "Assets", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
        }
        // PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
