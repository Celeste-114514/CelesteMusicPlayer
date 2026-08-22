# CelesteMusicPlayer 流媒体插件安装说明（WSL 侧）

## 依赖
- WSL（Ubuntu）可用；若未启用，运行 `wsl --install` 后重启。
- 无需安装 Go / 编译（发布包内含预编译二进制 `streamingserver`；如需从源码重建见 README.md）。
- Apple Music 平台需要 wrapper 服务在运行（默认端口 10021/20021/30021）；网易云 / QQ 等可直接用（需对应 cookie）。

## 安装
1. 解压本包到 WSL，如 `/home/<user>/celeste-streaming/`。
2. 复制你已有 musicbot 的配置：
   `cp .../musicbot-go/config.ini .`
   （config.ini 含各平台 cookie/账号，敏感，请勿公开；或参考 config_example.ini 填写）
3. 运行：`./run.sh`
   （缺 config.ini 会提示；可用环境变量 `STREAMING_ADDR` 改监听地址，默认 0.0.0.0:21010）

## 播放器接入
Windows 播放器「设置 → 流媒体」填服务地址（WSL IP:21010，或 localhost 若已做端口转发）即可。
