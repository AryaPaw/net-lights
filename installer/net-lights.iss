#define MyAppName "Net Lights"
#ifndef MyAppVersion
#define MyAppVersion "1.5.1"
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
SetupIconFile=..\src\NetLights.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=NetLights-Setup-win-x64-{#MyAppVersion}
SetupLogging=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AppMutex=Local\NetLights.SingleInstance

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

#ifndef PublishDir
#define PublishDir "..\artifacts\publish"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Net Lights"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent; Check: not IsUpdateAgentInstall
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent; Check: not IsUpdateAgentInstall

[Code]
function IsUpdateAgentInstall: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NETLIGHTSUPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "NetLights"; Flags: uninsdeletevalue dontcreatekey
