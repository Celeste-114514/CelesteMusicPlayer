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
    /// - **结构体 marshal**：ThumbBarAddButtons / ThumbBarUpdateButtons 用
    ///   [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] 数组参数。
    ///   之前 [In] ref THUMBBUTTON pButton 在 64-bit COM interop 下只 marshal 单元素
    ///   （= sizeof(THUMBBUTTON)），explorer 实际只看到 1 个按钮的 iId/hIcon，
    ///   剩 3 个按钮位置 iId=0/hIcon=NULL → 不渲染、不响应点击。
    ///   LPArray 让 marshaler 按 cButtons * sizeof(THUMBBUTTON) 把整个数组写到 unmanaged，
    ///   explorer 才能读到全部 4 个按钮的 iId/hIcon。
    /// - **dwMask 只设 ThbIcon | ThbTooltip | ThbFlags**：
    ///   之前双设 ThbBitmap | ThbIcon 会让 explorer 走 iBitmap=0 的 bitmap fallback，
    ///   导致 4 个按钮位置全部显示默认/不正确的图标（prev 是唯一"有图标"的位置，
    ///   但显示的是 explorer fallback 而非我们画的 HICON）。
    /// - **心形用两个相切椭圆 + 三角**：之前的椭圆在 x=8 处重叠 4px，
    ///   FillPath 默认 FillMode.Alternate 把重叠区（奇偶数判定为偶数）挖洞，
    ///   导致显示出来是"U 形"不是完整心形。新版两个椭圆恰好在 x=8 相切（无重叠），
    ///   再加三角填充心尖，干净饱满。
    /// - **空心 / 实心心形两套 HICON**：未收藏（默认）= 空心轮廓心，
    ///   已收藏 = 实心红心。ToggleFavorite 后调 UpdateFavorite 切换。
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
        // 收藏按钮：两套独立 HICON，空心 = 未收藏，实心 = 已收藏。
        private IntPtr _hHeartEmpty = IntPtr.Zero;
        private IntPtr _hHeartFilled = IntPtr.Zero;

        private bool _added;
        private bool _disposed;
        private bool _isPlaying;
        private bool _isFavorite;

        // 延迟注册：任务栏图标 Loaded 后还没完全 ready 时直接 AddButtons 会被 Explorer
        // 默默吞掉（hr=0 但按钮不显示）。分多轮重试解决：1500 / 3500 / 6500 / 11500ms。
        // 首次 hr=0 后立刻强制再调用一次，让 Explorer 真的把按钮渲染上去。
        private bool _delegatesReady;
        private DateTime _nextRetryAt = DateTime.MinValue;
        private int _retryAttempt;
        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromMilliseconds(0),
            TimeSpan.FromMilliseconds(3500),
            TimeSpan.FromMilliseconds(7000),
            TimeSpan.FromMilliseconds(11000),
        };
        private bool _confirmedVisible;
        private DateTime _confirmAt = DateTime.MinValue;

        // 阶段 4：AddButtons 成功后 explorer 内部仍可能没把 hIcon 渲染上去（DWM 合成异步），
        // 每 1.5s 主动 UpdateButtons 全部 4 个按钮一次，最多 4 轮（≈6s），强制 explorer
        // 从我们的进程重新取 4 个 HICON 句柄并落到 DWM thumbar 表面。
        private int _forceUpdateRound;
        private DateTime _nextForceUpdateAt = DateTime.MinValue;
        private static readonly TimeSpan ForceUpdateInterval = TimeSpan.FromMilliseconds(1500);

        public TaskbarThumbnailButtons(MainWindow owner, IntPtr hwnd)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _hwnd = hwnd;
            _taskbar = CreateTaskbarList();
        }

        /// <summary>
        /// 由外部驱动（polling 时钟/timer）调用的"推进器"。
        /// 第一次调用时只完成 SetWindowSubclass + 渲染 HICON + 第一次 AddButtons。
        /// 后续定时推进重试，直到 hr=0 之后再额外确认一次。
        /// </summary>
        public void Pump()
        {
            if (_disposed || _taskbar == null || _hwnd == IntPtr.Zero) return;

            // 阶段 1：准备 delegates/HICON
            if (!_delegatesReady)
            {
                try
                {
                    _subclassDelegate = SubclassWndProc;
                    bool subclassed = SetWindowSubclass(_hwnd, _subclassDelegate, _subclassId, IntPtr.Zero);
                    StartupLog.Write("[thumb] SetWindowSubclass ok=" + subclassed + " err=" + Marshal.GetLastWin32Error());

                    _hPrev = RenderGlyphHicon(PaintGlyphPrev);
                    _hPlay = RenderGlyphHicon(PaintGlyphPlay);
                    _hPause = RenderGlyphHicon(PaintGlyphPause);
                    _hNext = RenderGlyphHicon(PaintGlyphNext);
                    _hHeartFilled = RenderGlyphHicon(PaintGlyphHeartFilled);
                    _hHeartEmpty = RenderGlyphHicon(PaintGlyphHeartOutline);
                    StartupLog.Write("[thumb] HICON 渲染完成: prev=0x" + _hPrev.ToString("X")
                        + " play=0x" + _hPlay.ToString("X")
                        + " pause=0x" + _hPause.ToString("X")
                        + " next=0x" + _hNext.ToString("X")
                        + " heartFilled=0x" + _hHeartFilled.ToString("X")
                        + " heartEmpty=0x" + _hHeartEmpty.ToString("X"));

                    if (_hPrev != IntPtr.Zero && _hPlay != IntPtr.Zero && _hPause != IntPtr.Zero
                        && _hNext != IntPtr.Zero && _hHeartFilled != IntPtr.Zero && _hHeartEmpty != IntPtr.Zero)
                    {
                        _delegatesReady = true;
                        _retryAttempt = 0;
                        _nextRetryAt = DateTime.UtcNow + RetryDelays[0];
                    }
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("TaskbarThumbnailButtons.Pump.prepare", caught);
                }
            }

            // 阶段 2：在指定时间点尝试 AddButtons
            if (_delegatesReady && !_added && DateTime.UtcNow >= _nextRetryAt)
            {
                TryAddButtonsOnce();
            }

            // 阶段 3：首次 hr=0 后再补一次"确认" Add，让 explorer 真正渲染
            if (_added && !_confirmedVisible && DateTime.UtcNow >= _confirmAt)
            {
                StartupLog.Write("[thumb] 确认: 重新调一次 ThumbBarAddButtons 让 explorer 真正渲染");
                TryAddButtonsOnce();
                _confirmedVisible = true;
                _nextForceUpdateAt = DateTime.UtcNow + ForceUpdateInterval;
            }

            // 阶段 4：每 1.5s 主动 UpdateButtons 全部 4 个按钮，强制 explorer 从我们的进程
            // 重新读取 4 个 HICON 并渲染到 DWM thumbar 表面。即使 AddButtons hr=0 + 阶段3
            // 确认调用，某些 Win11 24H2+ 的 explorer 在 DWM 合成时仍把 thumbar HICON 异步
            // 渲染管道漏掉；UpdateButtons 是 explorer 已知的"立即重画"入口，4 轮后停手。
            if (_confirmedVisible && _forceUpdateRound < 4 && DateTime.UtcNow >= _nextForceUpdateAt)
            {
                TryForceUpdateAllButtons();
            }
        }

        private void TryForceUpdateAllButtons()
        {
            try
            {
                IntPtr heart = _isFavorite ? _hHeartFilled : _hHeartEmpty;
                string heartTip = _isFavorite ? "取消喜欢" : "添加到喜欢";
                var arr = new[]
                {
                    MakeIconButton(BtnPrev, _hPrev, "上一首"),
                    MakeIconButton(BtnPlayPause, _isPlaying ? _hPause : _hPlay, _isPlaying ? "暂停" : "播放"),
                    MakeIconButton(BtnNext, _hNext, "下一首"),
                    MakeIconButton(BtnFavorite, heart, heartTip)
                };
                int hr = _taskbar!.ThumbBarUpdateButtons(_hwnd, (uint)arr.Length, arr);
                _forceUpdateRound++;
                StartupLog.Write("[thumb] ForceUpdateAll 第 " + _forceUpdateRound + " 次 hr=0x" + hr.ToString("X8")
                    + " (id 1001-1004，强制 explorer 重读 hIcon)");
                _nextForceUpdateAt = DateTime.UtcNow + ForceUpdateInterval;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.TryForceUpdateAllButtons", caught);
            }
        }

        private void TryAddButtonsOnce()
        {
            try
            {
                IntPtr heart = _isFavorite ? _hHeartFilled : _hHeartEmpty;
                string heartTip = _isFavorite ? "取消喜欢" : "添加到喜欢";
                var prevBtn = MakeIconButton(BtnPrev, _hPrev, "上一首");
                var playBtn = MakeIconButton(BtnPlayPause, _hPlay, "播放");
                var nextBtn = MakeIconButton(BtnNext, _hNext, "下一首");
                var favBtn = MakeIconButton(BtnFavorite, heart, heartTip);
                var arr = new[] { prevBtn, playBtn, nextBtn, favBtn };

                // **关键**：用 THUMBBUTTON[] + [MarshalAs(UnmanagedType.LPArray)] 传整个数组。
                // 之前 [In] ref THUMBBUTTON pButton 在 64-bit COM interop 下只 marshal 1 个元素
                // （= sizeof(THUMBBUTTON)），explorer 实际只看到 1 个按钮，其它 3 个位置
                // iId=0/hIcon=NULL，所以剩 3 个位置 explorer 干脆不响应点击。
                int hr = _taskbar!.ThumbBarAddButtons(_hwnd, (uint)arr.Length, arr);
                StartupLog.Write("[thumb] AddButtons 第 " + (_retryAttempt + 1) + " 次 hr=0x" + hr.ToString("X8") + " (id 1001-1004)");

                if (hr == 0)
                {
                    _added = true;
                    if (_confirmAt == DateTime.MinValue)
                    {
                        _confirmAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
                    }
                }
                else
                {
                    _retryAttempt++;
                    if (_retryAttempt < RetryDelays.Length)
                    {
                        _nextRetryAt = DateTime.UtcNow + RetryDelays[_retryAttempt];
                        StartupLog.Write("[thumb] 计划重试于 " + (RetryDelays[_retryAttempt].TotalMilliseconds) + "ms 后");
                    }
                    else
                    {
                        StartupLog.Write("[thumb] 重试已耗尽，最后 hr=0x" + hr.ToString("X8"));
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.TryAddButtonsOnce", caught);
            }
        }

        /// <summary>兼容旧 API。无操作——Pump() 才能真正触发注册。</summary>
        public void Add()
        {
            StartupLog.Write("[thumb] Add() 被调用（已被 Pump 模式取代，立即 Pump 一次）");
            Pump();
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
                var arr = new[] { btn };
                int hr = _taskbar.ThumbBarUpdateButtons(_hwnd, 1, arr);
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

        /// <summary>
        /// 切换收藏按钮图标：isFavorite=true → 实心红心（点击取消收藏），
        /// isFavorite=false → 空心轮廓心（点击加入收藏）。未 AddButtons 时只缓存状态，
        /// AddButtons 成功后会在 TryAddButtonsOnce 自动应用当前 _isFavorite。
        /// </summary>
        public void UpdateFavorite(bool isFavorite)
        {
            _isFavorite = isFavorite;
            if (!_added || _disposed || _taskbar == null)
            {
                return;
            }

            try
            {
                IntPtr heart = isFavorite ? _hHeartFilled : _hHeartEmpty;
                string tip = isFavorite ? "取消喜欢" : "添加到喜欢";
                var btn = MakeIconButton(BtnFavorite, heart, tip);
                var arr = new[] { btn };
                int hr = _taskbar.ThumbBarUpdateButtons(_hwnd, 1, arr);
                StartupLog.Write("[thumb] UpdateFavorite fav=" + isFavorite + " hr=0x" + hr.ToString("X8"));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TaskbarThumbnailButtons.UpdateFavorite", caught);
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
            DestroyIconSafely(ref _hHeartFilled);
            DestroyIconSafely(ref _hHeartEmpty);
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

        private static void PaintGlyphHeartFilled(Graphics g)
        {
            using var path = HeartPath();
            // FillPath 默认 FillMode.Alternate：两个相切椭圆（x=8 处相切、不重叠），
            // 加上三角形（顶点在 x=8），整体是 3 个不相交的闭合区域，全部填实。
            g.FillPath(Red, path);
        }

        private static void PaintGlyphHeartOutline(Graphics g)
        {
            using var path = HeartPath();
            // 1.2px 描边：16x16 画布上 1.2 是抗锯齿后看起来像 ~1.5 像素，醒目但不糊。
            // 用 SmoothingMode.AntiAlias 让圆角自然过渡到直线段。
            using var pen = new Pen(Color.FromArgb(232, 17, 35), 1.2f);
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, path);
        }

        /// <summary>
        /// 16x16 经典心形路径：左叶椭圆 (1,3,7,6) + 右叶椭圆 (8,3,7,6)，
        /// 两个椭圆在 x=8 处**相切**（不重叠），加底部三角 (1,6)-(15,6)-(8,15) 拼心尖。
        /// 整体占 [1,15]×[3,15]，留 1px padding。
        /// </summary>
        private static GraphicsPath HeartPath()
        {
            var path = new GraphicsPath();
            path.AddEllipse(1, 3, 7, 6);   // 左叶：中心 (4.5, 6)
            path.AddEllipse(8, 3, 7, 6);   // 右叶：中心 (11.5, 6)
            path.AddPolygon(new[]          // 心尖：底边中点 (8, 15)
            {
                new Point(1, 6),
                new Point(15, 6),
                new Point(8, 15)
            });
            return path;
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
            // **dwMask 只设 ThbIcon**：之前双设 ThbBitmap | ThbIcon 时 explorer 优先走
            // iBitmap 路径（iBitmap=0 → 显示 explorer fallback 图标），导致 4 个按钮
            // 都显示默认/不正确的图标。修掉：只设 ThbIcon，explorer 必须读 hIcon。
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
        private const uint ThbBitmap = 0x00000001;
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
            // **参数类型必须用 [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[]**：
            // 之前 [In] ref THUMBBUTTON pButton 在 64-bit COM interop 下只 marshal 单元素，
            // explorer 实际只读到 1 个按钮的 iId/hIcon，剩下 3 个按钮位置 iId=0/hIcon=NULL
            // 因此不渲染、不响应点击（只剩 prev=1001 那个位置）。LPArray + 数组参数
            // 让 marshaler 按 cButtons * sizeof(THUMBBUTTON) 把整个数组的 4 个结构
            // 连续写到 unmanaged memory，explorer 端才能读到全部 4 个按钮的 iId/hIcon。
            [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [In, MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButtons);
            [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [In, MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] pButtons);
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
