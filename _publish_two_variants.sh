#!/bin/bash
# 本地 publish 两个变体（框架依赖 fd / 自包含 sc），供 NSIS 打包用。
# 沙箱缺 Windows 系统环境变量，dotnet 必须用 env 前缀补齐，否则 NuGet 报
# "Value cannot be null. (Parameter 'path1')"。
export PATH="/usr/bin:/bin:/c/Program Files/dotnet:$PATH"
cd "$(dirname "$0")" || exit 1
ENV=(env
  DOTNET_ROOT='C:\Program Files\dotnet'
  ProgramData='C:\ProgramData'
  SystemRoot='C:\WINDOWS'
  APPDATA='C:\Users\MSI\AppData\Roaming'
  ALLUSERSPROFILE='C:\ProgramData'
  USERPROFILE='C:\Users\MSI'
  ProgramFiles='C:\Program Files'
  'ProgramFiles(x86)=C:\Program Files (x86)'
  CommonProgramFiles='C:\Program Files\Common Files'
  CommonProgramW6432='C:\Program Files\Common Files'
  DOTNET_CLI_TELEMETRY_OPTOUT=1)

CS='CelesteMusicPlayer/CelesteMusicPlayer.csproj'
LOG=/tmp/publish_two.log
: > "$LOG"

run_publish() {
  local out="$1" extra="$2" tag="$3"
  for i in 1 2 3 4 5; do
    echo "=== $tag attempt $i $(date +%H:%M:%S) ===" >> "$LOG"
    if [ -z "$extra" ]; then
      "${ENV[@]}" dotnet publish "$CS" -c Release -r win-x64 -p:Platform=x64 -o "$out" >> "$LOG" 2>&1
    else
      "${ENV[@]}" dotnet publish "$CS" -c Release -r win-x64 -p:Platform=x64 "$extra" -o "$out" >> "$LOG" 2>&1
    fi
    ec=$?
    cs=$(grep -c "error CS" "$LOG")
    msb=$(grep -c "error MSB" "$LOG")
    echo "$tag exit=$ec cs_total=$cs msb_total=$msb" >> "$LOG"
    if [ $ec -eq 0 ] && [ -f "$out/CelesteMusicPlayer.exe" ]; then
      echo "$tag OK size=$(stat -c %s "$out/CelesteMusicPlayer.exe")" >> "$LOG"
      return 0
    fi
  done
  echo "$tag FAILED" >> "$LOG"
  return 1
}

run_publish 'dist/publish-fd' '' 'FD' || exit 1
run_publish 'dist/publish-sc' '-p:CelesteSelfContainedDistribute=true' 'SC' || exit 1
echo "PUBLISH_ALL_OK" >> "$LOG"
exit 0
