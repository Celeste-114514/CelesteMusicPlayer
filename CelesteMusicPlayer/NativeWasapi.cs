using System;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 原生 WASAPI 独占输出的 P/Invoke 基础设施（复刻 ECHO NEXT 的 wasapi_exclusive 思路）。
    /// 直接驱动 IAudioClient/IAudioRenderClient，源 PCM 字节按设备协商格式直写，不做 NAudio 的 sample 转换层。
    /// </summary>
    internal static partial class NativeWasapi
    {
        // ---- SubFormat GUID ----
        // KSDATAFORMAT_SUBTYPE_PCM  {00000001-0000-0010-8000-00AA00389B71}
        public static readonly Guid SubTypePcm = new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT {00000003-0000-0010-8000-00AA00389B71}
        public static readonly Guid SubTypeIeeeFloat = new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

        // ---- 共享/流向常量 ----
        public const int EDataFlowRender = 0;
        public const int ERoleMultimedia = 1;
        public const int CLSCTX_ALL = 0x17;
        public const uint AUDCLNT_SHAREMODE_EXCLUSIVE = 1;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        public const int DEVICE_STATE_ACTIVE = 0x1;
        public const int S_OK = 0;
        public const int E_PENDING = unchecked((int)0x8000000A);
        public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
        public const int AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = unchecked((int)0x88890019);
        public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
        public const int CO_E_RUNNING = unchecked((int)0x8004012F);

        // ---- 句柄错误码由 Marshal.GetLastWin32Error 或直接 HRESULT 表达 ----

        // WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE
        public const ushort WAVE_FORMAT_PCM = 1;
        public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
        public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct WAVEFORMATEXTRA
        {
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        public struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;

            public static WAVEFORMATEXTENSIBLE Make(int sampleRate, int channels, int bitsPerSample, Guid subFormat, uint channelMask)
            {
                var f = new WAVEFORMATEXTENSIBLE();
                f.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
                f.Format.nChannels = (ushort)channels;
                f.Format.nSamplesPerSec = (uint)sampleRate;
                f.Format.wBitsPerSample = (ushort)bitsPerSample;
                f.wValidBitsPerSample = (ushort)bitsPerSample;
                f.Format.nBlockAlign = (ushort)((channels * bitsPerSample) / 8);
                f.Format.nAvgBytesPerSec = (uint)(sampleRate * f.Format.nBlockAlign);
                f.Format.cbSize = (ushort)(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>() - Marshal.SizeOf<WAVEFORMATEX>());
                f.dwChannelMask = channelMask;
                f.SubFormat = subFormat;
                return f;
            }
        }

        // ---- COM 接口 ----

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(object callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(object callback);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint cDevices);
            [PreserveSig] int Item(uint nDevice, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr ppv);
            [PreserveSig] int OpenPropertyStore(int accessMode, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            // 参数：ShareMode, StreamFlags, hnsBufferDuration, hnsPeriodicity, WaveFormat, AudioSessionGuid
            [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WAVEFORMATEXTENSIBLE waveFormat, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
            [PreserveSig] int GetStreamLatency(out long phnsLatency);
            [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
            [PreserveSig] int IsFormatSupported(uint shareMode, ref WAVEFORMATEXTENSIBLE pFormat, out WAVEFORMATEXTENSIBLE closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
            [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, out IntPtr ppv);
        }

        [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioRenderClient
        {
            [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr dataBufferPointer);
            [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
            [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        }

        // IAudioEventClient 只是标记接口（无方法），用于绑定事件回调模式。
        [ComImport, Guid("6820A932-479C-4334-9F16-D9DD1734A1AB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEventClient
        {
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        /// <summary>诊断：最近一次设备枚举的 HRESULT/状态（供排障日志）。</summary>
        public static string? LastEnumDiag;

        /// <summary>创建并获取设备枚举器（IMMDeviceEnumerator）。</summary>
        public static IMMDeviceEnumerator? CreateEnumerator()
        {
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
            Guid clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E"); // CLSID_MMDeviceEnumerator
            Guid iid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");   // IID_IMMDeviceEnumerator（此前误用 clsid 当 riid → E_NOINTERFACE）
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr pUnk);
            if (hr != S_OK)
            {
                LastEnumDiag = "CoCreateInstance(MMDeviceEnumerator) hr=0x" + hr.ToString("X8");
                return null;
            }

            // 用 GetObjectForIUnknown 让 .NET 查询目标接口（与 NAudio 相同），规避 out object RCW 的类型推断问题。
            try
            {
                if (pUnk == IntPtr.Zero)
                {
                    LastEnumDiag = "CoCreateInstance 返回空指针";
                    return null;
                }

                object obj = Marshal.GetObjectForIUnknown(pUnk);
                if (obj is IMMDeviceEnumerator en)
                {
                    LastEnumDiag = "enumerator ok";
                    return en;
                }

                LastEnumDiag = "CoCreateInstance 对象类型不符: " + (obj?.GetType().FullName ?? "null");
                return null;
            }
            finally
            {
                if (pUnk != IntPtr.Zero)
                {
                    Marshal.Release(pUnk);
                }
            }
        }

        /// <summary>激活指定设备的 AudioClient。</summary>
        public static IAudioClient? ActivateAudioClient(IMMDevice device)
        {
            Guid iidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            int hr = device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out IntPtr p);
            if (hr != S_OK || p == IntPtr.Zero) return null;
            try
            {
                object o = Marshal.GetObjectForIUnknown(p);
                return o as IAudioClient;
            }
            finally
            {
                Marshal.Release(p);
            }
        }

        /// <summary>系统默认渲染设备（Multimedia）。</summary>
        public static IMMDevice? GetDefaultRenderDevice()
        {
            var enumerator = CreateEnumerator();
            if (enumerator == null) return null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out IMMDevice dev) == S_OK)
                {
                    return dev;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            return null;
        }

        /// <summary>按设备 ID 解析渲染设备（找不到回退系统默认）。</summary>
        public static IMMDevice? GetRenderDeviceById(string? id)
        {
            var enumerator = CreateEnumerator();
            if (enumerator == null) return null;
            try
            {
                IMMDevice dev;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    int hrDev = enumerator.GetDevice(id, out dev);
                    if (hrDev == S_OK) { LastEnumDiag = "GetDevice(" + id + ") ok"; return dev; }
                    LastEnumDiag = "GetDevice id failed hr=0x" + hrDev.ToString("X8") + ", fallback default";
                }

                int hrDef = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out dev);
                if (hrDef == S_OK) { LastEnumDiag = "GetDefaultAudioEndpoint ok"; return dev; }
                LastEnumDiag = "GetDefaultAudioEndpoint failed hr=0x" + hrDef.ToString("X8");

                int hrEnum = enumerator.EnumAudioEndpoints(EDataFlowRender, DEVICE_STATE_ACTIVE, out var coll);
                if (hrEnum == S_OK)
                {
                    uint count = 0;
                    int hrCnt = coll.GetCount(out count);
                    LastEnumDiag = "Enum endpoints ok count=" + count + " GetCount hr=0x" + hrCnt.ToString("X8");
                    if (hrCnt == S_OK && count > 0 && coll.Item(0, out dev) == S_OK)
                    {
                        return dev;
                    }
                }
                else
                {
                    LastEnumDiag = "EnumAudioEndpoints failed hr=0x" + hrEnum.ToString("X8");
                }
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        /// <summary>空字节块对齐边界辅助。</summary>
        public static int BlockAlign(int channels, int bitsPerSample) => (channels * bitsPerSample) / 8;
    }
}
