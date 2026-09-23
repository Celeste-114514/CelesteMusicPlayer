using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 专辑详情页的 DSD 预加载入口（2026-09-24）。
    /// 背景：FiiO KA13 上实时装箱播 DSD 会在固定位置卡顿；同一首预先装箱好的 WAV 直读则不卡（绿灯）。
    /// 所以在专辑页提供"预加载整专辑 DSD"按钮，把整专辑 DSF 提前转成缓存文件，播放时命中缓存即直读。
    /// 缓存目录由用户在选项设置里指定，可单独/整专辑清除；未命中缓存时播放行为完全不变。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly List<string> _albumDetailDsdFiles = new();
        private CancellationTokenSource? _dsdPreloadCts;
        private bool _dsdPreloadRunning;

        /// <summary>打开专辑详情时调用：按该专辑的曲目更新预加载区域（无 DSD 曲目则整个区域隐藏）。</summary>
        private void UpdateAlbumDetailPreloadUi(List<PlaylistItem> tracks)
        {
            _albumDetailDsdFiles.Clear();
            if (tracks != null)
            {
                foreach (PlaylistItem t in tracks)
                {
                    string p = t.FilePath ?? string.Empty;
                    if (!string.IsNullOrEmpty(p) && IsDsdFile(p))
                    {
                        _albumDetailDsdFiles.Add(p);
                    }
                }
            }

            bool show = _albumDetailDsdFiles.Count > 0;
            if (AlbumDetailPreloadPanel != null)
            {
                AlbumDetailPreloadPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AlbumDetailPreloadHint == null)
            {
                return;
            }

            if (!show)
            {
                AlbumDetailPreloadHint.Visibility = Visibility.Collapsed;
                return;
            }

            int cached = _albumDetailDsdFiles.Count(HasDsdCache);
            AlbumDetailPreloadHint.Visibility = Visibility.Visible;
            AlbumDetailPreloadHint.Text = cached == 0
                ? $"本专辑 {_albumDetailDsdFiles.Count} 首 DSD，尚未预加载（缓存目录：{DsdPreloadService.CacheRoot}）"
                : $"已预加载 {cached}/{_albumDetailDsdFiles.Count} 首（缓存目录：{DsdPreloadService.CacheRoot}）";
        }

        private static bool HasDsdCache(string dsf)
            => DsdPreloadService.TryGetCached(dsf, out _);

        private async void AlbumDetailPreloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dsdPreloadRunning)
            {
                // 运行中再点 = 取消
                try { _dsdPreloadCts?.Cancel(); } catch (Exception) { /* 忽略 */ }
                return;
            }

            List<string> files = _albumDetailDsdFiles.ToList();
            if (files.Count == 0)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _dsdPreloadCts = cts;
            _dsdPreloadRunning = true;
            SetPreloadBusy(true);

            int done = 0;
            int fail = 0;
            string? lastError = null;

            try
            {
                await Task.Run(() =>
                {
                    foreach (string f in files)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            break;
                        }

                        if (DsdPreloadService.TryGetCached(f, out _))
                        {
                            done++;
                            ReportPreloadProgress(done, fail, files.Count, null);
                            continue;
                        }

                        bool ok = DsdPreloadService.Build(f, out _, out string? err);
                        if (ok)
                        {
                            done++;
                        }
                        else
                        {
                            fail++;
                            lastError = err;
                        }

                        ReportPreloadProgress(done, fail, files.Count, lastError);
                    }
                }, cts.Token).ConfigureAwait(true);
            }
            catch (Exception caught)
            {
                lastError = caught.Message;
                StartupLog.WriteException("MainWindow.DsdPreload.cs", caught);
            }
            finally
            {
                _dsdPreloadRunning = false;
                _dsdPreloadCts = null;
                SetPreloadBusy(false);
                RefreshPreloadHint(done, fail, files.Count, lastError);
            }
        }

        private void ReportPreloadProgress(int done, int fail, int total, string? lastError)
        {
            _ = DispatcherQueue.TryEnqueue(() => RefreshPreloadHint(done, fail, total, lastError));
        }

        private void RefreshPreloadHint(int done, int fail, int total, string? lastError)
        {
            if (AlbumDetailPreloadHint == null)
            {
                return;
            }

            string s = $"预加载中/完成：{done}/{total}" + (fail > 0 ? $"，失败 {fail}" : string.Empty);
            if (!string.IsNullOrEmpty(lastError))
            {
                s += "（" + lastError + "）";
            }

            AlbumDetailPreloadHint.Visibility = Visibility.Visible;
            AlbumDetailPreloadHint.Text = s;
        }

        private void SetPreloadBusy(bool busy)
        {
            if (AlbumDetailPreloadText != null)
            {
                AlbumDetailPreloadText.Text = busy ? "取消预加载" : "预加载整专辑 DSD";
            }

            if (AlbumDetailPreloadButton != null)
            {
                AlbumDetailPreloadButton.IsEnabled = true;
            }

            if (AlbumDetailPreloadClearButton != null)
            {
                AlbumDetailPreloadClearButton.IsEnabled = !busy;
            }
        }

        private void AlbumDetailPreloadClearButton_Click(object sender, RoutedEventArgs e)
        {
            int n = 0;
            foreach (string f in _albumDetailDsdFiles)
            {
                if (DsdPreloadService.DeleteFor(f))
                {
                    n++;
                }
            }

            if (AlbumDetailPreloadHint != null)
            {
                AlbumDetailPreloadHint.Visibility = Visibility.Visible;
                AlbumDetailPreloadHint.Text = n == 0
                    ? "本专辑当前没有缓存文件。"
                    : $"已删除本专辑 {n} 个缓存文件（原 DSF 未动）。";
            }
        }
    }
}
