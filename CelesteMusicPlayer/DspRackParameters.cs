using System;
using System.Linq;

namespace CelesteMusicPlayer;

/// <summary>链路级状态：机架顺序（8 个内核模块的排列）+ 全局旁路。
/// 两者都不属于某一个效果模块，因此没有走 IDspParameterModel 那套"按控件名读写"的契约 ——
/// 这里只有顺序的编辑操作与旁路开关，页面与网页命令共用同一份，不再各存一份草稿。
/// 旁路是 A/B 对比用的内存态（与旧行为一致，不落盘），顺序则由 <see cref="DspRackStore"/> 持久化。</summary>
internal sealed class DspRackParameters
{
    private int[] _order = DefaultOrder();

    /// <summary>当前顺序（渲染用的只读视图；要落盘请用 <see cref="Snapshot"/>）。</summary>
    internal int[] Order => _order;

    /// <summary>全局旁路：开 = 所有 DSP 让路，bit-perfect 直通。</summary>
    internal bool Bypassed { get; private set; }

    /// <summary>顺序是否还是出厂排列。</summary>
    internal bool IsDefault => _order.SequenceEqual(DefaultOrder());

    internal int[] Snapshot() => (int[])_order.Clone();

    /// <summary>用盘中顺序装载。非法（不是 0..7 的排列）时回退默认 —— 与 RackState.Normalize 同口径。</summary>
    internal void Load(int[]? order)
    {
        _order = IsPermutation(order) ? (int[])order!.Clone() : DefaultOrder();
    }

    /// <summary>把选中行上下移动一格（delta=-1 上移 / +1 下移）；越界时返回 false。</summary>
    internal bool Move(int index, int delta)
    {
        int target = index + delta;
        if (index < 0 || target < 0 || index >= _order.Length || target >= _order.Length)
        {
            return false;
        }

        (_order[index], _order[target]) = (_order[target], _order[index]);
        return true;
    }

    /// <summary>恢复出厂顺序；已经就是默认时返回 false。</summary>
    internal bool Reset()
    {
        if (IsDefault)
        {
            return false;
        }

        _order = DefaultOrder();
        return true;
    }

    /// <summary>切换全局旁路；状态没变时返回 false。</summary>
    internal bool SetBypass(bool on)
    {
        if (Bypassed == on)
        {
            return false;
        }

        Bypassed = on;
        return true;
    }

    internal static int[] DefaultOrder() => new[]
    {
        DspCoreInterop.RackEqualizer,
        DspCoreInterop.RackConvolution,
        DspCoreInterop.RackReplayGain,
        DspCoreInterop.RackCompressor,
        DspCoreInterop.RackCrossfeed,
        DspCoreInterop.RackStereoField,
        DspCoreInterop.RackChannelMatrix,
        DspCoreInterop.RackChannelBalance
    };

    private static bool IsPermutation(int[]? order)
    {
        if (order == null || order.Length != DspCoreInterop.RackModuleCount)
        {
            return false;
        }

        bool[] seen = new bool[DspCoreInterop.RackModuleCount];
        foreach (int id in order)
        {
            if (id < 0 || id >= DspCoreInterop.RackModuleCount || seen[id])
            {
                return false;
            }

            seen[id] = true;
        }

        return true;
    }
}
