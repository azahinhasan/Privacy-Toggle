# PrivacyToggle

<img src="docs/icon-app.png" width="64" align="right">

A small Windows tray app for quick privacy toggles, using one click or a global hotkey.

- **Microphone (available now):** mutes or unmutes every microphone system-wide. While muted, no app (Zoom, Discord, Teams, browsers, OBS…) receives any audio.
- **Camera (planned):** a matching toggle for webcams.

Tray icon for the microphone toggle:

| Live | Muted | No mic |
|:---:|:---:|:---:|
| <img src="docs/icon-on.png" width="48"> | <img src="docs/icon-off.png" width="48"> | <img src="docs/icon-none.png" width="48"> |

## Using it

- **Left-click** the tray icon to toggle mute. **Right-click** for Mute/Unmute, Settings…, Start with Windows and Exit.
- **Hotkey:** `Ctrl+Alt+M` by default. It works even while a fullscreen app is focused.
- Hover over the tray icon to see the current state and hotkey.
- Launching `PrivacyToggle.exe` again opens the Settings window of the copy that is already running.
- `PrivacyToggle.exe --exit` closes a running copy cleanly.

## Settings

Every change saves immediately to `%AppData%\PrivacyToggle\settings.json`.

- **General:** Start with Windows; start minimized to tray; the mic state on start (keep last / always unmuted / always muted); optionally double-click the tray icon to open Settings. When that option is on, single clicks wait for the double-click time, so it's off by default.
- **Hotkey:** click the box and press a combination (at least one of Ctrl/Alt/Shift/Win plus a key). If another app already owns that combination, you get a "Hotkey already in use" warning and the old hotkey stays.
  - *Toggle* mode: press once to mute, press again to unmute.
  - *Push-to-talk* mode: mics stay muted and are live only while you hold the hotkey. Exiting PrivacyToggle in this mode unmutes them.
- **Feedback:** click sounds, a Windows notification on each toggle, and an on-screen "MIC OFF" badge. The badge stays on top and lets clicks pass through.
- **Microphones:** mute all microphones, or only the ones you select. *Enforce mute* re-mutes any mic that something else unmutes while PrivacyToggle has it muted. When enforce mute is off, PrivacyToggle follows the change instead.

PrivacyToggle also follows changes made elsewhere: Windows Sound settings, headset mute buttons, and mics plugged in or unplugged. A mic plugged in while muted gets muted too. Nothing needs admin rights.

## Build

Requires the .NET 8 SDK (or newer).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `publish\PrivacyToggle.exe`, a single self-contained file that doesn't need .NET installed. If [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed, the script also builds `installer\Output\PrivacyToggle-Setup-1.0.0.exe`. The installer is per-user (no admin rights), adds a Start Menu shortcut, offers "Start with Windows" and "Launch now", and its uninstaller removes the Run-key entry.

For development: `dotnet run --project src/PrivacyToggle`.

To change the icons, edit and rerun `tools\make-icons.ps1`. It draws them with System.Drawing and writes multi-size `.ico` files (16–256 px) into `src\PrivacyToggle\Assets`.

## Layout

```
src/PrivacyToggle/
  App.xaml(.cs)          startup, single instance, --exit
  AppController.cs       tray icon + glue between the services
  Services/              MicService (Core Audio), HotkeyService (RegisterHotKey),
                         PushToTalkService / KeyCaptureHook (low-level keyboard hook),
                         StartupService (Run key), SettingsService (JSON), SoundService, ThemeService
  Views/                 SettingsWindow, HotkeyRecorderBox, OverlayWindow
  Tray/TrayMenu.xaml     right-click menu
  Themes/                light/dark brushes (follows the Windows app theme) + control styles
installer/PrivacyToggle.iss    Inno Setup script
tools/make-icons.ps1     icon generator
```
