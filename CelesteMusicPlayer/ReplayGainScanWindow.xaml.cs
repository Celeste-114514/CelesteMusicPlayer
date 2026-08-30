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
    /// <summary>ReplayGain 扫描范围。</summary>
    public enum ReplayGainScanScope
    {
        Library,
        Playlist,
        Selection
    }

    /// <summary>
    /// ReplayGain 扫描窗口：选范围 → 算响度 →（可选）写回标签。
    /// 仅写入标签，不改动音频数据；默认勾选「写入标签」，取消勾选则为预览。
    /// </summary>
    public sealed partial class ReplayGainScanWindow : Window
    {
        private readonly Func<ReplayGainScanScope, List<RgScanInput>> _provider;
        private readonly ReplayGainScanner _scanner = new();
        private CancellationTokenSource? _cts;
        private bool _running;

        /// <summary>provider：根据范围返回待扫描曲目（含专辑信息用于分组）。</summary>
        public ReplayGainScanWindow(MainWindow owner, Func<ReplayGainScanScope, List<RgScanInput>> provider)
        {
            InitializeComponent();
            _provider = provider;
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            ReplayGainScanScope scope = ScopeLibrary.IsChecked == true ? ReplayGainScanScope.Library
                : ScopePlaylist.IsChecked == true ? ReplayGainScanScope.Playlist
                : ReplayGainScanScope.Selection;

            List<RgScanInput>? inputs = null;
            try { inputs = _provider(scope); }
            catch (Exception ex) { StartupLog.WriteException("ReplayGainScanWindow.Provider", ex); }

            if (inputs == null || inputs.Count == 0) { Log("没有可扫描的曲目。"); return; }

            bool write = WriteTagsBox.IsChecked == true;
            _running = true;
            _cts = new CancellationTokenSource();
            StartButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ProgressBar.Value = 0;
            Log($"开始扫描：{inputs.Count} 首，{(write ? "写入标签" : "仅预览")}。");
            Log("提示：预览满意后再勾选「写入标签」重新扫描即可落盘。");

            try
            {
                await RunScanAsync(inputs, write, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("已取消。");
            }
            catch (Exception ex)
            {
                Log("扫描出错：" + ex.Message);
                StartupLog.WriteException("ReplayGainScanWindow.RunScan", ex);
            }
            finally
            {
                _running = false;
                StartButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                StatusText.Text = "完成";
            }
        }

        private async Task RunScanAsync(List<RgScanInput> inputs, bool write, CancellationToken ct)
        {
            // 按专辑分组（AlbumArtist + Album）；无专辑名的每首自成一组（album gain = track gain）。
            var groups = new Dictionary<string, List<RgScanInput>>(StringComparer.OrdinalIgnoreCase);
            foreach (RgScanInput it in inputs)
            {
                string key = string.IsNullOrWhiteSpace(it.Album)
                    ? "file:" + it.FilePath
                    : ((it.AlbumArtist?.Trim() ?? string.Empty).ToLowerInvariant() + "||" + it.Album.Trim().ToLowerInvariant());
                if (!groups.TryGetValue(key, out var list)) { list = new List<RgScanInput>(); groups[key] = list; }
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
                    ReplayGainScanner.AlbumResult ar = await _scanner.MeasureAlbumAsync(paths, ct).ConfigureAwait(false);
                    if (ar.Error == null && !ar.Unsupported)
                    {
                        albumGain = ar.AlbumGainDb;
                        albumPeak = ar.AlbumPeakLinear;
                        Log($"[专辑] {g[0].Album}：album gain {albumGain:F2} dB, peak {albumPeak:F3}");
                    }
                    else
                    {
                        Log($"[专辑] {g[0].Album}：专辑测量失败（{ar.Error}），回退为按单曲。");
                    }
                }

                foreach (RgScanInput track in g)
                {
                    ct.ThrowIfCancellationRequested();
                    ReplayGainScanner.TrackResult tr = await _scanner.MeasureTrackAsync(track.FilePath, ct).ConfigureAwait(false);
                    if (tr.Unsupported || tr.Error != null)
                    {
                        skipped++;
                        Log($"[跳过] {Path.GetFileName(track.FilePath)}：{tr.Error}");
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
                        Log($"[{(write ? "写入" : "预览")}] {Path.GetFileName(track.FilePath)}：track {tr.TrackGainDb:F2} dB, peak {tr.TrackPeakLinear:F3} | album {ag:F2} dB");
                    }

                    done++;
                    double pct = (double)done / total;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressBar.Value = pct;
                        StatusText.Text = $"{done}/{total}";
                    });
                }
            }

            Log($"完成：成功 {ok}，跳过 {skipped}，共 {total}。");
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); }
            catch { /* 已取消 */ }
        }

        private void Log(string line)
        {
            DispatcherQueue.TryEnqueue(() => { LogBox.Text += line + "\n"; });
        }
    }
}
