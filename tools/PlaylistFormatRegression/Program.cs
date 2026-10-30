using CelesteMusicPlayer;
using System.Text.Json;

// 「Celeste 混合播放列表」文件格式的用例。纯函数，不碰磁盘、不碰网络、不需要声卡。
// 这条路径一旦出错就是用户丢播放列表，所以宁可把边界穷举一遍。

int failures = 0;

void Check(string name, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
}

void Throws(string name, Func<object> action, string expectedFragment)
{
    try { action(); Check(name, false, "没有抛异常"); }
    catch (Exception ex) { Check(name, ex.Message.Contains(expectedFragment, StringComparison.Ordinal), "实际=" + ex.Message); }
}

string TrackId(string seed) => "source-" + NetworkIdentity.Hash(seed);
string CueId(string seed) => "cue-" + NetworkIdentity.Hash(seed);

var sourceRef = new PortablePlaylistFormat.SourceRef("abc123", "webdav", "我的网盘", "https://dav.example.com", "/music");
var trackA = new NetworkTrack { Id = TrackId("a"), SourceId = "abc123", Title = "夜に駆ける", Artist = "YOASOBI" };
var trackB = new NetworkTrack { Id = CueId("b"), SourceId = "abc123", Title = "CUE 第二轨", Cue = true };
var plain = @"D:\Music\ローカル.flac";
var reference = NetworkIdentity.Reference(trackA.Id);

// 1. 往返：导出再导入，references 里的 .ref 路径要被换成重新解析后的引用
string json = PortablePlaylistFormat.Build(new[] { reference, plain }, new[] { trackA, trackB }, new[] { sourceRef });
var parsed = PortablePlaylistFormat.Parse(json);
Check("往返 曲目条数", parsed.Tracks.Length == 2, "实际=" + parsed.Tracks.Length);
Check("往返 曲目标题保留", parsed.Tracks[0].Title == "夜に駆ける" && parsed.Tracks[1].Cue, parsed.Tracks[0].Title);
Check("往返 来源条数", parsed.Sources.Length == 1 && parsed.Sources[0].Id == "abc123", parsed.Sources.Length.ToString());
Check("往返 来源名称保留", parsed.Sources[0].Name == "我的网盘", parsed.Sources[0].Name);
Check("往返 普通路径原样保留", parsed.References.Contains(plain));
Check("往返 引用路径被重映射", parsed.References[0] == reference, parsed.References[0]);

// 2. 文件里不能出现密码/令牌：导出必须只带五个定位字段
Check("导出不含 ProtectedSecret", !json.Contains("ProtectedSecret", StringComparison.OrdinalIgnoreCase));
Check("导出不含 ProtectedRefreshToken", !json.Contains("RefreshToken", StringComparison.OrdinalIgnoreCase));
Check("导出字段名 PascalCase", json.Contains("\"Provider\"") && json.Contains("\"Endpoint\""));
Check("容器字段小写", json.Contains("\"format\"") && json.Contains("\"references\"") && json.Contains("\"tracks\""));

// 3. 版本与格式
Throws("拒绝错误 format", () => PortablePlaylistFormat.Parse(json.Replace("celeste-playlist", "other-format")), "不支持的播放列表格式版本");
Throws("拒绝 version 2", () => PortablePlaylistFormat.Parse(json.Replace("\"version\": 1", "\"version\": 2")), "不支持的播放列表格式版本");
Throws("拒绝缺 format 字段", () => PortablePlaylistFormat.Parse("{\"version\":1}"), "不支持的播放列表格式版本");

// 4. id 校验
Throws("拒绝非法来源编号", () => PortablePlaylistFormat.Parse(json.Replace("\"Id\": \"abc123\"", "\"Id\": \"bad id!\"")), "来源编号无效");
Throws("拒绝非法曲目编号", () => PortablePlaylistFormat.Parse(json.Replace(TrackId("a"), "not-a-real-id")), "曲目编号无效");

// 5. 空文档：三个数组都缺，应得到空结果而不是崩
var empty = PortablePlaylistFormat.Parse("{\"format\":\"celeste-playlist\",\"version\":1}");
Check("空文档 全空", empty.Tracks.Length == 0 && empty.Sources.Length == 0 && empty.References.Length == 0);

// 6. references 里指向未知 id 的路径要原样保留（不能变成空串）
var unknown = PortablePlaylistFormat.Parse(
    "{\"format\":\"celeste-playlist\",\"version\":1,\"references\":[\"D:\\\\Music\\\\x.flac\"]}");
Check("未知引用原样保留", unknown.References.Length == 1 && unknown.References[0].EndsWith("x.flac"), string.Join("|", unknown.References));

Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;
