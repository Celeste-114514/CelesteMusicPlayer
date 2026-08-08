@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo 正在解除 Windows 对下载文件的阻止（若有）...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue"

echo 正在启动 CelesteMusicPlayer...
start "" "%~dp0CelesteMusicPlayer.exe"
