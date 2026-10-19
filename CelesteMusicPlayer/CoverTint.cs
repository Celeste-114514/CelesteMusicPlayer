// ============================================================================
// CoverTint —— 从专辑封面取主色，生成「保底压暗」的渐变底色（2026-10-19）
//
// 要解决的问题（用户 10-18 报的「浅色封面上白字/白条看不见」）：
//   直接把封面铺满当背景，遇到浅色封面（白底、浅色调）时，白色文字和白色进度条
//   等于画在白纸上，完全看不见。
//
// 定案做法（10-18 拍板，本轮落地）：
//   1. 从封面位图采一个主色（不是平均色 —— 平均会把所有颜色搅成灰）；
//   2. 主色转 HSL，**保留色相和饱和度**，只把亮度强制压到
//        起始 25%~30% / 终点 10%~16%；
//   3. 再叠一层 8%~12% 的黑纱兜底。
//   实测对比度：白字在 L=30% 的底上约 8:1；L=45% 掉到 5:1；L=50% 只有 3.9:1（不够）。
//   → 亮度上限卡在 28%，浅色封面被压成深灰不翻车，深色封面基本保持原样，
//     而且**还留着"随封面变色"**这个效果（不是一刀切固定黑底）。
//
// ⚠️ 解码走 WinRT 的 BitmapDecoder，不用 GDI+（System.Drawing 不认 WebP/HEIC/AVIF，
//    内嵌封面里这些格式不少，用 GDI+ 会整片取色失败）。
// ============================================================================

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    internal static class CoverTint
    {
        /// <summary>渐变起始亮度（HSL 的 L）。定案区间 25%~30% 的取值点。</summary>
        private const double StartLightness = 0.28;

        /// <summary>渐变终点亮度。定案区间 10%~16% 的取值点。</summary>
        private const double EndLightness = 0.13;

        /// <summary>没有封面（或取色失败）时的兜底色：与 FrostedGlass.DefaultTint 同族的深紫灰。</summary>
        public static Color FallbackBase => Color.FromArgb(255, 36, 30, 42);

        /// <summary>
        /// 从封面字节取主色。取不到返回 null（调用方回落兜底色）。
        /// 全程异步，UI 线程只拿到最后那个 Color。
        /// </summary>
        public static async Task<Color?> AnalyzeAsync(byte[]? coverBytes)
        {
            if (coverBytes == null || coverBytes.Length == 0)
            {
                return null;
            }

            try
            {
                return await Task.Run(() => SampleDominantCore(coverBytes)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverTint.Analyze", ex);
                return null;
            }
        }

        private static async Task<Color?> SampleDominantCore(byte[] coverBytes)
        {
            using var inStream = new InMemoryRandomAccessStream();
            await inStream.WriteAsync(coverBytes.AsBuffer());
            inStream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inStream);

            // 缩到 24×24 再采样：整图逐像素在大图上没必要，缩完已经足够代表主色
            var transform = new BitmapTransform
            {
                ScaledWidth = 24,
                ScaledHeight = 24,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] data = pixels.DetachPixelData();
            return ComputeDominant(data);
        }

        /// <summary>
        /// 主色 = 按「饱和度加权」的平均色。
        /// 直接求平均会把红+青搅成灰（封面常见），加权后鲜艳的那部分说了算；
        /// 近黑（暗边、阴影）和近白（白底、高光）都跳过 —— 它们最容易把结果带偏，
        /// 而且正是"浅色封面翻车"的元凶。
        /// </summary>
        private static Color? ComputeDominant(byte[] data)
        {
            double sr = 0, sg = 0, sb = 0, sw = 0;
            double ar = 0, ag = 0, ab = 0;
            int all = 0;

            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                byte a = data[i + 3];
                if (a < 128)
                {
                    continue;
                }

                byte r = data[i];
                byte g = data[i + 1];
                byte b = data[i + 2];

                all++;
                ar += r;
                ag += g;
                ab += b;

                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double l = (max + min) / 510.0;
                if (l < 0.10 || l > 0.92)
                {
                    continue; // 近黑/近白不计入加权平均
                }

                double sat = max <= 0 ? 0 : (max - min) / max;
                double w = 1.0 + sat * 2.5;
                sr += r * w;
                sg += g * w;
                sb += b * w;
                sw += w;
            }

            if (all == 0)
            {
                return null;
            }

            if (sw <= 0)
            {
                // 全是近黑或近白（例如纯白底 + 黑字的封面）：退回普通平均，
                // 再由后面的"强制压亮度"把它压成深灰 —— 结果仍然可读。
                return Color.FromArgb(255, (byte)(ar / all), (byte)(ag / all), (byte)(ab / all));
            }

            return Color.FromArgb(255, (byte)(sr / sw), (byte)(sg / sw), (byte)(sb / sw));
        }

        /// <summary>
        /// 由主色生成保底压暗的渐变两端色：保留色相/饱和度，只把亮度压到定案区间。
        /// </summary>
        public static (Color Start, Color End) BuildDimGradient(Color dominant)
        {
            RgbToHsl(dominant, out double h, out double s, out _);

            // 饱和度收一收：太艳的底配白字发飘，太灰就没有"随封面变色"的感觉
            s = Math.Clamp(s, 0.18, 0.55);

            Color start = FromHsl(h, s, StartLightness);
            Color end = FromHsl(h, s * 0.9, EndLightness);
            return (start, end);
        }

        /// <summary>直接给一个左上→右下的渐变画刷（迷你播放器 / 零散窗口直接拿去当底）。</summary>
        public static LinearGradientBrush CreateDimGradientBrush(Color dominant)
        {
            (Color start, Color end) = BuildDimGradient(dominant);

            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            brush.GradientStops.Add(new GradientStop { Color = start, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = end, Offset = 1 });
            return brush;
        }

        /// <summary>给毛玻璃层用的主色：比渐变起点再暗一档，避免亚克力提亮后翻车。</summary>
        public static Color DimmedBase(Color dominant, double lightness = 0.22)
        {
            RgbToHsl(dominant, out double h, out double s, out _);
            return FromHsl(h, Math.Clamp(s, 0.15, 0.5), lightness);
        }

        // ---------------------------------------------------------------- HSL

        private static void RgbToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2.0;

            double d = max - min;
            if (d <= 0.0001)
            {
                h = 0;
                s = 0;
                return;
            }

            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (Math.Abs(max - r) < 0.0001)
            {
                h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            }
            else if (Math.Abs(max - g) < 0.0001)
            {
                h = ((b - r) / d + 2) / 6.0;
            }
            else
            {
                h = ((r - g) / d + 4) / 6.0;
            }
        }

        private static Color FromHsl(double h, double s, double l)
        {
            if (s <= 0)
            {
                byte v = (byte)Math.Round(l * 255);
                return Color.FromArgb(255, v, v, v);
            }

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            return Color.FromArgb(
                255,
                (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
                (byte)Math.Round(HueToRgb(p, q, h) * 255),
                (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }
    }
}
