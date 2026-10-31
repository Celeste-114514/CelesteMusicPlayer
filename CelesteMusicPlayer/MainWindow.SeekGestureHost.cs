using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十六刀（2026-10-31）：正在播放进度条的拖拽/跳转手势。
/// 从壳 <c>MainWindow.Features.cs</c> 搬出：按下暂停刷新、松开/丢捕获时提交 seek。
/// （注：不能放进 MainWindow.NowPlayingLayout.cs —— 那个文件在 csproj 的
/// <c>_w13_npcluster</c> 排除块里，不参与编译。）
/// </summary>
public sealed partial class MainWindow
{

    }
