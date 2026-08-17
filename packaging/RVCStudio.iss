#define AppName "RVC Studio"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define AppVersion MyAppVersion
#define AppPublisher "RVC Studio"
#define StageDir "output\stage"

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
SetupIconFile=installer-assets\rvc-studio-icon-transparent.ico
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

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "vendor\vb-cable\official-package\*"; DestDir: "{tmp}\rvcstudio-vbcable"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall; Tasks: vbcable; Check: ShouldInstallVBCable

[Icons]
Name: "{group}\RVC Studio"; Filename: "{app}\RVC Studio.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\RVC Studio"; Filename: "{app}\RVC Studio.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\VB-CABLE 官网与捐赠"; Filename: "https://vb-audio.com/Cable/"

[Run]
Filename: "{app}\runtime\python.exe"; Parameters: "-I ""{app}\tools\package_healthcheck.py"" --root ""{app}"" --require-cuda"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; StatusMsg: "正在验证 CUDA、RVC 引擎和默认模型…"
Filename: "{tmp}\rvcstudio-vbcable\VBCABLE_Setup_x64.exe"; Parameters: "-i -h"; WorkingDir: "{tmp}\rvcstudio-vbcable"; Flags: waituntilterminated; StatusMsg: "正在安装标准 VB-CABLE 虚拟声卡…"; Tasks: vbcable; Check: ShouldInstallVBCable
Filename: "{app}\RVC Studio.exe"; Description: "启动 RVC Studio"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: CanLaunchImmediately

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\RunOnce"; ValueType: string; ValueName: "RVC Studio"; ValueData: """{app}\RVC Studio.exe"""; Flags: uninsdeletevalue; Tasks: vbcable; Check: ShouldInstallVBCable

[Code]
var
  VBCablePresentAtStart: Boolean;

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
  Result := not ShouldInstallVBCable;
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
