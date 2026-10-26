# 网页 DSP 与标签页实现记录

日期：2026-10-26。页面源码在 `outputs/webview2-shell/`；本次接续已有 DSP/标签框架开发。

## 已实现

- DSP 的 12 个导航页：听音方案、机架编排、输入余量、ReplayGain、SRC、参数 EQ、压缩器、声道工具、立体声场、矩阵、FIR、输出安全，均有对应宿主服务。
- EQ 独立到 `dsp-eq.js`。响应公式、对数横轴、纵轴档位和渐变沿用 `MainWindow.EqDsp.cs`；支持节点拖动、精细调整、键盘、频段类型/频率/增益/Q、增删/停用频段、预增益、简单模式、预设、撤销/重做、A/B、自动增益、APO 导入导出、真实频谱。频谱在离开页面后停读；拖动试听与最终存盘分开。修正旧桥接丢失滤波类型、不能增删频段、关闭 EQ 又被应用路径强行开启的问题。
- 最左侧节点按用户反馈改为与其他节点相同半径，所有圆心共用频率/增益坐标，字母按圆心居中。选中态仅变描边，避免第一个节点默认放大造成错位观感。
- `dsp-visuals.js` / `dsp-echo.css` 移植 ECHO 的 Headroom 预算、声道偏移条、压缩器衰减表、参数指标卡、FIR 路由、安全路由和矩阵预设，数据接 Celeste 控件及音频引擎。未提供的输入峰值/True Peak 不伪造读数。
- 标签六个子页均有操作：快速编辑、批量工具、排序编号、在线标签、在线歌词、批量歌词。支持添加文件/文件夹、内嵌封面的替换/导出/复制/粘贴/移除、文件名填充、查找替换/正则、大小写、清空字段/文字标签、按标签重命名、编号与步长、在线候选预览与勾选字段、歌词保存位置和目标、批量进度/停止。
- 表格保留多选、编辑、排序、列宽调整/记忆，补齐文件名与只读音频信息；左右栏独立滚动。歌曲右键及播放栏“编辑标签”进入网页相应曲目，可返回全部曲库。
- 标签写入只标记实际改变的字段；支持明确清空数字字段。组写入预检并在失败时尝试还原本组标签；失败草稿保留。封面组写入也做快照还原。在线搜索带批次标识，防止旧候选串入新搜索。

## 验证方式与边界

1. `node tools/WebShellDspTagsSmoke.cjs`：12 页导航、首节点几何、EQ 类型/开关/加段、ECHO 数据展示、声道偏移、离页停频谱、标签成功/失败草稿、多选部分字段、在线标签与歌词预览、批量进度、转换预览、按 ID 进入标签，1440/1024/760 无横向溢出。
2. `node tools/WebShellContextSmoke.cjs`：原有 11 类页面的右键、稳定 ID、多选、队列版本、歌单操作和响应式回归。
3. `dotnet test tools/TagEditTests/TagEditTests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~WebTagWriteTests`：6 项真实文件测试；MP3/FLAC/M4A 写入请求字段并保留其他字段、清空年份、相同值不改文件、无效数字拒绝写入。测试使用独立临时副本。
4. Release/x64 构建与自包含发布。机器曾出现一次 C# / XAML 编译器内存访问异常，采用单构建节点、关闭共享编译器重试；不修改 Windows 设置。
5. 用户要求停用电脑操作技能前，实际程序已打开 EQ 与 4031 首曲库的标签表格，首次 DSP 加载成功。后续全部使用代码、命令行和无界面的测试；没有用原始音乐做写入测试，也没有实测第三方服务在线可用性、IR 音色和各设备听感。

## 产物

完整预览发布目录：`dist/web-shell-dsp-tags-complete/`。`打开网页直接进播放器.cmd` 带 `--web-shell` 启动参数，免去手工输入。（早期版本里这个启动脚本叫 `打开网页预览.cmd`，现名已改，内容相同。）测试截图在 `outputs/web-dsp-eq-preview.png` 和 `outputs/web-tags-preview.png`。

当前其他 agent 的工作区修改保留。源码独占锁已停用，同时修正 `AGENTS.md` 与协作 README 的旧锁说明。

## 意外关机后的接续验证（2026-10-26）

- 检查 `%LOCALAPPDATA%/CelesteMusicPlayer/` 当前 9 份 JSON，均可解析；今天较早的 `.corrupt-*` 备份保留，没有覆盖设置或用户曲库。仓库 `global.json` 及项目配置可用，Release/x64 构建通过（91 个现有警告，0 错误）。这只确认文件与构建状态，不代表异常关机前的每个设置值都已恢复。
- 修复输入余量卡片正负号；修复 DSP 滑杆延迟提交时误用新页面模块的问题；失败回包只刷新原来那一版页面。
- 输出安全页的限制器通过 `headroom` 提交，读取成功前禁用；写入失败保持原显示，不虚报已切换。
- EQ 补齐原生选中/悬停虚线与频率标签（42px 防重叠）；全部节点保持相同半径与圆心文字；添加频段加入本地修订与试听序列。
- 标签写盘期间锁住编辑工作区及还原按钮，完成/失败均解锁，避免保存过程中输入的新内容被刷新覆盖；列宽拖动限制与读取验证统一为 56–800px。
- 扩展 `WebShellDspTagsSmoke.cjs` 并通过：余量符号、跨页延迟写入、限制器失败状态、EQ 辅助线/频率标注、保存期间锁定均有断言；原有 12 页/6 子页与三种宽度检查通过。`WebShellContextSmoke.cjs` 的 11 类页面回归通过；6 项 `WebTagWriteTests` 再次通过。
- 自包含发布首次遇到 MSBuild 内部 Regex/NullReferenceException，关闭构建服务器后，以单节点、禁共享编译、禁节点复用重试成功。未改系统设置。
- 发布到 `dist/web-shell-dsp-tags-complete/`，附 `打开网页预览.cmd`；核对 DLL SHA256 和所有网页源码/发布资源哈希。既有两个预览目录同步网页资源。按此前用户要求，本次未操作桌面或启动播放器，真实设备听感、在线服务与 IR 导入仍待人工实测。

文档过时说明：`PROJECT-BRIEF.md` 第 7 节仍把 `web-shell-library-preview` 写作唯一当前目录；本任务完整交付目录以上述 `web-shell-dsp-tags-complete` 为准。最初方案 `RESULT-*` 中“尚无 tags/dsp 桥接”“未改产品源码”是历史调研状态，已被本验证记录中的实际实现覆盖。

## 接续验证：测试 / 发布 / 实机（2026-10-26 19:00–19:15）

Codex 侧停在“代码完成、验证通过、尚未最终发布”之后，以下四步由接续方执行并留证。**全程未点击桌面、未用 UIA**，只用命令行、无界面探针和日志判读。

### 1. 单元测试

```
python _wb_test.py ""
→ 已通过! - 失败: 0，通过: 44，已跳过: 0，总计: 44
```

含新加的 `WebDspTagRulesTests`（EQ 32/64Hz 轴对齐、旧 10 段迁移、导入频率保留、多键排序）与 `WebTagWriteTests`（6 项真实文件写入）。单独筛跑这两组为 18 项，同样全过。

**顺带修掉一个真隐患**：`tools/TagEditTests` 的输出目录原先缺 `System.Security.Cryptography.ProtectedData.dll`。测试宿主加载主程序集时会走到 `AppSettingsStore` 的 DPAPI 解密，缺依赖 → 解密抛异常 → 程序把**用户真实的** `app-settings.json` 判成“损坏”并改名备份（历史上已累积 13 份 `.corrupt-*`）。已在 `TagEditTests.csproj` 的 `CopyWinAppSdkRuntime` 目标里补上该 DLL；修后重跑 44 项全过，且新增日志中“设置文件损坏”计数为 0。今后在测试工程里引用主程序集，都要把这条依赖带上。

### 2. 自包含发布与哈希核对

```
python _wb_build.py publish 3
python _wb_verify_publish.py <目标目录>
→ web assets: 30/30 identical
→ VERIFY_OK
```

- `CelesteMusicPlayer.dll` 2,963,968 字节，md5 `0114e1ee5602e714f8240e9c9c6ef541`。
- 逐个探测新桥成员均命中：`WebDspOpraAsync`、`WebTagSortRules`、`WebTagsReadAsync`、`WebTagsWriteAsync`、`WebTagToolsAsync`、`WebTagCoverAsync`、`WebTagRenameAsync`、`WebDspModuleInfo`、`WebShellWindow`。
- 455 个文件做了零字节扫描，无内容全零的假文件（本机沙箱覆盖写偶发产出“大小对、内容全零”的文件，这是必须查的一项）。
- 30 个网页资源与 `outputs/webview2-shell/` 源码逐项哈希一致，含本次新增的 `dsp-headphones.js`、`dsp-reference.css`。

旧交付目录整包移走保留：`dist/_prev_web-shell-dsp-tags-complete_20261026_1702`。新目录为 `dist/web-shell-dsp-tags-complete/`（310 MB，双击 `打开网页直接进播放器.cmd` 即以 `--web-shell` 启动），另附 `交付哈希清单.txt` 供以后对账。

### 3. OPRA 在线数据实测（真联网）

`tools/OpraOnlineProbe` 用产品自身的 `OpraService` 跑完整链路，不经过界面：

- 联网下载：从 `opra.roonlabs.net` 实拉 **12.1 MB / 7.2 s**，解析出 Products=6230、Eqs=12594、Vendors=714（下载走临时目录，不动用户缓存）。
- 搜索 + 曲线映射（5 款耳机，全部通）：

| 耳机 | 命中 | 曲线条数 | 映射结果 |
|---|---|---|---|
| Sennheiser HD 600 | 4 | 26 | 10 段，preamp -6.2 dB |
| Sony WH-1000XM5 | 1 | 4 | 10 段，preamp -6.2 dB |
| Moondrop Aria | 6 | 12 | 10 段，preamp -3.4 dB |
| AKG K371 | 3 | 20 | 10 段，preamp -5.0 dB |
| Apple AirPods Pro | 8 | 4 | 10 段，preamp -3.2 dB |

均为「导入 10、跳过 0」，归属信息（厂商/型号）完整。**这证明 OPRA 面板背后的数据是真的在线可取、真的能变成本机 EQ 状态**，不再只是界面占位。

### 4. FIR 卷积实测（内核级）

界面点测无法直接证明“卷积真的在算”，因此改用产品自己的 IR 加载代码 + 同一套内核调用，做冲激响应比对（`tools/FirConvolutionProbe`）：

```
[1] 用产品代码加载 IR        2048 taps，2ch，未超内核上限 8192
[2] 建内核并上传 IR          全部 rc=0，内核 taps 与上传一致
[3] 冲激比对
      开卷积：峰值 0.4600，64–400 样本 RMS = 0.068409
      关卷积：峰值 0.5000，64–400 样本 RMS = 0.000000
[4] 关卷积 = 直通            数据逐位不变
```

意义：关卷积时冲激之后是绝对静音（直通对照）；开卷积后能量被摊到冲激之后——**滤波确实在运算，不是“开关亮了但没接”**。

### 5. 实机启动与真实播放

`--web-shell` 单独启动：`[WebShell] initialized`、`library sent tracks=4031` 两条日志正常打出，无启动级错误。
带真实 FLAC 启动：`输出启动 mode=WasapiShared 源=32bit/48000Hz/2ch`、`[链路] 源=44100/16bit/2ch → 设备 48000/32bit`、`celeste_dsp_core 就绪`，无播放级错误。判定表 6/6 PASS。

### 仍未验证（不要当成已验证）

- **真实听感**：耳机/音箱上 OPRA 曲线和 FIR 的音色差异没有人工听过。FIR 只有内核波形证据，OPRA 只有数据与映射证据。
- **FIR 与主窗口联动**：命令行传文件走的是「零散播放窗口」——独立精简引擎，本来就没有完整 DSP 机架，所以那条路径不可能出现 FIR。完整 DSP 是在主窗口起播时恢复的（见 `MainWindow.Playback3.cs` 起播下发清单）。本次没有自动点开主窗口播放再听，因此“主窗口里 FIR 开起来听感如何”仍待人工。
- **写盘安全**：标签写盘只用临时副本测过，没有对用户真实曲库执行写操作。
- **已知噪音（三条，均为环境不是代码）**：`App.PrimaryLanguageOverride` InvalidOperationException ×2（每次启动必有）、更新检查 `403 rate limit exceeded`（GitHub 限流）。

### 可复现命令

```
python _wb_test.py ""                                  # 44 项单元测试
python _wb_build.py publish 3                          # 自包含发布（自带重试）
python _wb_verify_publish.py <目录>                     # 网页资源 + DLL 哈希核对
python _wb_opra_probe.py 3                             # OPRA 真联网实测
python _wb_fir_probe.py 3                              # FIR 卷积内核实测
python _wb_field_test.py 15                            # 实机启动 + 真实播放
python _wb_hash_manifest.py <目录>                      # 重新生成哈希清单
```

构建期若出现 MSBuild / Roslyn / XamlCompiler 内部异常（本机反复出现），属环境噪音：先 `python _wb_build.py shutdown`，再用带重试参数的脚本重跑，不要据此改代码。

### 6. 入库记录

按「一次任务一个 commit、不 `git add -A`」拆分，共 4 个本地提交（未推送）：

| commit | 内容 |
|---|---|
| `c7609db` | 网页外壳宿主与桥接（此前只在工作区，未入库）+ 网页源全量 + 测试探针 |
| `44afd2e` | 协作体系文档（AGENTS.md / collab）+ 本文件引用的验证脚本 |
| `b897b76` | 迷你播放器：流动炫光底色 + 歌词长行换行 |
| `e8304dd` | 工作日记 10-18/10-19/10-23/10-26 + 项目记忆中的网页外壳关键事实 |

未入库的仍是缓存与临时探针（`dist/`、`_binbak_*`、`_bin_verify/`、`_xc_*.txt`、`.reasonix/`、9 月的历史 handoff 文档等）。
