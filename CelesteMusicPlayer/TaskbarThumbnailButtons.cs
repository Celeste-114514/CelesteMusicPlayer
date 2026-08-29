using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 任务栏缩略图按钮（Thumbnail Toolbar Buttons）：鼠标悬停任务栏图标时，
    /// 在预览小窗口下方显示 上一首 / 播放暂停 / 下一首 / 添加到我喜欢 四个按钮。
    /// 实现：ITaskbarList3.ThumbarAddButtons + 子类化主窗口 WndProc 接收 WM_COMMAND
    /// （按钮 ID 位于 wParam 高 16 位）。图标用 System.Drawing 现画（无需素材文件）。
    /// </summary>
    internal sealed class TaskbarThumbnailButtons : IDisposable
    {
        public const int BtnPrev = 1001;
        public const int BtnPlayPause = 1002;
        public const int BtnNext = 1003;
        public const int BtnFavorite = 1004;

        private const uint WmCommand = 0x0111;

        private readonly MainWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly ITaskbarList3? _taskbar; // ITaskbarList3
        private IntPtr _oldWndProc = IntPtr.Zero;
        private WndProcDelegate? _wndProcDelegate;
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
                _wndProcDelegate = WndProc;
                _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndproc,
                    Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

                var buttons = new[]
                {
                    MakeButton(BtnPrev, IconFromStock(SiidMediaPrevious), "上一首"),
                    MakeButton(BtnPlayPause, IconFromStock(SiidMediaPlay), "播放 / 暂停"),
                    MakeButton(BtnNext, IconFromStock(SiidMediaNext), "下一首"),
                    MakeButton(BtnFavorite, MakeHeartIcon(), "添加到我喜欢")
                };
                // 注意：ThumbarAddButtons 用 ref 传第一个元素（数组参数会触发
                // 0x80131165 Typelib export 错误——CLR 需要类型库编组数组）
                _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, ref buttons[0]);
                _added = true;
                StartupLog.Write("任务栏缩略图按钮已添加");
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
                _iconPlayPause = playing ? IconFromStock(SiidMediaPause) : IconFromStock(SiidMediaPlay);
                var btn = MakeButton(BtnPlayPause, _iconPlayPause, playing ? "暂停" : "播放");
                _taskbar.ThumbBarUpdateButtons(_hwnd, 1, ref btn);
                // ThumbarUpdateButtons 已复制新图标，旧的释放掉避免 GDI 句柄泄漏
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
                if (_taskbar != null && _hwnd != IntPtr.Zero)
                {
                    // ITaskbarList3 无 ThumbBarRemoveButtons；按钮数置 0 即移除全部
                    // （cButtons=0 时 pButton 被忽略，传空按钮即可）
                    _taskbar.ThumbBarUpdateButtons(_hwnd, 0, ref EmptyButton);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("TaskbarThumbnailButtons.Dispose.remove", caught); }

            try
            {
                if (_oldWndProc != IntPtr.Zero && _hwnd != IntPtr.Zero)
                {
                    SetWindowLongPtr(_hwnd, GwlpWndproc, _oldWndProc);
                    _oldWndProc = IntPtr.Zero;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("TaskbarThumbnailButtons.Dispose.wndproc", caught); }

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

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmCommand)
            {
                uint id = (uint)((long)wParam >> 16);
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

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
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
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var heart = new GraphicsPath();
                // 心形：两个椭圆 + 底部三角，坐标按 32x32 缩放
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
            // GetHicon 生成的是独立 HICON，可安全包装为 Icon（不依赖 bmp 生命周期）
            return Icon.FromHandle(hicon);
        }

        // ---------------------------------------------------------------- P/Invoke

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int GwlpWndproc = -4;
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        /// <summary>ThumbBarUpdateButtons(cButtons=0) 时传入的空按钮（[In] ref 必须指向有效内存）。</summary>
        private static THUMBBUTTON EmptyButton = default;
    }
}
