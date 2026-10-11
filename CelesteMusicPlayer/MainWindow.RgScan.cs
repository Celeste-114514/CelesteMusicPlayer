using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ReplayGain 批量扫描（2026-10-12 阶段 2）：原先是独立窗口 ReplayGainScanWindow，
    /// 现在内嵌到「音效处理 → 响度 · ReplayGain」页。
    /// 扫描逻辑照搬原窗口（同一个 ReplayGainScanner），只把 UI 读写换成主窗口内的控件。
    /// 铁律不变：只写标签，绝不改动音频数据。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly ReplayGainScanner _rgScanScanner = new();
        private CancellationTokenSource? _rgScanCts;
        private bool _rgScanRunning;

        private async void RgScanStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rgScanRunning)
            {
                return;
            }

            ReplayGainScanScope scope = RgScanScopePlaylist?.IsChecked == true ? ReplayGainScanScope.Playlist
                : RgScanScopeSelection?.IsChecked == true ? ReplayGainScanScope.Selection
                : ReplayGainScanScope.Library;

            List<RgScanInput>? inputs = null;
            try
            {
                inputs = BuildRgScanInputs(scope);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("RgScan.Provider", ex);
            }

            if (inputs == null || inputs.Count == 0)
            {
                RgScanLog("没有可扫描的曲目。");
                return;
            }

            bool write = RgScanWriteTagsBox?.IsChecked == true;
            _rgScanRunning = true;
            _rgScanCts = new CancellationTokenSource();
            if (RgScanStartButton != null) RgScanStartButton.IsEnabled = false;
            if (RgScanCancelButton != null) RgScanCancelButton.IsEnabled = true;
            if (RgScanProgressBar != null) RgScanProgressBar.Value = 0;

            RgScanLog($"开始扫描：{inputs.Count} 首，{(write ? "写入标签" : "仅预览")}。");
            RgScanLog("提示：预览满意后再勾选「写入标签」重新扫描即可落盘。");

            try
            {
                await RunRgScanAsync(inputs, write, _rgScanCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RgScanLog("已取消。");
            }
            catch (Exception ex)
            {
                RgScanLog("扫描出错：" + ex.Message);
                StartupLog.WriteException("RgScan.Run", ex);
            }
            finally
            {
                _rgScanRunning = false;
                if (RgScanStartButton != null) RgScanStartButton.IsEnabled = true;
                if (RgScanCancelButton != null) RgScanCancelButton.IsEnabled = false;
                RgScanStatus("完成");
            }
        }

        private void RgScanCancelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _rgScanCts?.Cancel();
            }
            catch
            {
                // 已取消，忽略
            }
        }

        /// <summary>按范围给出待扫描曲目（原 OpenReplayGainScan 里的 provider，搬过来复用）。</summary>
        private List<RgScanInput> BuildRgScanInputs(ReplayGainScanScope scope)
        {
            if (scope == ReplayGainScanScope.Library)
            {
                return LibraryDb.GetAllTracksForScan();
            }

            if (scope == ReplayGainScanScope.Playlist)
            {
                return _playlist
                    .Select(p => new RgScanInput { FilePath = p.FilePath, Album = p.Album, AlbumArtist = p.AlbumArtist })
                    .ToList();
            }

            return GetSelectedMultiSelectSongs()
                .Select(p => new RgScanInput { FilePath = p.FilePath, Album = p.Album, AlbumArtist = p.AlbumArtist })
                .ToList();
        }

        private async Task RunRgScanAsync(List<RgScanInput> inputs, bool write, CancellationToken ct)
        {
            // 按专辑分组（AlbumArtist + Album）；无专辑名的每首自成一组（album gain = track gain）
            Dictionary<string, List<RgScanInput>> groups = new(StringComparer.OrdinalIgnoreCase);
            foreach (RgScanInput it in inputs)
            {
                string key = string.IsNullOrWhiteSpace(it.Album)
                    ? "file:" + it.FilePath
                    : ((it.AlbumArtist?.Trim() ?? string.Empty).ToLowerInvariant() + "||" + it.Album.Trim().ToLowerInvariant());
                if (!groups.TryGetValue(key, out List<RgScanInput>? list))
                {
                    list = new List<RgScanInput>();
                    groups[key] = list;
                }

                list.Add(it);
            }

            int total = inputs.Count;
            int done = 0;
            int ok = 0;
            int skipped = 0;

            foreach (List<RgScanInput> g in groups.Values)
            {
                ct.ThrowIfCancellationRequested();

                double albumGain = double.NaN;
                double albumPeak = double.NaN;

                if (g.Count > 1)
                {
                    List<string> paths = g.Select(x => x.FilePath).ToList();
                    ReplayGainScanner.AlbumResult ar = await _rgScanScanner.MeasureAlbumAsync(paths, ct).ConfigureAwait(false);
                    if (ar.Error == null && !ar.Unsupported)
                    {
                        albumGain = ar.AlbumGainDb;
                        albumPeak = ar.AlbumPeakLinear;
                        RgScanLog($"[专辑] {g[0].Album}：album gain {albumGain:F2} dB, peak {albumPeak:F3}");
                    }
                    else
                    {
                        RgScanLog($"[专辑] {g[0].Album}：专辑测量失败（{ar.Error}），回退为按单曲。");
                    }
                }

                foreach (RgScanInput track in g)
                {
                    ct.ThrowIfCancellationRequested();
                    ReplayGainScanner.TrackResult tr = await _rgScanScanner.MeasureTrackAsync(track.FilePath, ct).ConfigureAwait(false);
                    if (tr.Unsupported || tr.Error != null)
                    {
                        skipped++;
                        RgScanLog($"[跳过] {Path.GetFileName(track.FilePath)}：{tr.Error}");
                    }
                    else
                    {
                        double ag = double.IsNaN(albumGain) ? tr.TrackGainDb : albumGain;
                        double ap = double.IsNaN(albumPeak) ? tr.TrackPeakLinear : albumPeak;
                        if (write)
                        {
                            ReplayGainScanner.WriteTags(track.FilePath, tr.TrackGainDb, tr.TrackPeakLinear, ag, ap);
                        }

                        ok++;
                        RgScanLog($"[{(write ? "写入" : "预览")}] {Path.GetFileName(track.FilePath)}：track {tr.TrackGainDb:F2} dB, peak {tr.TrackPeakLinear:F3} | album {ag:F2} dB");
                    }

                    done++;
                    double pct = (double)done / total;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (RgScanProgressBar != null) RgScanProgressBar.Value = pct;
                        RgScanStatus($"{done}/{total}");
                    });
                }
            }

            RgScanLog($"完成：成功 {ok}，跳过 {skipped}，共 {total}。");
        }

        private void RgScanLog(string line)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (RgScanLogBox != null)
                {
                    RgScanLogBox.Text += line + "\n";
                }
            });
        }

        private void RgScanStatus(string text)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (RgScanStatusText != null)
                {
                    RgScanStatusText.Text = text;
                }
            });
        }
    }
}
