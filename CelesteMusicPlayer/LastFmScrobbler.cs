using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    public sealed class LastFmCredentials
    {
        public string ApiKey { get; set; } = string.Empty;

        public string SharedSecret { get; set; } = string.Empty;

        public string SessionKey { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;
    }

    public sealed class LastFmTrackInfo
    {
        public string Artist { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public int DurationSeconds { get; set; }
    }

    /// <summary>Last.fm scrobble：未配置凭据时为 no-op。</summary>
    public static class LastFmScrobbler
    {
        private const string ApiRootHttps = "https://ws.audioscrobbler.com/2.0/";
        private const string ApiRootHttp = "http://ws.audioscrobbler.com/2.0/";

        private static string GetApiRoot()
        {
            AppSettingsState s = AppSettingsStore.Load();
            return s.LastFmHttps ? ApiRootHttps : ApiRootHttp;
        }
        private const string FileName = "lastfm.json";
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static readonly Queue<(string Method, Dictionary<string, string> Params)> Pending = new();
        private static readonly object Gate = new();
        private static LastFmCredentials? _cache;

        private static string GetFilePath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static LastFmCredentials LoadCredentials()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return Clone(_cache);
                }

                try
                {
                    string path = GetFilePath();
                    if (File.Exists(path))
                    {
                        LastFmCredentials? loaded = JsonSerializer.Deserialize<LastFmCredentials>(File.ReadAllText(path));
                        _cache = loaded ?? new LastFmCredentials();
                    }
                    else
                    {
                        _cache = new LastFmCredentials();
                    }
                }
                catch
                {
                    _cache = new LastFmCredentials();
                }

                return Clone(_cache);
            }
        }

        public static void SaveCredentials(LastFmCredentials credentials)
        {
            lock (Gate)
            {
                _cache = credentials ?? new LastFmCredentials();
                try
                {
                    string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(GetFilePath(), json);
                }
                catch
                {
                }
            }
        }

        public static bool IsConfigured()
        {
            LastFmCredentials c = LoadCredentials();
            return !string.IsNullOrWhiteSpace(c.ApiKey)
                   && !string.IsNullOrWhiteSpace(c.SharedSecret)
                   && !string.IsNullOrWhiteSpace(c.SessionKey);
        }

        public static void QueueNowPlaying(LastFmTrackInfo track)
        {
            if (!IsConfigured() || track == null)
            {
                return;
            }

            LastFmCredentials c = LoadCredentials();
            var parameters = BuildTrackParameters(track);
            parameters["method"] = "track.updateNowPlaying";
            parameters["api_key"] = c.ApiKey;
            parameters["sk"] = c.SessionKey;
            Enqueue("track.updateNowPlaying", parameters);
        }

        public static void QueueScrobble(LastFmTrackInfo track, DateTime startedUtc)
        {
            if (!IsConfigured() || track == null)
            {
                return;
            }

            // 阈值：播放时长需达到「最少秒数」且达到「时长百分比」才记录
            AppSettingsState s = AppSettingsStore.Load();
            double playedSeconds = (DateTime.UtcNow - startedUtc.ToUniversalTime()).TotalSeconds;
            if (playedSeconds < s.LastFmLeastSeconds)
            {
                return;
            }

            if (track.DurationSeconds > 0
                && playedSeconds < track.DurationSeconds * s.LastFmLeastPercent / 100.0)
            {
                return;
            }

            LastFmCredentials c = LoadCredentials();
            var parameters = BuildTrackParameters(track);
            parameters["method"] = "track.scrobble";
            parameters["timestamp"] = ((DateTimeOffset)startedUtc.ToUniversalTime()).ToUnixTimeSeconds().ToString();
            parameters["api_key"] = c.ApiKey;
            parameters["sk"] = c.SessionKey;
            Enqueue("track.scrobble", parameters);
        }

        public static async Task FlushQueueAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
            {
                return;
            }

            (string Method, Dictionary<string, string> Params)[] batch;
            lock (Gate)
            {
                batch = Pending.ToArray();
                Pending.Clear();
            }

            LastFmCredentials c = LoadCredentials();
            foreach ((string _, Dictionary<string, string> parameters) in batch)
            {
                try
                {
                    parameters["api_sig"] = ComputeApiSig(parameters, c.SharedSecret);
                    using FormUrlEncodedContent content = new(parameters);
                    using HttpResponseMessage response = await Http.PostAsync(GetApiRoot(), content, cancellationToken).ConfigureAwait(false);
                    _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        public static async Task<bool> TryGetMobileSessionAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            LastFmCredentials c = LoadCredentials();
            if (string.IsNullOrWhiteSpace(c.ApiKey) || string.IsNullOrWhiteSpace(c.SharedSecret))
            {
                return false;
            }

            try
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["method"] = "auth.getMobileSession",
                    ["api_key"] = c.ApiKey,
                    ["username"] = username,
                    ["password"] = Md5Hex(password)
                };
                parameters["api_sig"] = ComputeApiSig(parameters, c.SharedSecret);

                using FormUrlEncodedContent content = new(parameters);
                using HttpResponseMessage response = await Http.PostAsync(GetApiRoot(), content, cancellationToken).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("session", out JsonElement session))
                {
                    return false;
                }

                c.Username = session.TryGetProperty("name", out JsonElement nameEl)
                    ? nameEl.GetString() ?? username
                    : username;
                c.SessionKey = session.TryGetProperty("key", out JsonElement keyEl)
                    ? keyEl.GetString() ?? string.Empty
                    : string.Empty;
                SaveCredentials(c);
                return !string.IsNullOrWhiteSpace(c.SessionKey);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> BuildTrackParameters(LastFmTrackInfo track) =>
            new(StringComparer.Ordinal)
            {
                ["artist"] = track.Artist ?? string.Empty,
                ["track"] = track.Title ?? string.Empty,
                ["album"] = track.Album ?? string.Empty,
                ["duration"] = Math.Max(0, track.DurationSeconds).ToString()
            };

        private static void Enqueue(string method, Dictionary<string, string> parameters)
        {
            lock (Gate)
            {
                Pending.Enqueue((method, new Dictionary<string, string>(parameters, StringComparer.Ordinal)));
            }
        }

        private static string ComputeApiSig(Dictionary<string, string> parameters, string sharedSecret)
        {
            var sorted = new SortedDictionary<string, string>(parameters, StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in sorted)
            {
                if (pair.Key == "format")
                {
                    continue;
                }

                sb.Append(pair.Key);
                sb.Append(pair.Value);
            }

            sb.Append(sharedSecret);
            return Md5Hex(sb.ToString());
        }

        private static string Md5Hex(string input)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static LastFmCredentials Clone(LastFmCredentials c) => new()
        {
            ApiKey = c.ApiKey,
            SharedSecret = c.SharedSecret,
            SessionKey = c.SessionKey,
            Username = c.Username
        };
    }
}
