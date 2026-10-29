using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private sealed record WebAudioFact(long stamp, int rate, int bits, string codec, bool? lossless);
        private readonly ConcurrentDictionary<string, WebAudioFact> _webCollectionFacts = new(StringComparer.OrdinalIgnoreCase);

        private static bool? WebLossless(string value)
        {
            string codec = value.ToLowerInvariant();
            if (codec.Contains("hybrid")) return null;
            if (new[] { "aac", "mp3", "mpeg", "vorbis", "opus", "musepack", "ac-3", "ac3", "eac3", "lossy" }.Any(codec.Contains)
                && !codec.Contains("alac") && !codec.Contains("lossless")) return false;
            if (new[] { "flac", "alac", "apple lossless", "lossless", "pcm", "wavpack", "monkey", "ape", "true audio", "tta", "truehd", "mlp", "dsd" }.Any(codec.Contains)) return true;
            return null;
        }

        private WebAudioFact WebReadAudioFact(PlaylistItem item, CancellationToken cancellation)
        {
            string path = item.FilePath;
            try
            {
                if (!item.IsRemote && Path.GetExtension(path).ToLowerInvariant() is ".dsf" or ".dff")
                {
                    uint nativeRate = DsdProbe.ProbeFreqHz(path);
                    if (nativeRate > 0) return new WebAudioFact(0, checked((int)nativeRate), 1, "dsd", true);
                }
                if (AudioInfoFormatter.TryPeekFacts(path, out int rate, out int bits, out string codec) && rate >= 8000 && bits > 0)
                    return new WebAudioFact(0, rate, bits, codec, WebLossless(codec));
                if (item.IsRemote) return new WebAudioFact(0, 0, 0, "", null);
                long stamp = File.GetLastWriteTimeUtc(path).Ticks;
                if (_webCollectionFacts.TryGetValue(path, out var cached) && cached.stamp == stamp) return cached;
                // Header/tag reads only: no full decode and no new ffmpeg process.
                using var file = TagLib.File.Create(path);
                var properties = file.Properties;
                codec = properties.Description ?? "";
                rate = properties.AudioSampleRate >= 8000 ? properties.AudioSampleRate : 0;
                bits = properties.BitsPerSample;
                if (rate == 0 || bits == 0 || WebLossless(codec) == null)
                {
                    var probed = WebProbeHeader(path, cancellation);
                    if (probed.rate > 0) rate = probed.rate;
                    if (probed.bits > 0) bits = probed.bits;
                    if (probed.codec.Length > 0) codec = probed.codec;
                }
                var result = new WebAudioFact(stamp, rate, bits, codec, WebLossless(codec));
                if (_webCollectionFacts.Count >= 8192) _webCollectionFacts.Clear();
                _webCollectionFacts[path] = result;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                cancellation.ThrowIfCancellationRequested();
                if (!item.IsRemote && File.Exists(path))
                {
                    try { var probed = WebProbeHeader(path, cancellation) with { stamp = File.GetLastWriteTimeUtc(path).Ticks }; _webCollectionFacts[path] = probed; return probed; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                return new WebAudioFact(0, 0, 0, "", null);
            }
        }

        private static WebAudioFact WebProbeHeader(string path, CancellationToken cancellation)
        {
            string? executable = AudioPlaybackEngine.FindFfmpeg();
            if (executable == null) return new(0, 0, 0, "", null);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-hide_banner", "-nostdin", "-i", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null) return new(0, 0, 0, "", null);
            using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000)) { try { process.Kill(true); } catch { } return new(0, 0, 0, "", null); }
            cancellation.ThrowIfCancellationRequested();
            string line = error.GetAwaiter().GetResult().Split('\n').FirstOrDefault(value => value.Contains("Audio:")) ?? "";
            string codec = Regex.Match(line, @"Audio:\s*([a-zA-Z0-9_]+)").Groups[1].Value;
            int.TryParse(Regex.Match(line, @"(\d+)\s*Hz").Groups[1].Value, out int rate);
            if (codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase)) rate = checked(rate * 8);
            return new(0, rate, AudioInfoFormatter.ParseBitDepth(line, codec), codec, WebLossless(codec));
        }

        internal async Task<object> WebCollectionOverviewAsync(string category, string name, CancellationToken cancellation)
        {
            if (name.Length > 2048 || category is not ("artists" or "albumArtists" or "albums")) throw new ArgumentException("未知的曲库分类。");
            string Key(PlaylistItem item) => category == "albums" ? item.Album : category == "albumArtists" ? item.AlbumArtist : item.Artist;
            var tracks = Library.AllTracks().Where(item => string.Equals(Key(item), name, StringComparison.CurrentCultureIgnoreCase)).Select(item => item.Clone()).ToArray();
            if (tracks.Length == 0) throw new InvalidOperationException("曲库已变化，请重新打开详情。");
            return await Task.Run<object>(() =>
            {
                int lossless = 0, lossy = 0, unknown = 0;
                var known = new Dictionary<string, WebAudioFact>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in tracks)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!known.TryGetValue(item.FilePath, out var fact)) { fact = WebReadAudioFact(item, cancellation); known[item.FilePath] = fact; }
                    if (fact.lossless == true) lossless++; else if (fact.lossless == false) lossy++; else unknown++;
                }
                var pcm = known.Values.Where(fact => fact.rate >= 8000 && !fact.codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase)).OrderByDescending(fact => fact.rate).ThenByDescending(fact => fact.bits).FirstOrDefault();
                var dsd = known.Values.Where(fact => fact.codec.Contains("dsd", StringComparison.OrdinalIgnoreCase) && fact.rate >= 1_000_000).OrderByDescending(fact => fact.rate).FirstOrDefault();
                var specs = new List<string>();
                if (pcm != null) specs.Add((pcm.bits > 0 ? pcm.bits + "bit" : "位深未知") + " / " + (pcm.rate / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "kHz");
                if (dsd != null) specs.Add("DSD / " + (dsd.rate / 1_000_000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "MHz");
                uint[] years = tracks.Where(item => item.Year > 0).Select(item => item.Year).ToArray();
                return new
                {
                    songCount = tracks.Length, albumCount = tracks.Select(item => item.Album).Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
                    duration = tracks.Sum(item => item.Duration.TotalSeconds), yearFrom = years.Length > 0 ? years.Min() : 0, yearTo = years.Length > 0 ? years.Max() : 0,
                    losslessCount = lossless, lossyCount = lossy, unknownCount = unknown,
                    losslessPercent = lossless + lossy > 0 ? (double?)Math.Round(lossless * 100d / (lossless + lossy), 1) : null,
                    highestSpec = string.Join(" · ", specs),
                    localCount = tracks.Count(item => !item.IsRemote), remoteCount = tracks.Count(item => item.IsRemote),
                    genres = tracks.Where(item => !string.IsNullOrWhiteSpace(item.Genre) && item.Genre != "未知流派").GroupBy(item => item.Genre, StringComparer.CurrentCultureIgnoreCase).OrderByDescending(group => group.Count()).Take(5).Select(group => group.Key).ToArray()
                };
            }, cancellation);
        }
    }
}
