using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace CelesteMusicPlayer {
internal static class NativeTaskbarIconFactory {
 private const byte MaskOpaqueAlpha=8;
 internal struct IconSet { internal nint Prev,Play,Pause,Next,HeartEmpty,HeartFilled; }
 internal static IconSet Create(){var set=new IconSet();try{set.Prev=Draw("prev");set.Play=Draw("play");set.Pause=Draw("pause");set.Next=Draw("next");set.HeartEmpty=Draw("heart");set.HeartFilled=Draw("heart-filled");return set;}catch{foreach(var h in new[]{set.Prev,set.Play,set.Pause,set.Next,set.HeartEmpty,set.HeartFilled})if(h!=0)DestroyIcon(h);throw;}}
 [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
 private static nint Draw(string kind){const int size=24;using var large=new Bitmap(96,96,PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(large)){g.Clear(Color.Transparent);g.ScaleTransform(4,4);g.SmoothingMode=SmoothingMode.AntiAlias;using var white=new SolidBrush(Color.White);using var red=new SolidBrush(Color.FromArgb(232,17,35));using var pen=new Pen(Color.White,1.8f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};if(kind=="play")g.FillPolygon(white,new[]{new PointF(8,4),new PointF(21,12),new PointF(8,20)});else if(kind=="pause"){g.FillRectangle(white,7,5,3,14);g.FillRectangle(white,15,5,3,14);}else if(kind=="prev"||kind=="next"){if(kind=="prev"){g.TranslateTransform(24,0);g.ScaleTransform(-1,1);}g.FillPolygon(white,new[]{new PointF(4,5),new PointF(15,12),new PointF(4,19)});g.DrawLine(pen,19,5,19,19);}else{using var heart=new GraphicsPath();heart.StartFigure();heart.AddBezier(12,20,9,17,3,13,3,8);heart.AddBezier(3,8,3,3,9,2,12,7);heart.AddBezier(12,7,15,2,21,3,21,8);heart.AddBezier(21,8,21,13,15,17,12,20);heart.CloseFigure();if(kind=="heart-filled")g.FillPath(red,heart);else{using var outline=new Pen(red,1.8f){LineJoin=LineJoin.Round};g.DrawPath(outline,heart);}}}using var bitmap=new Bitmap(size,size,PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(bitmap)){g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(large,new Rectangle(0,0,size,size));}var data=bitmap.LockBits(new Rectangle(0,0,size,size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);var bytes=new byte[size*size*4];try{Marshal.Copy(data.Scan0,bytes,0,bytes.Length);}finally{bitmap.UnlockBits(data);}return CreateIconFromPremultipliedBgra(bytes,size);}
        private static IntPtr CreateIconFromPremultipliedBgra(byte[] premul, int size)
        {
            int stride = size * 4;

            // DIB 是 bottom-up：第一行数据对应图片最下面一行
            var colorBits = new byte[stride * size];
            for (int y = 0; y < size; y++)
            {
                Buffer.BlockCopy(premul, y * stride, colorBits, (size - 1 - y) * stride, stride);
            }

            // 1bpp 掩码（bottom-up，每行按 4 字节对齐）：
            // bit=1 = 屏蔽该像素（透明），bit=0 = 显示颜色位图。
            //
            // **绝不能全 0**：全 0 掩码等于告诉系统"每个像素都不透明地画出来"，
            // 于是图标里 RGB=0 的透明区域被当成不透明黑像素绘制，任务栏 4 个按钮
            // 就成了 4 个小黑框。旧版走 GDI+ Bitmap.GetHicon() 时掩码由 GDI 依 alpha
            // 自动生成所以没问题，这里手动构造 DIB 就必须自己按 alpha 置位。
            int maskStride = ((size + 31) / 32) * 4;
            var maskBits = new byte[maskStride * size];
            for (int y = 0; y < size; y++)
            {
                int srcRow = (size - 1 - y) * size;   // 源 px 是 top-down，掩码是 bottom-up
                int dstRow = y * maskStride;
                for (int x = 0; x < size; x++)
                {
                    byte a = premul[(srcRow + x) * 4 + 3];
                    if (a >= MaskOpaqueAlpha)
                    {
                        continue;                      // 不透明 → 掩码位保持 0（正常绘制）
                    }
                    // 透明 → 掩码位置 1（屏蔽，该像素不被绘制）
                    maskBits[dstRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }

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
