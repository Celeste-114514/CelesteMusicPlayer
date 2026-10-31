using System;
using Microsoft.UI.Dispatching;

namespace CelesteMusicPlayer;

/// <summary>
/// 原生任务栏缩略图按钮域（2026-10-31 第十八刀整文件转活）。
/// 绑定 + 缩略图按钮泵（绑定后 250ms x 40 次，覆盖 Explorer 任务栏重绘窗口）。
/// 依赖的状态字段（_taskbarButtons / _thumbPumpTimer / _thumbPumpTicks）随批搬入。
/// </summary>
public sealed partial class MainWindow
{
    private TaskbarThumbnailButtons? _taskbarButtons;

    private DispatcherQueueTimer? _thumbPumpTimer;
    private int _thumbPumpTicks;

    internal nint NativeTaskbarHandle => _taskbarButtons?.BoundWindow ?? 0;
    internal void BindNativeTaskbar(nint hwnd){_taskbarButtons?.Dispose();_taskbarButtons=new TaskbarThumbnailButtons(this,hwnd);_thumbPumpTimer?.Stop();_thumbPumpTimer=null;_thumbPumpTicks=0;StartThumbnailButtonsPump();}

    private void StartThumbnailButtonsPump()
    {
        if (_thumbPumpTimer != null) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        int maxTicks = 40; // ~10 秒，足以覆盖 Explorer 任务栏重绘窗口
        timer.Tick += async (s, e) =>
        {
            _thumbPumpTicks++;
            try
            {
                if (_taskbarButtons != null)
                {
                    await _taskbarButtons.PumpAsync();
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("thumbPump.Tick", ex);
            }
            if (_thumbPumpTicks >= maxTicks)
            {
                timer.Stop();
                _thumbPumpTimer = null;
                StartupLog.Write("[thumb] Pump 已停止 ticks=" + _thumbPumpTicks);
            }
        };
        timer.Start();
        _thumbPumpTimer = timer;
        StartupLog.Write("[thumb] Pump 已启动，每 250ms 一次，最多 40 次");
    }
    }
