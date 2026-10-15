# 构建 / 发布坑（发版前必读）

> 操作流程以 `celeste-build-verify` skill 为准；本文件只留踩过的坑与判据。

## 命令与环境
- dotnet 必带 `env` 前缀补 APPDATA / SystemRoot / ProgramData 等（bash 沙箱这些为空，
  否则 NuGet `ConfigurationDefaults` 静态构造失败报 path1 null）。用 `env` 前缀，`export` 不行（变量名带括号）。
- 无 ImplicitUsings（如 `SemaphoreSlim` 须 `using System.Threading`）。
- run-latest 自包含发布：
  `dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:CelesteSelfContainedDistribute=true -p:EnableSourceControlManagerQueries=false`
- ⚠ **三个开关一个都不能少**（尤其 `WindowsPackageType=None`）：漏了会走 MSIX 路径 →
  `.pri` 只有约 253KB（正常约 1.6MB），**XAML 资源整批缺失、装完启动即失败**，
  但 exe 尺寸/哈希/安装过程全正常，极难发现（2026-10-15 事故即此）。
  打包脚本已内置 `Assert-PublishHealthy` 体检：pri<900KB、sc 版 exe<250KB、fd 版 exe>250KB 一律拒打包。

## 判成功
- = `rc==0` **且产物 mtime 是本次**（只看时间戳会把旧 exe 当成功）。
- XamlCompiler 偶发连崩两次才过 → 重试。publish 偶发挂 10+ 分钟 → communicate(timeout~540) 杀重试。
- IL 以 `icacls` 实查，「无标签」才是 UAC 关机的正确态。

## 0 字节 .g.cs 反馈环
- ⚠ **清 `obj/x64` 必须同时清 `bin/x64`**，只清 obj 会连续崩。
- ⚠ **XamlCompiler 确定性崩溃（exit 1 / CS=0 / 连崩 6 次）先怀疑 XAML 结构错位，不是机器抽风**：
  搬移大块 XAML 时少搬/多搬闭合标签会造成「标签数量平衡但嵌套错位」，XML 解析仍过、编译器必崩。
  判定法 = 把改动 `git stash` 掉构建上一版（能过就是改动的问题）。
  搬移必须连闭合标签一起搬，搬完立刻 `ET.parse` 校验 + 看有无孤立标签。
- 搬移后缩进可不必一致（不影响编译），但剪切区间必须**锚点定位 + 注意插入标记导致的 index 偏移**
  （曾少切一行 `</Grid>` 留孤立标签）。用脚本搬 + ElementTree 校验最稳。
- XamlCompiler 是 32 位进程，本机有 CPU 故障史，崩溃点会漂移。

## 产物被拷坏
- ⚠ 本机抽风时全量自包含发布的**原生 DLL 可能被拷坏**：症状 = 启动前无任何日志的
  `0xC0000005` 静默退，或 CLR `0x80131506`（Internal CLR error）。
  好备份能启动、新发布产物不能 = 中招。
  **恢复法**：好备份整体回填 run-latest（原生 DLL + 自包含 exe 都完好）→ 只把
  **带 `-p:CelesteSelfContainedDistribute=true`** 发布出的 `CelesteMusicPlayer.dll` 覆盖进去
  （dll 约 2.73MB；漏该开关会出 177KB 依赖运行时版 exe，混搭会在 Main begin 后以 3221227010 退）。
  判自包含 exe 看尺寸 ≈ 303KB。

## 安装包
- ⚠ **GitHub Release 上的安装包是 CI 打的，不是本地那份**（ci.yml tag 时调
  `package-installers.ps1` 现场打包后 `gh release upload --clobber`）。
  排查"用户装不上"先 `curl api.github.com/repos/o/r/releases/latest` 比 size，
  本地 dist 那份可能压根没传上去。
- 解 NSIS 安装包对比内容：完整版 7z 才行（`7zr.exe` 精简版不认 NSIS；先下 `7z2604-x64.exe`
  再用 7zr 解出 7z.exe）。静默安装验证用 PowerShell `Start-Process -Wait -PassThru`；
  bash 下 `/D` 必须用反斜杠。
- 本机**无 Windows 沙盒**（Containers-DisposableClientVM 不存在）、Hyper-V Disabled
  → 没法模拟全新电脑，改用解包对比 + 静默安装到临时目录。
