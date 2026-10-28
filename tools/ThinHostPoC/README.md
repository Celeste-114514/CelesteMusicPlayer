# W5 薄宿主 PoC

独立 WinExe：.NET 10 / WinForms（Win32 HWND 与 STA 消息循环）/ WebView2。无 Windows App SDK、WinUI 或正式播放器项目引用；链接 W1 的命令、快照和窗口协调源码。

这是模拟验证程序，不连接音频设备、音乐文件、正式曲库/设置/更新器。正式入口不变。

## 发布与隐藏自测

仓库根目录执行：

```powershell
dotnet publish tools/ThinHostPoC -c Release -r win-x64 --self-contained true -o dist/thin-host-poc
./tools/TestThinHostPoC.ps1
./tools/NewThinHostShortcut.ps1
```

隐藏自测会建立真实 STA HWND、WinForms WebView2、独立浏览器 profile，加载本地虚拟域名页面，再实际往返 snapshot/play/play/next/previous/pause/未知命令。成功必须收到网页完成消息，退出码及报告均通过；并检查发布依赖、已加载程序集没有 WinUI。任务栏 COM 工厂和 ITaskbarList3.HrInit 实际调用。

PoC 的浏览器 profile、日志和 JSON 报告位于 `%LocalAppData%/CelesteMusicPlayer-Poc/<时间-标识-PID>/`，与正式程序隔离；不自动删除历史报告。

## 人工验收

双击根目录“验证W5薄宿主.lnk”。窗口明确标注模拟会话，无实际音频。

1. 拖动、改变大小：网页随窗口调整，没有额外黑窗。
2. 点模拟播放/暂停、上一首、下一首：网页读数改变；窗口关闭不影响正式播放器。
3. 最小化后从任务栏恢复：窗口仍为同一会话。
4. 悬停此测试窗口的任务栏图标：三个模拟按钮能点击，进度正常显示。
5. 移到不同 DPI 显示器后调整大小/恢复，检查清晰度与点击坐标。
6. 关闭窗口后检查测试进程退出；Explorer 重启后的任务栏按钮补建另行验证。

自动隐藏测试不会显示窗口，因此第1、3、4、5、6项的可见表现不能据自动通过就打勾。W5 完整实机验收前不替换正式入口。

实现依据：[WebView2 STA 消息循环](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)、[WinForms 初始化接口](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.winforms.webview2.ensurecorewebview2async)、[Windows Taskbar COM](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)。
