; CelesteMusicPlayer installer script (NSIS 3.12, Modern UI 2, Chinese)
; Build: makensis.exe installer\CelesteMusicPlayer.nsi

; ---------- Metadata ----------
!define APP_NAME "CelesteMusicPlayer"
!define APP_VERSION "26.9.1"
!define APP_EXE "CelesteMusicPlayer.exe"
!define PUBLISH_DIR "C:\Users\admin\source\repos\CelesteMusicPlayer\CelesteMusicPlayer\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
!define APP_GUID "{F0C207C6-BD8C-4D7A-9127-F1B67F17E65B}"
!define REG_UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define REG_RUN "Software\Microsoft\Windows\CurrentVersion\Run"

Unicode true
; User-level install, no admin needed
RequestExecutionLevel user
Name "${APP_NAME}"
OutFile "C:\Users\admin\Desktop\CelesteMusicPlayer-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallLocation"
SetCompressor lzma

; ---------- Modern UI 2 ----------
!include "MUI2.nsh"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"
!define MUI_FINISHPAGE_RUN_CHECKED
!insertmacro MUI_PAGE_FINISH

; ---------- Uninstall pages ----------
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "卸载完成"
!define MUI_FINISHPAGE_TEXT "本程序已从您的电脑卸载。"
!insertmacro MUI_UNPAGE_FINISH

; ---------- Languages ----------
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------- Branding ----------
;BrandingText "${APP_NAME} ${APP_VERSION}"

; ---------- Install sections (components) ----------
Section "播放器主程序（必需）" SEC_APP
  SectionIn RO
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PUBLISH_DIR}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Add/Remove Programs (user-level)
  WriteRegStr HKCU "${REG_UNINST}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${REG_UNINST}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${REG_UNINST}" "Publisher" "${APP_NAME}"
  WriteRegStr HKCU "${REG_UNINST}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${REG_UNINST}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${REG_UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${REG_UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${REG_UNINST}" "NoRepair" 1
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallLocation" "$INSTDIR"
SectionEnd

Section "创建桌面快捷方式" SEC_DESKTOP
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "创建开始菜单快捷方式" SEC_STARTMENU
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0
SectionEnd

Section "开机自动启动" SEC_AUTORUN
  WriteRegStr HKCU "${REG_RUN}" "${APP_NAME}" '"$INSTDIR\${APP_EXE}"'
SectionEnd

; ---------- Section descriptions ----------
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "CelesteMusicPlayer 主程序和全部运行文件（必须安装）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在桌面创建启动快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTMENU} "在开始菜单创建启动与卸载快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTORUN} "登录 Windows 后自动启动 ${APP_NAME}。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- Uninstall ----------
Section "Uninstall"
  ; 询问是否删除用户数据（仅在 GUI 交互模式弹出；静默 /S 卸载自动保留数据）
  IfSilent +3
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "是否同时删除用户数据（设置、主题、播放列表、封面与转码缓存、日志）？$\r$\n推荐选择“否”以保留数据。$\r$\n注意：删除后无法恢复。" IDYES del_userdata IDNO keep_userdata

  del_userdata:
    RMDir /r "$LOCALAPPDATA\${APP_NAME}"
    goto after_userdata
  keep_userdata:
  after_userdata:

  ; 删除开机自启
  DeleteRegValue HKCU "${REG_RUN}" "${APP_NAME}"

  ; 删除快捷方式
  Delete "$DESKTOP\${APP_NAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APP_NAME}"

  ; 删除程序文件
  RMDir /r "$INSTDIR"

  ; 删除注册表
  DeleteRegKey HKCU "${REG_UNINST}"
  DeleteRegKey HKCU "Software\${APP_NAME}"
SectionEnd
