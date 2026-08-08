; ==============================================================
;  PDF Studio - NSIS Installer Script
;  Packages the self-contained WinUI3 publish output into a
;  Windows installer.
;
;  Build:
;    makensis /V4 installer\pdf_studio.nsi
;  Optional overrides:
;    makensis /DVERSION=1.2.0 installer\pdf_studio.nsi
; ==============================================================

Unicode True
SetCompressor /SOLID lzma

; --------------------------------------------------------------
;  Basic configuration
; --------------------------------------------------------------
!define APP_NAME      "PDF Studio"
!define APP_EXE       "pdf_studio.exe"
!define APP_PUBLISHER "PDF Studio"
!define APP_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\${APP_EXE}"
!define UNINST_REGKEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define SOURCE_DIR     "..\bin\win-x64\publish"
!define APP_ICON       "..\Assets\icon.ico"

!ifndef VERSION
  !define VERSION "1.0.0.0"
!endif

Name "${APP_NAME}"
OutFile "PDFStudio-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "${APP_DIR_REGKEY}" ""
RequestExecutionLevel admin

VIProductVersion "${VERSION}"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} Installer"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "LegalCopyright"  "(c) ${APP_PUBLISHER}"

; --------------------------------------------------------------
;  Modern UI 2
; --------------------------------------------------------------
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON   "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"

; NOTE: No MUI_FINISHPAGE_RUN here on purpose.
; The installer runs elevated (RequestExecutionLevel admin), so an app
; launched from the finish page would inherit the elevated token, which
; breaks shell integration such as drag-and-drop (UIPI). Let the user
; start the app themselves from a shortcut instead.

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

; --------------------------------------------------------------
;  Initialization
; --------------------------------------------------------------
Function .onInit
  ; x64-only build
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "This installer requires a 64-bit version of Windows."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Function un.onInit
  SetRegView 64
FunctionEnd

; --------------------------------------------------------------
;  Helper: close the app if it is running
; --------------------------------------------------------------
!macro CloseAppIfRunning
  nsExec::ExecToStack 'taskkill /F /IM ${APP_EXE}'
  Pop $0
  Pop $1
!macroend

; --------------------------------------------------------------
;  Install
; --------------------------------------------------------------
Section "!${APP_NAME} (required)" SecMain
  SectionIn RO

  !insertmacro CloseAppIfRunning

  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"

  ; Application icon for shortcuts
  SetOutPath "$INSTDIR"
  File "${APP_ICON}"

  ; App Paths registration
  WriteRegStr HKLM "${APP_DIR_REGKEY}" "" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${APP_DIR_REGKEY}" "Path" "$INSTDIR"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Add/Remove Programs entry
  WriteRegStr HKLM "${UNINST_REGKEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_REGKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${UNINST_REGKEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_REGKEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKLM "${UNINST_REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_REGKEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "NoRepair" 1

  ; Estimated size (KB) for Add/Remove Programs
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_REGKEY}" "EstimatedSize" "$0"

  ; Start Menu shortcuts
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

; --------------------------------------------------------------
;  Section descriptions
; --------------------------------------------------------------
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain}    "Core application files (required)."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Create a shortcut on the desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; --------------------------------------------------------------
;  Uninstall
; --------------------------------------------------------------
Section "Uninstall"
  !insertmacro CloseAppIfRunning

  ; Remove shortcuts
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  ; Remove installed files
  RMDir /r "$INSTDIR"

  ; Remove registry entries
  DeleteRegKey HKLM "${UNINST_REGKEY}"
  DeleteRegKey HKLM "${APP_DIR_REGKEY}"
SectionEnd
