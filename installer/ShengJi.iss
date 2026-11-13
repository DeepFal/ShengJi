; 声记 安装脚本（Inno Setup 6.5+）
; 由 build.ps1 调用：ISCC.exe /DAppVersion=X.Y.Z installer\ShengJi.iss（版本号来自 git 标签）
; 需要先生成 dist\build\声记.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "声记"
#define AppExe "声记.exe"

[Setup]
AppId={{B8E5A3C2-6D1F-4C7A-9E2B-5F3D8A1C6E40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 安装程序

; 按当前用户安装（装到 %LOCALAPPDATA%\Programs），不需要管理员权限
PrivilegesRequired=lowest
DefaultDirName={autopf}\ShengJi
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1

; 程序运行中时提示先关闭（与程序里的单实例互斥量同名）
AppMutex=ShengJi.SingleInstance.v1
CloseApplications=no

SetupIconFile=..\src\ShengJi\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}_v{#AppVersion}_安装版

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
chs.TaskDesktop=创建桌面快捷方式
chs.TaskAutoStart=开机自动启动（在右下角托盘待命）
chs.TaskGroup=附加选项：
chs.RunNow=立即运行声记

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:TaskGroup}"
Name: "autostart"; Description: "{cm:TaskAutoStart}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked

[Files]
Source: "..\dist\build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\build\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 卸载时总是清掉开机启动项（无论是安装时勾选的，还是在软件设置里打开的）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ShengJi"; ValueType: none; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "ShengJi"; ValueType: string; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只删除设置和日志；录音文件（默认在“文档\声记录音”）不会被删除
Type: filesandordirs; Name: "{userappdata}\ShengJi"
