// 内存自检工具（2026-10-07 · 诊断「播放时进程涨到 1G+」）
//
// 为什么需要它：
//   进程外的手段（任务管理器 / Get-Process / .NET CLR 性能计数器）都读不到
//   托管堆的分项——CLR 性能计数器在 .NET 9 上对外部进程已不可用，
//   于是「761MB 到底花在哪」只能猜。本文件让程序自己报数，口径最准。
//
// 挂载方式：在 App.OnLaunched 里调一次 MemProbe.Log("启动")，
//   之后在切歌/起播等时机再调，即可看出各项随时间的变化。
//
// 读法：
//   ManagedHeap    = GC.GetTotalMemory(false)           托管堆（含未回收的垃圾）
//   ManagedTotal   = GC.GetGCMemoryInfo().TotalCommittedBytes  实际已提交
//   LOH            = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes  大对象堆
//   WorkingSet     = 进程工作集（含原生）
//   PrivateBytes   = 私有提交（含所有非托管：AllocHGlobal、DSP、视频解码…）
//   非托管部分    = PrivateBytes - ManagedTotal  ← 这就是查「PCM 缓冲/视频解码」的抓手
//
// ⚠ LogOnly 模式不做任何强制回收，只观察，不干扰播放。

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace CelesteMusicPlayer
{
    internal static class MemProbe
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        // =====================================================================
        // 内存回收器（2026-10-07）
        //
        // 为什么需要：App.xaml.cs 里原本给**所有**输出模式都开了 SustainedLowLatency GC
        //（为修独占模式的 STW 卡顿），而低延迟模式让 GC 几乎不回收——3609 首曲库加载完
        // 就堆到 449MB 私有内存，播到第 5 首整进程 1.4GB，其中托管堆只有 100~160MB，
        // 差额全是没回收的垃圾。现已改为「只有独占/ASIO 才开低延迟」，
        // 但共享模式下默认 GC 是「按需回收」，并不会主动把内存还给系统，
        // 这里补一个**空闲时**的主动回收，拿到立竿见影的效果。
        //
        // 为什么放在「切歌后」而不是定时器：定时器会在播放中途回收，
        // 而 gen2/LOH 回收是 STW，会挂起渲染线程 → 可闻断音（这正是当初引入低延迟 GC
        // 要规避的问题）。切歌瞬间旧数据已无人引用，是安全窗口。
        //
        // 只收 Gen1 不碰 Gen2：Gen1 通常几毫秒；Gen2/LOH 动辄几十上百毫秒。
        // 要更彻底地排查再用 MemProbe.LogAfterGC（Aggressive 双次）。
        // =====================================================================
        internal static void CollectAfterTrackChange(bool playing)
        {
            // 正在播放时不回收：STW 会挂起渲染线程造成断音（低延迟 GC 本来就是为规避这个）。
            if (playing) return;

            try
            {
                long before = GC.GetTotalMemory(false);
                GC.Collect(1, GCCollectionMode.Forced, blocking: true, compacting: false);
                long after = GC.GetTotalMemory(false);
                long freed = before - after;
                if (freed > 8L * 1024 * 1024)
                {
                    StartupLog.Write(string.Format(
                        "[MEM] 切歌后回收 Gen1：托管堆 {0:F0}MB → {1:F0}MB（释放 {2:F0}MB）",
                        before / 1048576.0, after / 1048576.0, freed / 1048576.0));
                }
            }
            catch (Exception ex) { StartupLog.WriteException("MemProbe.CollectAfterTrackChange", ex); }
        }
        /// <summary>
        /// 内存快照的「上一行」与「上一次的数值」，用于在两次采样之间自动算出增量。
        /// 只有增量能回答真正的问题：「这一步到底吃掉了多少」。
        /// </summary>
        private static string? _lastLine;
        private static long _lastManagedHeap;
        private static long _lastPrivate;
        private static long _lastWorkingSet;

        /// <summary>是否允许在播放中周期性采样（默认关，由调试开关打开）。</summary>
        internal static bool SamplingEnabled;

        private static Timer? _timer;
        private static int _sampleSeq;

        /// <summary>
        /// 开启周期采样。每 <paramref name="seconds"/> 秒打一行，用来抓「不切歌也会慢慢涨」这类泄漏。
        /// </summary>
        internal static void StartSampling(int seconds = 5)
        {
            try
            {
                SamplingEnabled = true;
                _timer?.Dispose();
                _timer = new Timer(_ =>
                {
                    if (!SamplingEnabled) return;
                    _sampleSeq++;
                    Log("采样#" + _sampleSeq.ToString(CultureInfo.InvariantCulture));
                }, null, seconds * 1000, seconds * 1000);
            }
            catch (Exception ex) { StartupLog.WriteException("MemProbe.StartSampling", ex); }
        }

        internal static void StopSampling()
        {
            SamplingEnabled = false;
            try { _timer?.Dispose(); _timer = null; } catch { }
        }

        /// <summary>打一行内存快照到启动日志。</summary>
        public static void Log(string tag)
        {
            try
            {
                LogCore(tag, force: false);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MemProbe.Log", ex);
            }
        }

        /// <summary>
        /// 强制回收后再报数，用于区分「真在用」与「垃圾还没收」。
        /// 只在排查时手动调用，正常播放路径不要开（回收本身会冻线程，表现为卡顿）。
        /// </summary>
        public static void LogAfterGC(string tag)
        {
            try
            {
                LogCore(tag, force: true);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MemProbe.LogAfterGC", ex);
            }
        }

        private static void LogCore(string tag, bool force)
        {
            if (force)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: false);
            }

            long managedHeap = GC.GetTotalMemory(force);
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long managedTotal = gcInfo.TotalCommittedBytes;

            // GenerationInfo 顺序固定：[0]=Gen0 [1]=Gen1 [2]=Gen2 [3]=LOH [4]=POH
            // ⚠ .NET 9 的 GCGenerationInfo 没有 Generation 属性，只能按下标取。
            long loh = 0;
            var gens = gcInfo.GenerationInfo;
            if (gens.Length > 3)
            {
                loh = gens[3].SizeAfterBytes;
            }

            long pUnmanaged = 0;
            long workingSet = 0;
            int threads = 0;
            try
            {
                var me = Process.GetCurrentProcess();
                me.Refresh();
                pUnmanaged = me.PrivateMemorySize64;
                workingSet = me.WorkingSet64;
                threads = me.Threads.Count;
            }
            catch
            {
                // 拿不到就留 0，不让诊断本身影响运行
            }

            long nativeSide = pUnmanaged - managedTotal;

            var sb = new StringBuilder();
            sb.Append("[MEM] ").Append(tag);
            sb.Append("  t=").Append(Clock.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)).Append("s");
            sb.Append("  WS=").Append(Mb(workingSet));
            sb.Append("  Priv=").Append(Mb(pUnmanaged));
            sb.Append("  | 托管堆=").Append(Mb(managedHeap));
            sb.Append("  已提交=").Append(Mb(managedTotal));
            sb.Append("  LOH=").Append(Mb(loh));
            sb.Append("  | 非托管=").Append(Mb(nativeSide));
            sb.Append("  线程=").Append(threads.ToString(CultureInfo.InvariantCulture));
            sb.Append(force ? "  [已强制GC]" : "");

            // ---- 增量：这一步到底吃掉多少（第一次采样没有基线，就不显示）----
            bool hasDelta = _lastPrivate != 0;
            if (hasDelta)
            {
                sb.Append("  |Δ托管=").Append(MbDelta(managedHeap - _lastManagedHeap));
                sb.Append("  Δ非托管=").Append(MbDelta(pUnmanaged - managedTotal - (_lastPrivate - _lastManagedHeap)));
                sb.Append("  ΔWS=").Append(MbDelta(workingSet - _lastWorkingSet));
            }
            _lastManagedHeap = managedHeap;
            _lastPrivate = pUnmanaged;
            _lastWorkingSet = workingSet;

            string line = sb.ToString();
            StartupLog.Write(line);

            // 相邻两次同标签无变化时不重复刷屏（采样日志会很长）
            if (!force && string.Equals(_lastLine, line, StringComparison.Ordinal))
            {
                // 静默跳过
            }
            _lastLine = line;
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + "MB";
        }

        /// <summary>增量，带正负号——负数说明这一步释放了内存（那正是我们要抓的「该释放没释放」）。</summary>
        private static string MbDelta(long bytes)
        {
            double mb = bytes / 1048576.0;
            string sign = mb >= 0 ? "+" : "-";
            return sign + Math.Abs(mb).ToString("F1", CultureInfo.InvariantCulture) + "MB";
        }
    }
}
