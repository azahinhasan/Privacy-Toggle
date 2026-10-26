; Inno Setup script for PrivacyToggle.
; Build the EXE first (build.ps1 or dotnet publish into ..\publish), then compile:
;   ISCC.exe installer\PrivacyToggle.iss
;
; Installs for all users into Program Files (needs admin once) and sets up the camera helper
; service, which turns cameras on/off for the tray app. The tray app itself runs without admin.

#define AppName "PrivacyToggle"
; build.ps1 passes /DAppVersion=<Version from PrivacyToggle.csproj>; this default is only for compiling by hand.
#ifndef AppVersion
  #define AppVersion "1.7.0"
#endif
#define AppExe "PrivacyToggle.exe"
#define AppId "{8F6B2C1E-4D3A-4B8E-9C5F-2A7D1E3B6C90}"
#define ServiceName "PrivacyToggleHelper"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=azahinhasan
AppPublisherURL=https://github.com/azahinhasan/Privacy-Toggle
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Admin is needed once: the camera helper service must live in Program Files.
PrivilegesRequired=admin
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
; Tells Explorer to reload icons at the end of setup, so an upgrade with a new icon doesn't keep showing the old one.
ChangesAssociations=yes
; A running copy is closed by the [Code] section below (PrivacyToggle.exe --exit) instead of AppMutex,
; which would make setup/uninstall stop and ask (or silently abort) while the tray app is running.

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
; Windows caches icons by file path; refresh the cache so the taskbar and shortcuts pick up a changed app icon.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden waituntilterminated skipifdoesntexist runasoriginaluser
Filename: "{app}\{#AppExe}"; Parameters: "--install-service"; StatusMsg: "Setting up camera control..."; \
    Flags: runhidden waituntilterminated
; Start the tray app as the signed-in user, not elevated, with Settings open (--show ignores "Start minimized")
; and its tray icons on the taskbar rather than in the overflow menu (--promote-tray).
Filename: "{app}\{#AppExe}"; Parameters: "--show --promote-tray"; Description: "Launch PrivacyToggle now"; Flags: nowait postinstall skipifsilent runasoriginaluser
; In-app updates run setup silently with /relaunch=1: start the updated app again afterwards.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[UninstallRun]
; Turns back on any camera PrivacyToggle turned off, then removes the helper service.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHelper"

[Code]
// True when started by PrivacyToggle's in-app updater (setup.exe /SILENT /relaunch=1).
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// Asks a running copy to quit cleanly (it removes its tray icon) and waits for it to exit.
procedure CloseRunningApp(const ExePath: String);
var
  ResultCode: Integer;
begin
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Version 1.0 installed per-user (no admin) into AppData; remove that copy so there aren't two.
procedure RemoveOldPerUserInstall();
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1', 'UninstallString', Uninstaller) then
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE,
         ewWaitUntilTerminated, ResultCode);
end;

// Before installing over an existing copy (upgrade): close the app and stop the helper so the EXE isn't locked.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  CloseRunningApp(AddBackslash(WizardDirValue) + '{#AppExe}');
  RemoveOldPerUserInstall();
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// Before uninstalling.
function InitializeUninstall(): Boolean;
begin
  CloseRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The app itself may have written the Run entry (Settings > Start with Windows), so always remove it.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
end;
