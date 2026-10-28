#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif
#define AppName "无线外设电量"

[Setup]
AppId=PeripheralBatteryTray
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=wgen95
AppPublisherURL=https://github.com/wgen95/peripheral-battery-tray
AppSupportURL=https://github.com/wgen95/peripheral-battery-tray/issues
DefaultDirName={localappdata}\Programs\PeripheralBattery
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableWelcomePage=yes
DisableReadyPage=yes
UsePreviousTasks=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\PeripheralBattery.exe
SetupIconFile=..\src\PeripheralBattery.App\Assets\App.ico
OutputDir=..\dist
OutputBaseFilename=PeripheralBattery-{#AppVersion}-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误
ButtonBack=< 上一步
ButtonNext=下一步 >
ButtonInstall=安装
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是
ButtonNo=否
ButtonFinish=完成
ButtonBrowse=浏览…
ButtonWizardBrowse=浏览…
ClickNext=点击“下一步”继续，或点击“取消”退出安装
WizardSelectDir=选择安装位置
SelectDirDesc=将 [name] 安装到哪里？
SelectDirLabel3=程序将安装到以下文件夹
SelectDirBrowseLabel=点击“下一步”继续。如需更改位置，请点击“浏览”
DiskSpaceMBLabel=至少需要 [mb] MB 可用空间
WizardSelectTasks=快捷方式
SelectTasksDesc=选择需要创建的快捷方式
SelectTasksLabel2=安装会添加开始菜单入口。桌面快捷方式可按需选择
WizardPreparing=准备安装
PreparingDesc=正在准备安装 [name]
WizardInstalling=正在安装
InstallingLabel=请稍候，正在安装 [name]
StatusExtractFiles=正在复制文件…
StatusCreateIcons=正在创建快捷方式…
StatusCreateIniEntries=正在保存配置…
StatusCreateRegistryEntries=正在注册应用…
StatusSavingUninstall=正在准备卸载程序…
FinishedHeadingLabel=[name] 已安装
FinishedLabelNoIcons=程序已安装。可从开始菜单启动，或在 Windows“已安装的应用”中卸载
FinishedLabel=程序已安装。可从开始菜单启动，或在 Windows“已安装的应用”中卸载
ClickFinish=点击“完成”退出安装程序
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。确定退出吗？
ConfirmUninstall=确定卸载 %1 吗？
UninstallStatusLabel=正在卸载 %1，请稍候
UninstalledAll=%1 已卸载
UninstalledMost=%1 已卸载。部分文件仍在使用，可在关闭程序后删除

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\PeripheralBattery.exe"; Parameters: "--show"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\PeripheralBattery.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PeripheralBattery.exe"; Parameters: "--show"; Description: "启动无线外设电量"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only these product-owned directories; never delete their parent directories.
Type: filesandordirs; Name: "{localappdata}\PeripheralBattery"
Type: filesandordirs; Name: "{%TEMP}\.net\PeripheralBattery"
Type: filesandordirs; Name: "{%TEMP}\.net\PeripheralBattery.Backend"
Type: filesandordirs; Name: "{code:GetCustomBundleCache}"; Check: HasCustomBundleCache
Type: filesandordirs; Name: "{code:GetCustomBackendCache}"; Check: HasCustomBundleCache

[Code]
function HasCustomBundleCache(): Boolean;
begin
  Result := GetEnv('DOTNET_BUNDLE_EXTRACT_BASE_DIR') <> '';
end;

function GetCustomBundleCache(Param: String): String;
begin
  if HasCustomBundleCache() then
    Result := AddBackslash(ExpandFileName(GetEnv('DOTNET_BUNDLE_EXTRACT_BASE_DIR'))) + 'PeripheralBattery'
  else
    Result := ExpandConstant('{%TEMP}\.net\PeripheralBattery');
end;

function GetCustomBackendCache(Param: String): String;
begin
  Result := AddBackslash(ExpandFileName(GetEnv('DOTNET_BUNDLE_EXTRACT_BASE_DIR'))) + 'PeripheralBattery.Backend';
end;

function StopInstalledApp(): Boolean;
var
  ExitCode, Attempt: Integer;
  AppPath: String;
begin
  Result := True;
  if not CheckForMutexes('Local\PeripheralBattery.Tray.v1') then Exit;
  AppPath := ExpandConstant('{app}\PeripheralBattery.exe');
  if FileExists(AppPath) then
    Exec(AppPath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  for Attempt := 1 to 100 do begin
    if not CheckForMutexes('Local\PeripheralBattery.Tray.v1') then Exit;
    Sleep(100);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopInstalledApp() then
    Result := '应用仍在运行。请右键点击托盘图标并选择“退出”，然后重试。';
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopInstalledApp();
  if not Result then
    MsgBox('应用仍在运行。请先从托盘菜单退出，再进行卸载。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PeripheralBattery');
end;
