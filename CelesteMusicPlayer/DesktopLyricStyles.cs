using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
namespace CelesteMusicPlayer;

// Data-only styles: no file paths, playback state, credentials or executable content.
internal static class DesktopLyricStyles
{
    private static readonly string[] Keys = {
        "DesktopLyricFontSize", "DesktopLyricSecondaryFontSize", "DesktopLyricScale", "DesktopLyricFontFamily",
        "DesktopLyricDirection", "DesktopLyricColorMode", "DesktopLyricPlayedColor", "DesktopLyricUnplayedColor",
        "DesktopLyricGradientStart", "DesktopLyricGradientEnd", "DesktopLyricOutlineColor", "DesktopLyricOutlineWidth",
        "DesktopLyricShadowStrength", "DesktopLyricOpacity", "DesktopLyricVisibleLines", "DesktopLyricLineSpacing",
        "DesktopLyricShowBackgroundBar", "DesktopLyricBackgroundColor", "DesktopLyricMusicReactive", "DesktopLyricAlign",
        "DesktopLyricShowRomanization", "DesktopLyricHideTranslation"
    };
    private sealed record Package(string format, int version, string name, Dictionary<string, JsonElement> style);
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer", "desktop-lyric-styles");
    private static Dictionary<string, JsonElement> ReadCurrent()
    {
        var s = AppSettingsStore.Load();
        return Keys.ToDictionary(k => k, k => JsonSerializer.SerializeToElement(WebSettingsSchema.Readers[k](s)));
    }
    private static void Validate(JsonElement style)
    {
        if (style.ValueKind != JsonValueKind.Object || !style.EnumerateObject().Any()) throw new ArgumentException("样式内容为空。");
        var draft = AppSettingsStore.Load();
        foreach (var p in style.EnumerateObject())
        {
            if (!Keys.Contains(p.Name)) throw new ArgumentException("样式包含不支持的字段：" + p.Name);
            WebSettingsSchema.Writers[p.Name](draft, p.Value);
        }
    }
    public static void Apply(JsonElement style)
    {
        Validate(style); // Validate the whole package before writing anything.
        AppSettingsStore.Update(s => { foreach (var p in style.EnumerateObject()) WebSettingsSchema.Writers[p.Name](s, p.Value); });
    }
    private static Package Parse(string json)
    {
        var p = JsonSerializer.Deserialize<Package>(json) ?? throw new ArgumentException("样式文件为空。");
        if (p.format != "celeste-desktop-lyrics" || p.version != 1 || string.IsNullOrWhiteSpace(p.name) || p.name.Length > 64 || p.style == null)
            throw new ArgumentException("不是受支持的 Celeste 桌面歌词样式文件。");
        Validate(JsonSerializer.SerializeToElement(p.style)); return p;
    }
    private static string Store(Package p)
    {
        Directory.CreateDirectory(Folder);
        if (Directory.GetFiles(Folder, "*.json").Length >= 100) throw new ArgumentException("最多保存 100 个样式。");
        string id = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(Folder, id + ".json"), JsonSerializer.Serialize(p)); return id;
    }
    public static object[] List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<object>();
        var result = new List<object>();
        foreach (var path in Directory.GetFiles(Folder, "*.json").Take(100))
        {
            try { if (new FileInfo(path).Length <= 32768) { var p = Parse(File.ReadAllText(path)); result.Add(new { id = Path.GetFileNameWithoutExtension(path), name = p.name }); } }
            catch (Exception ex) { StartupLog.WriteException("Lyrics.Style", ex); }
        }
        return result.ToArray();
    }
    public static void Save(string name)
    {
        name = name.Trim(); if (name.Length == 0 || name.Length > 64) throw new ArgumentException("名称需为 1–64 个字。");
        Store(new("celeste-desktop-lyrics", 1, name, ReadCurrent()));
    }
    public static void Select(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效的样式编号。");
        var path = Path.Combine(Folder, id + ".json");
        if (new FileInfo(path).Length > 32768) throw new ArgumentException("样式文件超过 32 KB。");
        Apply(JsonSerializer.SerializeToElement(Parse(File.ReadAllText(path)).style));
    }
    public static void Import(string path)
    {
        if (new FileInfo(path).Length > 32768) throw new ArgumentException("样式文件超过 32 KB。");
        Store(Parse(File.ReadAllText(path)));
    }
    public static string Export() => JsonSerializer.Serialize(new Package("celeste-desktop-lyrics", 1, "我的桌面歌词", ReadCurrent()), new JsonSerializerOptions { WriteIndented = true });
}
