#define MyAppName "Net Lights"
#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "AryaPaw"
#define MyAppExeName "NetLights.exe"

[Setup]
AppId={{7C2B1A9E-4F31-4A6D-9C2E-1B8E5D2A0F11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) AryaPaw
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
CloseApplications=yes
RestartIfNeededByRun=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Запускать вместе с Windows"; Flags: unchecked

#ifndef PublishDir
#define PublishDir "..\artifacts\publish"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Net Lights"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\Net Lights"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent unchecked
