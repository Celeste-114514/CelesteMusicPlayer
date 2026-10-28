$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $repository 'dist/thin-host-poc'
$executable = Join-Path $folder 'CelesteThinHost.Poc.exe'
if (!(Test-Path -LiteralPath $executable)) { throw "W5 PoC has not been published: $executable" }
$linkPath = Join-Path $repository '验证W5薄宿主.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($linkPath)
$link.TargetPath = $executable
$link.WorkingDirectory = $folder
$link.IconLocation = "$executable,0"
$link.Description = 'W5 独立验证窗口，只有模拟会话，不是正式播放器'
$link.Save()
if ($shell.CreateShortcut($linkPath).TargetPath -ne $executable) { throw 'Shortcut verification failed' }
Write-Output $linkPath
