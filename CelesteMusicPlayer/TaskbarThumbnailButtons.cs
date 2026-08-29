using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 任务栏缩略图按钮（Thumbnail Toolbar Buttons）：鼠标悬停任务栏图标时，
    /// 在预览小窗口下方显示 上一首 / 播放暂停 / 下一首 / 添加到我喜欢 四个按钮。
    /// 实现：ITaskbarList3.ThumbarAddButtons + SetWindowSubclass 接收 WM_COMMAND。
    ///
    /// 关键点：
    /// - 用 SetWindowSubclass（comctl32）而不是 SetWindowLongPtr。WinUI 框架会在
    ///   窗口创建/激活后替换 WndProc，SetWindowLongPtr 会很快失效；SetWindowSubclass
    ///   维护一个子类化栈，被覆盖的子帧按入栈顺序调用，框架的版本在栈底不会被替换。
    /// - WM_COMMAND 中按钮 ID 在 LOWORD(wParam)；HIWORD 是通知码（THBN_CLICKED=0x1800）。
    ///   若错把 HIWORD 当 ID，switch 永远匹配不上 1001-1004。
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        public const int BtnPrev = 1001;
        public const int BtnPlayPause = 1002;
        public const int BtnNext = 1003;
        public const int BtnFavorite = 1004;

        private const uint WmCommand = 0x0111;
        private const ushort ThbnClicked = 0x1800;

        private readonly MainWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly ITaskbarList3? _taskbar;
        private SubclassProc? _subclassDelegate;
        private IntPtr _subclassId = new(0xC3); // 任意唯一标识
        private Icon? _iconPrev;
        private Icon? _iconPlayPause;
        private Icon? _iconNext;
        private Icon? _iconFavorite;
        private bool _added;
        private bool _disposed;

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

                var prevIcon = IconFromStock(SiidMediaPrevious);
                var playIcon = IconFromStock(SiidMediaPlay);
                var nextIcon = IconFromStock(SiidMediaNext);
                var favIcon = MakeHeartIcon();

                // 系统 stock 图标 32x32 + Bitmap 自画心形 32x32；显式缩放到 [16,32] 范围
                _iconPrev = NormalizeIcon(prevIcon, 32);
                _iconPlayPause = NormalizeIcon(playIcon, 32);
                _iconNext = NormalizeIcon(nextIcon, 32);
                _iconFavorite = NormalizeIcon(favIcon, 32);

                var buttons = new[]
                {
                    MakeButton(BtnPrev, _iconPrev, "上一首"),
                    MakeButton(BtnPlayPause, _iconPlayPause, "播放 / 暂停"),
                    MakeButton(BtnNext, _iconNext, "下一首"),
                    MakeButton(BtnFavorite, _iconFavorite, "添加到我喜欢")
                };
                _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, ref buttons[0]);
                _added = true;
                StartupLog.Write("任务栏缩略图按钮已添加（4 个）");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.Add", caught);
            }
        }

        /// <summary>更新播放/暂停按钮图标与提示（playing=true 显示暂停图标）。未添加时忽略。</summary>
        public void UpdatePlayPause(bool playing)
        {
            if (!_added || _disposed || _taskbar == null)
            {
                return;
            }

            try
            {
                Icon? old = _iconPlayPause;
                _iconPlayPause = NormalizeIcon(playing ? IconFromStock(SiidMediaPause) : IconFromStock(SiidMediaPlay), 32);
                var btn = MakeButton(BtnPlayPause, _iconPlayPause, playing ? "暂停" : "播放");
                _taskbar.ThumbBarUpdateButtons(_hwnd, 1, ref btn);
                if (old != null && !ReferenceEquals(old, _iconPlayPause))
                {
                    DestroyIconSafe(old);
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
                    _taskbar.ThumbBarUpdateButtons(_hwnd, 0, ref EmptyButton);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("TaskbarThumbnailButtons.Dispose.remove", caught); }

            DestroyIconSafe(_iconPrev);
            DestroyIconSafe(_iconPlayPause);
            DestroyIconSafe(_iconNext);
            DestroyIconSafe(_iconFavorite);
            GC.SuppressFinalize(this);
        }

        /// <summary>释放由 SHGetStockIconInfo / Bitmap.GetHicon 生成的 HICON 及其包装。</summary>
        private static void DestroyIconSafe(Icon? icon)
        {
            if (icon == null)
            {
                return;
            }

            try { DestroyIcon(icon.Handle); } catch { }
            try { icon.Dispose(); } catch { }
        }

        /// <summary>
        /// 把任意 HICON 缩放到指定尺寸（默认 32x32）并返回独立 HICON。
        /// 缩略图按钮要求 32x32 32-bit ARGB；缩放并复制像素保证格式兼容。
        /// </summary>
        private Icon NormalizeIcon(Icon? source, int size)
        {
            if (source == null || source.Handle == IntPtr.Zero)
            {
                return MakeFallbackIcon(size);
            }

            try
            {
                using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(source.ToBitmap(), 0, 0, size, size);
                }

                IntPtr hicon = bmp.GetHicon();
                return Icon.FromHandle(hicon);
            }
            catch
            {
                return MakeFallbackIcon(size);
            }
        }

        /// <summary>兜底图标：实心方块 + 文字（仅当 SHGetStockIconInfo 全部失败时使用）。</summary>
        private Icon MakeFallbackIcon(int size)
        {
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(Color.FromArgb(180, Color.Gray));
                g.FillRectangle(brush, 2, 2, size - 4, size - 4);
            }

            IntPtr hicon = bmp.GetHicon();
            return Icon.FromHandle(hicon);
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (msg == WmCommand)
            {
                long wp = wParam.ToInt64();
                int id = (int)(wp & 0xFFFF);          // LOWORD = 按钮 ID（关键修正）
                int notifyCode = (int)((wp >> 16) & 0xFFFF); // HIWORD = 通知码

                if (notifyCode == ThbnClicked)
                {
                    switch (id)
                    {
                        case BtnPrev:
                            _owner.PreviousPublic();
                            return IntPtr.Zero;
                        case BtnPlayPause:
                            _owner.TogglePlayPausePublic();
                            return IntPtr.Zero;
                        case BtnNext:
                            _owner.NextPublic();
                            return IntPtr.Zero;
                        case BtnFavorite:
                            _owner.FavoriteCurrentPublic();
                            return IntPtr.Zero;
                    }
                }
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private static THUMBBUTTON MakeButton(uint id, Icon? icon, string tip)
        {
            return new THUMBBUTTON
            {
                dwMask = ThbIcon | ThbTooltip,
                iId = id,
                hIcon = icon?.Handle ?? IntPtr.Zero,
                szTip = tip,
                dwFlags = ThbfEnabled
            };
        }

        private Icon? IconFromStock(uint siid)
        {
            try
            {
                var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
                int hr = SHGetStockIconInfo(siid, ShgsIcon | ShgsLargeIcon, ref info);
                if (hr == 0 && info.hIcon != IntPtr.Zero)
                {
                    return Icon.FromHandle(info.hIcon);
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>现画 32x32 红色心形图标（收藏）。</summary>
        private Icon MakeHeartIcon()
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var heart = new GraphicsPath();
                heart.AddEllipse(6, 6, 14, 12);
                heart.AddEllipse(14, 6, 14, 12);
                heart.AddPolygon(new[]
                {
                    new Point(3, 14),
                    new Point(29, 14),
                    new Point(16, 28)
                });
                using var brush = new SolidBrush(Color.FromArgb(232, 17, 35));
                g.FillPath(brush, heart);
            }

            IntPtr hicon = bmp.GetHicon();
            return Icon.FromHandle(hicon);
        }

        // ---------------------------------------------------------------- P/Invoke

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        private static readonly Guid ClsidTaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
        private const uint SiidMediaPrevious = 0x001B;
        private const uint SiidMediaNext = 0x001C;
        private const uint SiidMediaPlay = 0x001D;
        private const uint SiidMediaPause = 0x001E;
        private const uint ShgsIcon = 0x000000100;
        private const uint ShgsLargeIcon = 0x000000000;
        private const uint ThbIcon = 0x00000002;
        private const uint ThbTooltip = 0x00000004;
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

        [DllImport("comctl32.dll", SetLastError = true, EntryPoint = "#410")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true, EntryPoint = "#412")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll", EntryPoint = "#413")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

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
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [In] ref THUMBBUTTON pButton);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [In] ref THUMBBUTTON pButton);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
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

        private static THUMBBUTTON EmptyButton = default;
    }
}
