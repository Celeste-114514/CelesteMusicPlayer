using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 任务栏缩略图按钮（Thumbnail Toolbar Buttons）：鼠标悬停任务栏图标时，
    /// 在预览小窗口下方显示 上一首 / 播放暂停 / 下一首 / 添加到我喜欢 四个按钮。
    /// 实现：IImageList（ImageList_Create） + ITaskbarList3.ThumbarSetImageList +
    ///       ThumbBarAddButtons + SetWindowSubclass 接收 WM_COMMAND。
    ///
    /// 关键点：
    /// - 用 SetWindowSubclass（comctl32）而不是 SetWindowLongPtr。WinUI 框架会在
    ///   窗口创建/激活后替换 WndProc，SetWindowLongPtr 会很快失效；SetWindowSubclass
    ///   维护一个子类化栈，被覆盖的子帧按入栈顺序调用，框架的版本在栈底不会被替换。
    /// - WM_COMMAND 中按钮 ID 在 LOWORD(wParam)；HIWORD 是通知码（THBN_CLICKED=0x1800）。
    /// - 用 IImageList + dwMask=ThbBitmap 路径（不用 ThbIcon/HICON）—— 此路径对 marshal
    ///   错位免疫，且 ImageList 内置对 Explorer 透明，无需跨进程生存期管理。
    /// - 所有 ITaskbarList3 调用带 [PreserveSig] 返回 int，错误直接日志（之前 void
    ///   返回失败被 catch 吞掉，看起来"按钮存在但不能点"）。
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        public const int BtnPrev = 1001;
        public const int BtnPlayPause = 1002;
        public const int BtnNext = 1003;
        public const int BtnFavorite = 1004;

        private const uint WmCommand = 0x0111;
        private const ushort ThbnClicked = 0x1800;

        // IImageList flags
        private const uint IlcColor32 = 0x00000020; // 32-bit color depth
        private const uint IlcAlpha = 0x00000080;   // alpha channel (must pair with ILC_COLOR32)

        private readonly MainWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly ITaskbarList3? _taskbar;
        private SubclassProc? _subclassDelegate;
        private readonly IntPtr _subclassId = new(0xC3);
        private IntPtr _himl = IntPtr.Zero;        // HIMAGELIST handle (we own, destroy in Dispose)
        private bool _added;
        private bool _disposed;
        private bool _isPlaying;

        public TaskbarThumbnailButtons(MainWindow owner, IntPtr hwnd)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _hwnd = hwnd;
            _taskbar = CreateTaskbarList();
        }

        /// <summary>注册 4 个按钮并开始拦截 WM_COMMAND。重复调用安全。</summary>
        public void Add()
        {
            if (_added || _disposed || _hwnd == IntPtr.Zero || _taskbar == null)
            {
                return;
            }

            try
            {
                _subclassDelegate = SubclassWndProc;
                bool subclassed = SetWindowSubclass(_hwnd, _subclassDelegate, _subclassId, IntPtr.Zero);
                if (!subclassed)
                {
                    StartupLog.Write("任务栏缩略图：SetWindowSubclass 失败，错误=" + Marshal.GetLastWin32Error());
                }

                _himl = BuildImageList();
                if (_himl == IntPtr.Zero)
                {
                    StartupLog.Write("任务栏缩略图：ImageList_Create 失败");
                    return;
                }

                int hr = _taskbar.ThumbBarSetImageList(_hwnd, _himl);
                if (hr != 0)
                {
                    StartupLog.Write("任务栏缩略图：ThumbBarSetImageList 失败 hr=0x" + hr.ToString("X8"));
                    ImageList_Destroy(_himl);
                    _himl = IntPtr.Zero;
                    return;
                }

                // iBitmap 索引顺序：0=Prev / 1=Play / 2=Next / 3=Heart / 4=Pause
                var prevBtn = MakeBitmapButton(BtnPrev, 0, "上一首");
                var playBtn = MakeBitmapButton(BtnPlayPause, 1, "播放");
                var nextBtn = MakeBitmapButton(BtnNext, 2, "下一首");
                var favBtn = MakeBitmapButton(BtnFavorite, 3, "添加到我喜欢");
                var arr = new[] { prevBtn, playBtn, nextBtn, favBtn };

                hr = _taskbar.ThumbBarAddButtons(_hwnd, (uint)arr.Length, ref arr[0]);
                StartupLog.Write("任务栏缩略图：ThumbBarAddButtons hr=0x" + hr.ToString("X8")
                    + " size=" + Marshal.SizeOf<THUMBBUTTON>()
                    + " thumbCount=" + arr.Length);

                _added = hr == 0;
                if (_added)
                {
                    StartupLog.Write("任务栏缩略图按钮已添加（4 个）");
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.Add", caught);
            }
        }

        /// <summary>更新播放/暂停按钮图标（playing=true 显示暂停图标）。未添加时忽略。</summary>
        public void UpdatePlayPause(bool playing)
        {
            if (!_added || _disposed || _taskbar == null)
            {
                return;
            }

            _isPlaying = playing;
            try
            {
                // 按位置索引：第 2 个按钮（index 1）是播放/暂停
                var btn = MakeBitmapButton(BtnPlayPause, (uint)(playing ? 4 : 1), playing ? "暂停" : "播放");
                int hr = _taskbar.ThumbBarUpdateButtons(_hwnd, 1, ref btn);
                if (hr != 0)
                {
                    StartupLog.Write("任务栏缩略图：UpdatePlayPause hr=0x" + hr.ToString("X8"));
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.UpdatePlayPause", caught);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_subclassDelegate != null && _hwnd != IntPtr.Zero)
                {
                    RemoveWindowSubclass(_hwnd, _subclassDelegate, _subclassId);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("TaskbarThumbnailButtons.Dispose.subclass", caught); }

            try
            {
                if (_taskbar != null && _hwnd != IntPtr.Zero)
                {
                    // ThumbBarRemoveButtons 不存在；用空 ImageList 解绑并清理所有按钮
                    IntPtr empty = ImageList_Create(1, 1, IlcColor32, 0, 0);
                    if (empty != IntPtr.Zero)
                    {
                        _taskbar.ThumbBarSetImageList(_hwnd, empty);
                        ImageList_Destroy(empty);
                    }
                }
            }
            catch (Exception caught) { StartupLog.WriteException("TaskbarThumbnailButtons.Dispose.remove", caught); }

            if (_himl != IntPtr.Zero)
            {
                try { ImageList_Destroy(_himl); } catch (Exception caught) { StartupLog.WriteException("IL destroy", caught); }
                _himl = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        // ---------------------------------------------------------------- ImageList

        /// <summary>创建 32x32 ARGB ImageList，按 0=Prev 1=Play 2=Next 3=Heart 4=Pause 装入 5 个自绘图标。</summary>
        private IntPtr BuildImageList()
        {
            IntPtr himl = ImageList_Create(32, 32, IlcColor32 | IlcAlpha, 5, 0);
            if (himl == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr[] bitmaps = new IntPtr[5];
            try
            {
                bitmaps[0] = RenderGlyph(PaintGlyphPrev);
                bitmaps[1] = RenderGlyph(PaintGlyphPlay);
                bitmaps[2] = RenderGlyph(PaintGlyphNext);
                bitmaps[3] = RenderGlyph(PaintGlyphHeart);
                bitmaps[4] = RenderGlyph(PaintGlyphPause);

                for (int i = 0; i < bitmaps.Length; i++)
                {
                    if (bitmaps[i] == IntPtr.Zero)
                    {
                        StartupLog.Write("任务栏缩略图：glyph bitmap[" + i + "] 为空");
                        return IntPtr.Zero;
                    }

                    int added = ImageList_Add(himl, bitmaps[i], IntPtr.Zero);
                    if (added == -1)
                    {
                        StartupLog.Write("任务栏缩略图：ImageList_Add 失败 index=" + i);
                    }
                }
                return himl;
            }
            finally
            {
                foreach (IntPtr hb in bitmaps)
                {
                    if (hb != IntPtr.Zero)
                    {
                        DeleteObject(hb);
                    }
                }
            }
        }

        /// <summary>在 32x32 ARGB Bitmap 上绘制一个 glyph，返回 HBITMAP 句柄（调用方负责 DeleteObject 释放）。</summary>
        private static IntPtr RenderGlyph(Action<Graphics> paint)
        {
            using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                paint(g);
            }
            return bmp.GetHbitmap();
        }

        // Glyph 绘制：白色为主，红色心形为独立颜色
        private static readonly Brush White = new SolidBrush(Color.White);
        private static readonly Brush Red = new SolidBrush(Color.FromArgb(232, 17, 35));

        private static void PaintGlyphPrev(Graphics g)
        {
            // 单竖线 + 实心三角（左指）
            g.FillRectangle(White, 7, 8, 3, 16);
            g.FillPolygon(White, new[]
            {
                new Point(11, 8),
                new Point(25, 16),
                new Point(11, 24)
            });
        }

        private static void PaintGlyphPlay(Graphics g)
        {
            g.FillPolygon(White, new[]
            {
                new Point(9, 7),
                new Point(25, 16),
                new Point(9, 25)
            });
        }

        private static void PaintGlyphPause(Graphics g)
        {
            // 两条竖线
            g.FillRectangle(White, 9, 7, 5, 18);
            g.FillRectangle(White, 18, 7, 5, 18);
        }

        private static void PaintGlyphNext(Graphics g)
        {
            // 实心三角（右指）+ 单竖线
            g.FillPolygon(White, new[]
            {
                new Point(21, 8),
                new Point(7, 16),
                new Point(21, 24)
            });
            g.FillRectangle(White, 22, 8, 3, 16);
        }

        private static void PaintGlyphHeart(Graphics g)
        {
            using var heart = new GraphicsPath();
            heart.AddEllipse(5, 7, 14, 12);
            heart.AddEllipse(13, 7, 14, 12);
            heart.AddPolygon(new[]
            {
                new Point(4, 14),
                new Point(28, 14),
                new Point(16, 27)
            });
            g.FillPath(Red, heart);
        }

        // ---------------------------------------------------------------- Subclass

        private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (msg == WmCommand)
            {
                long wp = wParam.ToInt64();
                int id = (int)(wp & 0xFFFF);              // LOWORD = 按钮 ID
                int notifyCode = (int)((wp >> 16) & 0xFFFF); // HIWORD = 通知码
                if (notifyCode == ThbnClicked)
                {
                    switch (id)
                    {
                        case BtnPrev:
                            StartupLog.Write("任务栏按钮被点击: 上一首");
                            _owner.PreviousPublic();
                            return IntPtr.Zero;
                        case BtnPlayPause:
                            StartupLog.Write("任务栏按钮被点击: 播放/暂停");
                            _owner.TogglePlayPausePublic();
                            return IntPtr.Zero;
                        case BtnNext:
                            StartupLog.Write("任务栏按钮被点击: 下一首");
                            _owner.NextPublic();
                            return IntPtr.Zero;
                        case BtnFavorite:
                            StartupLog.Write("任务栏按钮被点击: 收藏");
                            _owner.FavoriteCurrentPublic();
                            return IntPtr.Zero;
                    }
                }
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private static THUMBBUTTON MakeBitmapButton(uint id, uint bitmapIndex, string tip)
        {
            return new THUMBBUTTON
            {
                dwMask = ThbBitmap | ThbTooltip | ThbFlags,
                iId = id,
                iBitmap = bitmapIndex,
                hIcon = IntPtr.Zero,
                szTip = tip,
                dwFlags = ThbfEnabled
            };
        }

        // ---------------------------------------------------------------- P/Invoke

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
        private const uint ThbBitmap = 0x00000001;
        private const uint ThbTooltip = 0x00000004;
        private const uint ThbFlags = 0x00000010;
        private const uint ThbfEnabled = 0x00000000;

        private static ITaskbarList3? CreateTaskbarList()
        {
            try
            {
                Type? type = Type.GetTypeFromCLSID(ClsidTaskbarList);
                if (type != null && Activator.CreateInstance(type) is ITaskbarList3 list)
                {
                    list.HrInit();
                    return list;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.CreateTaskbarList", caught);
            }
            return null;
        }

        [DllImport("comctl32.dll", EntryPoint = "#410")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", EntryPoint = "#412")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll", EntryPoint = "#413")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("comctl32.dll")]
        private static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int cInitial, int cGrow);

        [DllImport("comctl32.dll")]
        private static extern int ImageList_Add(IntPtr himl, IntPtr hbm, IntPtr hbmMask);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImageList_Destroy(IntPtr himl);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct THUMBBUTTON
        {
            public uint dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;
            public uint dwFlags;
        }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ITaskbarList
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            // ITaskbarList2
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            // ITaskbarList3
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int state);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI);
            // 全部加 PreserveSig，错误不被吞；调用方 HrStart 决定写日志还是抛
            [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [In] ref THUMBBUTTON pButton);
            [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [In] ref THUMBBUTTON pButton);
            [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hicon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, ref RECT prcClip);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
