# W6 第一小步：设置读写边界

codex 在 TASK-2026-10-28-thin-host-poc.md 里写明「下一阶段为 W6 服务迁出，可先从设置读写边界开始」。
用户 23:26 让我接手，23:31 选了这一项。

## 结论

设置读写这条链**整条都不依赖 WinUI/WinRT**，可以放进非 WinUI 宿主。PoC 编译 + 隐藏自测通过
（`dist/thin-host-poc`，报告 passed=true）：

```
Settings boundary: probed=61 cloneMismatch=0 schemaMismatch=0 defsMissingReader=0
```

## 为了让边界成立，改了什么

| 文件 | 改动 |
|---|---|
| `AppSettingsStore.cs` | 删掉历史残留的 `using Windows.Storage`（文件读写早就走普通目录）；`Clone` 由 private 改 internal，给自检用 |
| `AppClosePreferences.cs` | 去掉 WinRT `ApplicationData` 分支，统一用普通用户目录 —— 它和 AppSettingsStore 各写一处正是"保存正确却读回默认"的温床。未打包运行时那条分支本来就会抛异常，实际路径没变 |
| `SrcOptions.cs`（新） | SRC 质量档位/抖动模式的常量真值搬到这个不依赖 NAudio 的文件；`ResamplingSourceProvider` 保留同名常量指向它，老调用点不用改、值不变 |
| `PlaybackSnapshot.cs` | `PlaybackOrder` 枚举从 `MainWindow.xaml.cs`（8000 行界面文件）移到这里。命名空间没变，调用点无需改动 |
| `AppLog.cs` | `Timer` 改全限定 —— 同时引用 WinForms 的工程里会和 Forms 定时器撞名 |

## 新增机器检查（`tools/ThinHostPoC/SettingsBoundary.cs`）

两项，都是「以前靠人记、现在靠机器」：

1. **Clone 漏拷**：本项目出过多次「存进文件了，读回来却是默认值」，根因都在 `Clone()` 少拷字段。
2. **字段表三处同步**：`Defs`/`Readers`/`Writers` 少一处，网页上那一项就改不动或读不到。

### 第一次跑的结果（重要）

首轮报了 9 个「Clone 漏拷」，逐个查证后**全是误报**：

```
MiniPlayerBackground / ScatteredPlayBackground / LyricSourcePriority / WaveColorMode /
WaveSolidWhiteness / UiStyleMode / GeekPhosphorColor / GeekIconStyle / PlaylistDensity
```

这些字段在 `Clone()` 里都带**白名单夹取**（如 `MiniPlayerBackground` 只认 Cover/Dark/Frosted/Glow），
我的探针值不合法（"Cover-probe"）被夹回默认，看起来和漏拷一模一样。

修法：先用字段表登记的 `Writer` 写**合法值**再比对（Writer 内部已夹取）；字段表没登记的属性
只报"待确认"，不判失败。改完之后：登记项 61 个，漏拷 0、读写不闭环 0、缺 Reader 0。

**顺带发现**：上面 9 个字段**没有登记在 `WebSettingsSchema`**，也就是网页设置页里看不到它们
（界面风格、极客磷光色/图标风格、列表密度、波形配色、歌词来源优先级、迷你/零散窗口背景）。
这是 W6 下一小步的候选，不在本次范围。

## 没动的地方

- 正式工程的行为：删 using、移枚举都是等价改动；改常量改成引用真值，值不变。
- 用户真实设置文件：自检全程只在内存里造对象，不读也不写 `app-settings.json`。
- 托盘、任务栏条、音频链路一行没动。

## 用户 23:52 拍板：这 9 个字段都不补，继续留在旧界面改

`UiStyleMode`（界面风格）的取值：`Scene` 图景 / `ClassicSystem` 经典·跟随系统（默认）/
`ClassicLight` 经典浅色 / `ClassicDark` 经典深色 / `Geek` 极客终端风。用户当前是 `Geek`。

**不要再把这些字段往 WebSettingsSchema 里加**，W6 这一小步到此为止。

⚠ 一个没解决的观察：极客磷光色（`GeekPhosphorColor`）和极客图标风格（`GeekIconStyle`）
除了极客界面自己的几个文件（MainWindow.Geek*）之外，**旧设置窗口里也没有入口**
（SettingsWindow / SettingsPanelControl 都没引用）—— 目前只能手改配置文件。
已告知用户，他暂时不动。以后要调这两个再说。

用户当前的真实取值（供以后对照）：UiStyleMode=Geek、GeekPhosphorColor=Amber、
GeekIconStyle=Icon、PlaylistDensity=Comfortable、WaveColorMode=Solid、
WaveSolidWhiteness=0.41、LyricSourcePriority=ExternalLrc、MiniPlayerBackground=Cover、
ScatteredPlayBackground=Frosted、EnableFrostedGlass=True。
