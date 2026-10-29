# 完整网页样式包：后续实施方案

状态：方案已记录，按用户2026-10-29最新指令排在零散播放器重构之后。本期不实现运行时，不宣称当前迷你JSON包已满足此目标。

## 目标与范围

作者使用HTML/CSS/JavaScript制作完整页面，使用者本地导入、预览、应用、导出。可选择显示封面、歌词、歌曲信息、播放按钮等，可自由组织页面、背景和动画。程序提供数据及动作，页面不另起音频引擎。首期不要求可视化拖拽编辑器，也不建设在线商店/Steam分发。

面向 detail（主播放详情页）、mini（主会话控制视图）、quick（零散独立会话）。一包可含多个view，但必须分别声明入口和适用尺寸，不把一份大页面强行缩小。桌面歌词已有数据预设继续使用，是否加入网页包另行排期。

## 已确认参考及边界

本机Steam工坊5105090下：3816161858物语与3808907473 AM均是HTML/CSS/JS + theme.json，runtime.presentation=lyrics-view；3795920995是lyrics-background并订阅audio:spectrum。ECHO包展示了网页运行时、能力声明和消息桥接；社区源码未发现同一workshop-ui协议加载实现。可借鉴协议设计，不承诺直接加载ECHO包。Apple Music包声明All-Rights-Reserved；官方示例应自行创作，物语MIT素材复用也须保留署名与许可。

## 包结构及版本（拟定，非现有接口）

扩展名 .celeste-player，内容ZIP：manifest.json、views/detail/index.html、views/mini/index.html、views/quick/index.html、assets/、preview.webp、可选options.schema.json。manifest含format=celeste.player-page、schemaVersion、apiVersion、id/name/author/version/license、各view入口/最小及推荐尺寸、呈现模式、capabilities。普通完整页与只提供背景分开声明。

SDK是宿主按声明绑定的版本化接口。提供自带模板、字段说明、模拟数据预览器和打包检查工具；作者不必修改播放器源码。先发布v1固定字段，后续增量能力协商，不把所有当前MainWindow字段固化成公共API。

## 数据与动作

- track：sessionId/trackId/generation、歌名/歌手/专辑、受控封面地址及真实格式信息。
- clock：position/duration/state/rate/canSeek、单调时间锚点、seekGeneration；CUE时间相对分轨，更新立即纠正本地插值。
- lyrics：文档版本、行ID、时间、原文、译文、罗马音与可选逐字时间；缺数据显式标记，仅换文档时传全文。
- appearance：全局语义色、明暗、视口、可见性、减少动画、作者选项。
- spectrum/energy：显式订阅的降采样结果，不传原始PCM，不重新解码；隐藏/卸载自动取消，帧率与点数设上限。
- actions：播放暂停、前后曲、seek、音量、顺序、打开宿主队列；逐项能力校验、requestId应答。mini绑定主会话，quick绑定自己的会话。收藏/曲库能力在quick不可用时缺省隐藏。

宿主可提供可选封面/进度/歌词绑定组件，作者仍可自绘；不强制用指定按钮图形或固定DOM。SDK封装时钟和逐字基础逻辑，避免每个作者重新处理切歌/时间校准。

## 运行与生命周期

可信外壳负责窗口按钮、主题管理和恢复默认入口。作者页面放独立受限上下文；宿主只转发当前实例、当前会话、当前generation的白名单消息。网页不获得chrome.webview通用访问，不直接获得文件、输出引擎、DSP或账户对象。具体WebView2 frame桥接/CSP/MessageChannel方案先做隔离PoC，验明后固化，不能仅凭sandbox属性就声称隔离完成。

load→ready→activate→update→deactivate→dispose。握手超时、脚本异常或不兼容时保留默认界面，用户可随时从外层恢复。图片、视频、字体为包内资源；默认禁止外网脚本。包入库前检查路径越界、符号链接、重复路径、解压后大小、文件数/资源上限，临时校验后原子安装。

资源安装目录只读；选中主题、作者选项和主题私有存储另存，按包ID/view隔离。重复ID不同内容安装成新版本或副本，不无提示覆盖；新版本ready后才切换，保留旧版回退。导出不含曲库、凭据和本机路径。主题界面失败不能改变音频播放状态。

## 三种窗口的约束

- detail：占主壳详情区，返回入口在外层，不能覆盖全应用导航和关闭按钮。
- mini：主会话视图；窗口拖动区与按钮/滑条分离，声明大小范围，可选适配窄尺寸，置顶/恢复主窗由宿主执行。
- quick：独立临时队列；先使用固定底部控制栏。将来样式声明controls=host或controls=page，宿主保证只有一套控制；切换页面不重建播放会话。

## 分阶段交付与验收

P0 契约/模板：冻结manifest和数据版本；三个默认界面能用同一数据适配概念，原JSON预设继续可用。
P1 运行时：隔离PoC、主题资源服务、握手/卸载/回退；两个自主制作且构图不同的detail示例，切主题不断播。
P2 本地库：安装/预览/应用/更新/导入导出、作者选项、私有存储；干净配置导入后复现。
P3 mini与quick：分别接主会话/零散会话；布局明显不同的小窗实例，窗口尺寸/DPI/拖动/关闭及控制归属验证。
P4 音频可视化与作者工具：按需订阅、性能预算、模拟状态/歌词、兼容检查与教程。可视化拖拽编辑器仅未来另立需求。

每期单独构建、提交、备份后同步固定入口。验收覆盖快速切歌/seek/倍速/CUE、无歌词封面、长标题、不可seek源、宽窄及100/150/200%DPI、隐藏资源释放、坏包/失效消息/越界路径拒绝。P1–P3完成前不得以JSON改色包称为完整页面包。

与旧文档冲突时以本文件为准：RESULT-20261027-custom-now-playing-themes-plan和RESULT-20261028-auxiliary-players-lyrics-rebuild中的可视化编辑器不再是首期前置，高级包首期明确支持受限上下文内的作者JavaScript。
