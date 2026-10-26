using System;
using System.Linq;
using CelesteMusicPlayer;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
string[] roots = { @"D:\Music", @"D:\Music\Special" };
Check(WebBrowseRules.Folder(@"D:\Music\song.flac", roots) == "Music", "root-level song");
Check(WebBrowseRules.Folder(@"D:\Music\Rock\Album\song.flac", roots) == @"Music\Rock\Album", "root prefix stripped");
Check(WebBrowseRules.Folder(@"d:\music\Rock\song.flac", roots).EndsWith(@"Music\Rock", StringComparison.OrdinalIgnoreCase), "case-insensitive root");
Check(WebBrowseRules.Folder(@"D:\Music\Special\song.flac", roots) == "Special", "most specific root");
Check(WebBrowseRules.Folder(@"D:\MusicExtra\song.flac", roots) == @"D:\MusicExtra", "sibling prefix not a root match");
Check(WebBrowseRules.Folder(@"\\server\share\Music\Rock\song.flac", new[]{@"\\server\share\Music"}) == @"Music\Rock", "UNC root");
Check(WebBrowseRules.Folder(@"D:\Music\song.flac", new[]{@"D:\Music", @"E:\Music"}) != WebBrowseRules.Folder(@"E:\Music\song.flac", new[]{@"D:\Music", @"E:\Music"}), "same folder name across drives");
Track a=new("disc.flac:0"),b=new("disc.flac:90"),c=new("song.flac:0");
var source=new[]{a,b,c};
var moved=WebBrowseRules.Insert(source,new[]{b},a,t=>t.Id,t=>new Track(t.Id),true,false);
Check(moved.Select(t=>t.Id).SequenceEqual(new[]{a.Id,b.Id,c.Id}), "next retains CUE sibling");
Check(ReferenceEquals(moved[0],a), "current queue object retained");
var same=WebBrowseRules.Insert(source,new[]{a},a,t=>t.Id,t=>new Track(t.Id),true,false);
Check(same.Count==4 && ReferenceEquals(same[0],a) && same[1].Id==a.Id, "current song scheduled next keeps active entry");
var appended=WebBrowseRules.Insert(source,new[]{b,b},a,t=>t.Id,t=>new Track(t.Id),false,false);
Check(appended.Select(t=>t.Id).SequenceEqual(new[]{a.Id,c.Id,b.Id}), "append deduplicates by track including cue offset");
var prepended=WebBrowseRules.Insert(source,new[]{c},a,t=>t.Id,t=>new Track(t.Id),false,true);
Check(prepended.Select(t=>t.Id).SequenceEqual(new[]{c.Id,a.Id,b.Id}), "insert-at-begin preference");
Console.WriteLine("PASS: root/direct/nested/case/UNC/boundary/collision folder paths; CUE identity, current entry, next/append/prepend queue rules.");
sealed record Track(string Id);
