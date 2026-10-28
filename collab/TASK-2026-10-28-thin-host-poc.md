# W5：非 WinUI 薄宿主验证

状态：独立 PoC 已实现、发布和隐藏实测；用户随后回复“已验证”，W5 基础验收通过。此前交付请用户检查的窗口缩放、最小化恢复、任务栏三个模拟按钮按本次确认记录通过；跨屏 DPI 与 Explorer 重启未单独确认，仍待专项验收。

用户确认对应提交469c843。本次确认不代表真实音频/曲库已迁入薄宿主，正式入口继续保持原样；下一阶段为 W6 服务迁出，可先从设置读写边界开始。

位置：tools/ThinHostPoC；运行目录 dist/thin-host-poc；根目录“验证W5薄宿主.lnk”。正式“打开当前播放器”入口及 dist/web-ui-20261028 均未更新。本任务没有主工程、音频服务或托盘菜单改动。

技术路线：WinForms 仅提供 Win32 HWND、标准窗口行为和 STA 消息循环；WebView2 使用 .NET WinForms 封装，不引用 WinUI/Windows App SDK，不调用 Application.Start。虚拟域名本地页面、导航来源校验、消息版本/请求ID/动作白名单，弹窗和权限请求拒绝。原有 W1 PlaybackCommandRouter、PlaybackSnapshot/IPlaybackStateSource、MainWindowCoordinator/IMainWindowSurface 源码直接链接复用。

功能：模拟播放会话的命令/快照，窗口最小化/恢复/关闭，以及绑定测试 HWND 的 ITaskbarList3 进度和3个缩略图模拟按钮。Win32消息 THBN_CLICKED 投递消息循环执行；不拥有实际音频引擎、不读正式曲库/设置，不包含文件关联或托盘迁移。

验证：
- 独立 build / 自包含 publish 成功；移除了未使用的 WPF WebView2 引用，避免程序集冲突警告。
- 第一次隐藏试验发现 PoC COM 类ID错误（使用了接口ID），已按系统定义修正为 CLSID_TaskbarList=56FDF344；重测通过。未据此修改正式工程其它任务栏代码。
- 最终自包含包隐藏实测退出0，报告 `%LocalAppData%/CelesteMusicPlayer-Poc/20261028-232127-23e1f7-20632/result.json` 为 passed=true。
- 真实 WebView2 页面发 snapshot/play/play/next/previous/pause 消息，C# 使用 W1 命令执行并回传；网页验证重复play幂等、切歌变化、暂停、未知命令被拒，最后回传完成。不是浏览器mock。
- 任务栏 API 创建及 HrInit 成功；检查发布 deps.json 与加载模块没有 WinUI/Windows App SDK。关闭测试窗口的日志确认控件及任务栏资源释放。
- 隐藏运行期间没有显示/激活用户窗口，没有调用 computer-use；未验证实际鼠标点击、可见任务栏缩略图、跨屏 DPI 或 Explorer 重启。人工验收步骤在工具 README。

交付物与生产隔离；不覆盖主入口，所有产物与数据路径都有独立名称。下一阶段先完成 W5 实机验收，再以这些边界安排 W6 服务迁出；不能把这次模拟会话说成主播放器已经迁移。
