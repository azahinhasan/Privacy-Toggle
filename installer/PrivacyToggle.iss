; Inno Setup script for PrivacyToggle.
; Build the EXE first (build.ps1 or dotnet publish into ..\publish), then compile:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\PrivacyToggle.iss

#define AppName "PrivacyToggle"
#define AppVersion "1.0.0"
#define AppExe "PrivacyToggle.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{8F6B2C1E-4D3A-4B8E-9C5F-2A7D1E3B6C90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=azahinhasan
AppPublisherURL=https://github.com/azahinhasan/windows-utility-tools
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install: no admin prompt.
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=PrivacyToggle-Setup-{#AppVersion}
SetupIconFile=..\src\PrivacyToggle\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Matches the single-instance mutex in App.xaml.cs, so setup asks to close a running copy.
AppMutex=PrivacyToggle.SingleInstance

[Tasks]
Name: "startup"; Description: "Start PrivacyToggle with Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch PrivacyToggle now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Ask a running copy to quit cleanly (removes its tray icon) before files are deleted.
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitPrivacyToggle"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The app itself may have written the Run entry (Settings > Start with Windows), so always remove it.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
end;
