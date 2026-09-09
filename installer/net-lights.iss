#define MyAppName "Net Lights"
#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "AryaPaw"
#define MyAppURL "https://github.com/AryaPaw/net-lights"
#define MyAppExeName "NetLights.exe"

[Setup]
AppId={{7C2B1A9E-4F31-4A6D-9C2E-1B8E5D2A0F11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) AryaPaw
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\NetLights
DefaultGroupName=Net Lights
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=NetLights-Setup-win-x64-{#MyAppVersion}
SetupLogging=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Запускать вместе с Windows"

#ifndef PublishDir
#define PublishDir "..\artifacts\publish"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Net Lights"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\Net Lights"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent

[Code]
function TaskKillImage(const ImageName: String): Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ImageName + ' /T', '', SW_HIDE, ewWaitUntilTerminated, Result);
end;

function WaitUntilAppExited: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to 30 do
  begin
    if (TaskKillImage('NetLights.exe') = 128) and (TaskKillImage('NetLights.UpdateAgent.exe') = 128) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Result := (TaskKillImage('NetLights.exe') = 128) and (TaskKillImage('NetLights.UpdateAgent.exe') = 128);
end;

function LockedAppMessage: String;
begin
  Result := 'Net Lights всё ещё запущена, файлы заняты. Завершите её через «Выход» в трее и запустите установку снова.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  if WaitUntilAppExited then
    Result := ''
  else
    Result := LockedAppMessage;
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitUntilAppExited;
  if not Result then
    MsgBox(LockedAppMessage, mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    WaitUntilAppExited;
end;
