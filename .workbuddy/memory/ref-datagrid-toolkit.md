# toolkit DataGrid 硬事实（改标签页表格前必读）

> 来源：2026-10-16 扒 CommunityToolkit.WinUI.UI.Controls.DataGrid 7.1.2 源码坐实。
> 源码位置：GitHub `CommunityToolkit/WindowsCommunityToolkit` 分支 `rel/winui/7.1.2`，
> 路径 `CommunityToolkit.WinUI.UI.Controls.DataGrid/DataGrid/DataGrid.xaml` 与 `Themes/Generic.xaml`。

## 行选中 / 多选
① **行选中挂在 `Tapped`**（`AddHandler(..., handledEventsToo: true)`，子元素拦不住）。
   `UpdateStateOnTapped` **对交互式子元素零豁免**——点复选框时 CheckBox 的 Click 先跑、
   Tapped 随后把焦点行改成当前行，把多选冲掉。
   本项目多选方案 = `SelectionMode=Single`（只管焦点行）+ `_checkedSet` 自管 +
   **指针按下哨兵**（网格级 AddHandler PointerPressed，判 `OriginalSource` 是否在 CheckBox 内；
   本手势内无视连带 SelectionChanged，每次按下重判）。

## 行选中底色
② 模板 `Rectangle x:Name="BackgroundRectangle"` 的 Fill 绑主题资源、选中态动画写
   `(Fill).(SolidColorBrush.Color)`（DataGridRow 模板）。
   **设 `row.Background` 完全无效**；唯一杠杆 = `TagFileGrid.Resources` 覆盖四个
   `DataGridRowSelected*BackgroundColor` / `...Opacity`。圆角用 Rectangle 自带 RadiusX/RadiusY。
③ ⚠ **绝不碰 `BackgroundRectangle.Fill`**：动画按"模板原始那个画刷实例"定位目标属性链，
   运行时替换实例或 `ClearValue` → 动画 Begin 解析失败 → `COMException 0x80040154` 崩
   （2026-10-16 实测；"每行一支新画刷防串色"的设想不成立）。

## 勾选行的底色（非焦点行）
④ 勾选行（非焦点行）底色走**每个 `DataGridCell` 的 `Background`**：
   模板里 `CellRoot.Background` 是 `{TemplateBinding Background}`、**没有任何动画碰它** → 安全。
   按 `_checkedSet` 上色、取消勾选 `ClearValue`。找 cell 用自写可视树遍历
   （`DataGridCellsPresenter` 是 ItemsControl 派生，**别依赖 `.Children`**）。
   行回收（`UnloadingRow`）时一并清掉 cell 的本地 Background，防复用行串色。

## "点一下单元格冒出很显眼的方框"
⑤ = `DataGridCell` 模板 `CurrentStates` 的 `CurrentWithFocus` 态把 `FocusVisual`
   （2px 主框 + 1px 次框）拉到不透明。关法：把 `DataGridCellFocusVisualPrimaryBrush` /
   `DataGridCellFocusVisualSecondaryBrush` + `DataGridCurrencyVisualPrimaryBrush` 覆盖成透明
   （Default / Light / HighContrast 三个主题字典都写，**只改资源值**，不碰模板元素与动画）。

## 窄列
⑥ 窄列（如 30px 的选择框列）必须单独给 `CellStyle` 收内边距，
   否则勾选框被通用「左 10 右 6」挤扁。

## 列头（自绘 ControlTemplate）
- 默认模板不绑 ContentTemplate → 只换整个 ControlTemplate，里面 ContentPresenter 显式 ContentTemplate。
  保留 `x:Name=VerticalSeparator` / `SortIcon` 部件。
- 点击排序：PointerPressed 存 KeyModifiers + PointerReleased 判位移 >4px 不排序 +
  Tapped 兜底汇到 `SortFromHeader`（500ms 去重）。

## 回归教训（2026-10-14）
标签页整页不显示 = 上一轮崩溃的 XamlCompiler 弄坏发布 dll 内嵌的 TagPanelControl.xaml 资源
→ 运行时 XamlParseException 被 `EnsureTagEditUiBuilt` 吞掉。
修法 = 清 obj/bin 干净重建重发（**非源码错**）。
验证用临时 `new TagPanelControl()` 自检写日志（**禁 UIA 点击**），验后删。
