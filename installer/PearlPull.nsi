Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef ROLE
  !error "Pass -DROLE=Button or -DROLE=Receiver"
!endif
!ifndef APPDIR
  !error "Pass an absolute -DAPPDIR containing the compiled app"
!endif
!ifndef OUTDIR
  !define OUTDIR "."
!endif
!define PRODUCT "Pearl Pull ${ROLE}"
!define APP "PearlPull${ROLE}.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MinecraftUtils-PearlPull-${ROLE}"
Name "${PRODUCT}"
OutFile "${OUTDIR}/PearlPull-${ROLE}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\PearlPull\${ROLE}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "0.5.0.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileDescription" "${PRODUCT} installer"
VIAddVersionKey "FileVersion" "0.5.0"
VIAddVersionKey "LegalCopyright" "MinecraftUtils contributors"
!define MUI_WELCOMEPAGE_TITLE "Install ${PRODUCT}"
!define MUI_WELCOMEPAGE_TEXT "Install Button on PC 1 and Receiver on PC 2.$\r$\n$\r$\nPearl Pull runs beside the Windows clock and starts automatically when this user signs in. No PowerShell window is needed.$\r$\n$\r$\nClose old Pearl Pull script windows before continuing. Saved pairing settings are reused.$\r$\n$\r$\nInstall while signed in as the Windows user who plays Minecraft."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP}"
!define MUI_FINISHPAGE_RUN_PARAMETERS "--launch"
!define MUI_FINISHPAGE_RUN_TEXT "Open ${PRODUCT}"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_OK|MB_ICONEXCLAMATION "Pearl Pull needs Microsoft .NET Framework 4.8, included with Windows 11 and recent Windows 10. Install Windows updates or .NET Framework 4.8, then run setup again."
    Abort
  ${EndIf}
  SetRegView 32
FunctionEnd

Section "Install"
  SetShellVarContext current
  IfFileExists "$INSTDIR\${APP}" 0 +3
    ExecWait '"$INSTDIR\${APP}" --quit'
    Sleep 2500
  SetOutPath "$INSTDIR"
  File "${APPDIR}/${APP}"
  File "${APPDIR}/${APP}.config"
  File "${APPDIR}/START-HERE.txt"
  SetOutPath "$INSTDIR\icons"
  File "${APPDIR}/icons/pearl-pull.png"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Pearl Pull"
  CreateShortcut "$SMPROGRAMS\Pearl Pull\${PRODUCT}.lnk" "$INSTDIR\${APP}" "--launch"
  CreateShortcut "$DESKTOP\${PRODUCT}.lnk" "$INSTDIR\${APP}" "--launch"
  ; Immediate fallback; first launch installs a task with crash recovery if permitted.
  WriteRegDWORD HKCU "Software\MinecraftUtils\PearlPull" "${ROLE}StartupDisabled" 0
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MinecraftUtils-PearlPull-${ROLE}" '"$INSTDIR\${APP}" --background'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "0.5.0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "MinecraftUtils"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  ExecWait '"$INSTDIR\${APP}" --remove-startup' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "The automatic startup task could not be removed. Uninstall has stopped; open Pearl Pull and turn off Start with Windows, or ask a parent to check Task Scheduler."
    Abort
  ${EndIf}
  ExecWait '"$INSTDIR\${APP}" --quit'
  Sleep 2500
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MinecraftUtils-PearlPull-${ROLE}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  Delete "$SMPROGRAMS\Pearl Pull\${PRODUCT}.lnk"
  RMDir "$SMPROGRAMS\Pearl Pull"
  Delete "$DESKTOP\${PRODUCT}.lnk"
  Delete "$INSTDIR\${APP}"
  Delete "$INSTDIR\${APP}.config"
  Delete "$INSTDIR\START-HERE.txt"
  Delete "$INSTDIR\icons\pearl-pull.png"
  RMDir "$INSTDIR\icons"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ; Keep pairing files, logs, and the shared home-network firewall rule for reinstall.
SectionEnd
