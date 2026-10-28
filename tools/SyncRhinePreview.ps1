$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
$target=Join-Path $workspace 'dist/web-ui-20261028/WebShell'
if (!(Test-Path -LiteralPath $target)) { throw 'Current WebShell deployment is missing' }
$backup=Join-Path $workspace ('outputs/rhine-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
$files=@('index.html','rhine-albums.js','rhine-albums.css','rhine-renderer.js','rhine-cassette.glb','RHINE-NOTICE.txt')
foreach($file in $files) {
  $destination=Join-Path $target $file
  $source=Join-Path $workspace ('outputs/webview2-shell/'+$file)
  if(Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination (Join-Path $backup $file) }
  Copy-Item -LiteralPath $source -Destination $destination
  $sourceHash=Get-FileHash -LiteralPath $source
  $destinationHash=Get-FileHash -LiteralPath $destination
  if($sourceHash.Hash -ne $destinationHash.Hash) { throw "Sync mismatch: $file" }
}
Write-Output "Backup: $backup"
Write-Output '6 deployed file hashes match'
