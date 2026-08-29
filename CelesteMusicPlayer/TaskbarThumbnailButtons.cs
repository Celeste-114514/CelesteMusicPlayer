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
    ///
    /// 本版实施要点：
    /// - 图标走 ThbIcon（每个按钮独立 HICON），不用 ThbImageList。
    ///   原因：comctl32 v6 ImageList 在某些 WinUI 3 + 系统环境下 ImageList_Create 返回 0
    ///   （[日志] 任务栏缩略图：ImageList_Create 失败），整条 ImageList 路径就被卡死。
    /// - HICON 路径：每个按钮一个 16x16 32bpp ARGB Bitmap + GetHicon()，
    ///   thumbar 受影响的只有当前按钮那个 HICON，路由清晰。
    /// - 子类化：SetWindowSubclass(comctl32) 而不是 SetWindowLongPtr，
    ///   WinUI 框架会替换 WndProc，SetWindowLongPtr 几小时就失效；comctl32 子类化栈
    ///   在框架之前，过滤规则明确。
    /// - WM_COMMAND：按钮 ID 在 LOWORD(wParam)；HIWORD 是通知码 (THBN_CLICKED=0x1800)。
    /// - 所有 ITaskbarList3 调用一律 [PreserveSig] 返回 int，失败 hr 直接写日志。
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        public const int BtnPrev = 1001;
        public const int BtnPlayPause = 1002;
        public const int BtnNext = 1003;
        public const int BtnFavorite = 1004;

        private const uint WmCommand = 0x0111;
        private const ushort ThbnClicked = 0x1800;

        // Explorer 任务栏缩略图按钮的硬性尺寸（官方要求 16x16 @ 96 DPI）
        private const int GlyphSize = 16;

        private readonly MainWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly ITaskbarList3? _taskbar;

        private SubclassProc? _subclassDelegate;
        private readonly IntPtr _subclassId = new(0xC3);

        // 每个按钮自己的 HICON（ThbIcon 路径，不依赖 comctl32 ImageList）
        private IntPtr _hPrev = IntPtr.Zero;
        private IntPtr _hPlay = IntPtr.Zero;
        private IntPtr _hPause = IntPtr.Zero;
        private IntPtr _hNext = IntPtr.Zero;
        private IntPtr _hHeart = IntPtr.Zero;

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
            StartupLog.Write("[thumb] Add 被调用, hwnd=0x" + _hwnd.ToString("X")
                + " taskbar=" + (_taskbar != null)
                + " 已添加=" + _added + " 已释放=" + _disposed);

            if (_added || _disposed || _hwnd == IntPtr.Zero || _taskbar == null)
            {
                StartupLog.Write("[thumb] Add 早返回: " + (_added ? "已添加" : _disposed ? "已释放" : _hwnd == IntPtr.Zero ? "hwnd=0" : "taskbar=null"));
                return;
            }

            try
            {
                // 子类化窗口接 WM_COMMAND
                _subclassDelegate = SubclassWndProc;
                bool subclassed = SetWindowSubclass(_hwnd, _subclassDelegate, _subclassId, IntPtr.Zero);
                StartupLog.Write("[thumb] SetWindowSubclass ok=" + subclassed + " err=" + Marshal.GetLastWin32Error());

                // 5 个独立 HICON（不为 ImageList 所用，仅为 HICON 路径准备）
                _hPrev = RenderGlyphHicon(PaintGlyphPrev);
                _hPlay = RenderGlyphHicon(PaintGlyphPlay);
                _hPause = RenderGlyphHicon(PaintGlyphPause);
                _hNext = RenderGlyphHicon(PaintGlyphNext);
                _hHeart = RenderGlyphHicon(PaintGlyphHeart);
                StartupLog.Write("[thumb] HICON 渲染完成: prev=0x" + _hPrev.ToString("X")
                    + " play=0x" + _hPlay.ToString("X")
                    + " pause=0x" + _hPause.ToString("X")
                    + " next=0x" + _hNext.ToString("X")
                    + " heart=0x" + _hHeart.ToString("X"));

                if (_hPrev == IntPtr.Zero || _hPlay == IntPtr.Zero || _hPause == IntPtr.Zero
                    || _hNext == IntPtr.Zero || _hHeart == IntPtr.Zero)
                {
                    StartupLog.Write("[thumb] HICON 渲染至少一个为零，放弃 ThbIcon 路径");
                    FreeAllHicons();
                    return;
                }

                // ThbIcon 路径：每个按钮用自己的 HICON
                var prevBtn = MakeIconButton(BtnPrev, _hPrev, "上一首");
                var playBtn = MakeIconButton(BtnPlayPause, _hPlay, "播放");
                var nextBtn = MakeIconButton(BtnNext, _hNext, "下一首");
                var favBtn = MakeIconButton(BtnFavorite, _hHeart, "添加到喜欢");
                var arr = new[] { prevBtn, playBtn, nextBtn, favBtn };

                int hrAdd = _taskbar.ThumbBarAddButtons(_hwnd, (uint)arr.Length, ref arr[0]);
                StartupLog.Write("[thumb] ThumbBarAddButtons hr=0x" + hrAdd.ToString("X8")
                    + " count=" + arr.Length + " (id 1001-1004, ThbIcon path)");

                _added = hrAdd == 0;
                if (_added)
                {
                    StartupLog.Write("任务栏缩略图按钮已添加（4 个，ThbIcon HICON path）");
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
                var btn = MakeIconButton(BtnPlayPause, playing ? _hPause : _hPlay,
                    playing ? "暂停" : "播放");
                int hr = _taskbar.ThumbBarUpdateButtons(_hwnd, 1, ref btn);
                if (hr != 0)
                {
                    StartupLog.Write("[thumb] UpdatePlayPause hr=0x" + hr.ToString("X8"));
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

            // 销毁所有 HICON
            FreeAllHicons();
            GC.SuppressFinalize(this);
        }

        private void FreeAllHicons()
        {
            DestroyIconSafely(ref _hPrev);
            DestroyIconSafely(ref _hPlay);
            DestroyIconSafely(ref _hPause);
            DestroyIconSafely(ref _hNext);
            DestroyIconSafely(ref _hHeart);
        }

        private static void DestroyIconSafely(ref IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            try { DestroyIcon(h); }
            catch (Exception caught) { StartupLog.WriteException("DestroyIcon", caught); }
            h = IntPtr.Zero;
        }

        // ---------------------------------------------------------------- HICON 渲染

        /// <summary>在 GlyphSize x GlyphSize 32bpp ARGB Bitmap 上绘制 glyph，转成 HICON（caller 负责 DestroyIcon）。</summary>
        private static IntPtr RenderGlyphHicon(Action<Graphics> paint)
        {
            using var bmp = new Bitmap(GlyphSize, GlyphSize, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                paint(g);
            }
            IntPtr hIcon = bmp.GetHicon();
            if (hIcon == IntPtr.Zero)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.RenderGlyphHicon", new Exception("GetHicon 返回 0"));
            }
            return hIcon;
        }

        // Glyph 颜色
        private static readonly Brush White = new SolidBrush(Color.White);
        private static readonly Brush Red = new SolidBrush(Color.FromArgb(232, 17, 35));

        private static void PaintGlyphPrev(Graphics g)
        {
            g.FillRectangle(White, 4, 4, 2, 8);
            g.FillPolygon(White, new[]
            {
                new Point(12, 8),
                new Point(6, 4),
                new Point(6, 12)
            });
        }

        private static void PaintGlyphPlay(Graphics g)
        {
            g.FillPolygon(White, new[]
            {
                new Point(13, 8),
                new Point(4, 3),
                new Point(4, 13)
            });
        }

        private static void PaintGlyphNext(Graphics g)
        {
            g.FillPolygon(White, new[]
            {
                new Point(13, 8),
                new Point(4, 4),
                new Point(4, 12)
            });
            g.FillRectangle(White, 10, 4, 2, 8);
        }

        private static void PaintGlyphPause(Graphics g)
        {
            g.FillRectangle(White, 4, 3, 3, 10);
            g.FillRectangle(White, 9, 3, 3, 10);
        }

        private static void PaintGlyphHeart(Graphics g)
        {
            using var heart = new GraphicsPath();
            heart.AddEllipse(2, 3, 8, 7);
            heart.AddEllipse(6, 3, 8, 7);
            heart.AddPolygon(new[]
            {
                new Point(2, 7),
                new Point(14, 7),
                new Point(8, 14)
            });
            g.FillPath(Red, heart);
        }

        // ---------------------------------------------------------------- Subclass

        private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (msg == WmCommand)
            {
                long wp = wParam.ToInt64();
                int id = (int)(wp & 0xFFFF);               // LOWORD = 按钮 ID
                int notifyCode = (int)((wp >> 16) & 0xFFFF); // HIWORD = 通知码
                if (notifyCode == ThbnClicked)
                {
                    switch (id)
                    {
                        case BtnPrev:
                            StartupLog.Write("任务栏按钮被点击: 上一首 (id=1001)");
                            SafeCall(_owner.PreviousPublic);
                            return IntPtr.Zero;
                        case BtnPlayPause:
                            StartupLog.Write("任务栏按钮被点击: 播放/暂停 (id=1002)");
                            SafeCall(_owner.TogglePlayPausePublic);
                            return IntPtr.Zero;
                        case BtnNext:
                            StartupLog.Write("任务栏按钮被点击: 下一首 (id=1003)");
                            SafeCall(_owner.NextPublic);
                            return IntPtr.Zero;
                        case BtnFavorite:
                            StartupLog.Write("任务栏按钮被点击: 收藏 (id=1004)");
                            SafeCall(_owner.FavoriteCurrentPublic);
                            return IntPtr.Zero;
                        default:
                            StartupLog.Write("任务栏按钮未识别 id=" + id);
                            break;
                    }
                }
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private static void SafeCall(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception caught) { StartupLog.WriteException("Taskbar button invoke", caught); }
        }

        private static THUMBBUTTON MakeIconButton(uint id, IntPtr hIcon, string tip)
        {
            return new THUMBBUTTON
            {
                dwMask = ThbIcon | ThbTooltip | ThbFlags,
                iId = id,
                iBitmap = 0,
                hIcon = hIcon,
                szTip = tip,
                dwFlags = ThbfEnabled
            };
        }

        // ---------------------------------------------------------------- P/Invoke

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
        private const uint ThbIcon = 0x00000002;
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

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
            // ThumbBar 三件套全部 [PreserveSig]
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
