Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
Name "AiLocalApi"
OutFile "${OUTPUT}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\AiLocalApi"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "AiLocalApi"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "AiLocalApi 安装引导"
VIAddVersionKey "LegalCopyright" "silevilence"
; The embedded Velopack payload is already compressed.
SetCompress off
ShowInstDetails show
!define MUI_ABORTWARNING
!define MUI_CUSTOMFUNCTION_ABORT CheckAbort
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"
Var InnerStarted
Var Existing

Function .onInit
  SetRegView 64
  ReadRegStr $Existing HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AiLocalApi" "InstallLocation"
  ${If} $Existing != ""
    MessageBox MB_OK|MB_ICONEXCLAMATION "已安装 AiLocalApi：$Existing。请从应用检查更新；如需重新安装，请先卸载原实例，持久数据会保留。" /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
FunctionEnd

Function ValidateDirectory
  ${GetRoot} "$INSTDIR" $0
  ${If} $0 == ""
    MessageBox MB_OK|MB_ICONSTOP "请选择绝对路径。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\*.*" 0 writable
  MessageBox MB_OK|MB_ICONSTOP "目标目录已存在且包含文件。请选择新的空目录，避免覆盖其他副本。" /SD IDOK
  SetErrorLevel 2
  Abort
  writable:
  ClearErrors
  CreateDirectory "$INSTDIR"
  FileOpen $0 "$INSTDIR\.ailocal-write-probe" w
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "无法写入 $INSTDIR。请选择当前用户有写入权限的目录。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FileClose $0
  Delete "$INSTDIR\.ailocal-write-probe"
  RMDir "$INSTDIR"
FunctionEnd

Function CheckAbort
  ${If} $InnerStarted == "1"
    MessageBox MB_OK "正在完成安装，请等待安装程序结束。"
    Abort
  ${EndIf}
FunctionEnd

Section "安装"
  Call ValidateDirectory
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Payload.exe "${PAYLOAD}"
  StrCpy $InnerStarted "1"
  GetDlgItem $0 $HWNDPARENT 2
  EnableWindow $0 0
  ExecWait '"$PLUGINSDIR\Payload.exe" --silent --installto "$INSTDIR" --log "$TEMP\AiLocalApi-install.log"' $1
  StrCpy $InnerStarted "0"
  Delete "$PLUGINSDIR\Payload.exe"
  ${If} $1 != 0
    DetailPrint "安装失败，退出码 $1。日志：$TEMP\AiLocalApi-install.log"
    SetErrorLevel 1
    Abort
  ${EndIf}
  IfFileExists "$INSTDIR\Update.exe" 0 failed
  IfFileExists "$INSTDIR\current\AiLocalApi.Desktop.exe" 0 failed
  DetailPrint "安装完成：$INSTDIR"
  Goto done
  failed:
  DetailPrint "未找到完整安装结果。日志：$TEMP\AiLocalApi-install.log"
  SetErrorLevel 1
  Abort
  done:
SectionEnd
