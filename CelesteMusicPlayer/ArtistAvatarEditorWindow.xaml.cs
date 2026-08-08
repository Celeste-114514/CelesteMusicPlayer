using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 艺术家头像编辑：整图可见（contain）+ 拖动平移 + 滚轮缩放，圆内区域为裁切结果。
    /// </summary>
    public sealed partial class ArtistAvatarEditorWindow : Window
    {
        private const double ViewSize = 280;

        private readonly string _artistName;
        private readonly string _sourceImagePath;

        private bool _dragging;
        private Point _lastPoint;

        /// <summary>图片像素宽高（用于布局计算）</summary>
        private double _imagePixelWidth;
        private double _imagePixelHeight;

        /// <summary>把整图完整放进 ViewSize 的基础缩放（contain）</summary>
        private double _containScale = 1;

        /// <summary>用户缩放倍数，1 = 整图刚好完整可见</summary>
        private double _userZoom = 1;

        /// <summary>相对居中位置的平移（DIP）</summary>
        private double _offsetX;
        private double _offsetY;

        public event Action<BitmapImage>? AvatarConfirmed;

        public ArtistAvatarEditorWindow(string artistName, string sourceImagePath)
        {
            _artistName = artistName;
            _sourceImagePath = sourceImagePath;

            InitializeComponent();
            WindowIconHelper.Apply(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 520));
            Title = "编辑艺术家头像";

            CropViewport.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, ViewSize, ViewSize)
            };

            if (Content is FrameworkElement root)
            {
                root.Loaded += async (_, _) => await LoadSourceImageAsync();
            }
        }

        private async Task LoadSourceImageAsync()
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(_sourceImagePath);
                using IRandomAccessStream stream = await file.OpenReadAsync();

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                double srcW = decoder.PixelWidth;
                double srcH = decoder.PixelHeight;
                if (srcW <= 0 || srcH <= 0)
                {
                    throw new InvalidOperationException("图片尺寸无效。");
                }

                // 超大图缩小解码，布局尺寸与解码尺寸一致
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
            }
            catch (Exception ex)
            {
                ContentDialog dialog = new()
                {
                    Title = "无法打开图片",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
                Close();
            }
        }

        /// <summary>初始：整张图完整落在 280×280 内（可能留边）</summary>
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
            ApplyImageLayout();
        }

        private double CurrentScale => _containScale * _userZoom;

        private void ApplyImageLayout()
        {
            double scale = CurrentScale;
            double dispW = _imagePixelWidth * scale;
            double dispH = _imagePixelHeight * scale;

            EditImage.Width = dispW;
            EditImage.Height = dispH;

            double left = (ViewSize - dispW) / 2 + _offsetX;
            double top = (ViewSize - dispH) / 2 + _offsetY;
            Canvas.SetLeft(EditImage, left);
            Canvas.SetTop(EditImage, top);
        }

        private void ClampOffset()
        {
            double scale = CurrentScale;
            double dispW = _imagePixelWidth * scale;
            double dispH = _imagePixelHeight * scale;

            // 允许把图拖到圆外一点，但别拖没
            double maxX = Math.Max(0, (dispW - ViewSize) / 2) + ViewSize * 0.35;
            double maxY = Math.Max(0, (dispH - ViewSize) / 2) + ViewSize * 0.35;
            // 图比视口小时，仍可小幅移动
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
        {
            _dragging = false;
        }

        private void CropViewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(CropViewport).Properties.MouseWheelDelta;
            double factor = delta > 0 ? 1.1 : 1 / 1.1;
            double newZoom = Math.Clamp(_userZoom * factor, 0.5, 12.0);

            Point p = e.GetCurrentPoint(CropViewport).Position;
            double cx = ViewSize / 2;
            double cy = ViewSize / 2;
            double ox = p.X - cx;
            double oy = p.Y - cy;
            double ratio = newZoom / _userZoom;
            _offsetX = ox - (ox - _offsetX) * ratio;
            _offsetY = oy - (oy - _offsetY) * ratio;
            _userZoom = newZoom;

            ClampOffset();
            ApplyImageLayout();
            e.Handled = true;
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConfirmButton.IsEnabled = false;

                MaskPath.Visibility = Visibility.Collapsed;
                RingEllipse.Visibility = Visibility.Collapsed;

                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(CropViewport);
                IBuffer buffer = await rtb.GetPixelsAsync();
                byte[] pixels = buffer.ToArray();
                ApplyCircularAlphaMask(pixels, rtb.PixelWidth, rtb.PixelHeight);

                MaskPath.Visibility = Visibility.Visible;
                RingEllipse.Visibility = Visibility.Visible;

                await ArtistAvatarStore.SavePngAsync(
                    _artistName,
                    pixels,
                    (uint)rtb.PixelWidth,
                    (uint)rtb.PixelHeight);

                BitmapImage? saved = await ArtistAvatarStore.TryLoadAsync(_artistName);
                if (saved != null)
                {
                    AvatarConfirmed?.Invoke(saved);
                }

                Close();
            }
            catch (Exception ex)
            {
                ConfirmButton.IsEnabled = true;
                ContentDialog dialog = new()
                {
                    Title = "保存失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static void ApplyCircularAlphaMask(byte[] bgra, int width, int height)
        {
            double cx = width / 2.0;
            double cy = height / 2.0;
            double radius = Math.Min(cx, cy) - 0.5;
            double r2 = radius * radius;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double dx = x + 0.5 - cx;
                    double dy = y + 0.5 - cy;
                    if (dx * dx + dy * dy > r2)
                    {
                        int i = (y * width + x) * 4;
                        bgra[i + 3] = 0;
                    }
                }
            }
        }
    }
}
