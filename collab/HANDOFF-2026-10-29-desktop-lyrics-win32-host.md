# 桌面歌词 · Win32 合成宿主（WebView2，无 WinUI）交接 · 2026-10-29 04:45

用户拍板：桌面歌词**必须用 WebView2 重构，并且不要 WinUI**（他后面还有跨平台打算）。
功能参照 ECHO / Twilight_Echo，实现兜底参照 MusicPlayer2。

## 一、为什么放弃 WinUI（有实测证据，别再回头试）

- Codex 上一版把歌词窗建在 `Microsoft.UI.Xaml.Window` + 自定义透明 `SystemBackdrop`。
- 实机截屏取色：窗口区域是均匀 `#202020`（WinUI 的 Mica 兜底色），**桌面完全没透过来**。
- 原因：`Windows.UI.Composition` 新建的 Compositor 画刷和 WinUI 窗口不是同一棵合成树，赋给
  `ICompositionSupportsSystemBackdrop.SystemBackdrop` 会被丢弃 → 退回 Mica 兜底色。
- 结论：XAML `Window` 拿不到 DWM 逐像素透明。要透明只能自己建裸窗口。

## 二、现在这条路的形状（已跑通的证据）

裸 Win32 窗口 + DirectComposition + WebView2 合成宿主：

1. 窗口：`WS_POPUP`，扩展样式 `TOPMOST|TOOLWINDOW|NOACTIVATE|NOREDIRECTIONBITMAP`（穿透时加 `TRANSPARENT`）。
2. 合成：`Windows.UI.Composition.Compositor` → `ICompositorDesktopInterop.CreateDesktopWindowTarget(hwnd, true, out ptr)`。
   ⚠ 类型在 **`Windows.UI.Composition.Desktop`** 子命名空间（`DesktopWindowTarget`），不是 `Windows.UI.Composition` 根命名空间。
3. 合成树必须**两层**：
   - 根容器 `ContainerVisual`，`RelativeSizeAdjustment = Vector2.One`，挂到 `target.Root`；
   - 网页宿主 `ContainerVisual` 作为子视觉，给**绝对 Size**，赋给 `controller.RootVisualTarget`。
   ⚠ 直接把一个视觉当 target.Root 又当 RootVisualTarget，它的 Size 会被窗口接管、量出来是 0×0 →
   网页不画 + `SendMouseInput` 报「值不在预期范围内」。这是踩过的坑。
4. WebView2：`env.CreateCoreWebView2CompositionControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd))`。
   ⚠ **合成宿主也仍然要用 `controller.Bounds` 定尺寸**（官方文档原话），不设就是 0×0。
5. 鼠标：合成宿主没有子窗口，`WM_MOUSEMOVE/LBUTTON*/RBUTTON*/WHEEL/LEAVE` 由宿主转发 `controller.SendMouseInput`，
   坐标要除以 DPI 缩放换成 DIP。`WM_SETCURSOR` 用窗口类默认箭头（CoreCursor 拿不到 HCURSOR）。
6. 穿透：`WS_EX_TRANSPARENT` + `SetWindowPos(SWP_FRAMECHANGED)` 让系统重算框架。

**已验证**：设 `CELESTE_DL_SOLID=1` 时在网页宿主下面铺半透明红，截图取到 `(255,135,135)` —— 红色叠在桌面内容上，
说明「窗口透明 + DWM 逐像素混合」这条链路是通的。

**未验证**：网页画面是否真的画进合成树（`Bounds` 修复是在这次最后一次构建后加的，还没跑成功过一次）。

## 三、本轮改动清单（均未提交）

- `CelesteMusicPlayer/WebDesktopLyricsOverlay.cs` —— 整个重写成 Win32 合成宿主（替换 Codex 的 XAML 版）。
  对外接口保持不变：`Show/Close/Dispose/SetLyrics/Sync/SetPlaybackPaused/SetLocked/ApplySettings/SetSavedPosition/IsVisible/IsLocked/CurrentPosition/PositionProvider/ClosedByUser`。
- `CelesteMusicPlayer/MainWindow.WebShell.cs` —— 新增 `WebShellEnvironment`（复用主界面 WebView2 环境，省一个浏览器进程）。
- `CelesteMusicPlayer/MainWindow.Lyrics.cs`、`MainWindow.xaml.cs`、`MainWindow.UiTheme.cs` —— 类型换成新类 + 主窗关闭时一起收。
- `CelesteMusicPlayer/AppSettingsStore.cs` —— 四个新字段及 `Clone()`：`DesktopLyricDirection/FontFile/MusicReactive/Scale`。
- `CelesteMusicPlayer/WebSettingsSchema.cs` + `MainWindow.WebSettings.cs` —— 桌面歌词独立分类、字体选择器 `pickLyricFont`。
  ⚠ `MainWindow.WebSettings.cs` 里加了 `NativeStillUsedByWeb` 白名单：**不要再用「scope==native 一律隐藏」**，
  那会把置顶/关闭行为/滚轮音量/启动分类/任务栏进度 5 项从设置页悄悄抹掉（网页还在渲染它们）。
- `outputs/webview2-shell/desktop-lyrics.{html,css,js}` —— 页面 + 解锁条 + 逐字 + 竖排 + 极客风 + 解锁热区上报。
- `outputs/_probe_dl_render.cjs` —— 网页层核对（playwright-core，路径 `C:/Users/MSI/_pwtmp/node_modules/playwright-core`），已 PASS。

## 四、环境坑（这次踩得很疼，务必先看）

1. **构建工具链本身在崩**（不是代码问题）：XamlCompiler `-1073741819` 访问越界、VBCSCompiler `0x80131506`、
   dotnet 遥测 `Microsoft.ApplicationInsights` 崩溃、甚至 bash 自身 SIGSEGV。事件日志里能看到
   XamlCompiler `AccessViolationException` 和「初始化分析 API 附加基础结构时发生错误」。
   → 出现这些就重试；连续失败说明机器状态不健康（内存 80% 占用 / 这台的稳定性老毛病），建议重启。
2. ⚠ **绝对不要在项目目录里建备份目录**（例如 `CelesteMusicPlayer/_xxx/obj/...`）。
   里面的 `.g.i.cs` 会被默认 `**/*.cs` 扫描进去 → 每个 XAML 类型重复定义（CS0102/CS0111，上千条）。
   备份必须放到仓库根或仓库外。
3. ⚠ `_publish_retry_clean.py` 传了 `-p:WindowsPackageType=None`，而 `win-x64.pubxml` 明确警告不要传
   （会影响打包路径）。交付发布用：
   `dotnet publish CelesteMusicPlayer/CelesteMusicPlayer.csproj -c Release -r win-x64 -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`
4. 发布目录 `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`。清空它再发布，避免不同配置的产物混在一起。

## 五、当前阻塞与下一步

- 最后一次发布起来的程序**启动 6~8 秒后 `0xC0000409` 硬崩，界面都没出现**，
  把「启动即显示桌面歌词」关掉也一样崩 → **与本功能无关**，高度怀疑是反复搬动 obj/bin 造成的产物混装，
  或机器当前状态不健康。
- 下一步顺序：
  1. 重启机器 → 干净全量重建（obj/x64 与 bin/x64 一起移走，备份放仓库外）→ 确认程序能正常启动。
  2. 起程序，看日志有没有 `[桌面歌词] 视觉宿主 已接受`，并截图确认网页真的画在桌面上（背景透出）。
  3. 通了再依次接：竖排尺寸、解锁条（宿主按页面上报的 hintRect 做命中热区）、拖动、位置记忆、字体文件、能量光晕。
  4. 最后：网页层核对脚本 PASS + 实机截图 + 一次任务一个 commit（只提交桌面歌词这轮，别把未完成的迷你样式包带进去）。

## 六、注意事项

- 用户拍板过「迷你播放器、零散播放窗口暂不改」；本轮的 `MiniPlayerStyles.cs` / `MainWindow.MiniStyles.cs` /
  `mini-styles-settings.js` 是另一条并行 WIP，**别一起发布、别一起提交**。
- 测试时把 `%LOCALAPPDATA%\CelesteMusicPlayer\app-settings.json` 的 `OpenDesktopLyricsOnStartup` 临时置 true 拉起歌词窗，
  测完记得还原（本轮已还原成 false）。改之前先 `cp` 备份。
- `CELESTE_DL_SOLID=1` 这个排障开关保留在代码里，用来分辨「合成链路不通」还是「网页没画进去」。
