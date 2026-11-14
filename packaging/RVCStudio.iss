#define AppName "RVC Studio"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define AppVersion MyAppVersion
#define AppPublisher "RVC Studio"
#ifndef StageDir
  #define StageDir "output\stage"
#endif

[Setup]
AppId={{C1DE5C7B-2109-4A5D-971B-B34A71112914}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\RVC Studio
DefaultGroupName=RVC Studio
DisableProgramGroupPage=yes
OutputDir=output\installer
OutputBaseFilename=RVC-Studio-NVIDIA-Setup
SetupIconFile=installer-assets\rvc-studio-icon.ico
UninstallDisplayIcon={app}\RVC Studio.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
; Keep the complete offline package in one executable. The resulting file is
; hosted on Hugging Face because GitHub Release assets are limited to 2 GiB.
DiskSpanning=no
WizardStyle=modern
LicenseFile=installer-assets\LICENSE
InfoBeforeFile=INSTALLER-NOTICE.txt
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "vendor\inno\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："
Name: "vbcable"; Description: "安装标准 VB-CABLE 虚拟声卡（VB-Audio Donationware，可自愿捐赠）"; GroupDescription: "音频驱动："; Flags: checkedonce

[Dirs]
Name: "{app}\assets\weights"; Permissions: users-modify
Name: "{app}\assets\indices"; Permissions: users-modify

[Files]
; Keep maintenance inputs first: PrepareToInstall extracts these from the solid archive.
Source: "{#StageDir}\RVC Studio.exe"; DestName: "rvc-installer-maintenance.exe"; Flags: dontcopy
Source: "{#StageDir}\release-manifest.json"; DestName: "rvc-new-release-manifest.json"; Flags: dontcopy
; The old inventory stays authoritative until stale-file cleanup has succeeded.
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "assets\weights\*,assets\indices\*,configs\*,release-manifest.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\assets\weights\*"; DestDir: "{app}\assets\weights"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs
Source: "{#StageDir}\assets\indices\*"; DestDir: "{app}\assets\indices"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs
Source: "{#StageDir}\configs\*.py"; DestDir: "{app}\configs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\configs\*.json"; DestDir: "{app}\configs"; Flags: onlyifdoesntexist
Source: "{#StageDir}\configs\v1\*.json"; DestDir: "{app}\configs\v1"; Flags: ignoreversion
Source: "{#StageDir}\configs\v2\*.json"; DestDir: "{app}\configs\v2"; Flags: ignoreversion
Source: "vendor\vb-cable\official-package\*"; DestDir: "{tmp}\rvcstudio-vbcable"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall; Tasks: vbcable; Check: ShouldInstallVBCable

[Icons]
Name: "{group}\RVC Studio"; Filename: "{app}\RVC Studio.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\RVC Studio"; Filename: "{app}\RVC Studio.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\VB-CABLE 官网与捐赠"; Filename: "https://vb-audio.com/Cable/"

[Run]
Filename: "{app}\runtime\python.exe"; Parameters: "-I ""{app}\tools\package_healthcheck.py"" --root ""{app}"" --require-cuda"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; StatusMsg: "正在清理旧文件并验证 CUDA、RVC 引擎和默认模型…"; BeforeInstall: ReconcileInstalledFiles
Filename: "{tmp}\rvcstudio-vbcable\VBCABLE_Setup_x64.exe"; Parameters: "-i -h"; WorkingDir: "{tmp}\rvcstudio-vbcable"; Flags: waituntilterminated; StatusMsg: "正在安装标准 VB-CABLE 虚拟声卡…"; Tasks: vbcable; Check: ShouldInstallVBCable
Filename: "{app}\RVC Studio.exe"; Description: "启动 RVC Studio"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchImmediately

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\RunOnce"; ValueType: string; ValueName: "RVC Studio"; ValueData: """{app}\RVC Studio.exe"""; Flags: uninsdeletevalue; Tasks: vbcable; Check: ShouldInstallVBCable

[UninstallDelete]
Type: files; Name: "{app}\release-manifest.json"
Type: files; Name: "{app}\installer-cleanup.log"

[Code]
var
  VBCablePresentAtStart: Boolean;
  CleanupApplied: Boolean;

function IsVBCableInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\VBAudioVACMME');
end;

function ShouldInstallVBCable: Boolean;
begin
  Result := WizardIsTaskSelected('vbcable') and (not VBCablePresentAtStart);
end;

function CanLaunchImmediately: Boolean;
begin
  Result := CleanupApplied and (not ShouldInstallVBCable);
end;

procedure ReconcileInstalledFiles;
var
  ResultCode: Integer;
begin
  if CleanupApplied then
    Exit;
  if not Exec(ExpandConstant('{tmp}\rvc-installer-maintenance.exe'),
    ExpandConstant('--installer-cleanup apply "{tmp}\rvc-cleanup-plan.json"'),
    ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('无法启动旧文件清理。请关闭软件后重新运行完整安装包。');
  if ResultCode <> 0 then
  begin
    Log('Old-file cleanup failed: ' + IntToStr(ResultCode));
    RaiseException('旧文件清理未完成。请查看安装目录中的 installer-cleanup.log，关闭其他实例后重新运行完整安装包。');
  end;
  CleanupApplied := True;
end;

function NeedRestart: Boolean;
begin
  Result := ShouldInstallVBCable;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('RVC Studio NVIDIA 仅支持 64 位 Windows。', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  if not RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\nvlddmkm') then
  begin
    Result := MsgBox(
      '未检测到 NVIDIA 显卡驱动。该版本需要 NVIDIA RTX 显卡和兼容 CUDA 12.8 的驱动。是否仍要继续安装？',
      mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

procedure InitializeWizard;
begin
  VBCablePresentAtStart := IsVBCableInstalled;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\.rvc-update\journal.json')) then
  begin
    Result := '检测到未完成的软件更新。请先启动 RVC Studio 完成更新恢复，再运行完整安装包。';
    Exit;
  end;
  try
    ExtractTemporaryFile('rvc-installer-maintenance.exe');
    ExtractTemporaryFile('rvc-new-release-manifest.json');
    if not Exec(ExpandConstant('{tmp}\rvc-installer-maintenance.exe'),
      ExpandConstant('--installer-cleanup prepare "{app}" "{tmp}\rvc-new-release-manifest.json" "{tmp}\rvc-cleanup-plan.json"'),
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := '无法准备旧文件清理。尚未改动已安装文件。'
    else if ResultCode <> 0 then
      Result := '安装文件清单验证失败，已停止覆盖安装以保护现有文件。详情见本地 Updates\update.log。';
  except
    Result := '无法提取或验证安装清理工具。尚未改动已安装文件。';
  end;
end;
