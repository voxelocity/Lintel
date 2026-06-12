; Inno Setup script for Lintel — builds installer\LintelSetup.exe
; Compile with:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Lintel.iss

#define MyAppName "Lintel"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "voxelocity"
#define MyAppURL "https://github.com/voxelocity/Lintel"
#define MyAppExeName "Lintel.exe"

[Setup]
; Stable AppId so upgrades/uninstall are tracked across versions.
AppId={{B7A4F2E0-9C3D-4E51-A8F6-1D2C3B4A5E60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Per-user install — no administrator rights required.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=.
OutputBaseFilename=LintelSetup
SetupIconFile=..\assets\lintel.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
AppMutex=Lintel.SingleInstance.A7F3

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\Lintel.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\LintelUpdater.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; Make sure the running app is closed before uninstalling, then remove it.
[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillLintel"
