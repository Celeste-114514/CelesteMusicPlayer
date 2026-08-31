using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 标签排序面板字段表（模块 A 曲目列表列定制 + 模块 B 分类字段可配置 共用）。
    /// Key 对应 PlaylistItem 属性 / AudioInfoFormatter 格式信息；Numeric 用于排序与分组数值比较。
    /// </summary>
    public static class TagSortFields
    {
        /// <summary>字段基数（用于分类字段配置时给提示）。High = 每首歌唯一或接近唯一，不适合分类墙。</summary>
        public enum Cardinality { Low, High }

        public sealed record FieldDef(string Key, string Label, bool Numeric, bool Tech, Cardinality Cardinality);

        /// <summary>全部可选字段。Tech=true 表示来自 AudioInfoFormatter 的技术信息（懒解析有缓存）。</summary>
        public static readonly FieldDef[] All =
        {
            new("Artist",       "艺术家",     false, false, Cardinality.Low),
            new("AlbumArtist",  "专辑艺术家", false, false, Cardinality.Low),
            new("Album",        "专辑",       false, false, Cardinality.Low),
            new("Genre",        "流派",       false, false, Cardinality.Low),
            new("Year",         "年份",       true,  false, Cardinality.Low),
            new("TitleLetter",  "标题首字母", false, false, Cardinality.Low),
            new("Format",       "格式",       false, true,  Cardinality.Low),
            new("DepthRate",    "位深/采样率",false, true,  Cardinality.Low),
            new("Bitrate",      "码率",       false, true,  Cardinality.Low),
            // High：每首歌唯一/接近唯一，不适合做分类墙（卡片过多 + 重复读封面爆内存）。
            // 在「分组浏览」（模块 C）里使用——列表内按字段分组，组头即歌名/文件名。
            new("Title",        "标题",       false, false, Cardinality.High),
            new("FileName",     "文件名",     false, false, Cardinality.High),
            new("Track",        "音轨号",     true,  false, Cardinality.High),
            new("Disc",         "碟片号",     true,  false, Cardinality.High),
            new("Rating",       "评分",       true,  false, Cardinality.High),
            new("Duration",     "时长",       false, false, Cardinality.High),
        };

        public static FieldDef? Find(string key) => Array.Find(All, f => f.Key == key);

        public static bool IsNumeric(string key)
            => key is "Year" or "Track" or "Disc" or "Rating" or "Duration";

        /// <summary>分类 / 分组取值（无值统一为"未知"，供 GroupBy / 排序链使用）。</summary>
        public static string Value(PlaylistItem p, string key)
        {
            switch (key)
            {
                case "Artist": return Fallback(p.Artist);
                case "AlbumArtist": return Fallback(p.AlbumArtist);
                case "Album": return Fallback(p.Album);
                case "Genre": return Fallback(p.Genre);
                case "Year": return p.Year > 0 ? p.Year.ToString() : "未知";
                case "Title": return string.IsNullOrWhiteSpace(p.Title) ? "未知" : p.Title.Trim();
                case "Track": return p.Track > 0 ? p.Track.ToString() : "未知";
                case "Disc": return p.Disc > 0 ? p.Disc.ToString() : "未知";
                case "Rating": return p.Rating > 0 ? p.Rating.ToString() : "未知";
                case "TitleLetter": return TitleLetter(p.Title);
                case "Format": return Chip(p, 0);
                case "DepthRate": return Chip(p, 1);
                case "Bitrate": return Chip(p, 2);
                case "Duration": return p.DurationText;
                case "FileName": return p.FileName;
                default: return "未知";
            }
            static string Fallback(string s) => string.IsNullOrWhiteSpace(s) ? "未知" : s.Trim();
            static string Chip(PlaylistItem p, int i) => p.FormatChips.Count > i ? p.FormatChips[i] : "未知";
            static string TitleLetter(string title)
            {
                if (string.IsNullOrWhiteSpace(title)) return "未知";
                return char.ToUpperInvariant(title.Trim()[0]).ToString();
            }
        }

        /// <summary>曲目列表列显示文本（无值显示 "-"，时长 mm:ss，评分 ★，音轨号补零）。</summary>
        public static string ColumnText(PlaylistItem p, string key)
        {
            switch (key)
            {
                case "Title": return string.IsNullOrWhiteSpace(p.Title) ? p.FileName : p.Title;
                case "Artist": return TextOr(p.Artist);
                case "AlbumArtist": return TextOr(p.AlbumArtist);
                case "Album": return TextOr(p.Album);
                case "Genre": return TextOr(p.Genre);
                case "Year": return p.Year > 0 ? p.Year.ToString() : "-";
                case "Track": return p.Track > 0 ? p.Track.ToString("D2") : "-";
                case "Disc": return p.Disc > 0 ? p.Disc.ToString() : "-";
                case "Rating": return p.Rating > 0 ? new string('★', p.Rating) : "-";
                case "TitleLetter": return string.IsNullOrWhiteSpace(p.Title) ? "-" : char.ToUpperInvariant(p.Title.Trim()[0]).ToString();
                case "Duration": return p.DurationText;
                case "Format": return p.FormatChips.Count > 0 ? p.FormatChips[0] : "-";
                case "DepthRate": return p.FormatChips.Count > 1 ? p.FormatChips[1] : "-";
                case "Bitrate": return p.FormatChips.Count > 2 ? p.FormatChips[2] : "-";
                case "FileName": return p.FileName;
                default: return p.Title;
            }
            static string TextOr(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();
        }

        /// <summary>数值字段取值（排序比较用；Duration 取秒）。</summary>
        public static double NumericValue(PlaylistItem p, string key)
        {
            return key switch
            {
                "Year" => p.Year,
                "Track" => p.Track,
                "Disc" => p.Disc,
                "Rating" => p.Rating,
                "Duration" => p.Duration.TotalSeconds,
                _ => 0
            };
        }

        /// <summary>默认分类字段（无配置时 = 原来的固定 5 个）。</summary>
        public static string[] DefaultCategoryFields { get; } = { "Artist", "AlbumArtist", "Album", "Genre", "Year" };

        /// <summary>默认曲目列表列（无配置时 = 标题 / 艺术家 / 专辑 / 时长，比原固定 3 列更接近 foobar 默认）。</summary>
        public static List<ListColumnSpec> DefaultColumns() => new()
        {
            new() { Key = "Title", Weight = 3 },
            new() { Key = "Artist", Weight = 2 },
            new() { Key = "Album", Weight = 2 },
            new() { Key = "Duration", Weight = 1 },
        };
    }

    /// <summary>曲目列表一列的配置（模块 A）。Key 见 <see cref="TagSortFields.All"/>。</summary>
    public sealed class ListColumnSpec
    {
        public string Key { get; set; } = "Title";

        /// <summary>宽度权重 1–5。用 Star 比例分配（列头与行模板共用同一权重，天然对齐，无横向滚动同步问题）。</summary>
        public int Weight { get; set; } = 2;

        public bool Visible { get; set; } = true;
    }
}
