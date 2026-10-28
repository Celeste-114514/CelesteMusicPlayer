// ============================================================================
// CoverThumbnailer —— 封面解码 + 缩放到三档并编码为 JPEG（W6 第六小步 / 第 2 步换引擎）
//
// 2026-10-29 之前这段在 CoverArtService 里用 WinRT（BitmapDecoder / BitmapTransform /
// BitmapEncoder）实现，只能在 Windows 上跑；现在换成 SkiaSharp（MIT，跨平台，带原生库）。
//
// 为什么是 SkiaSharp 而不是 ImageSharp（试过，记下来免得下次再踩）：
//   · ImageSharp 3.x / 4.x 构建时强制校验 Six Labors 商业许可，缺 license 直接编译失败；
//   · ImageSharp 2.1.13 虽是 Apache-2.0 且能编过，但有已知高危漏洞（NU1903），不能用。
//   · SkiaSharp 是 MIT，无许可门槛，Google Skia 的官方绑定，编解码与缩放都是工业级实现。
//     代价是带一个原生库 libSkiaSharp.dll（约 10MB），跨平台时另加对应 NativeAssets 包即可。
//
// 与旧 WinRT 实现的等价点（改这几点前先想清楚）：
//   · 只缩不放 —— 源图不比目标档大就绝不重采样，甚至直接编码原图；
//   · EXIF 方向 —— 先按 EncodedOrigin 把图摆正再量尺寸，等价于旧实现的 RespectExifOrientation；
//   · 目标尺寸 —— 一律交给 CoverSizing.ComputeTargetSize 算，保证和磁盘上已有缓存档位对齐；
//   · JPEG 质量 92 —— 与旧实现一致（2026-10-19 为提高小图观感调过）。
//
// ⚠️ 本文件不引用任何 WinUI/WinRT，可链接进跨平台测试宿主。
// ============================================================================

using System;
using System.IO;
using SkiaSharp;

namespace CelesteMusicPlayer
{
    internal static class CoverThumbnailer
    {
        /// <summary>JPEG 质量。与旧 WinRT 实现一致（0.92）。</summary>
        public const int JpegQuality = 92;

        /// <summary>当前图像引擎标识，写日志与自检用（将来换库要同步改）。</summary>
        public const string EngineName = "SkiaSharp";

        /// <summary>
        /// 把源图缩到指定档位并写成 JPEG 到 <paramref name="outputPath"/>。
        /// 调用方负责放在后台线程、以及"先写 .tmp 再改名"的原子替换。
        /// 失败（不是图片、解码器不支持、IO 出错）返回 false 并带出原因，不抛。
        /// </summary>
        public static bool TryWriteVariant(byte[] source, string outputPath, CoverVariant variant, out string? error)
        {
            error = null;
            if (source == null || source.Length == 0)
            {
                error = "源字节为空";
                return false;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "输出路径为空";
                return false;
            }

            try
            {
                using SKData data = SKData.CreateCopy(source);

                SKEncodedOrigin origin;
                int encodedWidth;
                int encodedHeight;
                using (SKCodec? probe = SKCodec.Create(data))
                {
                    if (probe == null || probe.Info.Width <= 0 || probe.Info.Height <= 0)
                    {
                        error = "不是可识别的图片格式";
                        return false;
                    }

                    origin = probe.EncodedOrigin;
                    encodedWidth = probe.Info.Width;
                    encodedHeight = probe.Info.Height;
                }

                // 摆正后的长宽（方向为 90/270 时长宽互换）
                bool swapped = OriginSwapsAxes(origin);
                int uprightWidth = swapped ? encodedHeight : encodedWidth;
                int uprightHeight = swapped ? encodedWidth : encodedHeight;

                int longEdge = CoverSizing.LongEdge(variant);
                (int targetWidth, int targetHeight) = CoverSizing.ComputeTargetSize(uprightWidth, uprightHeight, longEdge);
                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    error = $"源图尺寸无效（{uprightWidth}x{uprightHeight}）";
                    return false;
                }

                using SKBitmap? decoded = SKBitmap.Decode(data);
                if (decoded == null)
                {
                    error = "解码失败";
                    return false;
                }

                // 不需要摆正、也不需要缩小：直接编码原图，一次重采样都不做（只缩不放）
                if (origin == SKEncodedOrigin.TopLeft
                    && targetWidth == decoded.Width && targetHeight == decoded.Height)
                {
                    return TryEncodeJpeg(decoded, outputPath, out error);
                }

                using SKBitmap upright = origin == SKEncodedOrigin.TopLeft
                    ? decoded.Copy()
                    : OrientUpright(decoded, origin);

                if (upright == null)
                {
                    error = "图片方向修正失败";
                    return false;
                }

                if (upright.Width == targetWidth && upright.Height == targetHeight)
                {
                    return TryEncodeJpeg(upright, outputPath, out error);
                }

                using SKBitmap scaled = Resize(upright, targetWidth, targetHeight);
                if (scaled == null)
                {
                    error = "缩放失败";
                    return false;
                }

                return TryEncodeJpeg(scaled, outputPath, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 内部实现

        private static bool TryEncodeJpeg(SKBitmap bitmap, string outputPath, out string? error)
        {
            error = null;
            try
            {
                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData? encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
                if (encoded == null)
                {
                    error = "JPEG 编码失败";
                    return false;
                }

                using FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoded.SaveTo(fs);
                return true;
            }
            catch (Exception ex)
            {
                error = "写文件失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>缩小到目标尺寸（Mitchell 三次采样：缩略图公认质量/锐度平衡点）。</summary>
        private static SKBitmap? Resize(SKBitmap source, int width, int height)
        {
            var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.Black); // JPEG 没有透明通道；与旧实现透明区偏黑的观感一致
                using SKImage image = SKImage.FromBitmap(source);
                var dest = new SKRect(0, 0, width, height);
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKCubicResampler.Mitchell), null);
            }

            return target;
        }

        /// <summary>
        /// 按 EXIF 方向把图摆正。这里用显式像素映射而不是 canvas 变换矩阵 ——
        /// 8 种方向的矩阵组合推错一次就是"静默躺倒/镜像"，显式映射写完能一眼对照。
        /// 只在真的带旋转标记的文件上走这条路（封面里极罕见）。
        /// </summary>
        private static SKBitmap? OrientUpright(SKBitmap source, SKEncodedOrigin origin)
        {
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            bool swapped = OriginSwapsAxes(origin);
            int width = swapped ? sourceHeight : sourceWidth;
            int height = swapped ? sourceWidth : sourceHeight;

            SKColor[] from = source.Pixels;
            if (from == null || from.Length != sourceWidth * sourceHeight)
            {
                return null;
            }

            var to = new SKColor[width * height];
            for (int y = 0; y < sourceHeight; y++)
            {
                for (int x = 0; x < sourceWidth; x++)
                {
                    (int dx, int dy) = MapPixel(origin, x, y, sourceWidth, sourceHeight);
                    to[dy * width + dx] = from[y * sourceWidth + x];
                }
            }

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Pixels = to;
            return bitmap;
        }

        private static (int X, int Y) MapPixel(SKEncodedOrigin origin, int x, int y, int width, int height) => origin switch
        {
            SKEncodedOrigin.TopRight => (width - 1 - x, y),          // 水平镜像
            SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y), // 180°
            SKEncodedOrigin.BottomLeft => (x, height - 1 - y),        // 垂直镜像
            SKEncodedOrigin.LeftTop => (y, x),                        // 转置
            SKEncodedOrigin.RightTop => (height - 1 - y, x),          // 顺时针 90°
            SKEncodedOrigin.RightBottom => (height - 1 - y, width - 1 - x), // 反转置
            SKEncodedOrigin.LeftBottom => (y, width - 1 - x),         // 逆时针 90°
            _ => (x, y),                                              // TopLeft / 未知
        };

        private static bool OriginSwapsAxes(SKEncodedOrigin origin) => origin switch
        {
            SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom => true,
            _ => false,
        };
    }
}
