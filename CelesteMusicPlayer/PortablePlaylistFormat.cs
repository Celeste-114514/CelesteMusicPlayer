using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「Celeste 混合播放列表」文件格式（`.json`，format=celeste-playlist、version=1）。
    ///
    /// W13：从 <c>MainWindow.WebBrowseActions.cs</c> 的导入导出分支里抽出来的**纯函数**：
    /// 只认字符串进、字符串出，不碰磁盘、不碰存储。这样播放列表这个最容易丢数据的路径
    /// 才能用控制台用例穷举（见 <c>tools/PlaylistFormatRegression</c>）。
    ///
    /// 两个字段命名不能动：
    /// - 容器字段是小写 `format / version / references / tracks / sources`；
    /// - 来源对象是 PascalCase `Id / Provider / Name / Endpoint / Root` —— 且**只导出这五个**，
    ///   千万别直接序列化 <see cref="NetworkSource"/>，那会把密码、令牌一起写进用户导出的文件里。
    /// </summary>
    internal static class PortablePlaylistFormat
    {
        internal const string FormatName = "celeste-playlist";
        internal const int FormatVersion = 1;

        /// <summary>导出时写进文件的来源信息，只有定位用的五个字段。</summary>
        internal sealed record SourceRef(string Id, string Provider, string Name, string Endpoint, string Root);

        private sealed record Document(string format, int version, string[] references, NetworkTrack[] tracks, SourceRef[] sources);

        /// <summary>解析结果：已校验的来源与曲目，以及对 references 做完 id 重映射后的路径。</summary>
        internal sealed record Parsed(NetworkTrack[] Tracks, SourceRef[] Sources, string[] References);

        internal static string Build(IReadOnlyList<string> references, IReadOnlyList<NetworkTrack> tracks, IReadOnlyList<SourceRef> sources)
            => JsonSerializer.Serialize(
                new Document(FormatName, FormatVersion, references.ToArray(), tracks.ToArray(), sources.ToArray()),
                new JsonSerializerOptions { WriteIndented = true });

        internal static Parsed Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("format", out JsonElement format) || format.GetString() != FormatName
                || !root.TryGetProperty("version", out JsonElement version) || version.GetInt32() != FormatVersion)
                throw new ArgumentException("不支持的播放列表格式版本。");

            var sources = new List<SourceRef>();
            if (root.TryGetProperty("sources", out JsonElement sourceArray))
            {
                foreach (JsonElement entry in sourceArray.EnumerateArray())
                {
                    string id = entry.GetProperty("Id").GetString() ?? "";
                    if (!Regex.IsMatch(id, "^[a-zA-Z0-9-]{1,64}$")) throw new ArgumentException("来源编号无效。");
                    sources.Add(new SourceRef(id,
                        entry.GetProperty("Provider").GetString() ?? "webdav",
                        entry.GetProperty("Name").GetString() ?? "导入来源",
                        entry.GetProperty("Endpoint").GetString() ?? "",
                        entry.GetProperty("Root").GetString() ?? ""));
                }
            }

            var tracks = new List<NetworkTrack>();
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("tracks", out JsonElement trackArray))
            {
                foreach (JsonElement raw in trackArray.EnumerateArray())
                {
                    NetworkTrack track;
                    try { track = raw.Deserialize<NetworkTrack>() ?? throw new ArgumentException("无效曲目。"); }
                    catch (JsonException) { throw new ArgumentException("无效曲目。"); }
                    if (!Regex.IsMatch(track.Id, "^(source-|cue-)[a-f0-9]{64}$")) throw new ArgumentException("曲目编号无效。");
                    tracks.Add(track);
                    remap[track.Id] = NetworkIdentity.Reference(track.Id);
                }
            }

            string[] references = root.TryGetProperty("references", out JsonElement referenceArray)
                ? referenceArray.EnumerateArray().Select(entry => entry.GetString() ?? "").Where(path => path.Length > 0)
                    .Select(path => remap.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(path), out string? reference) ? reference : path).ToArray()
                : Array.Empty<string>();

            return new Parsed(tracks.ToArray(), sources.ToArray(), references);
        }
    }
}
