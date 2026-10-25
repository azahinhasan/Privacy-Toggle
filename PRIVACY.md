# PrivacyToggle Privacy Policy

**Effective date:** October 23, 2026

PrivacyToggle is a free, open-source Windows app that mutes your microphone and turns off your camera. This policy explains what information the app handles and what it does with it.

## 1. Summary

PrivacyToggle does not collect, sell, share or send any personal information to the developer or to any third party. It has no accounts, no analytics, no advertising and no telemetry. Everything the app records stays on your PC.

## 2. Information stored on your device

The app saves the following files only on your computer. They are never uploaded anywhere.

- **Settings** (`%AppData%\PrivacyToggle\settings.json`): your preferences, such as hotkeys, theme, selected devices and alert options.
- **Activity history** (`%AppData%\PrivacyToggle\usage.json`): the names of apps that used your camera or microphone, and when they used them, kept for up to 7 days. PrivacyToggle reads this from Windows' own record of camera and microphone use, the same record Windows uses for its Privacy settings.
- **Error log** (`%AppData%\PrivacyToggle\error.log`): technical details about crashes, used only to fix problems. It stays on your PC unless you choose to share it.
- **Helper service state** (`%ProgramData%\PrivacyToggle\`): a list of the cameras and services PrivacyToggle has turned off, so it can turn them back on correctly.

You can delete these files at any time. Uninstalling PrivacyToggle turns your cameras back on and removes the app.

## 3. Microphone and camera

PrivacyToggle controls your microphone and camera. It never records, captures, views or transmits any audio or video. It only mutes, unmutes, disables or enables your devices, and checks which apps are using them.

## 4. Network connections

The only network connection the app makes is to GitHub (github.com and api.github.com), to check for new versions and to download an update when you ask it to. GitHub receives normal connection information such as your IP address, and handles it under its own [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). PrivacyToggle sends no personal information with this request.

## 5. Children's privacy

PrivacyToggle collects no personal information from anyone, including children under 13.

## 6. Changes to this policy

If this policy changes, the updated version will be published here with a new effective date.

## 7. Contact

If you have questions about this policy, please [open an issue](https://github.com/azahinhasan/Privacy-Toggle/issues).
