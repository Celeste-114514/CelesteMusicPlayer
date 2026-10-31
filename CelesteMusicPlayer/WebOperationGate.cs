using System;

namespace CelesteMusicPlayer;

/// <summary>
/// 网页发起的耗时操作互斥（挑选文件/目录、导入导出、删除确认）。
///
/// W13：原来是 <c>MainWindow._webBrowseBusy</c> 一个裸 bool，曲目动作与目录浏览两个分片各写一遍。
/// 现在两边共用同一个实例：**必须**是同一个，否则「挑文件夹」和「挑播放列表」能同时弹出，
/// 用户看到两个对话框叠在一起——这是行为变化，不是重构。
/// </summary>
internal sealed class WebOperationGate
{
    private bool _busy;

    /// <summary>进入互斥区。已被占用时返回 null（调用方照旧抛「请先完成当前操作」）。</summary>
    internal IDisposable? TryEnter()
    {
        if (_busy) return null;
        _busy = true;
        return new Lease(this);
    }

    private sealed class Lease(WebOperationGate gate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            gate._busy = false;
        }
    }
}
