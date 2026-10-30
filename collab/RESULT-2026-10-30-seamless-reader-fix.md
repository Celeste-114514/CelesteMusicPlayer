# 自动续播界面冻结修复

根因：SeamlessWaveProvider 切换后释放旧 reader，HiFiOutputBackend.UpdatePosition 在处理 SwitchedToNext 前读取旧 _waveFile.CurrentTime，NAudio 抛 NullReferenceException，通知永远到不了引擎/会话。b8f8f1c 已有这一组合，早于 W13。

修复：先接管新 reader、更新时长并发换轨事件，再读取进度；移除 MainWindow 每次起播的重复事件订阅，保留会话统一转发。

验证：tools/SeamlessTransitionRegression 在修改前准确复现 NAudio.WaveFileReader.get_Position 异常；修改后验证两个 tick 仅通知一次、时长对应新 reader。Release 自包含 publish 成功。真实 W13SeamlessUiProbe：1 秒 Hi!，7.1 秒 Speak to Me，主页/底栏/状态同步更新。

已同步 dist/web-ui-20261028。局部回退备份 dist/web-ui-20261028-pre-seamless-fix-20261030 保存原 DLL/PDB 与设置页面文件；settings.css/settings.js/appearance.js 应放回 WebShell 子目录，DLL/PDB 放根目录。W13 架构迁移仍未整体完成；PROJECT-BRIEF 的 WinUI 主技术栈描述已过时。
