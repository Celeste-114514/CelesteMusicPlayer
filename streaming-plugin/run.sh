#!/usr/bin/env bash
# CelesteMusicPlayer 流媒体插件服务 · WSL 侧启动脚本
# 用法：把本目录放到 WSL（如 /home/<user>/celeste-streaming/），放入 config.ini 后运行 ./run.sh
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
# config.ini 与二进制放在与 run.sh 同级（发布包布局：本目录即根）
CONFIG="${STREAMING_CONFIG:-$DIR/config.ini}"
ADDR="${STREAMING_ADDR:-0.0.0.0:21010}"
BIN="${STREAMING_BIN:-$DIR/streamingserver}"

if [ ! -f "$CONFIG" ]; then
  echo "缺少 config.ini：请把 musicbot 的 config.ini 复制到此目录（含各平台 cookie/账号），或参考 config_example.ini 填写。"
  exit 1
fi
if [ ! -x "$BIN" ]; then
  echo "缺少 $BIN 二进制：请先构建（见 README.md）或从发布渠道下载预编译版。"
  exit 1
fi

echo "启动流媒体服务 $ADDR（配置 $CONFIG）"
exec "$BIN" -c "$CONFIG" -addr "$ADDR"
