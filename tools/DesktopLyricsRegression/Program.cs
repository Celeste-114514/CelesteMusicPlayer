using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Collections;
var appDir = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(appDir, name.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, name.Name + ".dll")) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDir, "CelesteMusicPlayer.dll"));
var store = assembly.GetType("CelesteMusicPlayer.AppSettingsStore")!;
var styles = assembly.GetType("CelesteMusicPlayer.DesktopLyricStyles")!;
var temp = Path.Combine(Path.GetTempPath(), "CelesteLyricsRegression-" + Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer", "desktop-lyric-styles");
var created = new List<string>();
var testName = "Regression-" + Guid.NewGuid().ToString("N");
store.GetProperty("SettingsPathOverride", BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null, Path.Combine(temp, "settings.json"));
object? Call(string method, params object[] values) => styles.GetMethod(method, BindingFlags.Static|BindingFlags.Public)!.Invoke(null, values);
JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);
void Check(bool b, string reason) { if (!b) throw new Exception(reason); }
void Reject(Action action) { try { action(); } catch (TargetInvocationException e) when (e.InnerException is ArgumentException or JsonException) { return; } throw new Exception("Expected rejection"); }
try {
 Call("Apply", Json("{\"DesktopLyricFontSize\":42,\"DesktopLyricShowRomanization\":true}"));
 store.GetProperty("SettingsPathOverride", BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null, Path.Combine(temp, "settings.json"));
 var exported=(string)Call("Export")!;
 Check(Json(exported).GetProperty("style").GetProperty("DesktopLyricFontSize").GetDouble()==42,"style did not persist");
 Reject(()=>Call("Apply",Json("{\"DesktopLyricFontSize\":50,\"DesktopLyricFontFile\":\"C:/private.ttf\"}")));
 Check((string)Call("Export")! == exported,"rejected patch partially mutated state");
 var path=Path.Combine(temp,"style.json");var package=System.Text.Json.Nodes.JsonNode.Parse(exported)!;package["name"]=testName;File.WriteAllText(path,package.ToJsonString());Call("Import",path);
 var items=JsonSerializer.SerializeToElement(Call("List"));var id=items.EnumerateArray().Single(i=>i.GetProperty("name").GetString()==testName).GetProperty("id").GetString()!;created.Add(Path.Combine(folder,id+".json"));
 Call("Apply",Json("{\"DesktopLyricFontSize\":20}"));Call("Select",id);
 Check(Json((string)Call("Export")!).GetProperty("style").GetProperty("DesktopLyricFontSize").GetDouble()==42,"import/select failed");
 Reject(()=>Call("Select","../outside"));
 File.WriteAllText(path,exported.Replace("\"version\": 1","\"version\": 99"));Reject(()=>Call("Import",path));
 File.WriteAllText(path,new string('x',32769));Reject(()=>Call("Import",path));
 var audio=Path.Combine(temp,"song.flac");File.WriteAllText(Path.ChangeExtension(audio,".lrc"),"[00:01.00]原文\n[00:02.00]下一句");File.WriteAllText(Path.ChangeExtension(audio,".romaji.lrc"),"[00:01.00]genbun\n[00:02.00]tsugi");
 var loader=assembly.GetType("CelesteMusicPlayer.LyricsLoader")!;var lines=((IEnumerable)loader.GetMethod("LoadForAudio")!.Invoke(null,new object[]{audio})!).Cast<object>().ToArray();
 Check(lines.Length==2,"primary lyrics changed");Check((string?)lines[0].GetType().GetProperty("Romanization")!.GetValue(lines[0])=="genbun","romanization not attached by timestamp");
 Console.WriteLine("PASS: real settings persistence, atomic rejection, style export/import/select, version/size/path rejection, timed romanization");
} finally {
 foreach(var p in created)File.Delete(p);
 Directory.Delete(temp,true);
}
