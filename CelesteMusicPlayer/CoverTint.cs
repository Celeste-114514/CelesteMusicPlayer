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
//
// 2026-10-20 追加「显著色板」（AnalyzePaletteAsync）：迷你播放器炫光底色用 ——
//   按色相分桶取最多 3 个分得开的鲜艳色，光斑色由 GlowColor 提亮生成。
// ============================================================================

using System;
using System.Collections.Generic;
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
            byte[]? data = await SamplePixelsAsync(coverBytes).ConfigureAwait(false);
            return data is null ? null : ComputeDominant(data);
        }

        /// <summary>
        /// 解码 + 缩到 24×24 采样，返回 RGBA8 像素字节（主色 / 色板共用一次解码逻辑）。
        /// </summary>
        private static async Task<byte[]?> SamplePixelsAsync(byte[] coverBytes)
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

            return pixels.DetachPixelData();
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
        /// 从封面取「显著色板」（迷你播放器炫光模式用）：最多 maxColors 个**色相拉开**的鲜艳色。
        /// 取不到返回 null（调用方回落兜底家族色）。
        /// </summary>
        public static async Task<IReadOnlyList<Color>?> AnalyzePaletteAsync(byte[]? coverBytes, int maxColors = 3)
        {
            if (coverBytes == null || coverBytes.Length == 0)
            {
                return null;
            }

            try
            {
                return await Task.Run(() => SamplePaletteCore(coverBytes, maxColors)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CoverTint.AnalyzePalette", ex);
                return null;
            }
        }

        private static async Task<IReadOnlyList<Color>?> SamplePaletteCore(byte[] coverBytes, int maxColors)
        {
            byte[]? data = await SamplePixelsAsync(coverBytes).ConfigureAwait(false);
            return data is null ? null : ComputePalette(data, maxColors);
        }

        /// <summary>
        /// 显著色板 = 按色相分桶、饱和度加权，取权重最高的若干个**色相拉开差距**的桶的平均色。
        ///
        /// 为什么按色相分桶而不是直接求平均：炫光要的是"封面里那几个跳出来的颜色"
        /// （比如红底专辑上的一撮蓝），平均色会把它们搅成灰。分桶后每个桶是一种色相，
        /// 按权重挑、色相太近的跳过，出来的就是一组分得开的显著色。
        /// </summary>
        private static IReadOnlyList<Color>? ComputePalette(byte[] data, int maxColors)
        {
            const int HueBins = 18; // 每桶 20°
            double[] binWeight = new double[HueBins];
            double[] binR = new double[HueBins];
            double[] binG = new double[HueBins];
            double[] binB = new double[HueBins];
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
                if (l < 0.12 || l > 0.90)
                {
                    continue; // 近黑/近白不进桶（与主色同一口径）
                }

                double sat = max <= 0 ? 0 : (max - min) / max;
                double w = 1.0 + sat * 2.5; // 饱和度加权：鲜艳的说了算

                RgbToHsl(Color.FromArgb(255, r, g, b), out double h, out _, out _);
                int bin = (int)(h * HueBins) % HueBins;
                binWeight[bin] += w;
                binR[bin] += r * w;
                binG[bin] += g * w;
                binB[bin] += b * w;
            }

            if (all == 0)
            {
                return null;
            }

            // 桶按权重排序（只排有货的）
            var order = new List<int>();
            for (int bin = 0; bin < HueBins; bin++)
            {
                if (binWeight[bin] > 0)
                {
                    order.Add(bin);
                }
            }
            order.Sort((x, y) => binWeight[y].CompareTo(binWeight[x]));

            // 贪心挑：色相与已挑的差不够 30° 就跳过（相邻桶往往是同一个颜色的两侧）
            var picked = new List<Color>();
            var pickedHues = new List<double>();
            foreach (int bin in order)
            {
                if (picked.Count >= maxColors)
                {
                    break;
                }

                double hue = (bin + 0.5) / HueBins;
                bool tooClose = false;
                foreach (double ph in pickedHues)
                {
                    double d = Math.Abs(hue - ph);
                    d = Math.Min(d, 1.0 - d); // 色相是环，0° 和 360° 是邻居
                    if (d < 0.083)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose)
                {
                    continue;
                }

                pickedHues.Add(hue);
                picked.Add(Color.FromArgb(255,
                    (byte)(binR[bin] / binWeight[bin]),
                    (byte)(binG[bin] / binWeight[bin]),
                    (byte)(binB[bin] / binWeight[bin])));
            }

            if (picked.Count == 0)
            {
                // 全是近黑/近白（黑白封面）：退回普通平均，交给 GlowColor 决定怎么显色
                picked.Add(Color.FromArgb(255, (byte)(ar / all), (byte)(ag / all), (byte)(ab / all)));
            }

            return picked;
        }

        /// <summary>
        /// 炫光光斑色：保留色相，把亮度提到"发光"档（比压暗渐变亮一档，光斑才立得起来）。
        /// 灰度封面没有色相可展示 → 给一档冷紫灰（与兜底色 FallbackBase 同族），
        /// 免得 h=0 被硬染成红色、也免得灰光斑把白字衬没。
        /// </summary>
        public static Color GlowColor(Color source, double lightness = 0.40)
        {
            RgbToHsl(source, out double h, out double s, out _);
            if (s < 0.10)
            {
                h = 0.72;
                s = 0.20;
            }
            else
            {
                s = Math.Clamp(s, 0.30, 0.70);
            }

            return FromHsl(h, s, lightness);
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
