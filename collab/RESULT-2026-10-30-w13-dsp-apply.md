# W13 DSP 下发迁出与交付

DspApplyService 接收显式 EQ、试听状态、声道及安全参数，负责持久化和经 IDspParameterTarget 下发引擎；AudioPlaybackEngine 实现该窄接口。MainWindow 暂保留控件采集与展示刷新，以及初始化守卫。没有把 Slider/Toggle 或窗口句柄带进服务。

验证：Release/x64 build 0 错误、554 警告；自包含 publish 成功。EqEditRegression 共 25 项检查通过，覆盖 EQ 历史与 A/B，以及服务存盘顺序、试听不保存 EQ、无引擎仍保存参数、FIR 按需加载。真实进程 DSP 初始 get、模块、8 组控件、方案、机架、旁路与 10 步 EQ 编辑/A-B 操作，与提取前逐字段无差异。W13InterfaceRegression 冷启动/重载，4041 首曲库、0 失败、0 JS 错误、正常退出 0。实放探针在 7.1s、13.2s 连续自动切歌，底栏/主页/状态同步。

生产依赖核对：MSBuild Release/x64 的 Compile 清单中 MainWindow partial 共 91 个、47308 行（本轮末）。旧 SettingsWindow/SettingsPanelControl/MiniPlayerWindow/ScatteredPlayWindow/CurrentPlaylistWindow 的 xaml.cs 均不参与编译。原盘点的 79 文件总数与 45+46 分类数不一致，且未区分排除源码引用；不要据此承诺可直接删除 33455 行。本轮只核对相关生产入口，未宣称完成全仓语义可达性分析。

已备份日常 DLL/PDB 到 dist/web-ui-20261028-pre-w13-eq-20261030，并同步固定目录 dist/web-ui-20261028；DLL SHA256 与 publish 一致。没有变更依赖，回退时关闭播放器后将备份 DLL/PDB 复制回日常目录即可。源码第一刀 b3cbb8a，第二刀见本文件所在提交。其他 agent 既有工作区改动保留。

## 剩余工作

整体 W13 未完成。接下来应将 WebDsp 的声道/安全参数读写从控件替身转向统一参数模型，再逐个迁移其他 DSP 模块。当前 DspApplyService 接口已支持显式参数，但参数采集仍在 MainWindow，不能表述成整个 DSP 域已脱离旧壳。随后继续睡眠定时/AB 循环/输出设备域与播放快照化；生产入口仍依赖隐藏 MainWindow，未做干净机验收。
