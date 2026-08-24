; CelesteMusicPlayer installer script (NSIS 3.12)
; Usage: copy dotnet publish output to Desktop "CelesteMusicPlayer-发布", then:
;   makensis.exe installer\CelesteMusicPlayer.nsi

; ---------- Metadata ----------
!define APP_NAME "CelesteMusicPlayer"
!define APP_VERSION "26.8.24"
!define APP_EXE "CelesteMusicPlayer.exe"
!define PUBLISH_DIR "C:\Users\admin\source\repos\CelesteMusicPlayer\CelesteMusicPlayer\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
!define APP_GUID "{F0C207C6-BD8C-4D7A-9127-F1B67F17E65B}"

Unicode true
; User-level install, no admin needed
RequestExecutionLevel user
Name "${APP_NAME}"
OutFile "C:\Users\admin\Desktop\CelesteMusicPlayer-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallLocation"

; Compression: lzma (single-thread NSIS, no islzma dependency)
SetCompressor /SOLID lzma

; ---------- Pages ----------
Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

; ---------- Install ----------
Section "MainSection" SEC01
  SetOutPath "$INSTDIR"
  SetOverwrite on
  ; Recursively copy whole publish dir
  File /r "${PUBLISH_DIR}\*.*"
  ; Write uninstall info (user-level)
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "${APP_NAME}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "NoRepair" 1
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallLocation" "$INSTDIR"

  ; Start menu shortcut
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0

  ; Desktop shortcut
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

; ---------- Uninstall ----------
Section "Uninstall"
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APP_NAME}"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
  DeleteRegKey HKCU "Software\${APP_NAME}"
SectionEnd
