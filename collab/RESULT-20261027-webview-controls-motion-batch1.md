# WebView2 控件统一与动效：第一批 + 第二批前半 实施记录

日期：2026-10-27。方案见 [RESULT-20261027-webview-controls-motion-plan.md](C:/Users/MSI/source/repos/CelesteMusicPlayer/collab/RESULT-20261027-webview-controls-motion-plan.md)。
提交：`00e9bd1`。交付：`dist/web-shell-dsp-tags-complete/`（自包含 677 文件）。

## 这一轮做了什么

### 控件（方案 §3、§8 第一批）

**下拉改成"原地增强原生 select"** —— 这是这一轮最关键的设计决定，和调查文档里设想的做法不同。

原设想是"藏起原生 select + 插一个自绘按钮"。实测这条路会砸布局：
标签表的排序规则是 `grid-template-columns:minmax(100px,1fr) 90px 60px 60px`，
DSP 的 `.dsp-select` 是 `grid-column:2/-1`，插一个兄弟节点就会多占一格；
设置页的 `.set-control`、网络页的 `.set-row>select` 也是各自有位置规则。

现在的做法：**原生 select 就是触发器**。它保留值、尺寸、位置、读屏语义和 Tab 顺序，
我们只接管外框、箭头、焦点圈；点开时用自绘 listbox 接管展开层。
读屏与 Tab 顺序里只有一个控件，不会出现"看不见的原生控件 + 自绘按钮"两个。

连带好处：不用再维护"select 的值镜像到按钮文字"那套 setter 包装，
原生 select 自己显示当前值，代码少了一大半。

| 文件 | 作用 |
|---|---|
| `ui-controls.js` | 下拉组件（原地增强）、共享模态、复选框/开关/滑杆/输入框的令牌层 |
| `ui-controls.css` | 控件层样式，**加载在所有页面样式之后**（index.html 最后一行） |
| `popup-manager.js` | 共用浮层：层级、贴边翻转、外部点击、Esc、焦点恢复 |
| `motion.js` / `motion.css` | 动效控制器与语义时长/位移令牌 |

其它控件侧：
- 复选框/开关/滑杆/输入框统一到 `--input-bg/--input-line/--focus/--hover/--pressed` 等令牌；
- 标签页的原生 `confirm()` 换成页面内共享模态（只有最上层响应 Esc，焦点还给触发者）；
- 标签的"显示列"选择器从左栏面板挪进共用浮层 —— 之前它被左栏自己盖住（冒烟脚本点"显示注释"时被拦截就是这个）；
- 标签表头"全选"支持**部分选中**（indeterminate）状态；
- 清掉硬编码粉色焦点框：`tags.css` / `dsp.css` / `settings.css` / `shell.css` 四处换 `--focus`；
- 字符冒充图形改 SVG：专辑/歌曲封面占位音符（`albums.js` / `shell.js` / `transport.css` / `songs.css` / `library.css`）。

### 动效（方案 §4、§7、§8 第二批前半）

- `motion.js` 是唯一的时长与幅度来源：页面转场、浮层进出、列表 FLIP、按压回弹都走它。
  同一元素上的新操作会取消旧动画并推进 generation —— 旧动画结束不会把刚打开的层关掉。
- **速度语义换了方向**：预设里存的是旧的"时长倍率"（大 = 慢），网页要的是"速度"（大 = 快）。
  宿主派生时取倒数换算，旧的个人预设也按同一规则转换，不静默颠倒用户偏好。
- 宿主新增动效偏好：`motionMode`（跟随系统/完整/轻量/关闭）、`motionSpeed`（0.6–1.6，越大越快）、
  `motionStrength`（0–1.25）、`motionBounce`（克制/柔和/灵动）。
  **单独存放，不随外观预设切换** —— 换配色不该把用户调过的动效重置。
  旧的两条覆盖项（`light:motion-speed`）从外观页数值表里移除，改由新通道接管。
- 外观页新增「动效」卡：模式四选一、速度/幅度两个滑杆、回弹风格三选一、演示按钮。
- 页面转场接进控制器：`shell.js` 的 `navigate()` 走 `CelesteMotion.page()`，
  返回键反向，`pendingDirection` 记方向。
- 顺手修了一个隐性 bug：`--motion-continuity` 和 `--motion-fast` 这两个 token **从来没定义过**，
  `library.css` 与 `transport-panels.css` 里 30 处 transition 因此整条作废 ——
  专辑卡片、音频面板的悬停/按下一直是瞬变。补上别名后这些过渡才真的在动。

### 验证

新增两个冒烟脚本（`tools/WebShellControlsSmoke.cjs`、`tools/WebShellMotionSmoke.cjs`）：

- 下拉：7 个静态下拉全部接入统一控件、点击展开出自绘 listbox、值会变、层会关、焦点回到 select、
  纯键盘（Enter / 方向键 / Esc）可完成、禁用态点不开、动态插入的下拉自动接入；
- 模态：Esc 只关最上层、焦点还给触发者；
- 复选框：未选/选中/部分选中三种状态底色与图形都对；
- 动效：模式与倍率吃宿主给的值（速度 1.5 → 时长倍率 0.667）、轻量模式位移归零、
  关闭模式时长压到 0.001 但界面不消失、外观页「动效」卡结构完整、切页确实触发动画。

既有的 TagsWorkspace / Nav / PlayerBar / Typography / Sources 冒烟全过；Release 编译 0 错误；
单次启动验证：`[WebShell] initialized`、theme revision=37、曲库 4031 首，窗口内无新异常。

**已知的历史失败，与本轮无关**（改动前就存在）：Background 冒烟找不到「背景」卡片；
State 冒烟断言颜色格式（新版 Edge 序列化成 `rgba(...)`）；Albums/DspTags/Bridge 只 require
`playwright`（本机只装了 `playwright-core`）。

## 这一轮没做的（留给下一批）

- 方案 §8 第二批剩下的：分类开关（导航/弹窗/列表/控件/主题背景分别减）、回弹风格的完整三档手感调校、
  轮盘与底栏面板接入统一时长。
- 第三批：列表/DSP/标签工具/网络页的逐项状态动画（异步反馈、失败回滚、进度与结果展开）。
- 第四批：歌曲详情页与底栏共享封面转场。
- 宿主那个「添加到播放列表」仍是 WinUI 的 ContentDialog，没换成网页内共享模态（需要宿主数据配合）。
- 分类开关这一项在宿主里还没有字段，本轮只做了模式/速度/幅度/回弹四项。

## 踩到的坑（留给以后）

1. **原生控件不要藏起来再插兄弟节点** —— grid/flex 一格放不下。原地增强最省事。
2. **统一控件层要放在所有页面样式之后加载**，否则页面里 `background:` 简写会顺手清掉我们画的箭头图
   （箭头用 `background-image:...!important` 单独钉住，底色和边框仍归页面管）。
3. **同一个 select 可能被扫两次**（自动挂载先扫到、模块随后带参数调用）——
   `enhanceSelect` 要能把新参数**并进**已有实例，而不是直接返回旧的。
4. `file://` 下 `document.styleSheets` 读 `cssRules` 会抛安全错误 → 想扫 CSS 里的硬编码颜色，
   得在 Node 里直接读文件，别指望在页面里读样式表。
