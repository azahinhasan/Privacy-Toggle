<div align="center">

<img src="docs/icon-app.png" alt="PrivacyToggle icon" width="100">

# PrivacyToggle

**Kill your microphone, camera and internet with one click or one shortcut, from the Windows tray.**

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)
[![Latest release](https://img.shields.io/github/v/release/azahinhasan/Privacy-Toggle?label=version&color=blue)](https://github.com/azahinhasan/Privacy-Toggle/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

| | Live | Off | None found | Shortcut(Default) |
|---|:---:|:---:|:---:|:---:|
| 🎙️ **Microphone** | <img src="docs/icon-on.png?v=1.3.0" alt="on" width="42"> | <img src="docs/icon-off.png?v=1.3.0" alt="off" width="42"> | <img src="docs/icon-none.png?v=1.3.0" alt="none" width="42"> | `Ctrl+Alt+M` |
| 📷 **Camera** | <img src="docs/icon-cam-on.png?v=1.3.0" alt="on" width="42"> | <img src="docs/icon-cam-off.png?v=1.3.0" alt="off" width="42"> | <img src="docs/icon-cam-none.png?v=1.3.0" alt="none" width="42"> | `Ctrl+Alt+V` |
| 🌐 **Network** | <img src="docs/icon-net-on.png?v=1.6.0" alt="on" width="42"> | <img src="docs/icon-net-off.png?v=1.6.0" alt="off" width="42"> | <img src="docs/icon-net-none.png?v=1.6.0" alt="none" width="42"> | `Ctrl+Alt+N` |

</div>



## Features

### 🎙️ Microphone
- **System-wide mute.** No app (Zoom, Discord, Teams, browsers, OBS…) gets any audio while muted.
- **Toggle or push-to-talk**, with a global shortcut that works even in fullscreen apps.

### 📷 Camera
- **Real camera off.** Disables the webcam in Windows, like Device Manager does. Apps lose the picture at once and can't reopen it. If a program won't let go, PrivacyToggle tells you which one and asks before closing it.

### 🌐 Network
- **Internet kill switch.** Turns your network adapters (Wi-Fi, Ethernet, mobile broadband) off and back on, like disabling them in Windows. Every app loses the internet at once, not just the browser. If you're connected over Remote Desktop, it asks first, so you don't lock yourself out.
- **Per-app network use.** See which apps are using the internet right now, how fast, and how much data each one has moved over the last 7 days. Counted from Windows' own network event tracing (the source Resource Monitor uses), so the numbers are exact, not estimates.
- **Taskbar speed meter.** Your current download and upload speed, right next to the tray icons. Click it to open its settings. It slides in and out with an auto-hiding taskbar, steps aside for fullscreen games and videos, and works with the taskbar on any side of the screen. Show both speeds, or only one.

### 🛡️ For every device
- **Enforce off.** Re-mutes a mic, re-disables a camera or turns the network back off when something else switches it on, including headset buttons and newly plugged-in devices.
- **Per-device control.** Target all mics, cameras or network adapters, or only the ones you pick.
- **Start state.** Choose whether each device starts on, off, or the way you left it.
- **Alerts.** A Windows notification or a small corner badge each time a device switches, plus optional `MIC OFF` / `CAM OFF` / `NET OFF` badges that stay on screen while it's off (clicks pass through them). Set separately for each device, each badge in its own screen corner.

### 📊 Activity (Beta)
- **Right now:** a card for the mic, camera and network showing which apps are using each one, today's total, and a quick action: *Mute/Unmute*, *Turn off/Turn on*, *Disconnect/Connect*.
- **History:** a timeline with one lane each for the mic, camera and network (1 hour to 7 days), or a list. Filter by device, sort, and see totals for the selected range. Time while a device is muted or off isn't counted.
- Everything stays on your PC (see [Privacy](#privacy)).

### ⚙️ And
- **Light and dark themes**, or follow Windows. Click sounds can be turned off.
- **In-app updates.** *Settings → About* checks GitHub for a new release, then downloads, verifies and installs it.

##


<img width="850" height="530" alt="Screenshot 2026-10-28 153407" src="https://github.com/user-attachments/assets/8b95a63f-67ef-439f-bedc-fb0bb0e9c038" />
<img width="850" height="700" alt="Screenshot 2026-10-28 153505" src="https://github.com/user-attachments/assets/f436de7a-11a9-4d7c-9867-0b9e644c6339" />
<img width="850" height="530" alt="Screenshot 2026-10-28 154153" src="https://github.com/user-attachments/assets/49cd0698-d36c-44c2-b5a3-d51b69cdd837" />


## Install

Download `PrivacyToggle-Setup-<version>.exe` from the [latest release](https://github.com/azahinhasan/Privacy-Toggle/releases/latest) and run it. Settings opens when setup finishes.

> [!NOTE]
> Camera control, the network kill switch and per-app network use need the small **PrivacyToggle Helper** service, which the installer sets up (one admin prompt). The tray app itself never runs as admin. Mic control and the taskbar speed meter work without the service.

**Requirements:** Windows 10 or 11, 64-bit. Nothing else to install; .NET is included.

## Usage

- **Left-click** a tray icon to switch it. **Right-click** for all controls, Settings, *Start with Windows* and Exit.
- **Settings** pages:
  - **Overview:** every device at a glance; click a card to open its page.
  - **Activity:** live use and history.
  - **Microphone / Camera / Network:** global shortcut, devices, start state, tray icon and alerts for each. The Network page also has the taskbar speed meter.
  - **General:** startup, appearance and sound.
  - **About:** version and updates.
- Changes save instantly to `%AppData%\PrivacyToggle\settings.json`.
- Launching the app again opens Settings of the running copy. `PrivacyToggle.exe --exit` closes it.

## Privacy

PrivacyToggle has no accounts, analytics, ads or telemetry. It never records audio or video, and it never looks at what you send or receive over the network, only how much each app moves. Everything it records stays on your PC:

| File | What's in it |
|---|---|
| `%AppData%\PrivacyToggle\settings.json` | Your settings |
| `%AppData%\PrivacyToggle\usage.json` | Which apps used the mic and camera, and when (7 days) |
| `%AppData%\PrivacyToggle\network-history.json` | How much data each app sent and received (7 days) |
| `%ProgramData%\PrivacyToggle\` | The devices PrivacyToggle turned off, so it can turn them back on |

The only connection the app makes is to GitHub, to check for and download updates. Full details: [PRIVACY.md](PRIVACY.md).

## FAQ and known issues

Why an app shows as using the camera when you didn't turn it on, NVIDIA Broadcast, and other questions: see [Q&A](Q&A.md).

## 🛠️ Build

Requires the .NET 8 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1   # release build + installer
dotnet run --project src/PrivacyToggle               # dev run
```

- `publish\PrivacyToggle.exe`: self-contained single file, no .NET install needed.
- `installer\Output\PrivacyToggle-Setup-<version>.exe`: built if [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed. Installs the helper service, offers *Start with Windows*, and the uninstaller turns cameras and network adapters back on and removes everything.
- The version comes from `<Version>` in `src\PrivacyToggle\PrivacyToggle.csproj`; `build.ps1` passes it to the installer.
- Icons (tray, app and the images in `docs\`) are generated by `tools\make-icons.ps1`.

### Diagnostics

Command-line switches for troubleshooting (run from an **admin** terminal; in Command Prompt add `| more` to see the output):

| Switch | What it does |
|---|---|
| `--list-cameras` | Lists the cameras PrivacyToggle can control |
| `--list-adapters` | Lists the network adapters PrivacyToggle can control |
| `--camera-in-use` | Says whether any app is using a camera right now |
| `--network-usage` | Prints the busiest apps' network speeds every second for 20 seconds, then totals |

## License

[MIT](LICENSE) © azahinhasan
