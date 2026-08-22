# CelesteMusicPlayer 流媒体插件服务（WSL 侧）

## 这是什么
把 `musicbot-go` 的平台能力（Apple Music / 网易云 / QQ 的搜索、歌词、下载）以独立 HTTP 服务暴露，
供 Windows 播放器「设置 → 流媒体」直接调用。需要 WSL（Ubuntu）环境运行。

## 目录结构
- `main.go`          —— 独立 HTTP 服务源码（Go，可自行重编译）
- `run.sh`           —— WSL 启动脚本
- `config_example.ini` —— 配置模板（含各平台 cookie/账号，敏感；正式用请改为你自己的 `config.ini`）
- `install.md`       —— 安装说明
- `streamingserver`  —— 预编译二进制（发布包附带，仓库通常不提交）

## 构建（需要 Go，可选）
为复用 musicbot 的平台注册，`main.go` blank-import 了
`github.com/liuran001/MusicBot-Go/plugins/all`，需在完整 musicbot-go 工程内构建。推荐做法：
```bash
cp streaming-plugin/main.go /home/<you>/musicbot-go/cmd/streamingserver/main.go
cd /home/<you>/musicbot-go
mkdir -p streaming-plugin
go build -o streaming-plugin/streamingserver ./cmd/streamingserver/
```

## 安装与运行
1. 启用 WSL（Ubuntu）：`wsl --install`（首次需重启）。
2. 把 `streaming-plugin/` 目录放到 WSL，如 `/home/<you>/celeste-streaming/`（含 `streamingserver` 二进制）。
3. 复制登录配置：`cp /home/<you>/musicbot-go/config.ini /home/<you>/celeste-streaming/config.ini`
   （含各平台 cookie/账号，敏感，勿公开；或参考 `config_example.ini` 自行填写）
4. 启动：`./run.sh`
   - 默认监听 `0.0.0.0:21010`
   - 改端口：`STREAMING_ADDR=0.0.0.0:21010 ./run.sh`
5. Windows 播放器「设置 → 流媒体」填服务地址并「检测连接」：
   - WSL IP：在 WSL 运行 `hostname -I` 查看
   - 或已做端口转发则填 `http://localhost:21010`

## 平台说明
- **Apple Music**：需 wrapper 服务运行（10021/20021/30021）；歌词来自 MusicKit（需登录态）；**不提供音频下载**，仅搜索/歌词/标签。
- **网易云 / QQ**：需在 `config.ini` 配置对应 cookie；提供 **128kbps MP3 下载**（需登录解锁）。

## API
| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/ping` | 健康检查 |
| GET | `/api/platforms` | 已启用平台列表 |
| GET | `/api/search?platform=&q=&limit=` | 搜索 |
| GET | `/api/lyric?platform=&id=` | 歌词（Apple 含 `raw_ttml`） |
| GET | `/api/download?platform=&id=&quality=` | 下载信息（standard=128k 默认；high/lossless/hi_res） |

## 注意事项
- 下载为最低限度功能，请遵守各平台条款。
- 端口默认 21010；WSL 与 Windows 间隔了 Hyper-V 防火墙时需放行。
