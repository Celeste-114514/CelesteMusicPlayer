# W13 移除 WinUI 依赖 —— 本轮结果（2026-10-30）

结论先行：**架构主干已落地并可回退，但编译收尾未完**。项目已经不再引用 WinUI / Windows App SDK，
入口改成原生消息循环，旧 XAML 界面全部从编译中下线，MainWindow 从「WinUI 窗口」变成「纯业务宿主」。
但 MainWindow 那 49k 行旧界面残留代码对 XAML 控件的耦合很深，让它们编译通过还剩约 1450 处机械适配，
本轮没能收干净。**还不能对外宣称「已去掉 WinUI」**（编译未过 = 不能发布）。

提交：`14f38a5 w13: drop WinUI/AppSDK references, native entry, legacy XAML out of build`
（前一个提交 `5689fba` 是过渡层与宿主改造，`fc4a41d` 是零散窗收尾。三段都可单独回退。）

---

## 1. 本轮做完的（架构层面 = W13 的第 2~5 步）

| 项 | 改动 | 证据 |
|---|---|---|
| 项目不再引用 WinUI | `CelesteMusicPlayer.csproj`：`UseWinUI=false`；删掉 7 个 `Microsoft.WindowsAppSDK*` 包 + `CommunityToolkit.WinUI.UI.Controls.DataGrid`；`EnableMsixTooling=false` | csproj 中已无 WindowsAppSDK 包 |
| 旧 XAML 全部下线（**不删文件**） | 18 个 `Page Remove` + 28 个 `Compile Remove`（设置窗/均衡器窗/房间校正/颜色选择/艺术家头像/播放队列/当前播放列表/标签面板/设置面板/音频链面板/文件关联面板 + 迷你·零散相关 partial） | csproj ItemGroup |
| 迷你 / 零散源码未被改动 | 只从编译里排除，**文件原样留在仓库**（用户要求自己重构，没碰实现） | `MiniPlayerWindow.xaml.cs` 等仍在磁盘 |
| 入口改原生 | `Program.Main` 不再 `Application.Start`，改为建 App → `app.Run(args)` → `NativeMessagePump.Run()` | Program.cs |
| App 不再是 WinUI Application | `App : Application` → 普通类；`OnLaunched` → `Run(string[])`；补 `Exit()`（写干净退出标记 + 停消息泵）；去掉 `InitializeComponent` 与 XAML 资源字典 | App.xaml.cs |
| 自建调度器 | 新增 `NativeDispatcherQueue.cs`：Win32 隐藏消息窗口 + 消息队列，提供 `DispatcherQueue` / `DispatcherQueueSynchronizationContext` / `DispatcherQueueTimer` / `DispatcherQueueController`（同名命名空间，业务代码零改动） | 新文件 |
| 自建窗口 API | 新增 `NativeAppWindow.cs`：用 HWND 实现 `AppWindow` / `OverlappedPresenter` / `AppWindowTitleBar` / `DisplayArea`（Show/Hide/Resize/Move/最大化最小化/置顶） | 新文件 |
| MainWindow 变业务宿主 | 去掉 `: Window` 基类；删 `InitializeComponent()`；`MainWindow.xaml` 移出编译（改名 `_legacy_xaml/MainWindow.xaml.legacy`）；706 个 XAML 控件改为状态容器（`MainWindow.LegacyControls.cs`）；补 `DispatcherQueue` 属性与 `StartWebShellSession()` | MainWindow.xaml.cs / LegacyControls.cs |
| 过渡层（临时） | `LegacyWinUiShim*.cs` + `LegacyEnums.cs`：同名的 Microsoft.UI.Xaml 控件/类型/成员替身，控件只当状态存放用（DSP 各模块本来就直读写这些控件） | 新文件 |
| 顺带清理 | `EnsureTagEditUiBuilt` / `EnsureSettingsUiBuilt` 不再构造 XAML 面板；`ReplayGainScanScope` 枚举从已下线的窗口搬到 `LegacyEnums.cs` | 代码内注释标明 W13 |

---

## 2. 没做完的（必须知道）

- **编译未通过**。全量重建（`-t:Rebuild`）时真实剩余约 **1450 处**错误，全部在 MainWindow 那批旧界面残留代码里
  （UiTheme / Features / Playback / Misc / Geek* / Opra* / Visualizer / Terminal* 等）。
  典型类别：控件成员类型不匹配、事件处理器签名不匹配、缺类型/缺静态成员。
- 未做 publish 验证、未做实机冒烟 —— 编译没过，这两步没有意义。
- 因此 **W14（README/CHANGELOG/发布说明）不能开工**。

---

## 3. 踩到的坑（下次直接照抄）

1. **增量编译会骗人**：`dotnet build` 常常报「0 错误」，但 `-t:Rebuild` 下是几百上千条。
   判断编译是否真的过了，**必须用 `-t:Rebuild`**，这是本轮最大的时间陷阱。
2. **编译器分批暴露错误**：修完一批（比如 800 条）后，下一次编译会冒出下一批（可能更多）。
   不要看到「从 1734 降到 2」就以为收尾了。
3. **补录成员不要用 `object?`**：下游的索引、`+=`、颜色转换、await 会连锁报错；改成 `dynamic?` 后收敛明显。
4. **事件委托**：参数写成 `dynamic` 只适配 lambda 订阅；方法组订阅要求签名精确匹配
   → 处理器方法的参数也要改成 `dynamic sender, dynamic e`。
5. **类型放错命名空间会 CS0104 歧义**：补录的类型必须放在真 WinUI 的那个命名空间
   （`Storyboard` 在 `Media.Animation`、`Run` 在 `Documents`、`SelectionChangedEventArgs` 在 `Controls`…）。
6. **排除整批界面文件会更糟**：这些文件里混着业务方法/字段，排除后连锁错误反而变多（试过一次，1450 → 1980，已回退）。
7. Roslyn 偶发 `0x80131506` 崩溃是环境噪音，原样重试即可（本轮遇到过一次）。

---

## 4. 下一步建议（按性价比排序）

**路线 A（推荐）先把业务从界面文件里搬出来，再整批排除界面文件**
- 对每个界面文件（UiTheme / Features / Geek* / Opra* / Visualizer / Terminal* …），
  先把它里面被别处调用的方法/字段搬到 `MainWindow.Business*.cs`（或对应服务），
  确认没有外部引用后，再从 csproj 里 `Compile Remove`，**每搬一个文件编译一次**。
- 好处：错误量随文件逐个消失，不会像现在这样"修一批冒一批"。

**路线 B 继续编译驱动适配**
- 仓库里已有工具：`_w13_tools/_iter2.py`（Rebuild → 收集缺类型/缺字段/缺成员 → 自动补录 → 循环）。
- 适合在路线 A 搬完主要文件后，用它清剩下的零散错误。

**验收口径**
- `dotnet build ... -t:Rebuild` 0 错误 → `dotnet publish`（`-p:CelesteSelfContainedDistribute=true -p:CsWinRTAotOptimizerEnabled=false`）→ 后台真实进程冒烟（冷启动/热启动/重载/关闭）。
- 冒烟过了才可以在待办里把 W13 标完成，然后才轮到 W14。

---

## 5. 红线

- 迷你播放器、零散播放窗口的源码**不要改实现**（用户要自己重构）；只能从编译里排除，文件必须留着。
- 编译没过之前，不得对外/文档宣称「已移除 WinUI」。
- 一事项一提交，不 `git add -A`；本轮三段提交都可独立回退。
