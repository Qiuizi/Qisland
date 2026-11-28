; Qisland 安装程序（Inno Setup 6.5+）
; 简体中文界面：Inno Setup 官方安装包不带 ChineseSimplified.isl（只在 issrc 的 Unofficial 译本里），
; 所以仓库附带一份 installer\Languages\ChineseSimplified.isl（取自 issrc is-6_7_3）；编译器目录里有同名文件时优先用编译器的。
;
; 平时不用手动编译：scripts\release.ps1 检测到 ISCC.exe 时会基于自带运行时的发布目录自动打出
; releases\Qisland-<版本>-Setup.exe。手动编译（在仓库根目录）：
;   ISCC.exe /DAppVersion=5.0.0 /DAppVersionNumeric=5.0.0.0 /DSourceDir=..\publish\Qisland-full installer\Qisland.iss
;
; 约定（和 src\Qisland.Core\SetupInfo.cs 一致，自检逐项比对）：
; · AppId 固定，覆盖安装 / 升级 / 卸载都认它；
; · 默认装到 {localappdata}\Programs\Qisland，免管理员；程序目录可写，应用内自动更新能直接覆盖；
; · 装的是自带 .NET 8 运行时的全量发布（有 hostfxr.dll），更新器据此只挑 -full-x64.zip；
; · 用户数据在 {userappdata}\Qisland，卸载时默认保留，只在你点「是」时才删。

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\Qisland-full"
#endif
#ifndef OutputDir
  #define OutputDir "..\releases"
#endif
#define AppName "Qisland"
#define AppExe "Qisland.exe"
#define AppIdGuid "835DC0CA-BDF5-4972-AAA4-1BC585564F72"

#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
  #define HaveChinese
  #define ChineseIsl "compiler:Languages\ChineseSimplified.isl"
#elif FileExists(AddBackslash(SourcePath) + "Languages\ChineseSimplified.isl")
  #define HaveChinese
  #define ChineseIsl AddBackslash(SourcePath) + "Languages\ChineseSimplified.isl"
#else
  #pragma message "ChineseSimplified.isl not found (neither in the compiler nor in installer\Languages). The installer will be English only."
#endif

[Setup]
AppId={{{#AppIdGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Qisland
AppPublisherURL=https://github.com/Qiuizi/Qisland
AppSupportURL=https://github.com/Qiuizi/Qisland/issues
AppUpdatesURL=https://github.com/Qiuizi/Qisland/releases
AppCopyright=MIT License
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={localappdata}\Programs\Qisland
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Qisland-{#AppVersion}-Setup
SetupIconFile=..\src\Qisland.App\Assets\qisland.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter={#AppExe}
RestartApplications=no
ShowLanguageDialog=auto
SetupLogging=yes

[Languages]
#ifdef HaveChinese
Name: "chinesesimplified"; MessagesFile: "{#ChineseIsl}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.GroupExtras=Extras:
english.TaskDesktop=Create a &desktop shortcut
english.TaskAutostart=Start Qisland when I sign in to Windows
english.LaunchNow=Launch Qisland now
english.AppRunning=Qisland is running. It needs to close before setup can continue.%n%nClick OK to close it now (your data is kept).
english.AppRunningUninstall=Qisland is running. Click OK to close it and continue uninstalling.
english.AskDeleteData=Also delete your Qisland settings and data?%n%n%1%n%nChoose No to keep them (recommended if you may reinstall later).
#ifdef HaveChinese
chinesesimplified.GroupExtras=附加选项：
chinesesimplified.TaskDesktop=创建桌面快捷方式(&D)
chinesesimplified.TaskAutostart=登录 Windows 时自动启动 Qisland
chinesesimplified.LaunchNow=立即启动 Qisland
chinesesimplified.AppRunning=Qisland 正在运行，需要先关闭它才能继续安装。%n%n点「确定」自动关闭（你的数据不会受影响）。
chinesesimplified.AppRunningUninstall=Qisland 正在运行。点「确定」关闭它并继续卸载。
chinesesimplified.AskDeleteData=要一并删除 Qisland 的设置和数据吗？%n%n%1%n%n选「否」会保留它们（以后重装还能接着用，推荐）。
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:GroupExtras}"; Flags: unchecked
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:GroupExtras}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 和程序自己的约定一致：HKCU\...\Run 里的 Qisland = "<程序>" --autostart；卸载时删掉
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Qisland"; ValueData: """{app}\{#AppExe}"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function DataDir(): String;
begin
  Result := ExpandConstant('{userappdata}\Qisland');
end;

function IsAppRunning(): Boolean;
var
  Tmp: String;
  S: AnsiString;
  Code: Integer;
begin
  Result := False;
  Tmp := AddBackslash(GetTempDir()) + 'qisland-setup-tasklist.txt';
  DeleteFile(Tmp);
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#AppExe}" /NH > "' + Tmp + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    if LoadStringFromFile(Tmp, S) then
      Result := Pos(Lowercase('{#AppExe}'), Lowercase(String(S))) > 0;
  DeleteFile(Tmp);
end;

{ 先礼后兵：普通 taskkill 请它自己退出，等 5 秒还在才强制结束 }
procedure CloseRunningApp();
var
  Code, I: Integer;
begin
  if not IsAppRunning() then Exit;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 20 do
  begin
    if not IsAppRunning() then Exit;
    Sleep(250);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(600);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsAppRunning() then
  begin
    if not WizardSilent() then
      if MsgBox(CustomMessage('AppRunning'), mbConfirmation, MB_OKCANCEL) <> IDOK then
      begin
        Result := CustomMessage('AppRunning');
        Exit;
      end;
    CloseRunningApp();
  end;
end;

{ 只删指向本安装目录的自启项，别的安装（比如解压版）留着 }
procedure RemoveRunValueIfOurs();
var
  V: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'Qisland', V) then
    if Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(V)) > 0 then
      RegDeleteValue(HKCU, RunKey, 'Qisland');
end;

{ 托盘里开过「登录即启动」会建一个名为 Qisland 的计划任务；指向本目录才尝试删（删不掉不影响卸载） }
procedure RemoveLogonTaskIfOurs();
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  StringChangeEx(Exe, '''', '''''', True);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "$t = Get-ScheduledTask -TaskName Qisland -ErrorAction SilentlyContinue; ' +
    'if ($t -and $t.Actions[0].Execute -ieq ''' + Exe + ''') { Unregister-ScheduledTask -TaskName Qisland -Confirm:$false -ErrorAction SilentlyContinue }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ 不勾「自动启动」：写下程序认的关闭标记（%APPDATA%\Qisland\autostart.off），免得第一次启动又把自己加回去 }
procedure ApplyAutostartChoice();
var
  Off: String;
begin
  Off := DataDir() + '\autostart.off';
  if WizardIsTaskSelected('autostart') then
  begin
    if FileExists(Off) then DeleteFile(Off);
  end
  else
  begin
    ForceDirectories(DataDir());
    SaveStringToFile(Off, '', False);
    RemoveRunValueIfOurs();
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then ApplyAutostartChoice();
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsAppRunning() then
  begin
    if not UninstallSilent() then
      if MsgBox(CustomMessage('AppRunningUninstall'), mbConfirmation, MB_OKCANCEL) <> IDOK then
      begin
        Result := False;
        Exit;
      end;
    CloseRunningApp();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveRunValueIfOurs();
    RemoveLogonTaskIfOurs();
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    { 用户数据默认保留；静默卸载从不删 }
    if DirExists(DataDir()) and (not UninstallSilent()) then
      if MsgBox(FmtMessage(CustomMessage('AskDeleteData'), [DataDir()]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir(), True, True, True);
    { 应用内自动更新后来加进程序目录的文件不在安装记录里：目录名确实是 Qisland 时整个清掉，不会误删别的目录 }
    if CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))), 'Qisland') = 0 then
      DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;
