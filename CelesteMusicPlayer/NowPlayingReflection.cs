using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 生成「水面倒影」贴图：封面底部一条带 → 上下翻转 → 自上而下的透明度衰减。
    ///
    /// 为什么把效果烤进图片，而不是用透明度遮罩：
    ///   WinUI 3 的 UIElement <b>没有 OpacityMask</b>（UWP 有，WinUI 3 移除了），
    ///   代码里写 element.OpacityMask 会直接编译报 CS1061。烤图最稳：
    ///   不引入新依赖（复用项目已有的 GDI+ / System.Drawing.Common），也不依赖 WinUI 版本特性。
    ///
    /// 为什么先铺到正方形画布：
    ///   水面布局下封面是"完整显示"（Stretch=Uniform，长方形封面不裁切、居中、留白透明）。
    ///   这里用同一规则先预渲染，再取"实际显示区域"的底部条带，才能保证镜像线正好落在
    ///   封面图案的底边上，任何比例的封面都不会出现"倒影和封面之间留一条缝"。
    ///   注意：条带必须从独立裁出的显示区域位图取（见 CreateReflection 内注释），
    ///   直接从带透明留白的方画布采样会让倒影边缘缺 1~2 像素。
    /// </summary>
    internal static class NowPlayingReflection
    {
        /// <summary>倒影高度占封面显示高度的比例。UI 侧 UpdateNowPlayingReflectionGeometry 必须与此一致。</summary>
        internal const double HeightRatio = 0.30;

        /// <summary>预渲染正方形画布边长；显示宽度最多 300 出头，512 已足够清晰。</summary>
        internal const int CanvasSize = 512;

        /// <summary>
        /// 由封面原始字节生成倒影 PNG；失败返回 Png=null。
        /// 返回尺寸均为 512 坐标系下的像素值：
        ///   DispW / DispH —— 封面在正方形画布中"实际显示"的宽高（长方形封面完整显示时小于 512）；
        ///   StripH        —— 倒影 PNG 的实际像素高（= DispH × HeightRatio）。
        /// UI 侧容器必须按「实际像素 × (cover/512)」换算，比例才与贴图严格一致，
        /// 否则 Stretch=Uniform 会 letterbox（这正是之前倒影左右缺 1px 的根因之一）。
        ///
        /// 封面显示规则与界面一致：Stretch=Uniform 完整显示（长方形封面不裁切、居中、留白透明）。
        ///
        /// 【一次缩放】关键：直接把原图底部条带画进目标位图，不再"先铺方画布再裁条带"。
        /// 两次双三次缩放会让边缘各被抹淡一圈，叠加后倒影左右各缺 1~2 像素（用户实测现象）。
        /// 单次缩放后，横向采样的边界正好是原图自身的左右边界（TileFlipXY 镜像补边有效），
        /// 纵向唯一处在图像内部的上边界落在翻转后的"渐隐尾部"（本来就全透明），无副作用。
        /// </summary>
        internal static (byte[]? Png, int DispW, int DispH, int StripH) CreateReflection(byte[]? coverBytes)
        {
            if (coverBytes == null || coverBytes.Length == 0)
            {
                return (null, 0, 0, 0);
            }

            try
            {
                using var input = new MemoryStream(coverBytes);
                using var src = Image.FromStream(input);

                // 与界面 Stretch=Uniform 一致：取较小缩放比 → 完整放进正方形画布、居中。
                // Uniform 下"显示区域"就是整张图，所以倒影直接取原图底部条带即可。
                double scale = Math.Min((double)CanvasSize / src.Width, (double)CanvasSize / src.Height);
                int dispW = Math.Max(1, (int)Math.Round(src.Width * scale));
                int dispH = Math.Max(1, (int)Math.Round(src.Height * scale));
                int stripH = Math.Max(1, (int)Math.Round(dispH * HeightRatio));
                // 条带在原始像素里的高度（与显示尺度一致，保证倒影上下不拉伸）
                double srcStripH = Math.Max(1.0, stripH / scale);

                using var bmp = new Bitmap(dispW, stripH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using var ia = new ImageAttributes();
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    // 翻转画布后，原图最底行落在输出第 0 行 → 倒影顶端紧贴封面底边，接缝天然对上
                    g.TranslateTransform(0, stripH);
                    g.ScaleTransform(1, -1);
                    g.DrawImage(
                        src,
                        new Rectangle(0, 0, dispW, stripH),
                        0f,
                        (float)(src.Height - srcStripH),
                        src.Width,
                        (float)srcStripH,
                        GraphicsUnit.Pixel,
                        ia);
                    g.ResetTransform();
                }

                ApplyTopToBottomFade(bmp);
                ReinforceEdgeColumns(bmp);

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return (ms.ToArray(), dispW, dispH, stripH);
            }
            catch (Exception caught)
            {
                // 静默失败会让上层只看到"没有倒影"而无从定位，这里记日志。
                // 典型：封面是 WebP / AVIF / HEIC（GDI+ 不认这些格式）。
                global::CelesteMusicPlayer.StartupLog.WriteException("NowPlayingReflection.cs", caught);
                return (null, 0, 0, 0);
            }
        }

        /// <summary>
        /// 边缘加固兜底：把最左/最右第 3 列的像素复制到外侧两列。
        /// 重采样链（双三次 × 两次绘制）不管怎么补边，最外 1~2 列的能量总有残留损失
        /// （表现为边缘半透明细缝）。倒影是渐变模糊效果，复制 2 列内容肉眼不可察，
        /// 但能保证贴图边缘与封面边缘严格对齐、不再缺像素。
        /// </summary>
        private static void ReinforceEdgeColumns(Bitmap bmp)
        {
            const int Band = 4;
            int w = bmp.Width;
            int h = bmp.Height;
            if (w < Band * 2 + 3)
            {
                return;
            }

            for (int y = 0; y < h; y++)
            {
                Color innerLeft = bmp.GetPixel(Band, y);
                Color innerRight = bmp.GetPixel(w - Band - 1, y);
                for (int x = 0; x < Band; x++)
                {
                    bmp.SetPixel(x, y, innerLeft);
                    bmp.SetPixel(w - 1 - x, y, innerRight);
                }
            }
        }

        /// <summary>自上而下渐隐：顶部最实（镜像起点），到底部完全透明（倒影没入水中）。</summary>
        private static void ApplyTopToBottomFade(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            for (int y = 0; y < h; y++)
            {
                double t = h <= 1 ? 0.0 : (double)y / (h - 1);
                double remaining = 1.0 - t;
                // 平方衰减：靠近封面的部分变化慢、尾端收得干净，比线性更像水面。
                byte alpha = (byte)Math.Clamp((int)Math.Round(255 * remaining * remaining * 0.92), 0, 255);
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bmp.SetPixel(x, y, Color.FromArgb(alpha, c.R, c.G, c.B));
                }
            }
        }
    }
}
