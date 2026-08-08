using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>封面放大模糊背景；模糊度刻意克制，让前景毛玻璃更突出。</summary>
    internal static class AlbumArtBackground
    {
        public static byte[]? CreateHeavilyBlurredPng(byte[] coverBytes, int workSize = 96, int blurRadius = 2)
        {
            if (coverBytes == null || coverBytes.Length == 0)
            {
                return null;
            }

            try
            {
                using var input = new MemoryStream(coverBytes);
                using var src = Image.FromStream(input);
                using var small = new Bitmap(workSize, workSize, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float scale = Math.Max((float)workSize / src.Width, (float)workSize / src.Height);
                    float w = src.Width * scale;
                    float h = src.Height * scale;
                    float x = (workSize - w) / 2f;
                    float y = (workSize - h) / 2f;
                    g.DrawImage(src, x, y, w, h);
                }

                using Bitmap blurred = StackBlur(small, blurRadius, passes: 2);
                using var large = new Bitmap(workSize * 6, workSize * 6, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(large))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(blurred, 0, 0, large.Width, large.Height);
                }

                using var ms = new MemoryStream();
                large.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap StackBlur(Bitmap src, int radius, int passes)
        {
            Bitmap current = (Bitmap)src.Clone();
            for (int p = 0; p < passes; p++)
            {
                Bitmap next = new Bitmap(current.Width, current.Height, PixelFormat.Format32bppArgb);
                BlurHorizontal(current, next, radius);
                current.Dispose();
                current = next;

                next = new Bitmap(current.Width, current.Height, PixelFormat.Format32bppArgb);
                BlurVertical(current, next, radius);
                current.Dispose();
                current = next;
            }

            return current;
        }

        private static void BlurHorizontal(Bitmap src, Bitmap dst, int radius)
        {
            int w = src.Width;
            int h = src.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = Math.Clamp(x + k, 0, w - 1);
                        Color c = src.GetPixel(xx, y);
                        r += c.R;
                        g += c.G;
                        b += c.B;
                        a += c.A;
                        n++;
                    }

                    dst.SetPixel(x, y, Color.FromArgb(a / n, r / n, g / n, b / n));
                }
            }
        }

        private static void BlurVertical(Bitmap src, Bitmap dst, int radius)
        {
            int w = src.Width;
            int h = src.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = Math.Clamp(y + k, 0, h - 1);
                        Color c = src.GetPixel(x, yy);
                        r += c.R;
                        g += c.G;
                        b += c.B;
                        a += c.A;
                        n++;
                    }

                    dst.SetPixel(x, y, Color.FromArgb(a / n, r / n, g / n, b / n));
                }
            }
        }
    }
}
