using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放列表的「媒体」面：封面（选图/联网找/清空）、导出、导入、复制到另一个列表。
    ///
    /// W13：从 <c>MainWindow.WebBrowseActions.cs</c> 的 switch 里整块搬出。
    /// 这块零播放、零队列、零 UI 控件 —— 只碰文件和播放列表存储，所以也**不需要宿主口子**：
    /// 父窗口句柄靠静态的 <see cref="NativeMainWebWindow.HandleOf"/> 现取。
    ///
    /// 与「入队 / 播放 / 从磁盘删除」分开是有意的：那些会动引擎和列表控件，
    /// 留到播放域那一刀一起处理，别在这里顺手牵过来。
    /// </summary>
    internal sealed class WebPlaylistMediaService
    {
        private static string PlaylistName(JsonElement msg)
        {
            string name = msg.GetProperty("name").GetString() ?? "";
            if (!NamedPlaylistStore.List().Contains(name)) throw new InvalidOperationException("播放列表已不存在，请刷新。");
            return name;
        }

        internal async Task SetCoverAsync(JsonElement msg, object window)
        {
            string name = PlaylistName(msg);
            var picker = new FileOpenPicker();
            foreach (string extension in new[] { ".jpg", ".jpeg", ".png" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(window));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            byte[] bytes = await File.ReadAllBytesAsync(file.Path);
            if (bytes.Length > 20_000_000) throw new ArgumentException("请选择小于 20MB 的图片。");
            using (var stream = new MemoryStream(bytes)) using (var image = System.Drawing.Image.FromStream(stream)) { }
            PlaylistName(msg); // 选择器开着的时候原生界面可能已经改过列表，重取一次。
            PlaylistLibraryService.WriteCustomCover(name, bytes);
        }

        internal void ResetCover(JsonElement msg) => PlaylistLibraryService.ClearCustomCover(PlaylistName(msg));

        internal async Task SearchCoverAsync(JsonElement msg)
        {
            string name = PlaylistName(msg);
            string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(name);
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("未找到封面。");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
            byte[] bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length > 20_000_000) throw new ArgumentException("封面图片过大。");
            using (var stream = new MemoryStream(bytes)) using (var image = System.Drawing.Image.FromStream(stream)) { }
            PlaylistName(msg);
            PlaylistLibraryService.WriteCustomCover(name, bytes);
        }

        internal async Task CopyAsync(JsonElement msg, object window)
        {
            var paths = NamedPlaylistStore.LoadSongs(PlaylistName(msg));
            string? target = await PickTargetPlaylistAsync(window);
            if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        internal async Task ExportAsync(JsonElement msg, object window)
        {
            string name = PlaylistName(msg);
            var paths = NamedPlaylistStore.LoadSongs(name);
            if (paths.Count == 0) throw new InvalidOperationException("播放列表为空。");

            var picker = new FileSavePicker { SuggestedFileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) };
            bool portable = paths.Any(path => TrackReference.ReferenceId(path).Length > 0);
            picker.FileTypeChoices.Add(portable ? "Celeste 混合播放列表（保留来源与 CUE）" : "M3U8 播放列表", new List<string> { portable ? ".json" : ".m3u8" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(window));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            if (!portable) { M3uPlaylistIO.WriteM3u8(file.Path, paths); return; }

            var records = paths.Select(TrackReference.FromReference).Where(item => item != null)
                .Select(item => NetworkLibraryStore.Track(item!.SourceTrackId)).Where(track => track != null).Select(track => track!).ToArray();
            var sourceIds = records.Select(track => track.SourceId).ToHashSet();
            var sources = NetworkLibraryStore.Sources().Where(source => sourceIds.Contains(source.Id))
                .Select(source => new PortablePlaylistFormat.SourceRef(source.Id, source.Provider, source.Name, source.Endpoint, source.Root)).ToArray();
            await File.WriteAllTextAsync(file.Path, PortablePlaylistFormat.Build(paths, records, sources));
        }

        internal async Task ImportAsync(JsonElement msg, object window)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".m3u"); picker.FileTypeFilter.Add(".m3u8"); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(window));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string[] paths;
            if (Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(file.Path).Length > 20_000_000) throw new ArgumentException("播放列表文件过大。");
                paths = ImportPortable(await File.ReadAllTextAsync(file.Path));
            }
            else paths = M3uPlaylistIO.Parse(file.Path, existingOnly: false).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            if (paths.Length == 0) throw new InvalidOperationException("文件中没有歌曲条目。");
            string? target = await PickTargetPlaylistAsync(window);
            if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 便携格式导入：**先整体校验，再写存储**。
        /// 原实现边校验边写，遇到坏曲目时前面几条来源/曲目已经落盘了 —— 这里改成两段。
        /// </summary>
        private static string[] ImportPortable(string json)
        {
            PortablePlaylistFormat.Parsed parsed = PortablePlaylistFormat.Parse(json);

            var knownSources = NetworkLibraryStore.Sources().Select(source => source.Id).ToHashSet();
            foreach (var source in parsed.Sources)
            {
                if (knownSources.Contains(source.Id)) continue;
                // 导入的来源不带任何凭据，保持禁用，直到用户显式配置。
                NetworkLibraryStore.SaveSource(new NetworkSource
                {
                    Id = source.Id, Provider = source.Provider, Name = source.Name + "（请配置连接）",
                    Endpoint = source.Endpoint, Root = source.Root, Enabled = false
                });
            }
            foreach (var track in parsed.Tracks)
            {
                if (NetworkLibraryStore.Track(track.Id) != null) continue;
                track.Indexed = false; track.Pinned = true;
                NetworkLibraryStore.Put(new[] { track });
            }
            return parsed.References;
        }

        /// <summary>「添加到哪个播放列表」的原生小对话框。取消返回 null，不回退成默认名。</summary>
        internal Task<string?> PickTargetPlaylistAsync(object window)
        {
            using var dialog = new System.Windows.Forms.Form
            {
                Text = "添加到播放列表", Width = 420, Height = 160,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false
            };
            var choice = new System.Windows.Forms.ComboBox { Dock = System.Windows.Forms.DockStyle.Top, MaxLength = 120 };
            choice.Items.AddRange(NamedPlaylistStore.List().Cast<object>().ToArray());
            var add = new System.Windows.Forms.Button { Text = "添加", Dock = System.Windows.Forms.DockStyle.Bottom, DialogResult = System.Windows.Forms.DialogResult.OK };
            dialog.Controls.Add(choice); dialog.Controls.Add(add); dialog.AcceptButton = add;
            if (dialog.ShowDialog(window as System.Windows.Forms.IWin32Window) != System.Windows.Forms.DialogResult.OK) return Task.FromResult<string?>(null);
            string name = choice.Text.Trim();
            if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("请输入有效的播放列表名称。");
            if (!NamedPlaylistStore.List().Contains(name)) NamedPlaylistStore.Create(name);
            return Task.FromResult<string?>(name);
        }
    }
}
