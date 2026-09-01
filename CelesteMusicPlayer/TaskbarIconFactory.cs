using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 任务栏缩略图按钮图标（Thumbnail Toolbar Buttons）的生成工厂。
    ///
    /// 本版彻底替换掉「FontIcon 字形 → GDI+ Bitmap.GetHicon()」的旧路径，原因有两个长期问题：
    ///
    /// 1. **描边字形太细、粗细不均**：旧版用 Segoe Fluent Icons 的码点
    ///    （上一首 E892 / 播放 E768 / 暂停 E769 / 下一首 E893 / 空心心 EB51 / 实心心 EB52）。
    ///    其中 EB51「空心心」本身就是**描边（stroke）字形**而不是填充字形——它天生是细线，
    ///    缩到 16px 再被 explorer 缩放后线条粗细完全失控，和其它几个填充字形放一起也明显不协调。
    ///    而且字形笔画粗细是字体设计者定的，我们**无法调**。
    ///
    /// 2. **GDI+ Bitmap.GetHicon() 会产生黑边**：GetHicon 创建的图标，其颜色位图是
    ///    非预乘（straight alpha）BGRA，而 Windows 绘制 32bpp 图标时按**预乘 alpha** 合成；
    ///    透明/半透明像素的 RGB 仍是 (0,0,0)，alpha 混合时被当成"半透明黑色"参与合成，
    ///    在抗锯齿边缘（尤其是空心心那圈细描边）留下很明显的黑边。
    ///
    /// 本版做法：
    /// - **矢量路径自绘**：上一首/播放/暂停/下一首/心形全部用 WinUI <see cref="Path"/> +
    ///   自写 PathGeometry 描述，画在 24×24 设计网格上，笔画粗细完全由我们控制（且各图标统一）。
    ///   实心图标用填充 + 同色细描边（圆角化 + 微量加粗）；空心心用描边环（Fill=null）。
    /// - **手动构造 HICON**：不碰 GDI+。自己建 32bpp BGRA **预乘** DIB（bottom-up）
    ///   + 1bpp 全 0 掩码，调 CreateIconIndirect。透明像素 RGB 被正确预乘成 0，
    ///   合成时不会带出黑色 → 黑边消失。
    /// - **超采样 + 盒式下采样**：先按 6 倍尺寸渲染（且 RenderTargetBitmap 本身按设备 DPI
    ///   出图，高 DPI 屏上源图更大），再盒式降采样到系统小图标尺寸，边缘干净、不糊。
    /// </summary>
    internal static class TaskbarIconFactory
    {
        public enum IconKind
        {
            Prev,
            Play,
            Pause,
            Next,
            HeartEmpty,
            HeartFilled,
        }

        public sealed class IconSet
        {
            public IntPtr Prev;
            public IntPtr Play;
            public IntPtr Pause;
            public IntPtr Next;
            public IntPtr HeartEmpty;
            public IntPtr HeartFilled;

            public bool AllValid =>
                Prev != IntPtr.Zero && Play != IntPtr.Zero && Pause != IntPtr.Zero &&
                Next != IntPtr.Zero && HeartEmpty != IntPtr.Zero && HeartFilled != IntPtr.Zero;
        }

        /// <summary>设计网格边长：所有几何都按 24×24 画，最后整体缩放到目标尺寸。</summary>
        private const double Grid = 24.0;

        /// <summary>填充图标的同色描边：圆角化边缘 + 轻微加粗（向外扩张 = 该值的一半）。</summary>
        private const double FillStroke = 1.0;

        /// <summary>空心心的描边环粗细（24 网格单位）。调大 = 心形边框更粗。</summary>
        private const double HeartRingStroke = 2.8;

        /// <summary>超采样倍率：先大后小，抗锯齿质量与直接小尺寸渲染不可同日而语。</summary>
        private const int Supersample = 6;

        // ------------------------------------------------------------------ 对外入口

        /// <summary>
        /// 生成任务栏需要的 6 个 HICON。必须在 UI 线程调用（RenderTargetBitmap 要求）。
        /// </summary>
        /// <param name="host">宿主容器：必须已在视觉树中、有 XamlRoot（主窗口那个隐藏 Canvas）。</param>
        /// <param name="normalColor">上一首/播放/暂停/下一首的颜色。</param>
        /// <param name="heartColor">心形颜色。</param>
        public static async Task<IconSet> CreateAsync(Canvas host, Windows.UI.Color normalColor, Windows.UI.Color heartColor)
        {
            var set = new IconSet();
            if (host == null)
            {
                StartupLog.Write("[thumb] TaskbarIconFactory.CreateAsync 失败: host 为 null");
                return set;
            }

            int iconSize = GetSmallIconSize();

            set.Prev = await RenderOneAsync(host, IconKind.Prev, normalColor, iconSize).ConfigureAwait(true);
            set.Play = await RenderOneAsync(host, IconKind.Play, normalColor, iconSize).ConfigureAwait(true);
            set.Pause = await RenderOneAsync(host, IconKind.Pause, normalColor, iconSize).ConfigureAwait(true);
            set.Next = await RenderOneAsync(host, IconKind.Next, normalColor, iconSize).ConfigureAwait(true);
            set.HeartEmpty = await RenderOneAsync(host, IconKind.HeartEmpty, heartColor, iconSize).ConfigureAwait(true);
            set.HeartFilled = await RenderOneAsync(host, IconKind.HeartFilled, heartColor, iconSize).ConfigureAwait(true);

            StartupLog.Write("[thumb] 矢量图标渲染完成 size=" + iconSize
                + " prev=0x" + set.Prev.ToString("X")
                + " play=0x" + set.Play.ToString("X")
                + " pause=0x" + set.Pause.ToString("X")
                + " next=0x" + set.Next.ToString("X")
                + " heartEmpty=0x" + set.HeartEmpty.ToString("X")
                + " heartFilled=0x" + set.HeartFilled.ToString("X"));

            return set;
        }

        /// <summary>取系统小图标尺寸（通常 16，高 DPI 下更大），夹在 [16,32]。</summary>
        private static int GetSmallIconSize()
        {
            int s = GetSystemMetrics(SmCxSmIcon);
            if (s < 16)
            {
                s = 16;
            }
            if (s > 32)
            {
                s = 32;
            }
            return s;
        }

        private static async Task<IntPtr> RenderOneAsync(Canvas host, IconKind kind, Windows.UI.Color color, int iconSize)
        {
            // 超采样：先在 6 倍尺寸上画，最后再盒式降采样回系统图标尺寸。
            double render = iconSize * (double)Supersample;
            Path path = BuildPath(kind, color, render);

            host.Children.Add(path);
            try
            {
                path.Measure(new Size(render, render));
                path.Arrange(new Rect(0, 0, render, render));

                var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rtb.RenderAsync(path);
                var buffer = await rtb.GetPixelsAsync();

                byte[] bgra = new byte[buffer.Length];
                Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(bgra);

                int w = rtb.PixelWidth;
                int h = rtb.PixelHeight;
                if (w <= 0 || h <= 0)
                {
                    StartupLog.Write("[thumb] 渲染 " + kind + " 得到 0 尺寸位图");
                    return IntPtr.Zero;
                }

                return HiconFromBgra(bgra, w, h, iconSize);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarIconFactory.RenderOneAsync(" + kind + ")", caught);
                return IntPtr.Zero;
            }
            finally
            {
                host.Children.Remove(path);
            }
        }

        // ------------------------------------------------------------------ 几何

        /// <summary>
        /// 按 24×24 设计网格构造图标，再用几何级 ScaleTransform 放大到 renderSize。
        /// 用几何变换而不是 Viewbox 缩放：Viewbox 是"先按原始尺寸栅格化再拉伸位图"，
        /// 放大 6 倍时边缘会发虚；几何变换在 Direct2D 几何管线里做，与分辨率无关，始终锐利。
        /// </summary>
        private static Path BuildPath(IconKind kind, Windows.UI.Color color, double renderSize)
        {
            double scale = renderSize / Grid;
            bool outlineOnly = kind == IconKind.HeartEmpty;

            PathGeometry geo = BuildGeometry(kind);
            geo.Transform = new ScaleTransform { ScaleX = scale, ScaleY = scale };

            var path = new Path
            {
                Data = geo,
                Width = renderSize,
                Height = renderSize,
                // None = 几何坐标 1:1（经上面的 ScaleTransform 放大后）落在元素框里。
                // 不能用 Fill：Fill 会把"几何自身包围盒"拉伸满元素，各图标相对大小就乱了。
                Stretch = Stretch.None,
                // 空心心：只描边不填充 → 一圈均匀的粗环
                // 其余：填充 + 同色细描边（圆角化 + 轻微加粗），保证几个图标的视觉重量一致
                Fill = outlineOnly ? null : new SolidColorBrush(color),
                Stroke = new SolidColorBrush(color),
                // 描边粗细是"元素坐标系"单位，不会被几何变换缩放，所以这里手动乘 scale
                StrokeThickness = (outlineOnly ? HeartRingStroke : FillStroke) * scale,
                StrokeLineJoin = PenLineJoin.Round,
                // WinUI3 Shape 把单数 StrokeLineCap 拆成头/尾两个属性（与 WPF 不同）
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            return path;
        }

        private static PathGeometry BuildGeometry(IconKind kind)
        {
            switch (kind)
            {
                case IconKind.Play:
                    // 右向三角（10 × 14 网格）
                    return Poly(new[] { new Point(8.5, 5.0), new Point(18.5, 12.0), new Point(8.5, 19.0) });

                case IconKind.Pause:
                    // 两根竖条：条宽 2.8、内缝 4.4（加描边后 = 条 3.8 / 缝 3.4）
                    // 缝必须留够，否则 16px 下两根条会糊成一块
                    return Poly(
                        new[] { new Point(7.0, 5.0), new Point(9.8, 5.0), new Point(9.8, 19.0), new Point(7.0, 19.0) },
                        new[] { new Point(14.2, 5.0), new Point(17.0, 5.0), new Point(17.0, 19.0), new Point(14.2, 19.0) });

                case IconKind.Prev:
                    // 左侧竖条 + 左向三角
                    return Poly(
                        new[] { new Point(5.0, 5.0), new Point(7.8, 5.0), new Point(7.8, 19.0), new Point(5.0, 19.0) },
                        new[] { new Point(18.8, 5.0), new Point(18.8, 19.0), new Point(9.6, 12.0) });

                case IconKind.Next:
                    // 右向三角 + 右侧竖条
                    return Poly(
                        new[] { new Point(5.2, 5.0), new Point(14.4, 12.0), new Point(5.2, 19.0) },
                        new[] { new Point(16.2, 5.0), new Point(19.0, 5.0), new Point(19.0, 19.0), new Point(16.2, 19.0) });

                case IconKind.HeartEmpty:
                case IconKind.HeartFilled:
                    return HeartGeometry();

                default:
                    return new PathGeometry();
            }
        }

        /// <summary>由若干闭合多边形构造 PathGeometry。</summary>
        private static PathGeometry Poly(params Point[][] rings)
        {
            var geo = new PathGeometry();
            foreach (Point[] ring in rings)
            {
                if (ring == null || ring.Length < 3)
                {
                    continue;
                }

                var fig = new PathFigure { StartPoint = ring[0], IsClosed = true };
                var poly = new PolyLineSegment();
                for (int i = 1; i < ring.Length; i++)
                {
                    poly.Points.Add(ring[i]);
                }
                fig.Segments.Add(poly);
                geo.Figures.Add(fig);
            }
            return geo;
        }

        /// <summary>
        /// 心形：底尖 + 左右两个圆润上瓣，用 6 段三次贝塞尔闭合。
        /// 同一条几何既用于实心（Fill）也用于空心（Stroke 环），两者轮廓完全一致 →
        /// 收藏/取消收藏切换时心形不会"跳形"。
        /// </summary>
        private static PathGeometry HeartGeometry()
        {
            // 外框 x 4.4..19.6 / y 5.0..19.6（15.2 × 14.6 网格）——和三角、竖条那几个
            // 图标的 14 网格高度保持一致，图标组看起来才不会一个大一个小。
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(12.0, 19.6), IsClosed = true };

            // 底部尖 → 左侧腰 → 左上瓣顶
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(7.2, 15.3),
                Point2 = new Point(4.4, 12.4),
                Point3 = new Point(4.4, 9.3),
            });
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(4.4, 6.6),
                Point2 = new Point(6.3, 5.0),
                Point3 = new Point(8.6, 5.0),
            });
            // 左上瓣 → 中间凹口
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(10.1, 5.0),
                Point2 = new Point(11.3, 5.9),
                Point3 = new Point(12.0, 7.4),
            });
            // 中间凹口 → 右上瓣顶
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(12.7, 5.9),
                Point2 = new Point(13.9, 5.0),
                Point3 = new Point(15.4, 5.0),
            });
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(17.7, 5.0),
                Point2 = new Point(19.6, 6.6),
                Point3 = new Point(19.6, 9.3),
            });
            // 右上瓣 → 右侧腰 → 回到底部尖
            fig.Segments.Add(new BezierSegment
            {
                Point1 = new Point(19.6, 12.4),
                Point2 = new Point(16.8, 15.3),
                Point3 = new Point(12.0, 19.6),
            });

            geo.Figures.Add(fig);
            return geo;
        }

        // ------------------------------------------------------------------ HICON 构造

        /// <summary>
        /// BGRA 像素 → HICON。全程自己构造 DIB，不用 GDI+ GetHicon()，因此：
        /// - 颜色位图是**预乘 alpha**（GDI 图标合成的正确格式）→ 没有黑边；
        /// - 掩码位图全 0（不屏蔽任何像素），透明完全交给 32bpp alpha 通道。
        /// </summary>
        private static IntPtr HiconFromBgra(byte[] bgra, int srcW, int srcH, int outSize)
        {
            try
            {
                // RenderTargetBitmap 的 alpha 语义（预乘/非预乘）随版本与渲染路径有差异，
                // 这里用"是否存在通道值 > alpha"来判定：预乘格式下通道值不可能超过 alpha。
                EnsurePremultiplied(bgra);

                byte[] px = (srcW == outSize && srcH == outSize)
                    ? bgra
                    : BoxDownsample(bgra, srcW, srcH, outSize, outSize);

                return CreateIconFromPremultipliedBgra(px, outSize);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarIconFactory.HiconFromBgra", caught);
                return IntPtr.Zero;
            }
        }

        /// <summary>若像素是非预乘（straight alpha），就地转成预乘。</summary>
        private static void EnsurePremultiplied(byte[] px)
        {
            bool straight = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a)
                {
                    straight = true;
                    break;
                }
            }
            if (!straight)
            {
                return;
            }

            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a == 255)
                {
                    continue;
                }
                if (a == 0)
                {
                    px[i] = 0;
                    px[i + 1] = 0;
                    px[i + 2] = 0;
                    continue;
                }
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }
        }

        /// <summary>
        /// 盒式降采样。在**预乘**空间做平均是数学正确的（线性可加），
        /// 直接平均非预乘像素会让半透明边缘渗出背景色。
        /// </summary>
        private static byte[] BoxDownsample(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh;
                int y1 = (y + 1) * sh / dh;
                if (y1 <= y0)
                {
                    y1 = y0 + 1;
                }
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw;
                    int x1 = (x + 1) * sw / dw;
                    if (x1 <= x0)
                    {
                        x1 = x0 + 1;
                    }

                    long b = 0, g = 0, r = 0, a = 0;
                    int n = 0;
                    for (int sy = y0; sy < y1 && sy < sh; sy++)
                    {
                        int rowBase = sy * sw;
                        for (int sx = x0; sx < x1 && sx < sw; sx++)
                        {
                            int si = (rowBase + sx) * 4;
                            b += src[si];
                            g += src[si + 1];
                            r += src[si + 2];
                            a += src[si + 3];
                            n++;
                        }
                    }
                    if (n == 0)
                    {
                        continue;
                    }

                    int di = (y * dw + x) * 4;
                    dst[di] = (byte)(b / n);
                    dst[di + 1] = (byte)(g / n);
                    dst[di + 2] = (byte)(r / n);
                    dst[di + 3] = (byte)(a / n);
                }
            }
            return dst;
        }

        /// <summary>
        /// 用预乘 BGRA 建 32bpp 颜色 DIB（bottom-up）+ 1bpp 全 0 掩码，生成 HICON。
        /// CreateIconIndirect 会**拷贝**这两张位图，所以函数内可以（也必须）释放它们。
        /// </summary>
        private static IntPtr CreateIconFromPremultipliedBgra(byte[] premul, int size)
        {
            int stride = size * 4;

            // DIB 是 bottom-up：第一行数据对应图片最下面一行
            var colorBits = new byte[stride * size];
            for (int y = 0; y < size; y++)
            {
                Buffer.BlockCopy(premul, y * stride, colorBits, (size - 1 - y) * stride, stride);
            }

            // 1bpp 掩码，每行按 4 字节对齐，全 0 = 不屏蔽任何像素
            int maskStride = ((size + 31) / 32) * 4;
            var maskBits = new byte[maskStride * size];

            GCHandle colorHandle = GCHandle.Alloc(colorBits, GCHandleType.Pinned);
            GCHandle maskHandle = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
            IntPtr hbmColor = IntPtr.Zero;
            IntPtr hbmMask = IntPtr.Zero;
            try
            {
                var info = new BITMAPINFO();
                info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                info.bmiHeader.biWidth = size;
                info.bmiHeader.biHeight = size;              // 正值 = bottom-up
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = 0;            // BI_RGB
                info.bmiHeader.biSizeImage = (uint)(stride * size);

                hbmColor = CreateDIBSection(IntPtr.Zero, ref info, DibRgbColors, out IntPtr bitsPtr, IntPtr.Zero, 0);
                if (hbmColor == IntPtr.Zero || bitsPtr == IntPtr.Zero)
                {
                    StartupLog.Write("[thumb] CreateDIBSection 失败 err=" + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                Marshal.Copy(colorBits, 0, bitsPtr, colorBits.Length);

                hbmMask = CreateBitmap(size, size, 1, 1, maskHandle.AddrOfPinnedObject());
                if (hbmMask == IntPtr.Zero)
                {
                    StartupLog.Write("[thumb] CreateBitmap(mask) 失败 err=" + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }

                var iconInfo = new ICONINFO
                {
                    fIcon = true,
                    xHotspot = 0,
                    yHotspot = 0,
                    hbmMask = hbmMask,
                    hbmColor = hbmColor,
                };

                IntPtr hIcon = CreateIconIndirect(ref iconInfo);
                if (hIcon == IntPtr.Zero)
                {
                    StartupLog.Write("[thumb] CreateIconIndirect 失败 err=" + Marshal.GetLastWin32Error());
                }
                return hIcon;
            }
            finally
            {
                if (hbmColor != IntPtr.Zero)
                {
                    DeleteObject(hbmColor);
                }
                if (hbmMask != IntPtr.Zero)
                {
                    DeleteObject(hbmMask);
                }
                maskHandle.Free();
                colorHandle.Free();
            }
        }

        // ------------------------------------------------------------------ P/Invoke

        private const uint DibRgbColors = 0;      // DIB_RGB_COLORS
        private const int SmCxSmIcon = 49;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(
            int nWidth,
            int nHeight,
            uint cPlanes,
            uint cBitsPerPel,
            IntPtr lpvBits);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }
    }
}
