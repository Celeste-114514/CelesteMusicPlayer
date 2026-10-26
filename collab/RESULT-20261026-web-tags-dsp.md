# 网页标签页与 DSP 页：调研结论及 WorkBuddy 交接

日期：2026-10-26。性质：方案，未改产品源码。来源项目只作为实现与交互参考；本方案以 Celeste 当前原生 UI、数据模型和音频内核为准。项目采用多 agent 并行维护，`collab/LOCK.md` 已停用，不应把它当成源码修改阻塞条件。

## 1. 先纠正旧方案中的基准

`outputs/WebView2迁移方案-标签页与DSP-2026-10-26.md` 把 `EqualizerWindow.xaml`（独立的旧十段 EQ 窗口）称作“用户满意的 EQ 页”，这个定位不对。主窗口真正的 DSP EQ 在 `CelesteMusicPlayer/MainWindow.xaml:3960-4745` 和 `MainWindow.EqDsp.cs:431-1010`：对数频率轴、自适应 dB 轴、渐变响应曲线、可拖节点、悬停读数、post-DSP 实时频谱、简易/高级模式，以及撤销/重做、A/B、自动增益、预设和 APO 导入导出。网页应以这套外观和操作为验收基准。ECHO 的 `EqCurveView.tsx` 适合参考 SVG 几何和指针坐标转换，不应覆盖 Celeste 已经做好的设计。

旧方案还说 `WebAudioSettingsAsync` 通道“已经通了”，这只适用于输出模式、设备、速度、睡眠、设备 DSP 配置等现有操作（`MainWindow.WebTransport.cs:85-162`）。`WebShellWindow.cs:178-205` 尚无 `tags` 或 `dsp` 命令；`index.html:71-72` 只有入口，导航时仍落到 `category-page` 占位页（`shell.js:96-105`）。标签和 DSP 的网页桥接需要新建，不能把已有 audioSettings 当作功能已迁移。

## 2. 两个参考项目各取什么

| 来源 | 代码证据 | 借鉴范围 |
|---|---|---|
| Kid3 | `src/gui/forms/kid3form.cpp:291-386,463-597` 将文件/目录选择、标签帧、封面组织到可分隔的工作区；`filelist.cpp:228-244` 处理列表交互 | 保留“选文件→看标签→修改→明确保存”的工作流和批量选择语义。Celeste 的标签页已是 Kid3 思路的本地版本，不必照搬 Kid3 的 Qt 控件或 Tag 1/Tag 2 分栏。 |
| ECHO | `src/renderer/pages/DspPage.tsx:46-50` 给 DSP 模块独立 ID；`components/audio/EqCurveView.tsx:301-359,378-460` 计算响应并绘 SVG/处理拖动 | 借鉴模块导航、状态卡片与 SVG 技术。Celeste 的 EQ 可视化优先；不移植 ECHO 六千多行的页面结构。 |
| Celeste | `TagPanelControl.xaml:148-171,354-613,1347-1630` 为左侧六个工作面板、右侧表格；`TagEditorService.cs:10-151,190-337` 为字段脏标记与读写 | 网页功能对齐现有 UI；后端复用现有服务。 |

参考仓库各有许可文件（ECHO 的 `LICENSE` 为 AGPLv3，Kid3 的 `COPYING` 为 GPLv2）。实现时记录参考来源，优先重写交互和样式，不直接复制大段源码或素材。

## 3. 页面结构建议

### 标签页

保留当前左侧工作区、右侧曲目表格。左侧六个子页按原生功能分组：快速编辑、批量工具、排序编号、在线标签、歌词下载、批量歌词。顶部固定“保存 / 还原”和当前选择数量；选曲切换时提示未保存草稿。右侧第一版保留原表格的列、排序、多选、列宽与单元格编辑需求，**不改成卡片列表**：原表格 `TagPanelControl.xaml:1347-1630` 已有这些行为，降级会影响日常效率。可以分阶段先实现快速编辑及表格，再接批量与在线能力；过渡阶段入口可回到原生标签页。

数据边界：网页使用单独草稿 `{trackId, baseRevision, values, dirtyFields}`；多选草稿只发送被明确改动的字段，空字符串须能表达“清空”，未改字段不参与写入。后端由 `trackId` 解析已知曲库路径，重新核对磁盘修改时间或修订号，再调用 `TagEditorService.TrySaveTag`。服务的 `Dirty` 为空会跳过写盘（`TagEditorService.cs:124-134,223-241`）。批量写入还须沿用原生的备份/失败回滚语义（`TagPanelControl.xaml:175-196` 的用户提示）；不要仅循环调用单曲保存后报告“全部成功”。封面经独立受控操作读写，不把图片字节塞进普通草稿 JSON。

### DSP 页

采用“机架概览 + 模块导航 + 当前模块编辑区”。模块名与顺序读 Celeste 现有 `MainWindow.DspNav.cs` / `DspRackState.cs`，不要直接拿 ECHO 的模块 ID 作持久化键。第一阶段先做机架总开关、EQ 状态和 EQ 编辑；下一阶段按原生已有页面扩展 Headroom、SRC、FIR、声道、ReplayGain、安全、压缩、声场、矩阵和机架顺序（原生落点见 `MainWindow.xaml:3302-6563`）。未接桥的模块显示“在原生界面打开”，不放无效滑杆。

EQ 图层顺序照 `MainWindow.EqDsp.cs:623-750`：频谱底层→dB/频率网格→渐变填充与曲线→节点/读数。网页 SVG 只承担绘制与交互；响应计算公式、范围和滤波类型由 Celeste `EqCurveState` / `MainWindow.EqDsp.cs` 的语义决定。频谱取 `_audioEngine.TryGetSpectrum`（`MainWindow.EqDsp.cs:1830-1870`），仅在 DSP EQ 页可见且用户打开频谱时推送，页面隐藏立即停推；未播放时平滑落零。先验证 WebView2 消息频率和渲染开销，再定最终刷新率。

## 4. 桥接与状态约束

| 操作 | 建议协议 | 关键约束 |
|---|---|---|
| 标签读取 | `tags/get`、`tags/list` | 分页列表；只接受后端已知曲目 ID；返回字段和修订号。 |
| 标签保存 | `tags/save`、`tags/batchSave` | 只写 dirtyFields；批量操作回传逐文件结果；冲突时保留网页草稿并要求用户决定。 |
| 封面 | `tags/cover` 系列 | 使用受控文件选择与后端读写；更新网页封面缓存。 |
| DSP 快照 | `dsp/get` | 返回设备/模式、机架开关及各模块状态、修订号；明确哪些模块被旁路。 |
| DSP 修改 | `dsp/preview`、`dsp/commit`、`dsp/reset` | 拖动时前端即时绘制、后端节流试听；松手再提交持久化。请求带递增序号，旧回包不能覆盖较新的本地值。 |
| 频谱 | `dsp/spectrumStart`、`dsp/spectrumStop` | 仅可见时推送；数据量有上限；停止后不保留后台定时器。 |

把新命令挂到 `WebShellWindow.cs:144-210` 已有 `celeste.command`/`celeste.reply` 协议，按 `MainWindow.WebBrowse.cs` 的分域写法拆为 `MainWindow.WebTags.cs` 与 `MainWindow.WebDsp.cs`。WebView2 前端分别用 `tags.js/css`、`dsp.js/css`、`dsp-eq.js/css`，避免堆进 `shell.js`。读写均验证参数范围、trackId 和修订号；写入须有失败反馈。`MainWindow.WebTransport.cs` 的 `_webSettingsBusy` 只保护现有音频设置，不自动覆盖 DSP 新命令。

## 5. 分期与验收

1. **静态原型**：两页替换占位，保留现有壳的导航、字体、主题与响应式宽度。DSP 的 EQ 曲线先用固定假数据完成布局；标签页用假数据验证表格密度、长文本和多选。无写盘。
2. **DSP EQ 可用**：接当前曲线/预设/开关/拖动/撤销 A-B/频谱；与原生页对照同一预设的曲线、声音和状态。切设备、切歌、关页面后无滑杆回跳或后台频谱推送。
3. **标签安全闭环**：单曲读取、改一字段、保存、还原、重新打开核对文件；再做多选部分字段、封面、批量失败回滚。用测试音乐副本验证文件修改时间：无 dirty 字段时不应改动文件。
4. **扩展其余 DSP 与标签工具**：逐模块/逐子页接入；每一块单独提交、构建与实机冒烟。保留原生页面入口直到相应网页能力验收。

## 6. 交给 WorkBuddy 的机械工作

以下是最初的交接清单。用户已停用源码独占锁，WorkBuddy 接手时查看当前 diff 与任务记录即可，不需要申请锁。后续实际实现与验证见 `VERIFY-20261026-web-tags-dsp.md`。

- 从 `TagPanelControl.xaml` 的六个面板和 DataGrid 逐项抄出“控件/现有事件/是否写盘/网页阶段”清单，写入独立 `collab/TASK-*`，只给行号与简短说明，不粘整页。
- 从 `MainWindow.DspNav.cs`、`DspRackState.cs` 和各 `MainWindow.Dsp*.cs` 整理模块 ID、开关、参数范围、store、原生事件处理器，形成桥接映射表；标出需要播放中实时更新的字段。
- 按已确认的结构制作静态 `tags`、`dsp` 原型 DOM/CSS，复用 `outputs/webview2-shell/` 的现有 token 与控件类；准备浅色/深色、窄窗/宽窗截图给用户看。不要自行换掉现有 EQ 的配色、曲线层次或交互。
- 每个独立任务做 JS 语法检查、WebView2 实机点击、`dotnet build`，核对 `CelesteMusicPlayer.dll` 新鲜度；源码只改 `outputs/webview2-shell/` 与对应后端，不改 `dist` 真身。完成后写 `VERIFY-*`。

需要先让用户看静态原型的两个视觉点：标签表格在网页中的行高/列宽，以及 DSP 机架导航与 EQ 曲线的比例。功能与数据语义已有明确方向，可先按上面分期推进。
