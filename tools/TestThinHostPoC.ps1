param([string]$Executable = (Join-Path $PSScriptRoot '../dist/thin-host-poc/CelesteThinHost.Poc.exe'))
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$deps = Join-Path (Split-Path $executablePath) 'CelesteThinHost.Poc.deps.json'
if ((Get-Content -LiteralPath $deps -Raw) -match 'Microsoft.WindowsAppSDK|Microsoft.UI.Xaml|Microsoft.WinUI') {
    throw 'PoC unexpectedly depends on WinUI/Windows App SDK'
}
$test = Start-Process -FilePath $executablePath -ArgumentList '--self-test' -WorkingDirectory (Split-Path $executablePath) -WindowStyle Hidden -PassThru
if (!$test.WaitForExit(45000)) { $test.Kill(); throw 'Hidden PoC test exceeded 45 seconds' }
$root = Join-Path $env:LOCALAPPDATA 'CelesteMusicPlayer-Poc'
$run = Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name.EndsWith('-' + $test.Id) } | Select-Object -First 1
if (!$run) { throw 'No report directory was produced by this test process' }
$result = Join-Path $run.FullName 'result.json'
$report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
if ($test.ExitCode -ne 0 -or !$report.passed) { throw "PoC test failed; see $result" }
Write-Output 'PASS: independent packaged host, native API, real WebView2 roundtrip, no WinUI dependency'
Write-Output "Report: $result"
Write-Output 'Visible taskbar buttons, monitor DPI changes and focus still require manual verification.'
