# 实时 DoP 独占事件提交修复

用户要求：修复关闭整轨 WAV 预缓存后 DSD64/128 频繁断音，再由用户试听。

代码问题：NativeWasapiExclusiveOut 以 EXCLUSIVE + EVENTCALLBACK 初始化，却在轮询超时也按 padding 部分提交；预填充可能提交不足整缓冲；ReleaseBuffer 失败未检查。此行为不符合独占事件流的整包要求。参考：https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getcurrentpadding 。这是已确认的实现错误，是否解释全部可闻断音仍以试听为准。

修改：仅设备事件到达才提交一个完整端点缓冲；3 秒无事件作为失败处理；对齐重试时保持周期等于缓冲；预填充始终提交整包，累计帧数包含预填充；检查 SetEventHandle、Reset、GetBuffer、ReleaseBuffer，失败保存 HRESULT 并触发失败事件。统计改为事件整包次数，不再用 padding 水位冒充健康指标。DSF 解析、DoP 位序、DSP 算法和设备配置均未更改。此输出器也供旧 PCM 独占路径使用；新 native2 输出器不受修改影响。

验证：tools/DsdRealtimeProbe 直接编译正式输出器和 DSD 数据源，在本机已配置设备上各播放 12 秒。DSD64 / 176400Hz：整包17640帧，最大事件间隔100.9ms，真欠载0，DoP补静音0；DSD128 / 352800Hz：整包35280帧，最大事件间隔100.9ms，真欠载0，DoP补静音0。两者源进度12.20秒（含已排队缓冲），数据源标记抽样合法。日志 outputs/dsd-event-probe.log。探针不代表人工听感验收。

交付采用独立自包含 publish，验证后同步当前 dist/web-ui-20261028。旧文件逐个备份并附新增文件清单。保持用户 DsdPreloadEnabled=false，已有缓存保留。

用户验收：从当前 EXE 启动，DAC 的 WASAPI 独占 + DoP、DSP关闭，分别播放原始 DSD64/128 DSF 至少一分钟，并检查暂停恢复、切歌和拖进度。记录 DAC 是否保持 DSD、是否短暂断音。若仍断音，对照新日志的事件间隔、HRESULT、DoP补静音及位置继续定位，不据健康计数否定听感。

交付结果：自包含 publish 首次遇 XamlCompiler 访问冲突，重试成功。当前 dist 更新6个差异文件，旧文件备份 outputs/dsd-event-backup-*/manifest.json；主 DLL SHA256 与 publish 一致。当前 EXE 启动存活，网页标题 Celeste、进程响应正常，保留窗口供用户测试。用户配置仍为 WasapiExclusive / Dop / DsdPreloadEnabled=false。尚未宣称人工听感通过。
