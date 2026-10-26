param(
    [Parameter(Mandatory=$true)][string]$LogPath,
    [Parameter(Mandatory=$true)][string[]]$DotnetArguments
)
# Session-local workaround for this machine's intermittent CLR/Roslyn/XamlCompiler crashes.
# Child compiler processes inherit affinity; no system setting or project compiler option changes.
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnetProcess = Start-Process -FilePath 'C:/Program Files/dotnet/dotnet.exe' -ArgumentList $DotnetArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $LogPath -RedirectStandardError ($LogPath + '.stderr.log')
$dotnetProcess.ProcessorAffinity = [IntPtr](1L -shl [Math]::Min(63, [Environment]::ProcessorCount - 1))
$dotnetProcess.WaitForExit()
$dotnetProcess.Refresh()
$taskExit = $dotnetProcess.ExitCode
$dotnetProcess.Dispose()
Get-Content -LiteralPath $LogPath -Tail 6
if ($taskExit -ne 0) { Get-Content -LiteralPath ($LogPath + '.stderr.log') -Tail 8 }
exit $taskExit
