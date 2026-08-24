using System;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 任务栏悬停缩略图工具栏（Thumbnail Toolbar）：上一首 / 播放暂停 / 下一首。
    /// 基于 ITaskbarList3::ThumbBarAddButtons，按钮图标用 GDI 内存绘制 Unicode 符号。
    /// 点击通过 WM_COMMAND(idCommand) 回传主窗口。
    /// </summary>
    public sealed class ThumbnailToolbar : IDisposable
    {
        // idCommand（WM_COMMAND 低 16 位）
        public const int CmdPrevious = 0x5001;
        public const int CmdPlayPause = 0x5002;
        public const int CmdNext = 0x5003;

        private const uint THBF_ENABLED = 0x0000;
        private const uint THBF_DISABLED = 0x0001;
        private const uint THBF_HIDDEN = 0x0008;
        private const uint THB_BITMAP = 0x0001;
        private const uint THB_TOOLTIP = 0x0002;
        private const uint THB_FLAGS = 0x0004;

        private readonly IntPtr _hwnd;
        private ITaskbarList3? _list;
        private readonly IntPtr _previousBmp;
        private readonly IntPtr _playBmp;
        private readonly IntPtr _pauseBmp;
        private readonly IntPtr _nextBmp;
        private readonly THUMBBUTTON[] _buttons;
        private bool _playing;

        public ThumbnailToolbar(IntPtr hwnd)
        {
            _hwnd = hwnd;
            try
            {
                _list = (ITaskbarList3)new TaskbarList();
                _list.HrInit();
            }
            catch
            {
                _list = null;
            }

            _previousBmp = CreateSymbolBitmap("⏮");
            _playBmp = CreateSymbolBitmap("▶");
            _pauseBmp = CreateSymbolBitmap("⏸");
            _nextBmp = CreateSymbolBitmap("⏭");

            _buttons = new THUMBBUTTON[3];
            for (int i = 0; i < 3; i++)
            {
                _buttons[i].dwMask = THB_BITMAP | THB_TOOLTIP | THB_FLAGS;
                _buttons[i].dwFlags = THBF_ENABLED;
                _buttons[i].iBitmap = 0;
            }

            _buttons[0].idCommand = CmdPrevious;
            _buttons[0].pszTip = "上一首";
            _buttons[0].hIcon = _previousBmp;
            _buttons[1].idCommand = CmdPlayPause;
            _buttons[1].pszTip = "播放 / 暂停";
            _buttons[1].hIcon = _playBmp;
            _buttons[2].idCommand = CmdNext;
            _buttons[2].pszTip = "下一首";
            _buttons[2].hIcon = _nextBmp;

            TryAddButtons();
        }

        private bool _added;

        private void TryAddButtons()
        {
            if (_list == null || _added)
            {
                return;
            }

            try
            {
                _list.ThumbBarAddButtons(_hwnd, (uint)_buttons.Length, _buttons);
                _added = true;
            }
            catch
            {
            }
        }

        /// <summary>播放/暂停状态切换时更新中间按钮的位图与提示（▶/⏸）。</summary>
        public void SetPlaying(bool playing)
        {
            if (_playing == playing)
            {
                return;
            }

            _playing = playing;
            if (_list == null)
            {
                return;
            }

            try
            {
                _buttons[1].hIcon = playing ? _pauseBmp : _playBmp;
                _buttons[1].pszTip = playing ? "暂停" : "播放";
                _list.ThumbBarUpdateButtons(_hwnd, 1, new[] { _buttons[1] });
                TryAddButtons();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_list != null)
            {
                try
                {
                    _list.ThumbBarUpdateButtons(_hwnd, 0, Array.Empty<THUMBBUTTON>());
                }
                catch
                {
                }
            }

            DeleteObject(_previousBmp);
            DeleteObject(_playBmp);
            DeleteObject(_pauseBmp);
            DeleteObject(_nextBmp);
            _list = null;
        }

        /// <summary>在内存 HDC 里画 Unicode 符号为 24x24 位图（GDI 单色不够，用 32bpp 白底黑字）。</summary>
        private static IntPtr CreateSymbolBitmap(string glyph)
        {
            const int size = 24;
            IntPtr hdc = GetDC(IntPtr.Zero);
            IntPtr bmp = IntPtr.Zero;
            if (hdc != IntPtr.Zero)
            {
                IntPtr mem = CreateCompatibleDC(hdc);
                if (mem != IntPtr.Zero)
                {
                    bmp = CreateCompatibleBitmap(hdc, size, size);
                    IntPtr old = SelectObject(mem, bmp);
                    // 白底
                    RECT rc = new RECT { left = 0, top = 0, right = size, bottom = size };
                    IntPtr brush = CreateSolidBrush(0xFFFFFF);
                    FillRect(mem, ref rc, brush);
                    DeleteObject(brush);
                    // 黑字（粗体）
                    IntPtr font = CreateFont(-18, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
                    IntPtr oldFont = SelectObject(mem, font);
                    SetTextColor(mem, 0x000000);
                    SetBkMode(mem, 1 /* TRANSPARENT */);
                    RECT tr = new RECT { left = 0, top = 3, right = size, bottom = size };
                    DrawTextW(mem, glyph, glyph.Length, ref tr, 0x11 /* DT_CENTER|DT_VCENTER */);
                    SelectObject(mem, oldFont);
                    DeleteObject(font);
                    SelectObject(mem, old);
                    DeleteDC(mem);
                }

                ReleaseDC(IntPtr.Zero, hdc);
            }

            return bmp;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct THUMBBUTTON
        {
            public uint dwMask;
            public uint iBitmap;
            public uint idCommand;
            public uint dwFlags;      // THBF_*（fsState）
            public byte fsStyle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string pszTip;
            public IntPtr hIcon;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight,
            uint italic, uint underline, uint strike, uint charSet, uint outPrec, uint clipPrec,
            uint quality, uint pitchAndFamily, string face);
        [DllImport("user32.dll")]
        private static extern bool SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("user32.dll")]
        private static extern bool FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
        [DllImport("user32.dll")]
        private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        // ITaskbarList3 vtable：HrInit(3) 起 13 个方法（含 SetProgressValue/State 与 ThumbBar*）。
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            [PreserveSig] void HrInit();
            [PreserveSig] void AddTab(IntPtr hwnd);
            [PreserveSig] void DeleteTab(IntPtr hwnd);
            [PreserveSig] void ActivateTab(IntPtr hwnd);
            [PreserveSig] void SetActiveAlt(IntPtr hwnd);
            [PreserveSig] void MarkFullscreenWindow(IntPtr hwnd, bool fullscreen);
            [PreserveSig] void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            [PreserveSig] void SetProgressState(IntPtr hwnd, uint state);
            [PreserveSig] void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            [PreserveSig] void UnregisterTab(IntPtr hwndTab);
            [PreserveSig] void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            [PreserveSig] void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint reserve);
            [PreserveSig] void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
            [PreserveSig] void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
            [PreserveSig] void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            [PreserveSig] void SetOverlayIcon(IntPtr hwnd, IntPtr hicon, string description);
            [PreserveSig] void SetThumbnailTooltip(IntPtr hwnd, string description);
            [PreserveSig] void SetThumbnailClip(IntPtr hwnd, ref RECT rect);
        }

        [ComImport]
        [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarList
        {
        }
    }
}
