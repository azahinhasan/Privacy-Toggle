# Q&A

## Questions

### Why does the Activity page say Chrome (or another app) is using my camera when I never turned my camera on?

PrivacyToggle sees when an app **connects** to the camera, not when it actually **receives video**. Apps connect for other reasons too. For example, Google Meet asks Chrome to read your camera's formats when the page loads, even with the camera button off. Windows logs that connection like any other, so the app shows up.

### Why does NVIDIA Broadcast show as using the camera even when its camera effects are off?

When NVIDIA Broadcast starts, it connects to your webcam to get ready to feed its virtual camera. PrivacyToggle sees that connection and counts it.

### Why does it take a few seconds for the camera to show up or disappear under "In use now"?

New camera use appears about 2 seconds late on purpose: PrivacyToggle waits to see whether other apps react right after it, so it can tell which app really started. When an app stops, the change shows within about 1–2 seconds, because Windows never says when an app disconnects and PrivacyToggle has to check.

## Known issues

### Camera activity isn't accurate while NVIDIA Broadcast is running

NVIDIA Broadcast keeps your webcam open the whole time it runs. While it does:

- **Apps don't leave "In use now" when they stop.** Turning your camera off in a browser call may not remove the browser from the list until you close NVIDIA Broadcast or the browser. PrivacyToggle can only check whether *any* app is using the camera, and the answer stays "yes".
- **Apps can show up when they aren't using the camera.** Browsers reconnect to the camera whenever another app opens or closes it, and that can look like real use.
- **Some sessions show as "Connected to the camera · may not be using it"**, because PrivacyToggle can't confirm them.

Closing NVIDIA Broadcast makes camera activity accurate again. Microphone activity isn't affected.
