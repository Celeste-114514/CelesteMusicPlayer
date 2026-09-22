#!/usr/bin/env bash
# build.sh — 在沙箱（无 vcvars 环境）里编译 celeste_core.dll
#
# 沙箱要点（2026-09-23 实测）：
#  1. 一切参数走 cl 响应文件（build_args.rsp / build/link_args.rsp），不经 shell 传参
#     （Git Bash 会把 /DLL、/OUT: 这类参数当路径转换成 DLL.obj，直接毁掉调用）。
#  2. bash 的 export 不传播到 Windows 子进程，必须用 env 前缀传 LIB。
#  3. LIB 环境变量里的路径必须用反斜杠（正斜杠 link.exe 不认，LNK1104 假象）。
#  4. link 的 default lib（msvcrt/vcruntime/ucrt/kernel32…）靠 LIB 搜索目录解析；
#     显式输入库写相对路径（相对本目录），ole32/avrt/winmm/uuid 从 build/libs 给。
#  5. cl 偶发段错误（exit 139）/ 链接偶发失败 → 重试循环（最多 6 次），DLL 出现才算成功。
#
# 有 vcvars 的机器上也可以直接：cl @build_args.rsp && link @build/link_args.rsp
# （vcvars 已设好 INCLUDE/LIB，env LIB 前缀可省）。

set -u
cd "$(dirname "$0")"

VCTOOLS='C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.52.36725'
CLBIN="/c/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.52.36725/bin/Hostx64/x64"
WINKITS_LIB='C:\Program Files (x86)\Windows Kits\10\Lib\10.0.28000.0'

# 注意：必须用 env 前缀把 LIB 传给 link.exe —— 本沙箱 bash 的 export 不传播到
# Windows 子进程（这是反复踩过的坑），但 env 前缀可以。
LIB="build/libs;$VCTOOLS\lib\x64;$WINKITS_LIB\ucrt\x64;$WINKITS_LIB\um\x64"
export PATH="/c/Program Files/Git/cmd:/c/Program Files/Git/mingw64/bin:/usr/bin:/bin:$PATH"

rm -f build/celeste_core.dll build/celeste_core.obj build/celeste_core.exp build/celeste_core.lib

ok=0
for i in 1 2 3 4 5 6; do
  echo "[build.sh] 第 $i 次尝试：编译…"
  env LIB="$LIB" "$CLBIN/cl.exe" @build_args.rsp > "build/build_cl_$i.log" 2>&1
  if [ ! -f build/celeste_core.obj ]; then
    echo "[build.sh] 编译失败（cl exit=$?），重试…"; sleep 1; continue
  fi
  echo "[build.sh] 第 $i 次尝试：链接…"
  env LIB="$LIB" "$CLBIN/link.exe" @build/link_args.rsp > "build/build_link_$i.log" 2>&1
  if [ -f build/celeste_core.dll ]; then
    echo "[build.sh] 成功：build/celeste_core.dll"
    ls -la build/celeste_core.dll
    ok=1
    break
  fi
  echo "[build.sh] 链接失败，重试…"; sleep 1
done

if [ "$ok" != "1" ]; then
  echo "[build.sh] 6 次均失败，最后日志："
  for f in build/build_cl_6.log build/build_link_6.log; do
    [ -f "$f" ] && { echo "--- $f ---"; tr -d '\000' < "$f" | tail -30; }
  done
  exit 1
fi
