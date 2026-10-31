# W13 收尾：假控件清零 + 三处「被 Content.XamlRoot 掐死的对话框」

日期：2026-11-01
范围：`CelesteMusicPlayer/`（本仓）
前置：`collab/PLAN-2026-10-30-w13-legacy-shell.md` 的四步策略（冻结 → 救真活 → 快照化 → 整批排除）

---

## 一、三句话结论

1. **假控件层已经彻底清空**：`MainWindow.LegacyControls.cs`（706 个假控件字段）整文件删除，
   业务代码里对假控件的引用从 **2248 条降到 0 条**。这一轮 22 个提交、净删 10,031 行。
2. **垫片的「类型定义」还在**（12 个文件 / 2,484 行 / `Microsoft.UI.*` 下 339 个类型），
   但已经**不含任何界面控件实例**，只作为「使宿主窗口代码能编译」的类型替身存在，
   仍有 92 个文件在写 `Microsoft.UI.*`。要删它得先换掉 6 个宿主窗口的类型 —— 属新工程，不是收尾。
3. **本轮修好了三处被 `Content.XamlRoot` 静默掐死的对话框**（错误提示、歌词保存询问、关闭询问）：
   界面搬去网页壳之后 `MainWindow.Content` 恒为 null，而那层「等 XamlRoot 就绪再弹」的守卫
   把三处全部挡在日志里。原生对话框（`NativeContentDialogs`）根本不需要 XamlRoot。

---

## 二、本轮实际改了什么（可逐条回退）

| 提交区段 | 内容 | 判据 |
|---|---|---|
| `8e83c10..8c7e9a6` | 假控件引用 2248 → 17：删 700 个未使用垫片字段、空壳方法、假绘制器、后台渲染器、列宽自适应、行选中样式链等 | 编译 0 错 + 接口回归 + 出货同步 |
| `8c7e9a6` | `MainWindow.LegacyControls.cs` 整文件 `git rm`（706 字段 → 0） | 同上 |
| `5cd83da` | **事故修复**：`Content = RootShell` 三件套删除后启动初始化整条链断掉（用户报「只剩 cue」），改由 `RunPostLoadStartup()` 直接调用 | 扫描 4031 路径 / 入库 4096 首 / `tracks=4096` |
| `247d403` | 冒烟脚本补硬判据（ERR=0、tracks≥1000、`MainWindow_Loaded begin` 恰好 1 次、启动收尾有结论） | `SMOKE=PASS` |
| 本次（dialogfix） | ① `ShowErrorAsync` 去掉 XamlRoot 轮询/守卫，错误能真的弹出来；② 歌词保存询问去掉会抛异常的 `XamlRoot = Content.XamlRoot`（原来恒判「不保存」）；③ 关闭询问去掉同样的守卫；④ 删掉永不可能接上的滚轮/键盘根处理器（`Content` 恒 null）；⑤ 删两处 `Content is FrameworkElement` 空动作 | Rebuild 0 错 + 冒烟 4 条硬判据 |

### 本次删除的失效代码（`MainWindow.LibraryLoadHost.cs`，75 行）

`AttachGlobalMouseWheelVolume` / `Root_PointerWheelChanged` / `AttachRootKeyboardAccelerators` /
`Root_KeyDown` —— 四条都挂在 `Content is UIElement root` 上，`Content` 恒 null，
所以「全窗口滚轮调音量」「空格暂停 / 左右方向键 ±5s」从网页壳接管那天起就**没有生效过**。
对应设置项 `GlobalMouseWheelVolume` 的 `scope` 已是 `native`（网页设置页不显示），没有误导用户的入口。

> ⚠ 这一条是**功能欠账**而不是纯清理：如果用户要「空格暂停 / 方向键快进」，
> 正确做法是在网页侧 `document.addEventListener('keydown')` 里判断焦点后走 `shell` 传输命令，
> 不建议再接回宿主的根元素事件。

---

## 三、为什么垫片类型本体这一轮不删

`Microsoft.UI.*` 现在是这样被用的（非垫片文件里 `Microsoft.UI.*` 出现次数）：

- 前 10 名：`MainWindow.xaml.cs` 29、`MainWindow.Playback.cs` 17、`Playback3` 14、`Playback2` 12、
  `Search` 10、`Playback4` 10、`NavigationStyleHost` 10、`Misc2` 10、`Lyrics` 10、`ContextMenu` 10
- 合计 **92 个文件**，其中真正的大头是 `DispatcherQueue`（41 个文件、140 处）和
  `SolidColorBrush`/`Brush`/`BitmapImage` 这类「画一个图标/缩略图」用的类型。

也就是说：**控件实例已经清零，剩下的是宿主侧（托盘菜单、任务栏缩略图、桌面歌词、对话框、
零散窗）还在用 WinUI 形状的类型名写代码。** 真要删掉垫片，等于把这 92 个文件改写一遍原生类型，
必须分轮做、每轮都要实机验证桌面歌词/托盘/任务栏三块 —— 这是下一阶段（W14 或独立任务），
不是「收尾」。

---

## 四、验证证据（2026-11-01 03:1x）

- 全量 Rebuild：`0 错误 / 454 警告 / 25.3s`；三判据 `errorCS=0 / errorMSB=0 / fatal=0`。
- 启动冒烟（出货产物 `dist/web-ui-20261028`）：`SMOKE=PASS`
  - ERR 行 = 0
  - `library sent tracks=4096`
  - `MainWindow_Loaded begin` 恰好 1 次
  - `启动加载遮罩已撤下` 有结论
- 出货目录已同步；旧 DLL/PDB 回退点：`dist/web-ui-20261028-pre-w13-dialogfix-20261101/`。

---

## 五、遗留（已写进 `outputs/待办清单-2026-11-01.md`）

1. **关闭策略没接上**：主界面现在是 WinForms 宿主窗口，点 X 直接走 WinForms 默认关闭 →
   进程退出。`AppWindow_Closing`（缩到托盘 / 退出 / 每次询问）挂在「旧主窗口」的 AppWindow 上，
   新宿主压根不会触发它。`AppClosePreferences` 与托盘图标都还在，缺的是把关闭事件接到新宿主。
2. **垫片类型本体**：见第三节。
3. **全局快捷键**：见第二节末的提示。

---

## 六、教训（写给下一轮的我）

- **`Content` 恒 null 不是「无害」**：所有 `if (Content is Xxx root)` 都变成静默死分支，
  所有 `XamlRoot = Content.XamlRoot` 都变成 RuntimeBinderException。
  扫一遍 `Content` / `XamlRoot` 的引用，比继续删控件更容易找到「真坏掉的功能」。
- **原生对话框不需要 XamlRoot**：`ContentDialog.ShowAsync()` 在垫片里是
  `NativeContentDialogs.ShowNativeAsync()`（WinForms + `PickerHostHandle()` 做 owner）。
  任何「等 XamlRoot 就绪」的守卫都是**纯有害**的遗留。
- **删除任何 handler 之前先问：它挂在哪棵树上？** 树不存在 ⇒ handler 从未执行，
  删掉是清理；但如果它对应一个用户可见功能（空格暂停），要在报告里明确记为欠账。
