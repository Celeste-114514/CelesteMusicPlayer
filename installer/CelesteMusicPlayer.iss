; CelesteMusicPlayer 安装包脚本 (Inno Setup)
; 用法:把 dotnet publish 的输出复制到桌面 "CelesteMusicPlayer-发布",然后
;   ISCC.exe installer\CelesteMusicPlayer.iss
; 或直接在 Inno Setup 编译器中打开本文件编译。

#define MyAppName "CelesteMusicPlayer"
#define MyAppVersion "26.8.19"
#define MyAppPublisher "CelesteMusicPlayer"
#define MyAppExeName "CelesteMusicPlayer.exe"
#define PublishDir "C:\Users\admin\Desktop\CelesteMusicPlayer-发布"

[Setup]
AppId={{F0C207C6-BD8C-4D7A-9127-F1B67F17E65B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=C:\Users\admin\Desktop
OutputBaseFilename=CelesteMusicPlayer-Setup-{#MyAppVersion}
SetupIconFile=C:\Users\admin\source\repos\CelesteMusicPlayer\CelesteMusicPlayer\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
