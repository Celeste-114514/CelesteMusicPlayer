# The current desktop delivery must include .NET; never publish a framework-dependent folder.
param([string]$OutputDirectory = 'outputs/quick-selfcontained')
& "$PSScriptRoot/Invoke-PinnedDotnet.ps1" -LogPath "$PSScriptRoot/../outputs/current-player-publish.log" -DotnetArguments @('publish',"$PSScriptRoot/../CelesteMusicPlayer/CelesteMusicPlayer.csproj",'-c','Release','-p:Platform=x64','-p:WindowsPackageType=None','-r','win-x64','--self-contained','true','-o',$OutputDirectory)
exit $LASTEXITCODE
