using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    public enum HotkeyAction
    {
        PlayPause,
        Stop,
        Next,
        Previous,
        VolumeUp,
        VolumeDown,
        SeekForward,
        SeekBack,
        ToggleDesktopLyrics,
        ToggleFavorite,
        ShowHideMain
    }

    /// <summary>全局热键：内部创建仅消息 HWND 接收 WM_HOTKEY，支持自定义绑定。</summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const uint WmHotkey = 0x0312;
        private const int GwlpWndproc = -4;
        private const uint WsExNoActivate = 0x08000000;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint VkOemPlus = 0xBB;
        private const uint VkOemMinus = 0xBD;

        private static readonly IntPtr HwndMessage = new(-3);

        /// <summary>默认绑定表（可被设置中的 CustomHotkeys 覆盖）。</summary>
        public static readonly IReadOnlyDictionary<HotkeyAction, string> DefaultBindings =
            new Dictionary<HotkeyAction, string>
            {
                [HotkeyAction.PlayPause] = "Ctrl+Alt+P",
                [HotkeyAction.Stop] = "Ctrl+Alt+S",
                [HotkeyAction.Next] = "Ctrl+Alt+N",
                [HotkeyAction.Previous] = "Ctrl+Alt+B",
                [HotkeyAction.VolumeUp] = "Ctrl+Alt+=",
                [HotkeyAction.VolumeDown] = "Ctrl+Alt+-",
                [HotkeyAction.SeekForward] = "Ctrl+Alt+Right",
                [HotkeyAction.SeekBack] = "Ctrl+Alt+Left",
                [HotkeyAction.ToggleDesktopLyrics] = "Ctrl+Alt+L",
                [HotkeyAction.ToggleFavorite] = "Ctrl+Alt+F",
                [HotkeyAction.ShowHideMain] = "Ctrl+Alt+M"
            };

        private readonly Dictionary<int, HotkeyAction> _idToAction = new();
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private WndProcDelegate? _wndProcDelegate;
        private bool _started;
        private int _nextId = 1;
        private bool _disposed;

        public event Action? PlayPause;
        public event Action? Stop;
        public event Action? Next;
        public event Action? Previous;
        public event Action? VolumeUp;
        public event Action? VolumeDown;
        public event Action? SeekForward;
        public event Action? SeekBack;
        public event Action? ToggleDesktopLyrics;
        public event Action? ToggleFavorite;
        public event Action? ShowHideMain;

        public void Start()
        {
            if (_started || _disposed)
            {
                return;
            }

            _wndProcDelegate = WndProc;
            _hwnd = CreateWindowEx(
                WsExNoActivate,
                "STATIC",
                "CelesteMusicPlayerHotkeys",
                0,
                0, 0, 0, 0,
                HwndMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create hotkey message window.");
            }

            _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndproc,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

            _started = true;
            ApplyBindings(null);
        }

        /// <summary>按设置应用热键绑定；custom 为空时使用默认表。</summary>
        public void ApplyBindings(IReadOnlyDictionary<string, string>? custom)
        {
            if (!_started)
            {
                return;
            }

            foreach (int id in _idToAction.Keys.ToArray())
            {
                UnregisterHotKey(_hwnd, id);
            }

            _idToAction.Clear();

            foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
            {
                string spec = DefaultBindings[action];
                if (custom != null
                    && custom.TryGetValue(action.ToString(), out string? s)
                    && !string.IsNullOrWhiteSpace(s))
                {
                    spec = s;
                }

                if (!TryParseHotkey(spec, out uint modifiers, out uint virtualKey))
                {
                    continue;
                }

                RegisterOne(action, modifiers, virtualKey);

                // +/-（OEM_PLUS / OEM_MINUS）物理键同时注册 Shift 变体：
                // 兼容「直接按 = / - 键」与「按 Shift+= 输入 +」两种习惯。
                if ((virtualKey == VkOemPlus || virtualKey == VkOemMinus) && (modifiers & ModShift) == 0)
                {
                    RegisterOne(action, modifiers | ModShift, virtualKey);
                }
            }
        }

        private void RegisterOne(HotkeyAction action, uint modifiers, uint virtualKey)
        {
            int id = _nextId++;
            if (RegisterHotKey(_hwnd, id, modifiers, virtualKey))
            {
                _idToAction[id] = action;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GlobalHotkey] 注册失败: {action}");
            }
        }

        /// <summary>解析 "Ctrl+Alt+P" 形式的字符串；'+'/'-' 键按物理键处理。</summary>
        public static bool TryParseHotkey(string text, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // 兼容 "Ctrl+Alt++"：末尾 "++" 表示 "=" 键(用户常把 = 写成 +)
            if (text.EndsWith("++", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 1) + "=";
            }

            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= ModControl;
                        break;
                    case "alt":
                        modifiers |= ModAlt;
                        break;
                    case "shift":
                        modifiers |= ModShift;
                        break;
                    case "win":
                    case "windows":
                    case "cmd":
                    case "meta":
                        modifiers |= ModWin;
                        break;
                    default:
                        return false;
                }
            }

            string key = parts[^1];
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    virtualKey = c;
                    return true;
                }

                switch (key)
                {
                    // 注意：按 '+' 分割后 key 不会是 '+'，此分支仅为防御保留
                    case "+":
                        virtualKey = VkOemPlus;
                        modifiers |= ModShift;
                        return true;
                    case "-":
                        virtualKey = VkOemMinus;
                        return true;
                    case "=":
                        virtualKey = VkOemPlus;
                        return true;
                    default:
                        return false;
                }
            }

            switch (key.ToLowerInvariant())
            {
                case "space": virtualKey = 0x20; return true;
                case "enter":
                case "return": virtualKey = 0x0D; return true;
                case "tab": virtualKey = 0x09; return true;
                case "escape":
                case "esc": virtualKey = 0x1B; return true;
                case "home": virtualKey = 0x24; return true;
                case "end": virtualKey = 0x23; return true;
                case "pageup": virtualKey = 0x21; return true;
                case "pagedown": virtualKey = 0x22; return true;
                case "insert": virtualKey = 0x2D; return true;
                case "delete": virtualKey = 0x2E; return true;
                case "left": virtualKey = 0x25; return true;
                case "up": virtualKey = 0x26; return true;
                case "right": virtualKey = 0x27; return true;
                case "down": virtualKey = 0x28; return true;
                case "oemplus": virtualKey = VkOemPlus; return true;
                case "oemminus": virtualKey = VkOemMinus; return true;
                case "f1": virtualKey = 0x70; return true;
                case "f2": virtualKey = 0x71; return true;
                case "f3": virtualKey = 0x72; return true;
                case "f4": virtualKey = 0x73; return true;
                case "f5": virtualKey = 0x74; return true;
                case "f6": virtualKey = 0x75; return true;
                case "f7": virtualKey = 0x76; return true;
                case "f8": virtualKey = 0x77; return true;
                case "f9": virtualKey = 0x78; return true;
                case "f10": virtualKey = 0x79; return true;
                case "f11": virtualKey = 0x7A; return true;
                case "f12": virtualKey = 0x7B; return true;
                default: return false;
            }
        }

        public void StopListening()
        {
            if (!_started)
            {
                return;
            }

            foreach (int id in _idToAction.Keys.ToArray())
            {
                UnregisterHotKey(_hwnd, id);
            }

            _idToAction.Clear();

            if (_hwnd != IntPtr.Zero)
            {
                if (_oldWndProc != IntPtr.Zero)
                {
                    SetWindowLongPtr(_hwnd, GwlpWndproc, _oldWndProc);
                    _oldWndProc = IntPtr.Zero;
                }

                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            _started = false;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmHotkey)
            {
                int id = wParam.ToInt32();
                if (_idToAction.TryGetValue(id, out HotkeyAction action))
                {
                    RaiseAction(action);
                }

                return IntPtr.Zero;
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void RaiseAction(HotkeyAction action)
        {
            try
            {
                switch (action)
                {
                    case HotkeyAction.PlayPause:
                        PlayPause?.Invoke();
                        break;
                    case HotkeyAction.Stop:
                        Stop?.Invoke();
                        break;
                    case HotkeyAction.Next:
                        Next?.Invoke();
                        break;
                    case HotkeyAction.Previous:
                        Previous?.Invoke();
                        break;
                    case HotkeyAction.VolumeUp:
                        VolumeUp?.Invoke();
                        break;
                    case HotkeyAction.VolumeDown:
                        VolumeDown?.Invoke();
                        break;
                    case HotkeyAction.SeekForward:
                        SeekForward?.Invoke();
                        break;
                    case HotkeyAction.SeekBack:
                        SeekBack?.Invoke();
                        break;
                    case HotkeyAction.ToggleDesktopLyrics:
                        ToggleDesktopLyrics?.Invoke();
                        break;
                    case HotkeyAction.ToggleFavorite:
                        ToggleFavorite?.Invoke();
                        break;
                    case HotkeyAction.ShowHideMain:
                        ShowHideMain?.Invoke();
                        break;
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            StopListening();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
