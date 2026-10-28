; Claude 多开启动器 Windows 安装程序（Inno Setup 6）。由 scripts/setup.ps1 调用 ISCC 生成。
; 按当前用户安装到 %LOCALAPPDATA%\Programs，不需要管理员权限，自建更新可直接替换程序。
; 卸载只删除本程序安装的文件和更新残留；绝不删除任何账号数据目录
; （%APPDATA%\Claude-*、%LOCALAPPDATA%\Claude-Data、商店版 Packages 目录）或启动器日志/状态目录。

#ifndef AppVersion
  #define AppVersion "1.6"
#endif
#ifndef FileVersion
  #define FileVersion "1.6.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\bin\ClaudeLauncher.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\setup"
#endif

[Setup]
AppId={{7E3C2B5A-4F1D-4C8E-9A61-2D5B8E0C6F14}
AppName=Claude 多开启动器
AppVersion={#AppVersion}
AppVerName=Claude 多开启动器 {#AppVersion}
VersionInfoVersion={#FileVersion}
VersionInfoProductName=Claude 多开启动器
VersionInfoDescription=Claude 多开启动器安装程序
DefaultDirName={autopf}\Claude 多开启动器
DefaultGroupName=Claude 多开启动器
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ClaudeLauncher-Setup-{#AppVersion}
SetupIconFile=..\assets\claude.ico
UninstallDisplayIcon={app}\ClaudeLauncher.exe
UninstallDisplayName=Claude 多开启动器
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 只关闭占用本程序文件的进程（即启动器本身），不涉及 Claude。
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "ClaudeLauncher.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\Claude 多开启动器"; Filename: "{app}\ClaudeLauncher.exe"
Name: "{group}\卸载 Claude 多开启动器"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Claude 多开启动器"; Filename: "{app}\ClaudeLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClaudeLauncher.exe"; Description: "{cm:LaunchProgram,Claude 多开启动器}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 自建更新在程序旁留下的备份与中间文件，仅限安装目录内。
Type: files; Name: "{app}\ClaudeLauncher.exe.previous-*.exe"
Type: files; Name: "{app}\ClaudeLauncher.exe.new-*"
Type: files; Name: "{app}\ClaudeLauncher.exe.failed-*.exe"
