# Twilight 仪表主题、全屏与队列拖动

## 本轮已实现

- 播放详情页右上新增主题选择：默认封面/歌词与 Twilight 音频仪表。选择记在当前网页壳的本地偏好中，独立于 Windows/Apple 外壳预设。
- 参考用户截图及本机 `Twilight_Echo-main/resources/audio-visualizer/index.html`、`AudioVisualizerPanel.vue`，用 Celeste 自己的 DOM/SVG 重建网格、圆形刻度、方封面、红色仪表线、音频信息与控制区。不引入 Vue/Electron，不直接运行外部页面脚本。
- 频谱调用现有 post-DSP `TryGetSpectrum`，瞬时波形调用 `TryGetSamples`。仅详情仪表主题可见、宿主可见且实际播放时以 100ms 轮询；只允许一条未完成请求；暂停、收起、切主题、窗口隐藏即停止。异步回包校验页面世代与曲目身份。
- 频谱使用分析器已有对数分频、倾斜显示补偿的相对幅值，不将其冒充标定 dB；RMS 标为瞬时值，不冒充 LUFS 或动态范围。取不到采样时清空图形并说明，不用随机动画伪造音频。
- 分类轮盘相对整个外壳定位。修正背景层令工作区变成 relative 定位祖先的问题，圆盘开关上移到距外壳顶边约 20px 的区域，保留间隙。
- 主界面与详情页右上新增全屏按钮；F11 切换、Esc 优先退出全屏。WinUI 宿主使用真正的 FullScreen presenter，并保存原窗口 presenter 与最大化状态以恢复。
- 播放队列支持拖动排序：整行拖动、拖动握柄、插入线、滚动边缘辅助；Alt+上/下也可移动。请求包含队列 revision、来源序号与曲目身份，拒绝过期队列操作。
- 队列修改的是原有 ObservableCollection，发一个 Move 通知；保留当前播放条目的准确序号，包含 CUE 分轨和重复歌曲；不重启音频。

## 验证与交付

- Release build 0 错误；首次构建为 172 条既有警告。
- 发布重试时遇到 XamlCompiler / Roslyn Internal CLR error；关闭构建服务、禁用共享编译等未解决。最终将 `CelesteMusicPlayer/obj` 完整备份至 `outputs/twilight-build-obj-backup-20261027/` 后使用新缓存发布，退出码 0。未删除原缓存，未修改 SDK 或系统配置。
- 发布完整性：55/55 网页资源与源码一致，VERIFY_OK；DLL 与本轮构建的 MD5 均为 `b087731f231007b20612bee0f36054af`。新发布已原生启动，窗口标题为“Celeste · 网页界面”，进程正常响应。
- 独立产物：`dist/web-shell-twilight-preview/`。原修复版、此前 DSP/标签发布目录均保留。
- 启动入口：`tools/OpenTwilightPreview.cmd`。点底栏封面，右上选择 Twilight 主题。
- `WebShellTwilightSmoke.cjs`：真实 HTML 拖放、键盘排序、旧 revision 拒绝、当前播放标记、全屏/F11/Esc、三种宽度、深浅色、采样绘制、切主题停止请求、暂停归零，全部通过。
- `WebQueueRegression`：150 个移动/播放索引组合及 CUE、重复条目、空闲播放索引、单次 Move 通知，通过。
- `WebShellNowPlayingSmoke.cjs`、`WebShellPlaybackRepairSmoke.cjs`、`WebShellBridgeSmoke.cjs`、`WebShellNavSmoke.cjs` 通过。
- 自动化网页截图中的封面与信号是测试夹具；实际网页宿主的数据接口直接使用现有音频引擎。真实音源的动态表现、特定输出设备无采样的情况需实机试听确认。

## Windows / Apple 方案

详细方案位于 `collab/PLAN-20261027-platform-materials.md`，包含材质层级、浅深色数值起点、边缘高光、透明/模糊强度、圆角、字体、控件、动效、可访问性、性能、宿主及网页实施落点与验收矩阵。本轮没有把该方案中的整套材质改造称为已实现。

ECHO 的 `LyricsPage.tsx` 确实已有 `lyrics-music-reactive-layer`，包含 wash 与 12 条频谱，受开关和性能策略控制；这与本次截图对应的 Twilight 音频仪表是两种页面。
