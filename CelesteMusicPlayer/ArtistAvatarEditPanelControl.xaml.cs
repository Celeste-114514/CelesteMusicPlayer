using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 艺术家头像面板（2026-10-12）：在线搜图 + 裁剪，塞在艺术家详情页右侧的
    /// 「歌曲列表 / 专辑墙 / 头像」页签区里，取代原来两个独立窗口。
    ///
    /// 逻辑照搬 ArtistAvatarSearchWindow / ArtistAvatarEditorWindow，
    /// 只把「窗口」语义换成「面板」：不 Activate、不 Close，关闭交给宿主页签。
    /// </summary>
    public sealed partial class ArtistAvatarEditPanelControl : UserControl
    {
        private const double ViewSize = 280;

        private readonly List<OnlineSongResult> _hits = new();
        private readonly SemaphoreSlim _searchGate = new(1, 1);

        private string _artistName = string.Empty;
        private string _storeKey = string.Empty;
        private string _source = "All";
        private CancellationTokenSource? _cts;

        // 裁剪状态
        private bool _dragging;
        private Point _lastPoint;
        private double _imagePixelWidth;
        private double _imagePixelHeight;
        private double _containScale = 1;
        private double _userZoom = 1;
        private double _offsetX;
        private double _offsetY;
        private bool _updatingZoomUi;

        /// <summary>面板就绪标记：InitializeComponent 解析 XAML 时滑杆的初值（Minimum=0.5 ≠ 默认 0）
        /// 会就地触发 ValueChanged，此时 CropViewport / EditImage 这些后面的 x:Name 还没连上，
        /// 直接刷新布局就是空引用——被 XAML 解析器包成「RangeBase.Minimum 赋值失败」弹出来
        /// （2026-10-12 用户实测）。构造跑完才置 true，与 DSP 机架的 _dspRackReady 同一套路。</summary>
        private bool _panelReady;

        /// <summary>头像已保存（返回新头像，宿主持此刷新详情页与艺术家墙）。</summary>
        public event Action<BitmapImage>? AvatarConfirmed;

        /// <summary>用户点「关闭」：宿主应把页签切回歌曲列表 / 专辑墙。</summary>
        public event Action? CloseRequested;

        public ArtistAvatarEditPanelControl()
        {
            InitializeComponent();

            SourceCombo.Items.Add("全部平台");
            SourceCombo.Items.Add("网易云音乐");
            SourceCombo.Items.Add("QQ音乐");
            SourceCombo.Items.Add("Apple Music");
            SourceCombo.Items.Add("MusicBrainz");
            SourceCombo.SelectedIndex = 0;

            CropViewport.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Rect(0, 0, ViewSize, ViewSize)
            };

            _panelReady = true;
        }

        /// <summary>
        /// 为某个艺术家开始：填好名字并自动搜一轮（跟原窗口行为一致：打开即出结果）。
        /// </summary>
        public void BeginFor(string artistName, string storeKey)
        {
            _artistName = artistName ?? string.Empty;
            _storeKey = storeKey ?? string.Empty;
            SearchBox.Text = _artistName;
            ShowResults();
            _ = SearchAsync();
        }

        /// <summary>
        /// 直接用本地图片进入裁剪（右键菜单「从本地选择艺术家头像」走这里）。
        /// 与 BeginFor 的区别：不联网搜索，直接进裁剪区。
        /// </summary>
        public async void BeginWithLocalFile(string artistName, string storeKey, string path)
        {
            _artistName = artistName ?? string.Empty;
            _storeKey = storeKey ?? string.Empty;
            SearchBox.Text = _artistName;
            ShowCrop();
            await LoadSourceImageAsync(path);
        }


        // ── 搜索 ────────────────────────────────────────────────────
        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string previous = _source;
            _source = SourceCombo.SelectedIndex switch
            {
                1 => "NetEase",
                2 => "QQ",
                3 => "iTunes",
                4 => "MusicBrainz",
                _ => "All"
            };

            if (!string.Equals(previous, _source, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = SearchAsync();
            }
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _ = SearchAsync();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

        private async Task SearchAsync()
        {
            string query = SearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                StatusText.Text = "请输入搜索内容";
                return;
            }

            await _searchGate.WaitAsync();
            try
            {
                SearchButton.IsEnabled = false;
                StatusText.Text = "正在搜索…";
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();

                _hits.Clear();
                var sources = _source == "All"
                    ? new[] { "NetEase", "QQ", "iTunes", "MusicBrainz" }
                    : new[] { _source };

                var all = new List<OnlineSongResult>();
                foreach (string src in sources)
                {
                    try
                    {
                        var results = await OnlineMusicApi.SearchArtistsAsync(src, query, _cts.Token);
                        all.AddRange(results);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 单个平台失败不影响其它平台
                    }
                }

                _hits.AddRange(all.Where(r => !string.IsNullOrWhiteSpace(r.CoverUrl))
                    .GroupBy(r => r.CoverUrl)
                    .Select(g => g.First())
                    .ToList());

                ResultGrid.ItemsSource = _hits.Select(h => new ArtistAvatarSearchItem(h)).ToList();
                StatusText.Text = _hits.Count == 0
                    ? "未找到结果，请换个平台或关键词再试"
                    : $"找到 {_hits.Count} 个头像候选，点一张进入裁剪";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "搜索已取消";
            }
            catch
            {
                StatusText.Text = "搜索失败，请检查网络";
            }
            finally
            {
                SearchButton.IsEnabled = true;
                _searchGate.Release();
            }
        }

        private async void ResultGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not ArtistAvatarSearchItem item
                || string.IsNullOrWhiteSpace(item.CoverUrl))
            {
                return;
            }

            try
            {
                StatusText.Text = "正在下载所选头像…";
                string tmp = Path.Combine(Path.GetTempPath(), "celeste-avatar-" + Guid.NewGuid().ToString("N") + ".jpg");
                string? saved = await OnlineMusicApi.DownloadCoverAsync(item.CoverUrl, tmp);
                if (saved == null || !File.Exists(saved))
                {
                    StatusText.Text = "下载头像失败，请重试";
                    return;
                }

                ShowCrop();
                await LoadSourceImageAsync(saved);
            }
            catch (Exception ex)
            {
                StatusText.Text = "下载头像失败：" + ex.Message;
            }
        }

        // ── 裁剪 ────────────────────────────────────────────────────
        private void ShowResults()
        {
            ResultScroll.Visibility = Visibility.Visible;
            CropArea.Visibility = Visibility.Collapsed;
        }

        private void ShowCrop()
        {
            ResultScroll.Visibility = Visibility.Collapsed;
            CropArea.Visibility = Visibility.Visible;
            ApplyShape();
        }

        private void BackToResultsButton_Click(object sender, RoutedEventArgs e) => ShowResults();

        private void CropCancelButton_Click(object sender, RoutedEventArgs e)
        {
            ShowResults();
            _ = SearchAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        private void ShapeButton_Click(object sender, RoutedEventArgs e) => ApplyShape();

        private void ApplyShape()
        {
            bool circle = ShapeCircleButton.IsChecked == true;
            if (circle)
            {
                ShapeRectButton.IsChecked = false;
            }
            else
            {
                ShapeCircleButton.IsChecked = false;
            }

            MaskPath.Visibility = circle ? Visibility.Visible : Visibility.Collapsed;
            RingEllipse.Visibility = circle ? Visibility.Visible : Visibility.Collapsed;
            RingRect.Visibility = circle ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadSourceImageAsync(string path)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenReadAsync();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                double srcW = decoder.PixelWidth;
                double srcH = decoder.PixelHeight;
                if (srcW <= 0 || srcH <= 0)
                {
                    throw new InvalidOperationException("图片尺寸无效。");
                }

                const double maxEdge = 2048;
                double decodeScale = Math.Min(1.0, maxEdge / Math.Max(srcW, srcH));
                uint decodeW = (uint)Math.Max(1, Math.Round(srcW * decodeScale));
                uint decodeH = (uint)Math.Max(1, Math.Round(srcH * decodeScale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = decodeW,
                    ScaledHeight = decodeH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(software);

                _imagePixelWidth = software.PixelWidth;
                _imagePixelHeight = software.PixelHeight;

                EditImage.Source = source;
                ResetToShowFullImage();
                StatusText.Text = "拖动图片调整位置，滚轮或滑杆缩放；框内就是最终头像";
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开图片：" + ex.Message;
                ShowResults();
            }
        }

        private void ResetToShowFullImage()
        {
            if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0)
            {
                return;
            }

            _containScale = Math.Min(ViewSize / _imagePixelWidth, ViewSize / _imagePixelHeight);
            _userZoom = 1;
            _offsetX = 0;
            _offsetY = 0;
            SyncZoomSlider();
            ApplyImageLayout();
        }

        private double CurrentScale => _containScale * _userZoom;

        private void SyncZoomSlider()
        {
            _updatingZoomUi = true;
            try
            {
                ZoomSlider.Value = _userZoom;
            }
            finally
            {
                _updatingZoomUi = false;
            }
        }

        private void ApplyImageLayout()
        {
            // 双保险：万一有别的路径在面板就绪前摸到这里（EditImage 还没连上），直接退出去
            if (EditImage == null || _imagePixelWidth <= 0 || _imagePixelHeight <= 0)
            {
                return;
            }

            double scale = CurrentScale;
            double dispW = _imagePixelWidth * scale;
            double dispH = _imagePixelHeight * scale;

            EditImage.Width = dispW;
            EditImage.Height = dispH;
            Canvas.SetLeft(EditImage, (ViewSize - dispW) / 2 + _offsetX);
            Canvas.SetTop(EditImage, (ViewSize - dispH) / 2 + _offsetY);
        }

        private void ClampOffset()
        {
            double scale = CurrentScale;
            double dispW = _imagePixelWidth * scale;
            double dispH = _imagePixelHeight * scale;

            double maxX = Math.Max(0, (dispW - ViewSize) / 2) + ViewSize * 0.35;
            double maxY = Math.Max(0, (dispH - ViewSize) / 2) + ViewSize * 0.35;
            if (dispW <= ViewSize)
            {
                maxX = ViewSize * 0.35;
            }

            if (dispH <= ViewSize)
            {
                maxY = ViewSize * 0.35;
            }

            _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
            _offsetY = Math.Clamp(_offsetY, -maxY, maxY);
        }

        private void CropViewport_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragging = true;
            _lastPoint = e.GetCurrentPoint(CropViewport).Position;
            CropViewport.CapturePointer(e.Pointer);
        }

        private void CropViewport_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            Point p = e.GetCurrentPoint(CropViewport).Position;
            _offsetX += p.X - _lastPoint.X;
            _offsetY += p.Y - _lastPoint.Y;
            _lastPoint = p;
            ClampOffset();
            ApplyImageLayout();
        }

        private void CropViewport_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false;
            CropViewport.ReleasePointerCapture(e.Pointer);
        }

        private void CropViewport_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => _dragging = false;

        private void CropViewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(CropViewport).Properties.MouseWheelDelta;
            SetZoom(_userZoom * (delta > 0 ? 1.1 : 1 / 1.1), e.GetCurrentPoint(CropViewport).Position);
            e.Handled = true;
        }

        private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // 面板还没构造完（XAML 解析期初值就地触发）时什么都不做：
            // 此刻 EditImage 等后续 x:Name 仍是 null，写布局直接空引用崩整个面板。
            if (!_panelReady || _updatingZoomUi)
            {
                return;
            }

            SetZoom(e.NewValue, new Point(ViewSize / 2, ViewSize / 2));
        }

        private void SetZoom(double newZoom, Point focus)
        {
            newZoom = Math.Clamp(newZoom, 0.5, 12.0);
            double cx = ViewSize / 2;
            double cy = ViewSize / 2;
            double ox = focus.X - cx;
            double oy = focus.Y - cy;
            double ratio = newZoom / _userZoom;
            _offsetX = ox - (ox - _offsetX) * ratio;
            _offsetY = oy - (oy - _offsetY) * ratio;
            _userZoom = newZoom;

            ClampOffset();
            ApplyImageLayout();
            SyncZoomSlider();
        }

        private async void CropConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CropConfirmButton.IsEnabled = false;

                // 保存的是这块方形区域本身：圆形 / 矩形只是取景框，
                // 艺术家墙显示时自己裁成圆、详情页显示时就是矩形，所以不需要给像素打圆孔。
                MaskPath.Visibility = Visibility.Collapsed;
                RingEllipse.Visibility = Visibility.Collapsed;
                RingRect.Visibility = Visibility.Collapsed;

                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(CropViewport);
                IBuffer buffer = await rtb.GetPixelsAsync();

                ApplyShape();

                string key = string.IsNullOrWhiteSpace(_storeKey) ? _artistName : _storeKey;
                await ArtistAvatarStore.SavePngAsync(key, buffer.ToArray(), (uint)rtb.PixelWidth, (uint)rtb.PixelHeight);

                BitmapImage? saved = await ArtistAvatarStore.TryLoadAsync(key);
                if (saved != null)
                {
                    AvatarConfirmed?.Invoke(saved);
                }

                StatusText.Text = "头像已保存";
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败：" + ex.Message;
            }
            finally
            {
                CropConfirmButton.IsEnabled = true;
            }
        }
    }
}
