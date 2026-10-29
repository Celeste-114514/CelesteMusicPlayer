// W13：用 Win32 消息泵替代 Windows App SDK 的 DispatcherQueue。
// 程序不再有 WinUI Application/Window，UI 线程靠一个隐藏消息窗口驱动：
// 后台线程 TryEnqueue → PostMessage 到该窗口 → 消息循环里取出并执行。
// 这样 async/await 回 UI 线程、定时器、跨线程更新都能照旧工作。
#pragma warning disable CS0436
#pragma warning disable CS0067
#pragma warning disable CS0169

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
    }

    /// <summary>隐藏消息窗口 + 消息队列，全进程唯一（UI 线程）。</summary>
    internal static class NativeMessagePump
    {
        private const uint WmExecute = 0x8001;      // WM_APP + 1：执行一个排队的委托
        private const uint WmExecuteHigh = 0x8002;  // 高优先级
        private static readonly WndProcDelegate s_wndProc = WndProc;
        private static readonly Queue<Action> s_high = new();
        private static readonly Queue<Action> s_normal = new();
        private static readonly Queue<Action> s_low = new();
        private static readonly object s_gate = new();
        private static nint s_hwnd;
        private static nint s_classAtom;

        private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint RegisterClassExW(ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint DispatchMessageW(ref Msg lpMsg);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint GetModuleHandleW(string? lpModuleName);

        internal static nint WindowHandle => s_hwnd;

        internal static void EnsureWindow()
        {
            if (s_hwnd != 0) return;

            WndClassEx wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                lpszClassName = "CelesteNativePump",
                hInstance = GetModuleHandleW(null),
            };
            s_classAtom = RegisterClassExW(ref wc);
            if(s_classAtom == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot register native message pump");
            // 消息专用窗口：不显示、不参与任务栏、不走 DWM 合成。
            s_hwnd = CreateWindowExW(0, "CelesteNativePump", null, 0, 0, 0, 0, 0,
                new nint(-3) /* HWND_MESSAGE */, 0, GetModuleHandleW(null), 0);
            if(s_hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot create native message pump");
        }

        internal static void Enqueue(Action action, DispatcherQueuePriority priority)
        {
            lock (s_gate)
            {
                switch (priority)
                {
                    case DispatcherQueuePriority.High: s_high.Enqueue(action); break;
                    case DispatcherQueuePriority.Low: s_low.Enqueue(action); break;
                    default: s_normal.Enqueue(action); break;
                }
            }
            PostMessageW(s_hwnd, priority == DispatcherQueuePriority.High ? WmExecuteHigh : WmExecute, 0, 0);
        }

        private static Action? Dequeue()
        {
            lock (s_gate)
            {
                if (s_high.Count > 0) return s_high.Dequeue();
                if (s_normal.Count > 0) return s_normal.Dequeue();
                if (s_low.Count > 0) return s_low.Dequeue();
            }
            return null;
        }

        private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WmExecute || msg == WmExecuteHigh)
            {
                Action? work = Dequeue();
                if (work != null)
                {
                    try { work(); }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeMessagePump", caught); }
                }
                return 0;
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        /// <summary>UI 线程消息循环；返回退出码。</summary>
        internal static int Run()
        {
            EnsureWindow();
            while (GetMessageW(out Msg msg, 0, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            return 0;
        }

        internal static void Quit(int exitCode = 0) => PostQuitMessage(exitCode);
    }

    public sealed partial class DispatcherQueue
    {
        private static DispatcherQueue? t_current;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        private DispatcherQueue() { }

        public static DispatcherQueue GetForCurrentThread()
        {
            if (t_current == null)
            {
                NativeMessagePump.EnsureWindow();
                t_current = new DispatcherQueue();
            }
            return t_current;
        }

        public DispatcherQueueTimer CreateTimer() => new();

        public static DispatcherQueue? GetForCurrentThreadOrNull() => t_current?.HasThreadAccess == true ? t_current : null;

        /// <summary>UI 线程判定：当前线程就是这个队列所属线程。</summary>
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _ownerThreadId;

        public bool TryEnqueue(Action callback)
        {
            if (callback == null) return false;
            NativeMessagePump.Enqueue(callback, DispatcherQueuePriority.Normal);
            return true;
        }

        public bool TryEnqueue(DispatcherQueuePriority priority, Action callback)
        {
            if (callback == null) return false;
            NativeMessagePump.Enqueue(callback, priority);
            return true;
        }

        public event global::Microsoft.UI.Xaml.AnyEventHandler? ShutdownStarting;
        public event global::Microsoft.UI.Xaml.AnyEventHandler? ShutdownCompleted;

        internal void RaiseShutdownStarting() => ShutdownStarting?.Invoke(this, EventArgs.Empty);
        internal void RaiseShutdownCompleted() => ShutdownCompleted?.Invoke(this, EventArgs.Empty);
    }

    public sealed partial class DispatcherQueueController
    {
        public DispatcherQueue DispatcherQueue { get; } = DispatcherQueue.GetForCurrentThread();

        public static DispatcherQueueController CreateOnCurrentThread() => new();

        public void ShutdownQueue()
        {
            DispatcherQueue.RaiseShutdownCompleted();
            NativeMessagePump.Quit();
        }
    }

    /// <summary>把 await 的延续回投到原生消息泵（替代 WinUI 的同名类型）。</summary>
    public sealed partial class DispatcherQueueSynchronizationContext : SynchronizationContext
    {
        private readonly DispatcherQueue _queue;

        public DispatcherQueueSynchronizationContext(DispatcherQueue queue)
        {
            _queue = queue;
        }

        public DispatcherQueueSynchronizationContext() : this(DispatcherQueue.GetForCurrentThread()) { }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (d == null) return;
            _queue.TryEnqueue(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (d == null) return;
            if (_queue.HasThreadAccess) d(state);
            else
            {
                using ManualResetEventSlim done = new(false);
                _queue.TryEnqueue(() =>
                {
                    try { d(state); }
                    finally { done.Set(); }
                });
                done.Wait();
            }
        }

        public override SynchronizationContext CreateCopy() => new DispatcherQueueSynchronizationContext(_queue);
    }

    public sealed partial class DispatcherQueueTimer
    {
        private Timer? _timer;
        private int _generation, _pending;
        private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
        public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(100);
        public bool IsRunning => _timer != null;
        public bool IsRepeating { get; set; } = true;
        public event global::Microsoft.UI.Xaml.AnyEventHandler? Tick;
        public void Start() {
            Stop(); int generation = _generation;
            _timer = new Timer(_ => {
                if (generation != Volatile.Read(ref _generation) || Interlocked.Exchange(ref _pending,1)!=0) return;
                _queue.TryEnqueue(() => {
                    Interlocked.Exchange(ref _pending,0);
                    if(generation != _generation) return;
                    if(!IsRepeating) Stop();
                    Tick?.Invoke(this,EventArgs.Empty);
                });
            },null,Interval,IsRepeating ? Interval : Timeout.InfiniteTimeSpan);
        }
        public void Stop() {
            Interlocked.Increment(ref _generation); Interlocked.Exchange(ref _pending,0);
            _timer?.Dispose(); _timer=null;
        }
    }
}
