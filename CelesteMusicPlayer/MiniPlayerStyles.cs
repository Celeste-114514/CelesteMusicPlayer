using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace CelesteMusicPlayer;
internal static class MiniPlayerStyles
{
    internal sealed class Style
    {
        public string format { get; set; } = "celeste-mini-style";
        public int version { get; set; } = 1;
        public string id { get; set; } = "";
        public string name { get; set; } = "我的样式";
        public string mode { get; set; } = "follow";
        public Dictionary<string,string> colors { get; set; } = new();
        public string layout { get; set; } = "cover-left";
        public int coverSize { get; set; } = 62;
        public int cornerRadius { get; set; } = 7;
        public string textAlign { get; set; } = "center";
        public int lyricSize { get; set; } = 21;
    }
    private sealed class Library { public Library() {} public string selected { get; set; } = "follow"; public List<Style> styles { get; set; } = new(); }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static Library? _library;
    private static string PathName => Path.Combine(AppSettingsStore.GetConfigDirectory(), "mini-player-styles.json");
    internal static int Revision { get; private set; }
    private static Library Data
    {
        get
        {
            if (_library != null) return _library;
            try
            {
                _library = File.Exists(PathName) ? JsonSerializer.Deserialize<Library>(File.ReadAllText(PathName)) : null;
                if (_library != null)
                {
                    if (_library.styles == null || _library.styles.Count > 100) throw new ArgumentException("样式库无效");
                    foreach(var style in _library.styles) { Validate(style); if (!style.id.StartsWith("user-", StringComparison.Ordinal)) throw new ArgumentException("样式标识无效"); }
                }
            }
            catch(Exception ex) { _library = null; StartupLog.WriteException("MiniPlayerStyles.Load",ex); }
            return _library ??= new Library();
        }
    }
    internal static Style[] All() => new[] { new Style { id="follow",name="跟随主程序" }, new Style { id="apple",name="Apple 深色",mode="dark" } }.Concat(Data.styles).ToArray();
    internal static Style Current => All().FirstOrDefault(s=>s.id==Data.selected) ?? All()[0];
    internal static object Snapshot() => new { selected=Current.id, styles=All(), revision=Revision };
    private static void Save()
    {
        try
        {
        Directory.CreateDirectory(AppSettingsStore.GetConfigDirectory());
        if(File.Exists(PathName)) File.Copy(PathName,PathName+".bak",true);
        File.WriteAllText(PathName+".tmp",JsonSerializer.Serialize(Data,Json));
        File.Move(PathName+".tmp",PathName,true); Revision++;
        }
        catch { _library = null; throw; }
    }
    internal static void Select(string id) { if(!All().Any(s=>s.id==id))throw new ArgumentException("样式不存在"); Data.selected=id; Save(); }
    internal static void Validate(Style s)
    {
        if(s.format!="celeste-mini-style" || s.version!=1)throw new ArgumentException("不是受支持的迷你播放器样式文件");
        if(string.IsNullOrWhiteSpace(s.name)||s.name.Length>60)throw new ArgumentException("样式名称需为 1–60 个字");
        if(s.mode is not ("follow" or "light" or "dark"))throw new ArgumentException("明暗模式无效");
        if(s.colors==null || s.colors.Count>7 || s.colors.Any(p=>!new[]{"background","text","muted","accent","track","popup","onAccent"}.Contains(p.Key)||!Regex.IsMatch(p.Value??"","^#[0-9a-fA-F]{6}$")))throw new ArgumentException("颜色需为 #RRGGBB；只接受模板中的颜色字段");
        if(s.layout is not ("cover-left" or "cover-right" or "text-only"))throw new ArgumentException("布局无效");
        if(s.coverSize<40||s.coverSize>72||s.cornerRadius<0||s.cornerRadius>36)throw new ArgumentException("封面尺寸或圆角超出范围");
        if(s.textAlign is not ("left" or "center" or "right"))throw new ArgumentException("对齐方式无效");
        if(s.lyricSize<14||s.lyricSize>32)throw new ArgumentException("歌词字号范围为 14–32");
    }
    internal static void Add(string json)
    {
        if(json.Length>32768)throw new ArgumentException("样式文件过大");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out _) || !root.TryGetProperty("version", out _) || !root.TryGetProperty("name", out _))
            throw new ArgumentException("样式文件缺少 format、version 或 name；请使用导出的模板");
        var s=JsonSerializer.Deserialize<Style>(json)??throw new ArgumentException("样式为空");Validate(s);
        if(Data.styles.Count>=100)throw new ArgumentException("最多保存 100 个用户样式");
        s.id="user-"+Guid.NewGuid().ToString("N");Data.styles.Add(s);Data.selected=s.id;Save();
    }
    internal static string Export(string id) => JsonSerializer.Serialize(All().First(s=>s.id==id),Json);
    internal static void Remove(string id) { if(!id.StartsWith("user-",StringComparison.Ordinal))throw new ArgumentException("内置样式不能删除");Data.styles.RemoveAll(s=>s.id==id);if(Data.selected==id)Data.selected="follow";Save(); }
}
