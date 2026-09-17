using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 内置「图景」背景预设生成器：程序运行时生成无版权的抽象渐变图，
    /// 避免把图片/视频素材放进安装包带来的体积与版权风险。
    /// 生成结果缓存到 %LOCALAPPDATA%\CelesteMusicPlayer\BackgroundPresets，
    /// 只有缓存不存在或损坏时才重新生成。
    /// </summary>
    internal static class BackgroundPresetGenerator
    {
        public const string PresetAurora = "Aurora";
        public const string PresetSunset = "Sunset";
        public const string PresetMidnight = "Midnight";

        private const int Size = 1152; // 与自定义背景模糊管线 workSize 192 * 6 对齐

        /// <summary>预设名是否有效。</summary>
        public static bool IsKnownPreset(string? preset)
            => preset is PresetAurora or PresetSunset or PresetMidnight;

        /// <summary>返回缓存文件路径；若不存在则生成并保存。</summary>
        public static string? GetPresetPath(string preset)
        {
            if (!IsKnownPreset(preset))
            {
                return null;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CelesteMusicPlayer",
                "BackgroundPresets");

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"Preset_{preset}.png");

            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                byte[]? png = Generate(preset);
                if (png == null || png.Length == 0)
                {
                    return null;
                }

                File.WriteAllBytes(path, png);
                return path;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException($"BackgroundPresetGenerator.{preset}", caught);
                return null;
            }
        }

        /// <summary>生成指定预设的 PNG 字节。</summary>
        private static byte[]? Generate(string preset)
        {
            using var bitmap = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                switch (preset)
                {
                    case PresetAurora:
                        DrawAurora(g);
                        break;
                    case PresetSunset:
                        DrawSunset(g);
                        break;
                    case PresetMidnight:
                        DrawMidnight(g);
                        break;
                }
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static void DrawAurora(Graphics g)
        {
            // 极光：从左上到右下的深青→靛蓝→紫罗兰渐变
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, Size, Size),
                Color.FromArgb(0, 48, 72),
                Color.FromArgb(72, 24, 96),
                LinearGradientMode.ForwardDiagonal);
            brush.InterpolationColors = new ColorBlend(4)
            {
                Colors = new[]
                {
                    Color.FromArgb(4, 40, 64),
                    Color.FromArgb(0, 96, 112),
                    Color.FromArgb(72, 40, 128),
                    Color.FromArgb(24, 16, 56)
                },
                Positions = new[] { 0.0f, 0.35f, 0.65f, 1.0f }
            };
            g.FillRectangle(brush, 0, 0, Size, Size);

            // 加一层非常淡的横向光带，增加层次感
            AddSoftGlow(g, Color.FromArgb(40, 200, 180, 220), 0.25f, 0.35f, 0.55f);
        }

        private static void DrawSunset(Graphics g)
        {
            // 日落：顶部深紫→中部橙红→底部暗红的垂直渐变
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, Size, Size),
                Color.FromArgb(48, 24, 64),
                Color.FromArgb(80, 20, 24),
                LinearGradientMode.Vertical);
            brush.InterpolationColors = new ColorBlend(5)
            {
                Colors = new[]
                {
                    Color.FromArgb(32, 16, 56),
                    Color.FromArgb(96, 40, 96),
                    Color.FromArgb(200, 80, 48),
                    Color.FromArgb(255, 140, 40),
                    Color.FromArgb(72, 20, 24)
                },
                Positions = new[] { 0.0f, 0.30f, 0.55f, 0.72f, 1.0f }
            };
            g.FillRectangle(brush, 0, 0, Size, Size);

            AddSoftGlow(g, Color.FromArgb(50, 255, 180, 120), 0.55f, 0.60f, 0.85f);
        }

        private static void DrawMidnight(Graphics g)
        {
            // 深夜：深蓝黑径向渐变，中心略亮、四周压暗
            var midnight = Color.FromArgb(4, 8, 24);
            using var brush = new PathGradientBrush(new[]
            {
                new PointF(0, 0),
                new PointF(Size, 0),
                new PointF(Size, Size),
                new PointF(0, Size)
            })
            {
                CenterColor = Color.FromArgb(16, 32, 72),
                SurroundColors = new[] { midnight, midnight, midnight, midnight },
                CenterPoint = new PointF(Size * 0.5f, Size * 0.45f)
            };
            g.FillRectangle(brush, 0, 0, Size, Size);

            // 稀疏星点：随机但固定，保证每次生成一样
            DrawStars(g);
        }

        private static void AddSoftGlow(Graphics g, Color color, float yCenter, float yTop, float yBottom)
        {
            int top = (int)(Size * yTop);
            int bottom = (int)(Size * yBottom);
            int height = Math.Max(1, bottom - top);
            using var brush = new LinearGradientBrush(
                new Rectangle(0, top, Size, height),
                color,
                Color.FromArgb(0, color.R, color.G, color.B),
                LinearGradientMode.Vertical);
            brush.InterpolationColors = new ColorBlend(3)
            {
                Colors = new[]
                {
                    Color.FromArgb(0, color.R, color.G, color.B),
                    color,
                    Color.FromArgb(0, color.R, color.G, color.B)
                },
                Positions = new[] { 0.0f, 0.5f, 1.0f }
            };
            g.FillRectangle(brush, 0, top, Size, height);
        }

        private static void DrawStars(Graphics g)
        {
            // 固定种子：同一张预设每次生成完全一致
            var rng = new Random(20260917);
            using var brush = new SolidBrush(Color.FromArgb(180, 220, 230, 255));
            for (int i = 0; i < 180; i++)
            {
                float x = (float)(rng.NextDouble() * Size);
                float y = (float)(rng.NextDouble() * Size);
                float r = 0.5f + (float)rng.NextDouble() * 1.2f;
                g.FillEllipse(brush, x - r, y - r, r * 2, r * 2);
            }
        }
    }
}
