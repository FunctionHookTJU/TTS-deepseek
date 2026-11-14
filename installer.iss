; ============================================================
; DeepSeek 语音合成（TTS）安装包脚本  ·  Inno Setup 6
;
; 用法：
;   1. 先运行 dotnet publish 生成 publish\DeepSeekTTS.exe
;   2. ISCC installer.iss
;   产物：dist\DeepSeekTTS-Setup-<版本>.exe
; ============================================================

#define AppName        "DeepSeek 语音合成"
#define AppNameEn      "DeepSeek TTS"
#define AppVersion     "2.0.0"
#define AppPublisher   "DeepSeek TTS"
#define AppExeName     "DeepSeekTTS.exe"
#define SourceDir      "publish"
#define OutputDir      "dist"

[Setup]
AppId={{8F1C3A72-4B6E-4D91-9A2C-5E7D3B1A9C48}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; 安装包输出
OutputDir={#OutputDir}
OutputBaseFilename=DeepSeekTTS-Setup-{#AppVersion}
SetupIconFile=assets\logo.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard.bmp
WizardSmallImageFile=assets\banner.bmp

; 权限：安装到 Program Files 需要管理员；配置文件写在用户目录，无需提权即可保存
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 仅 64 位
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; 最低系统要求
MinVersion=10.0.17763

; 允许用户在安装时关闭自动启动
AllowNoIcons=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "quicklaunchicon"; Description: "创建快速启动栏图标"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序（单文件自包含）
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 说明文档
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
; 图标备份（供卸载等场景引用）
Source: "assets\logo.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\logo.ico"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\logo.ico"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理安装目录下可能残留的文件（用户配置在 %APPDATA%，默认保留）
Type: filesandordirs; Name: "{app}"

[Code]
{ 卸载时询问是否一并删除用户配置与缓存 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Prompt: String;
  Res: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\DeepSeekTTS');
    if DirExists(DataDir) then
    begin
      { 静默卸载不弹窗，默认保留用户数据（更安全，避免误删 API Key） }
      if UninstallSilent then
        exit;

      Prompt := 'Delete saved API Key and temporary cache?';
      Prompt := Prompt + #13#10 + #13#10 + DataDir;
      Res := MsgBox(Prompt + #13#10 + #13#10 +
                    'Choose No to keep the data for next install.',
                    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Res = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
