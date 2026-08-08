using System;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>任务栏进度显示（ITaskbarList3）。</summary>
    public sealed class TaskbarProgressHelper : IDisposable
    {
        private const uint TbpNoProgress = 0x00000000;
        private const uint TbpNormal = 0x00000001;
        private const uint TbpPaused = 0x00000008;

        private readonly IntPtr _hwnd;
        private readonly ITaskbarList3? _list;

        public TaskbarProgressHelper(IntPtr hwnd)
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
        }

        public void SetProgress(double completed, double total, bool paused)
        {
            if (_list == null)
            {
                return;
            }

            try
            {
                if (total <= 0 || completed <= 0)
                {
                    _list.SetProgressState(_hwnd, TbpNoProgress);
                    return;
                }

                _list.SetProgressState(_hwnd, paused ? TbpPaused : TbpNormal);
                _list.SetProgressValue(_hwnd, (ulong)Math.Max(0, completed), (ulong)Math.Max(1, total));
            }
            catch
            {
            }
        }

        public void Clear()
        {
            if (_list == null)
            {
                return;
            }

            try
            {
                _list.SetProgressState(_hwnd, TbpNoProgress);
            }
            catch
            {
            }
        }

        public void Dispose() => Clear();

        // ITaskbarList3 的 vtable 前 8 个方法（HrInit 起），按 COM 顺序声明。
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, uint state);
        }

        [ComImport]
        [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarList
        {
        }
    }
}
