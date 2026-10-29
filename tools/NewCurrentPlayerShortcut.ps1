# Recreate the local shortcut after moving/cloning this repository. Does not launch the app.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$delivery = Join-Path $repository 'dist/web-ui-20261028'
$executable = Join-Path $delivery 'CelesteMusicPlayer.exe'
if (!(Test-Path -LiteralPath $executable)) { throw "Current delivery is missing: $executable" }
$shortcutPath = Join-Path $repository '打开当前播放器.lnk'
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $delivery
$iconFile = Join-Path $delivery "Assets/AppIcon.ico"
$shortcut.IconLocation = "$iconFile,0"
$shortcut.Description = '当前开发验证版；直接启动 EXE，不经过命令行黑窗'
$shortcut.Save()
$verified = $shortcutShell.CreateShortcut($shortcutPath)
if ($verified.TargetPath -ne $executable -or $verified.WorkingDirectory -ne $delivery) { throw 'Shortcut verification failed' }
Write-Output $shortcutPath
