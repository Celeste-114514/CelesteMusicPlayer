using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 在线来源（WebDAV / 百度 / Jellyfin / Emby / SMB / SSHFS / Subsonic）的配置与浏览。
    ///
    /// W13：从 <c>MainWindow.WebSources.cs</c> 搬出的 `sources` 命令整块 ——
    /// 新建/修改来源、开关、同步、取消、测试连接、浏览目录、钉住曲目、百度授权。
    /// 这些都只跟网络库存储与 provider 打交道，唯一要宿主的只有「取消在途下载」（令牌在播放域）。
    ///
    /// ⚠ 别把这个服务做成"顺手也能起播"：起播、原音频下载、缓存租约都在播放域，
    /// 混进来就会把两套令牌生命周期缠在一起。
    /// </summary>
    internal sealed class WebSourcesService(ISourcePlaybackHost host)
    {
        internal async Task<object> HandleAsync(JsonElement msg)
        {
            NetworkLibraryStore.MigrateWebDav();
            string operation = ReadString(msg, "operation"), id = ReadString(msg, "sourceId");
            try
            {
                switch (operation)
                {
                    case "list": return Snapshot();
                    case "save":
                    {
                        lock (NetworkLibraryService.MutationGate)
                        {
                            var source = id.Length == 0 ? new NetworkSource() : NetworkLibraryStore.Source(id);
                            NetworkLibraryService.Cancel(source.Id);
                            if (id.Length > 0 && msg.TryGetProperty("revision", out var revision) && revision.GetInt32() != source.Revision) throw new InvalidOperationException("来源已被其他窗口修改，请重新打开编辑。");
                            string provider = ReadString(msg, "provider");
                            if (!NetworkSourceProvider.Providers.Contains(provider)) throw new ArgumentException("请选择来源类型。");
                            if (id.Length > 0 && source.Provider != provider) throw new ArgumentException("已有来源不能变更类型，请新建连接。");
                            source.Provider = provider; source.Name = ReadString(msg, "name").Trim(); source.Endpoint = ReadString(msg, "endpoint").Trim();
                            if (source.Provider == "baidu" && source.Endpoint.Length == 0) source.Endpoint = "https://pan.baidu.com";
                            if (source.Name.Length is < 1 or > 100 || source.Endpoint.Length == 0) throw new ArgumentException("请填写来源名称和地址。");
                            if (provider is not ("smb" or "sshfs"))
                            {
                                if (!Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
                                    throw new ArgumentException("请输入 http(s) 服务器地址，账号与密码请填在独立字段。");
                            }
                            else if (!Path.IsPathFullyQualified(source.Endpoint)) throw new ArgumentException("请使用完整 UNC、映射盘或 SSHFS 挂载路径。");
                            source.Root = ReadString(msg, "root"); source.Username = ReadString(msg, "username"); source.UserId = ReadString(msg, "userId");
                            source.Auth = ReadString(msg, "auth") == "token" ? "token" : "password"; source.ClientId = ReadString(msg, "clientId");
                            if (msg.TryGetProperty("enabled", out var enabled)) source.Enabled = enabled.GetBoolean();
                            foreach (var pair in new[] { ("secret", 0), ("refreshToken", 1), ("clientSecret", 2) })
                            {
                                string value = ReadString(msg, pair.Item1);
                                if (value.Length == 0) continue;
                                string protectedValue = NetworkSecrets.Protect(value);
                                if (pair.Item2 == 0) source.ProtectedSecret = protectedValue;
                                else if (pair.Item2 == 1) source.ProtectedRefreshToken = protectedValue;
                                else source.ProtectedClientSecret = protectedValue;
                            }
                            source.Revision++; NetworkLibraryStore.SaveSource(source); NetworkLibraryService.NotifyChanged();
                            return Snapshot(source.Id);
                        }
                    }
                    case "toggle":
                        lock (NetworkLibraryService.MutationGate)
                        {
                            var source = NetworkLibraryStore.Source(id); NetworkLibraryService.Cancel(id); source.Enabled = !source.Enabled; source.Revision++;
                            NetworkLibraryStore.SaveSource(source); NetworkLibraryService.NotifyChanged(); return Snapshot(id);
                        }
                    case "sync": NetworkLibraryService.StartSync(id); return Snapshot(id);
                    case "cancel": NetworkLibraryService.Cancel(id); return Snapshot(id);
                    case "cancelDownload": host.CancelSourceDownload(); return new { cancelled = true };
                    case "clearCache": return new { count = NetworkLibraryService.ClearCache() };
                    case "test":
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                        var source = NetworkLibraryStore.Source(id);
                        var entries = await NetworkSourceProvider.For(source).BrowseAsync(source, "", timeout.Token);
                        return new { ok = true, message = "连接成功，根目录 " + entries.Length + " 项。" };
                    }
                    case "browse":
                    {
                        var source = NetworkLibraryStore.Source(id); string path = ReadString(msg, "path");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                        var provider = NetworkSourceProvider.For(source); var entries = await provider.BrowseAsync(source, path, timeout.Token);
                        var tracks = await NetworkLibraryService.BuildTracksAsync(source, entries.ToList(), provider, timeout.Token);
                        if (NetworkLibraryStore.Source(id).Revision != source.Revision) throw new InvalidOperationException("来源已修改，请重新浏览。");
                        NetworkLibraryStore.Put(tracks);
                        return new { sourceId = id, path, folders = entries.Where(e => e.Folder).Select(e => new { path = e.Key, name = e.Name }).ToArray(),
                            tracks = tracks.Select(t => { var item = WebLibraryService.FromNetwork(t, source); return new { id = t.Id, title = item.Title, artist = item.Artist, album = item.Album,
                                duration = item.Duration.TotalSeconds, cue = t.Cue, cached = File.Exists(item.FilePath), coverUrl = "https://celeste-cover.internal/cover/" + t.Id }; }).ToArray() };
                    }
                    case "pin": NetworkLibraryStore.Pin(ReadString(msg, "trackId")); NetworkLibraryService.NotifyChanged(); return Snapshot(id);
                    case "authorize":
                        var authSource = NetworkLibraryStore.Source(id);
                        if (authSource.Provider != "baidu") throw new ArgumentException("仅百度来源使用此授权入口。");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BaiduSourceProvider.AuthorizeUrl(authSource)) { UseShellExecute = true });
                        return new { opened = true };
                    case "exchange":
                    {
                        var source = NetworkLibraryStore.Source(id);
                        if (source.Provider != "baidu") throw new ArgumentException("仅百度来源使用授权码。");
                        await BaiduSourceProvider.ExchangeAsync(source, ReadString(msg, "code"), CancellationToken.None); return Snapshot(id);
                    }
                    default: throw new ArgumentException("未知的网络来源操作。");
                }
            }
            catch (Exception ex) { throw new InvalidOperationException(NetworkLibraryService.Friendly(ex)); }
        }

        private static object Snapshot(string selectedId = "")
        {
            var tracks = NetworkLibraryStore.Tracks();
            return new { selectedId, providers = NetworkSourceProvider.Providers, sources = NetworkLibraryStore.Sources().Select(s => new
            {
                id = s.Id, provider = s.Provider, name = s.Name, endpoint = s.Endpoint, root = s.Root, username = s.Username, auth = s.Auth, userId = s.UserId,
                clientId = s.ClientId, enabled = s.Enabled, hasSecret = s.ProtectedSecret.Length > 0, hasRefresh = s.ProtectedRefreshToken.Length > 0,
                hasClientSecret = s.ProtectedClientSecret.Length > 0, count = tracks.Count(t => t.SourceId == s.Id && (t.Indexed || t.Pinned) && !t.Missing),
                lastSync = s.LastSync, error = s.LastError, revision = s.Revision, job = new { running = NetworkLibraryService.Job(s.Id).Running, phase = NetworkLibraryService.Job(s.Id).Phase, discovered = NetworkLibraryService.Job(s.Id).Discovered, error = NetworkLibraryService.Job(s.Id).Error }
            }).ToArray() };
        }

        private static string ReadString(JsonElement msg, string property)
        {
            if (msg.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            return "";
        }
    }
}
