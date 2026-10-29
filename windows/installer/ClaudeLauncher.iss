; Claude 多开启动器 Windows 安装程序（Inno Setup 6）。由 scripts/setup.ps1 调用 ISCC 生成。
; 按当前用户安装到 %LOCALAPPDATA%\Programs，不需要管理员权限。应用内更新下载本 Setup，以
; /SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1 运行（只显示中文进度窗口），装好后重新打开启动器。
; 卸载只删除本程序安装的文件和更新残留；绝不删除任何账号数据目录
; （%APPDATA%\Claude-*、%LOCALAPPDATA%\Claude-Data、商店版 Packages 目录）或启动器日志/状态目录。

#ifndef AppVersion
  #define AppVersion "1.8.0"
#endif
#ifndef FileVersion
  #define FileVersion "1.8.0.0"
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
; 与清单 minimumOS 一致。
MinVersion=10.0.19041
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ClaudeLauncherSetup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\ClaudeLauncher.exe
UninstallDisplayName=Claude 多开启动器
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 只关闭占用本程序文件的进程（即启动器本身），不涉及 Claude。
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文向导文字，取自 Inno Setup 官方源码 Files/Languages/ChineseSimplified.isl（6.5.0+）。
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

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
; 应用内更新的静默安装：完成后重新打开启动器。
Filename: "{app}\ClaudeLauncher.exe"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
; 自建更新在程序旁留下的备份与中间文件，仅限安装目录内。
Type: files; Name: "{app}\ClaudeLauncher.exe.previous-*.exe"
Type: files; Name: "{app}\ClaudeLauncher.exe.new-*"
Type: files; Name: "{app}\ClaudeLauncher.exe.failed-*.exe"

[Code]
// SPEC 更新与安装：已安装时先正常关闭启动器、静默卸载旧版，确认完成后再安装；失败则中止，旧版保持可用。
// 只关闭启动器本身（按 PID 核对窗口后发送退出消息与 WM_CLOSE），不结束任何进程，不涉及 Claude。
const
  AppKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E3C2B5A-4F1D-4C8E-9A61-2D5B8E0C6F14}_is1';
  LauncherTitle = 'Claude 多开启动器';
  WM_CLOSE = $0010;

var
  OldUninstaller, OldVersion, OldLocation: String;

function FindWindowEx(Parent, After: HWND; ClassName: Longint; Title: String): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function GetWindowThreadProcessId(Wnd: HWND; var ProcessId: DWORD): DWORD;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function RegisterWindowMessage(Name: String): Cardinal;
  external 'RegisterWindowMessageW@user32.dll stdcall';

// 返回从旧安装目录运行的启动器 PID，形如 ",12,34,"；没有则为空。
function RunningLauncherIds(): String;
var
  Locator, Service, Items: Variant;
  I: Integer;
  Path: String;
begin
  Result := '';
  Path := AddBackslash(OldLocation) + 'ClaudeLauncher.exe';
  StringChangeEx(Path, '\', '\\', True);
  StringChangeEx(Path, '''', '\''', True);
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Service := Locator.ConnectServer('.', 'root\CIMV2');
  Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = ''' + Path + '''');
  for I := 0 to Items.Count - 1 do
    Result := Result + ',' + IntToStr(Items.ItemIndex(I).ProcessId);
  if Result <> '' then Result := Result + ',';
end;

// 新版启动器关闭窗口只会隐藏到通知区域，需发送专用退出消息；同时发 WM_CLOSE 兼容旧版本。
procedure CloseLauncherWindows(Ids: String);
var
  Wnd: HWND;
  Pid: DWORD;
  ExitMessage: Cardinal;
begin
  ExitMessage := RegisterWindowMessage('ClaudeMultiLauncher.Exit');
  Wnd := FindWindowEx(0, 0, 0, LauncherTitle);
  while Wnd <> 0 do begin
    Pid := 0;
    GetWindowThreadProcessId(Wnd, Pid);
    if Pos(',' + IntToStr(Pid) + ',', Ids) > 0 then begin
      PostMessage(Wnd, ExitMessage, 0, 0);
      PostMessage(Wnd, WM_CLOSE, 0, 0);
    end;
    Wnd := FindWindowEx(0, Wnd, 0, LauncherTitle);
  end;
end;

function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if RegQueryStringValue(HKCU, AppKey, 'UninstallString', OldUninstaller) then begin
    OldUninstaller := RemoveQuotes(OldUninstaller);
    if not RegQueryStringValue(HKCU, AppKey, 'DisplayVersion', OldVersion) then OldVersion := '';
    if not RegQueryStringValue(HKCU, AppKey, 'InstallLocation', OldLocation) then OldLocation := '';
    Result := SuppressibleMsgBox('检测到已安装 ' + OldVersion + '，将先卸载再安装新版。' + #13#10 +
      '只卸载启动器程序本身，不删除任何账号数据，不退出 Claude。', mbConfirmation, MB_OKCANCEL, IDOK) = IDOK;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Ids: String;
  I, Code: Integer;
begin
  Result := '';
  if OldUninstaller = '' then Exit;
  if OldLocation <> '' then begin
    try
      Ids := RunningLauncherIds();
      if Ids <> '' then begin
        CloseLauncherWindows(Ids);
        for I := 1 to 50 do begin
          if RunningLauncherIds() = '' then Break;
          Sleep(200);
        end;
        if RunningLauncherIds() <> '' then begin
          Result := '无法正常关闭正在运行的 Claude 多开启动器（可能有删除操作未完成）。请手动关闭启动器后重试。已中止安装，旧版仍可使用，Claude 不受影响。';
          Exit;
        end;
      end;
    except
      Result := '无法检查启动器是否正在运行：' + GetExceptionMessage + '。已中止安装，旧版仍可使用。';
      Exit;
    end;
  end;
  // 进度窗口里告诉用户当前在做什么（卸载程序本身静默运行）。
  WizardForm.PreparingLabel.Caption := '正在卸载旧版 ' + OldVersion + '（只卸载启动器程序，账号数据不受影响）…';
  WizardForm.PreparingLabel.Visible := True;
  if not Exec(OldUninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then begin
    Result := '旧版卸载失败（代码 ' + IntToStr(Code) + '）。已中止安装，旧版仍可使用。';
    Exit;
  end;
  // 卸载程序会复制到临时目录继续运行，等卸载项和旧卸载程序文件都消失才算完成。
  for I := 1 to 150 do begin
    if not RegKeyExists(HKCU, AppKey) and not FileExists(OldUninstaller) then Exit;
    Sleep(200);
  end;
  Result := '旧版卸载未在 30 秒内完成，已中止安装。请稍后重试。';
end;
