using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave.Asio;
using CelesteMusicPlayer;

namespace DsdAsioProbe
{
    /// <summary>
    /// P0 只读体检：问清楚这台机器上的 ASIO 驱动到底能不能进原生 DSD、支持到哪一档。
    /// 全程不 CreateBuffers、不 Start，不会出声；结束前把 IO 格式和采样率恢复原样。
    /// 依据：ASIO SDK 2.3 DSD 扩展（kAsioSet/Get/CanDoIoFormat）+ NAudio 2.2.1 AsioDriver.Future。
    /// </summary>
    internal static class Program
    {
        private const int KAsioSetIoFormat = 0x23111961;
        private const int KAsioGetIoFormat = 0x23111983;
        private const int KAsioCanDoIoFormat = 0x23112004;

        private const int FmtInvalid = -1;
        private const int FmtPcm = 0;
        private const int FmtDsd = 1;

        private static readonly int AsioSuccess = unchecked((int)0x3f4847a0);

        private static readonly (double Hz, string Name)[] DsdRates =
        {
            (2822400, "DSD64"),
            (5644800, "DSD128"),
            (11289600, "DSD256"),
            (22579200, "DSD512"),
        };

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            if (args.Length > 0 && args[0] == "--play")
            {
                int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? Math.Clamp(s, 1, 30) : 3;
                return PlayDsdSilence(seconds);
            }

            Console.WriteLine("=== ASIO 原生 DSD 能力探测（只读，不发声）===");
            Console.WriteLine("注意：会短暂切换驱动的 IO 格式与采样率，结束前恢复；建议先暂停播放。");
            Console.WriteLine();

            string[] names;
            try
            {
                names = AsioDriver.GetAsioDriverNames();
            }
            catch (Exception ex)
            {
                Console.WriteLine("枚举 ASIO 驱动失败：" + ex);
                return 2;
            }

            if (names.Length == 0)
            {
                Console.WriteLine("本机没有注册任何 ASIO 驱动。请确认飞傲官方 USB 驱动已安装、KA13 已插好。");
                return 2;
            }

            Console.WriteLine("已注册的 ASIO 驱动：");
            for (int i = 0; i < names.Length; i++)
            {
                Console.WriteLine($"  [{i}] {names[i]}");
            }

            string target = args.Length > 0
                ? args[0]
                : names.FirstOrDefault(n => n.IndexOf("FiiO", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[0];
            Console.WriteLine();
            Console.WriteLine("目标驱动：" + target);

            AsioDriver? drv = null;
            AsioDriverExt? ext = null;
            double originalRate = 0;
            int outCount = 0;

            try
            {
                try
                {
                    drv = AsioDriver.GetAsioDriverByName(target);
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("打开驱动失败：" + ex.Message);
                    Console.WriteLine("常见原因：KA13 没插、驱动没装、或设备正被别的程序独占。");
                    return 3;
                }

                ext = new AsioDriverExt(drv);
                var cap = ext.Capabilities;
                outCount = cap.OutputChannelInfos.Length;
                originalRate = cap.SampleRate;

                Console.WriteLine($"驱动名：{cap.DriverName}");
                Console.WriteLine($"输入/输出通道数：{cap.InputChannelInfos.Length} / {outCount}");
                Console.WriteLine($"当前采样率：{originalRate}");
                Console.WriteLine($"缓冲尺寸：min={cap.BufferMinSize} pref={cap.BufferPreferredSize} max={cap.BufferMaxSize} granularity={cap.BufferGranularity}");
                Console.WriteLine();

                DumpChannels(drv, outCount, "① 初始状态");

                Console.WriteLine();
                Console.WriteLine("--- ASIO 2.3 DSD 扩展（IoFormat）---");

                var (rcGet, fmtGet) = Io(drv, KAsioGetIoFormat, FmtPcm);
                Console.WriteLine($"  kAsioGetIoFormat   返回 {DescribeRc(rcGet)}；当前 FormatType={fmtGet}（{FmtName(fmtGet)}）");

                var (rcCan, fmtCan) = Io(drv, KAsioCanDoIoFormat, FmtDsd);
                // NAudio 2.2.1 的 AsioDriver.Future 是 void，把 ASIOError 返回值吞了。
                // ASIO SDK 规定：驱动不支持某请求时，会把 ASIOIoFormat.FormatType 改成 kASIOFormatInvalid(-1)；
                // 支持则原样保留。所以判据是「回来还是不是请求的格式」，不是返回值。
                bool canDsd = fmtCan == FmtDsd;
                Console.WriteLine($"  kAsioCanDoIoFormat 返回 {DescribeRc(rcCan)}；请求 DSD 后 FormatType={fmtCan}（{FmtName(fmtCan)}）");
                Console.WriteLine($"  ⇒ 驱动{(canDsd ? "声明支持" : "未声明支持")}原生 DSD 的 IoFormat 切换");

                var (rcSet, fmtSet) = Io(drv, KAsioSetIoFormat, FmtDsd);
                bool dsdSet = fmtSet == FmtDsd;
                Console.WriteLine($"  kAsioSetIoFormat   返回 {DescribeRc(rcSet)}；切到 DSD 后 FormatType={fmtSet}（{FmtName(fmtSet)}）");
                Console.WriteLine($"  ⇒ {(dsdSet ? "已成功切入 DSD 格式" : "切入 DSD 格式失败")}");

                if (dsdSet)
                {
                    DumpChannels(drv, outCount, "② 切入 DSD 格式后");
                }

                Console.WriteLine();
                Console.WriteLine("--- 逐档采样率支持（在 DSD 格式下问）---");
                var observed = new System.Collections.Generic.List<(string Name, double Hz, int[] Types)>();
                foreach (var (hz, name) in DsdRates)
                {
                    bool supported;
                    try
                    {
                        supported = ext.IsSampleRateSupported(hz);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {name,-7} {hz,10:0} Hz  查询异常：{ex.Message}");
                        continue;
                    }

                    Console.WriteLine($"  {name,-7} {hz,10:0} Hz  IsSampleRateSupported = {supported}");
                    if (!supported)
                    {
                        continue;
                    }

                    try
                    {
                        ext.SetSampleRate(hz);
                        DumpChannels(drv, outCount, $"{name} {hz:0} Hz 时");
                        observed.Add((name, hz, ChannelTypes(drv, outCount)));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    切到该速率失败：{ex.Message}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("--- 判定 ---");
                var dsdObservations = observed.Where(o => o.Types.Any(IsDsdType)).ToArray();
                bool sawDsdType = dsdSet && dsdObservations.Length > 0;
                if (sawDsdType)
                {
                    Console.WriteLine("✅ 驱动在原生 DSD 模式下给出了 DSD 采样类型，Native DSD 可行，按 P1 继续。");
                    foreach (var o in dsdObservations)
                    {
                        string types = string.Join(", ", o.Types.Select(t => $"{t}({TypeName(t)})"));
                        Console.WriteLine($"   {o.Name,-7} {o.Hz,10:0} Hz → {types}");
                    }

                    int required = dsdObservations[0].Types.First(IsDsdType);
                    Console.WriteLine($"   ⇒ 驱动要求的位序：{required}（{TypeName(required)}）");
                    if (required == 33)
                    {
                        Console.WriteLine("   ⚠ 是 MSB-first。主流 DSF 是 LSB-first，喂之前必须逐字节做 bit 反转（项目里已有 Rev8Table）。");
                    }

                    Console.WriteLine("   ⚠ KA13 硬件上限 DSD256；DSD512 不要发。");
                }
                else
                {
                    Console.WriteLine("⚠ 没有观察到 DSD 采样类型。");
                    Console.WriteLine("   可能：设备不在位、驱动不开放 native DSD、或切换时机不对。");
                    Console.WriteLine("   结论：先别动播放器代码，拿这份输出再判。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("探测过程异常：" + ex);
                return 4;
            }
            finally
            {
                if (drv != null)
                {
                    try
                    {
                        var (rcBack, fmtBack) = Io(drv, KAsioSetIoFormat, FmtPcm);
                        var (_, fmtVerify) = Io(drv, KAsioGetIoFormat, FmtPcm);
                        Console.WriteLine();
                        Console.WriteLine($"收尾：恢复 PCM 格式 → {DescribeRc(rcBack)}，FormatType={fmtBack}（{FmtName(fmtBack)}）；复查当前格式={fmtVerify}（{FmtName(fmtVerify)}）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("恢复 PCM 格式异常：" + ex.Message);
                    }

                    try
                    {
                        if (originalRate > 0)
                        {
                            ext?.SetSampleRate(originalRate);
                            Console.WriteLine($"收尾：恢复采样率 {originalRate:0} Hz");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("恢复采样率异常：" + ex.Message);
                    }

                    try
                    {
                        ext?.ReleaseDriver();
                        Console.WriteLine("收尾：已释放驱动");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("释放驱动异常：" + ex.Message);
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// P1 最小实播自测：用 AsioNativeDsdOutput 播几秒「合法 DSD 静音码流」。
        /// 听不到声音是预期的，目的是验证格式切换 → 采样率 → 缓冲 → 回调搬运 → 还原整条链路不崩，
        /// 并让用户用 KA13 的绿灯确认 DAC 真的收到了原生 DSD。
        /// </summary>
        private static int PlayDsdSilence(int seconds)
        {
            Console.WriteLine($"=== P1 最小实播自测：{seconds} 秒合法 DSD 静音码流 ===");
            Console.WriteLine("听不到声音是正常的；请盯着 KA13 机身窗口里的 RGB 灯 —— 绿色 = 收到 DSD。");
            Console.WriteLine();

            string[] names = AsioDriver.GetAsioDriverNames();
            if (names.Length == 0)
            {
                Console.WriteLine("没有 ASIO 驱动，放弃。");
                return 2;
            }

            string target = names.FirstOrDefault(n => n.IndexOf("FiiO", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[0];

            byte[] pattern = new byte[1 << 16];
            for (int i = 0; i < pattern.Length; i++)
            {
                pattern[i] = ((i & 1) == 0) ? (byte)0x69 : (byte)0x96;
            }

            Func<byte[], int, int> read = (buf, need) =>
            {
                int n = Math.Min(need, pattern.Length);
                Buffer.BlockCopy(pattern, 0, buf, 0, n);
                return n;
            };

            try
            {
                using var output = new AsioNativeDsdOutput(
                    target, 2822400, 2, sourceLsbFirst: true, read, s => Console.WriteLine(s));
                output.Open();
                Console.WriteLine($"驱动类型={output.DriverDsdType}，bufferSize={output.BufferFrames} 个 1-bit 采样"
                    + $" = {output.BytesPerChannel} 字节/通道");
                output.Start();
                Console.WriteLine("已起播…… 现在看灯。");
                Thread.Sleep(seconds * 1000);
                output.Stop();
                Console.WriteLine($"已停止：取走 {output.BytesConsumed} 字节，欠载 {output.Underruns} 次");
                Console.WriteLine($"理论字节数 ≈ {seconds * 705600}（DSD64 双声道 = 705600 字节/秒）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("实播失败：" + ex);
                return 5;
            }
            finally
            {
                Console.WriteLine("收尾：驱动已还原成 PCM / 原采样率。");
            }

            return 0;
        }

        private static (object? Rc, int FormatType) Io(AsioDriver drv, int selector, int requestFormat)
        {
            IntPtr p = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.Copy(new byte[512], 0, p, 512);
                Marshal.WriteInt32(p, 0, requestFormat);
                object? rc = CallFuture(drv, selector, p);
                return (rc, Marshal.ReadInt32(p, 0));
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private static object? CallFuture(AsioDriver drv, int selector, IntPtr opt)
        {
            MethodInfo? mi = typeof(AsioDriver).GetMethod(
                "Future",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(IntPtr) },
                null);
            if (mi == null)
            {
                Console.WriteLine("  ✗ NAudio 的 AsioDriver.Future 不可用");
                return null;
            }

            return mi.Invoke(drv, new object[] { selector, opt });
        }

        private static void DumpChannels(AsioDriver drv, int outCount, string tag)
        {
            Console.WriteLine($"  [{tag}] 输出通道采样类型：");
            for (int i = 0; i < outCount; i++)
            {
                try
                {
                    var info = drv.GetChannelInfo(i, false);
                    int t = (int)info.type;
                    string note = IsDsdType(t) ? "  ← 原生 DSD 类型 ✓" : "";
                    Console.WriteLine($"    ch{i}: {info.type,-22} ({t,2}){note}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ch{i}: 读取失败 {ex.Message}");
                }
            }
        }

        private static int[] ChannelTypes(AsioDriver drv, int outCount)
        {
            var result = new int[outCount];
            for (int i = 0; i < outCount; i++)
            {
                try
                {
                    result[i] = (int)drv.GetChannelInfo(i, false).type;
                }
                catch
                {
                    result[i] = -1;
                }
            }

            return result;
        }

        private static bool IsDsdType(int t) => t == 32 || t == 33 || t == 40;

        private static string TypeName(int t) => t switch
        {
            32 => "DSDInt8LSB1 / LSB-first",
            33 => "DSDInt8MSB1 / MSB-first",
            40 => "DSDInt8NER8",
            18 => "Int32LSB / PCM",
            _ => "其他",
        };

        private static string FmtName(int f) => f switch
        {
            FmtInvalid => "无效 / 驱动拒绝",
            FmtPcm => "PCM",
            FmtDsd => "DSD",
            _ => "未知",
        };

        private static string DescribeRc(object? rc)
        {
            if (rc == null)
            {
                return "NAudio 吞掉了返回值（Future 为 void），以 FormatType 为准";
            }

            try
            {
                int v = Convert.ToInt32(rc);
                string tag = v == AsioSuccess ? "ASE_SUCCESS ✓" : v == 0 ? "ASE_OK（不算成功）" : "非成功";
                return $"0x{unchecked((uint)v):X8} {tag}";
            }
            catch
            {
                return rc.ToString() ?? "?";
            }
        }
    }
}
