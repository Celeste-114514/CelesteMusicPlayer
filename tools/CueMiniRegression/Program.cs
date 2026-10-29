using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Collections;
var appDir=Path.GetFullPath(args[0]);AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(appDir,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir,n.Name+".dll")):null;
var a=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir,"CelesteMusicPlayer.dll"));
var temp=Path.Combine(Path.GetTempPath(),"CelesteCueMini-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var store=a.GetType("CelesteMusicPlayer.AppSettingsStore")!;store.GetProperty("SettingsPathOverride",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,Path.Combine(temp,"settings.json"));
void Check(bool b,string reason){if(!b)throw new Exception(reason);}
var flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
try{
var parser=a.GetType("CelesteMusicPlayer.CueSheetParser")!;string cue=Directory.GetFiles(args[1],"*.cue").Single();var tracks=((IEnumerable)parser.GetMethod("LoadCue")!.Invoke(null,new object[]{cue})!).Cast<object>().ToArray();
string Text(object o,string p)=>(string)o.GetType().GetProperty(p)!.GetValue(o)!;
Check(tracks.Length==10,"expected ten tracks");Check(tracks.All(t=>File.Exists(Text(t,"FilePath"))),"encoding failed to resolve WAV");Check(tracks.All(t=>!Text(t,"Album").Contains('\uFFFD')&&!Text(t,"Title").Contains('\uFFFD')),"garbled metadata");
var type=a.GetType("CelesteMusicPlayer.PlaylistItem")!;var item=Activator.CreateInstance(type)!;var wav=Text(tracks[0],"FilePath");type.GetProperty("FilePath")!.SetValue(item,wav);
double duration=0;using(var stream=File.OpenRead(wav))using(var br=new BinaryReader(stream)){stream.Position=12;uint byteRate=0;while(stream.Position+8<=stream.Length){string chunkId=new string(br.ReadChars(4));uint len=br.ReadUInt32();long next=stream.Position+len+(len%2);if(chunkId=="fmt "){br.ReadUInt16();br.ReadUInt16();br.ReadUInt32();byteRate=br.ReadUInt32();}if(chunkId=="data"){duration=(double)len/byteRate;break;}stream.Position=next;}}
type.GetProperty("Duration")!.SetValue(item,TimeSpan.FromSeconds(duration));var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;list.Add(item);
var expanded=((IEnumerable)a.GetType("CelesteMusicPlayer.CueTrackExpansion")!.GetMethod("Expand")!.Invoke(null,new object[]{list})!).Cast<object>().ToArray();
Check(expanded.Length==10,"scan did not expand");Check(expanded.Select(t=>Text(t,"Album")).Distinct().Count()==1,"not grouped as one album");Check(expanded.All(t=>Text(t,"FormatInfoLine").StartsWith("CUE/PCM")),"format label incorrect");var seconds=expanded.Sum(t=>((TimeSpan)type.GetProperty("Duration")!.GetValue(t)!).TotalSeconds);Check(Math.Abs(seconds-duration)<.02,"track boundaries do not cover WAV");
Console.WriteLine($"CUE PASS: {Text(tracks[0],"Album")} / {Text(tracks[0],"AlbumArtist")}, {tracks.Length} tracks, {duration:F3}s, one album, CUE/PCM");
var styles=a.GetType("CelesteMusicPlayer.MiniPlayerStyles")!;object? Call(string m,params object[] v)=>styles.GetMethod(m,flags)!.Invoke(null,v);
string json="""{"format":"celeste-mini-style","version":1,"name":"Regression","mode":"dark","colors":{"accent":"#123456"},"lyricSize":24,"layout":"cover-right","coverSize":70,"cornerRadius":20,"textAlign":"left"}""";
Call("Add",json);var snapshot=JsonSerializer.SerializeToElement(Call("Snapshot"));string id=snapshot.GetProperty("selected").GetString()!;string exported=(string)Call("Export",id)!;Check(exported.Contains("cover-right"),"layout not exported");
void Reject(string bad){try{Call("Add",bad);}catch(TargetInvocationException e)when(e.InnerException is ArgumentException or JsonException){return;}throw new Exception("invalid style accepted");}
Reject(json.Replace("#123456","javascript:bad"));Reject(json.Replace("\"version\":1","\"version\":99"));Reject(json.Replace("\"coverSize\":70","\"coverSize\":999"));Call("Select","follow");Call("Select",id);Call("Add",exported);snapshot=JsonSerializer.SerializeToElement(Call("Snapshot"));Check(snapshot.GetProperty("styles").GetArrayLength()==4,"roundtrip failed");Call("Remove",id);Console.WriteLine("MINI PASS: save/select/export/import/layout, invalid color/version/range rejection, remove");
}finally{Directory.Delete(temp,true);}
