; Raxicore Editor -- NSIS installer
; ---------------------------------------------------------------------------------------------
; Parameterized for CI. Everything the build needs is overridable from the makensis command line:
;
;   makensis /DAPP_VERSION=0.1.0 ^
;            "/DAPP_VERSION_DISPLAY=0.1.0 Beta" ^
;            "/DSOURCE_DIR=C:\path\to\publish" ^
;            "/DOUTFILE=C:\path\to\RaxicoreEditor-Setup-0.1.0.exe" ^
;            installer\raxicore-editor.nsi
;
; SOURCE_DIR is a `dotnet publish -r win-x64 --self-contained` output folder. The GitHub Actions
; workflow (.github/workflows/build-installer.yml) reads the version from Directory.Build.props and
; passes all four defines, so the installer version always tracks the app version.
; ---------------------------------------------------------------------------------------------

Unicode true
SetCompressor /SOLID lzma

;--- Overridable build parameters -------------------------------------------------------------
!ifndef APP_VERSION
  !define APP_VERSION "0.1.0"
!endif
!ifndef APP_VERSION_DISPLAY
  !define APP_VERSION_DISPLAY "${APP_VERSION} Beta"
!endif
; SOURCE_DIR / OUTFILE default relative to this script's directory (installer\). CI passes absolute paths.
!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\publish"
!endif
!ifndef OUTFILE
  !define OUTFILE "RaxicoreEditor-Setup-${APP_VERSION}.exe"
!endif

;--- Fixed product metadata -------------------------------------------------------------------
!define APP_NAME "Raxicore Editor"
!define APP_EXE "RaxicoreEditor.Editor.exe"
!define APP_PUBLISHER "Raxicore Editor contributors"
!define APP_URL "https://github.com/psforever/raxicore-editor"
!define APP_ICON "..\src\RaxicoreEditor.Editor\Assets\raxicore.ico"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME} ${APP_VERSION_DISPLAY}"
OutFile "${OUTFILE}"
; Per-user install: no admin elevation, lands in the user's local app data (like VS Code's user setup).
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel user
ManifestDPIAware true

;--- Version resource stamped onto the installer .exe -----------------------------------------
VIProductVersion "${APP_VERSION}.0"
VIFileVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${APP_VERSION_DISPLAY}"
VIAddVersionKey "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 ${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} Setup"

;--- Modern UI --------------------------------------------------------------------------------
!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

;--- Install ----------------------------------------------------------------------------------
Section "Install"
  ; Per-user shortcuts and registry entries (current user, no admin required).
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  ; The entire published app (exe + runtime + native libs).
  File /r "${SOURCE_DIR}\*"

  ; Start Menu shortcuts
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallDir" "$INSTDIR"

  ; Add / Remove Programs entry (Apps & features, per-user)
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION_DISPLAY}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKCU "${UNINST_KEY}" "URLInfoAbout"    "${APP_URL}"
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; Report install size to Apps & features
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

;--- Uninstall --------------------------------------------------------------------------------
Section "Uninstall"
  SetShellVarContext current
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"

  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP_NAME}"
SectionEnd
