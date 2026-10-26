using System;
using System.Collections.Generic;
using System.Globalization;

namespace CelesteMusicPlayer
{
    // Independent of WinUI: the JSON adapter and the tag writer share the same dirty-field rules.
    internal static class WebTagEditRules
    {
        internal static bool Apply(TagEditModel model, Dictionary<string, string> fields)
        {
            foreach (string field in fields.Keys)
                if (field is not ("title" or "artist" or "album" or "albumArtist" or "genre" or "composer" or "comment" or "bpm" or "lyrics" or "year" or "track" or "trackCount" or "disc" or "discCount"))
                    throw new ArgumentException("标签字段不可写：" + field);
            bool changed = false;
            void Text(string key, TagField field, string previous, Action<string> set)
            {
                if (!fields.TryGetValue(key, out var value) || string.Equals(previous, value, StringComparison.Ordinal)) return;
                set(value); model.MarkDirty(field); changed = true;
            }
            void Number(string key, TagField field, uint previous, Action<uint> set)
            {
                if (!fields.TryGetValue(key, out var text)) return;
                uint value = 0;
                if (text.Length > 0 && !uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(key + " 必须是非负整数。");
                if (previous == value) return;
                set(value); model.MarkDirty(field); changed = true;
            }
            Text("title", TagField.Title, model.Title, v => model.Title = v);
            Text("artist", TagField.Performers, model.Artist, v => model.Artist = v);
            Text("album", TagField.Album, model.Album, v => model.Album = v);
            Text("albumArtist", TagField.AlbumArtists, model.AlbumArtist, v => model.AlbumArtist = v);
            Text("genre", TagField.Genres, model.Genre, v => model.Genre = v);
            Text("composer", TagField.Composers, model.Composer, v => model.Composer = v);
            Text("comment", TagField.Comment, model.Comment, v => model.Comment = v);
            Text("bpm", TagField.Bpm, model.Bpm, v => model.Bpm = v);
            Text("lyrics", TagField.Lyrics, model.Lyrics, v => model.Lyrics = v);
            Number("year", TagField.Year, model.Year, v => model.Year = v);
            Number("track", TagField.Track, model.Track, v => model.Track = v);
            Number("trackCount", TagField.TrackCount, model.TrackCount, v => model.TrackCount = v);
            Number("disc", TagField.Disc, model.Disc, v => model.Disc = v);
            Number("discCount", TagField.DiscCount, model.DiscCount, v => model.DiscCount = v);
            return changed;
        }
    }
}
