using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    public sealed class TagEditModel
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public string AlbumArtist { get; set; } = string.Empty;

        public uint Year { get; set; }

        public uint Track { get; set; }

        public string Genre { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public string Lyrics { get; set; } = string.Empty;
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
                model.Artist = tag.FirstPerformer ?? string.Empty;
                model.Album = tag.Album ?? string.Empty;
                model.AlbumArtist = tag.FirstAlbumArtist ?? string.Empty;
                model.Year = tag.Year;
                model.Track = tag.Track;
                model.Genre = tag.FirstGenre ?? string.Empty;
                model.Comment = tag.Comment ?? string.Empty;
                model.Lyrics = tag.Lyrics ?? string.Empty;

                if (string.IsNullOrWhiteSpace(model.Title) && string.IsNullOrWhiteSpace(model.Artist))
                {
                    TagEditModel fromName = TryParseTagsFromFileName(Path.GetFileNameWithoutExtension(path));
                    if (!string.IsNullOrWhiteSpace(fromName.Title))
                    {
                        model.Title = fromName.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(fromName.Artist))
                    {
                        model.Artist = fromName.Artist;
                    }
                }
            }
            catch
            {
            }

            return model;
        }

        public static void SaveTag(string path, TagEditModel model)
        {
            if (string.IsNullOrWhiteSpace(path) || model == null)
            {
                return;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                TagLib.Tag tag = file.Tag;
                tag.Title = model.Title ?? string.Empty;
                tag.Album = model.Album ?? string.Empty;
                tag.AlbumArtists = string.IsNullOrWhiteSpace(model.AlbumArtist)
                    ? Array.Empty<string>()
                    : new[] { model.AlbumArtist };
                tag.Performers = string.IsNullOrWhiteSpace(model.Artist)
                    ? Array.Empty<string>()
                    : new[] { model.Artist };
                tag.Year = model.Year;
                tag.Track = model.Track;
                tag.Genres = string.IsNullOrWhiteSpace(model.Genre)
                    ? Array.Empty<string>()
                    : new[] { model.Genre };
                tag.Comment = model.Comment ?? string.Empty;
                tag.Lyrics = model.Lyrics ?? string.Empty;

                // 按设置写 ID3v2.3（默认）或保持 ID3v2.4
                if (AppSettingsStore.Load().WriteId3v23
                    && (file.TagTypes & TagLib.TagTypes.Id3v2) != 0
                    && file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    id3.Version = 3;
                }

                file.Save();
            }
            catch
            {
            }
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
            catch
            {
            }
        }

        public static void ClearEmbeddedLyrics(string path)
        {
            SaveEmbeddedLyrics(path, string.Empty);
        }

        /// <summary>读取文件内嵌封面图片数据（无封面返回 null）。</summary>
        public static byte[]? ReadCoverBytes(string path)
        {
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

        /// <summary>替换（或写入）指定音频文件的内嵌封面图片字节。返回是否成功。</summary>
        public static bool SetCoverBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(path) || bytes == null || bytes.Length == 0)
            {
                return false;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                file.Tag.Pictures = new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(bytes))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = "image/jpeg",
                        Description = "Cover"
                    }
                };
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
                model.Artist = match.Groups["artist"].Value.Trim();
                model.Title = match.Groups["title"].Value.Trim();
                return model;
            }

            match = TitleByArtistRegex.Match(name);
            if (match.Success)
            {
                model.Title = match.Groups["title"].Value.Trim();
                model.Artist = match.Groups["artist"].Value.Trim();
                return model;
            }

            model.Title = name;
            return model;
        }
    }
}
