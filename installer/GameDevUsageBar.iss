; Build with tools/build-installer.ps1. Smoke packages use an isolated identity.
#ifndef Stage
  #error Stage is required
#endif
#ifndef Version
  #error Version is required
#endif
#ifndef Output
  #error Output is required
#endif
#ifndef AppIcon
  #error AppIcon is required
#endif
#ifdef TestId
  #define AppIdentity TestId
  #define ProductName "GameDevUsageBar Installer Test " + TestId
  #define FolderName "GameDevUsageBar-InstallerTest-" + TestId
  #define ShortcutName "GameDevUsageBar Installer Test " + TestId
  #define PackageName "GameDevUsageBar-" + Version + "-win-x64-setup-test-" + TestId
  #define RunningMutex "Local\GameDevUsageBar.InstallerTest." + TestId
#else
  #define AppIdentity "{{A62A6344-CFA1-42C0-933F-C648F614D433}"
  #define ProductName "GameDevUsageBar"
  #define FolderName "GameDevUsageBar"
  #define ShortcutName "GameDevUsageBar"
  #define PackageName "GameDevUsageBar-" + Version + "-win-x64-setup"
  #define RunningMutex "Local\GameDevBar"
#endif

[Setup]
AppId={#AppIdentity}
AppName={#ProductName}
AppVersion={#Version}
AppVerName={#ProductName} {#Version}
AppPublisher=tabztggg
AppPublisherURL=https://github.com/tabztggg/GameDevUsageBar
AppSupportURL=https://github.com/tabztggg/GameDevUsageBar/issues
AppUpdatesURL=https://github.com/tabztggg/GameDevUsageBar/releases
DefaultDirName={localappdata}\Programs\{#FolderName}
DefaultGroupName={#ShortcutName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Output}
OutputBaseFilename={#PackageName}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\GameDevUsageBar.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
AppMutex={#RunningMutex}
UninstallDisplayName={#ProductName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopTask=Create a desktop shortcut
english.AutostartTask=Start GameDevUsageBar when I sign in to Windows
english.OpenApplication=Open GameDevUsageBar
english.AppRunning=GameDevUsageBar is running. Choose Exit in its tray menu, then try again. Your accounts and settings will be preserved.
english.FinishedNote=Accounts, settings and saved credentials stay in your user profile. Uninstalling the app keeps this data and never removes Codex or Claude CLI logins.
chinesesimp.DesktopTask=创建桌面快捷方式
chinesesimp.AutostartTask=登录 Windows 时自动启动 GameDevUsageBar
chinesesimp.OpenApplication=打开 GameDevUsageBar
chinesesimp.AppRunning=GameDevUsageBar 正在运行。请在托盘菜单中选择“退出”后再试。账户和设置将保留。
chinesesimp.FinishedNote=账户、设置和保存的凭据位于您的用户配置目录。卸载保留这些数据，也不会删除 Codex 或 Claude CLI 的登录文件。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopTask}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutostartTask}"

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ShortcutName}"; Filename: "{app}\GameDevUsageBar.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#ShortcutName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#ShortcutName}"; Filename: "{app}\GameDevUsageBar.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\{#ShortcutName}"; Filename: "{app}\GameDevUsageBar.exe"; WorkingDir: "{app}"; Tasks: autostart

[InstallDelete]
; An upgrade can explicitly turn off a previously selected shortcut.
Type: files; Name: "{userdesktop}\{#ShortcutName}.lnk"; Tasks: not desktopicon
Type: files; Name: "{userstartup}\{#ShortcutName}.lnk"; Tasks: not autostart

[Run]
Filename: "{app}\GameDevUsageBar.exe"; Description: "{cm:OpenApplication}"; Flags: postinstall nowait skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('{#RunningMutex}') then
    Result := CustomMessage('AppRunning');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10 + #13#10 + CustomMessage('FinishedNote');
end;
