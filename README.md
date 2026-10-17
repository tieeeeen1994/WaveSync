# WaveSync

A Windows tray app that keeps **Windows' default playback device** and **Elgato Wave Link 3's output** in sync.

- **Pick from the tray** (left or right click), or use a hotkey, and both sides switch.
- **Change it in Windows** (Sound settings, the volume flyout, another switcher) and Wave Link follows.
- **Change it in Wave Link** (the app or a Stream Deck) and Windows follows.

## Build and run

You need the [.NET 8 SDK or newer](https://dot.net). It has no NuGet dependencies.

- Double-click **`build.cmd`**. It produces `publish\WaveSync.exe`. Copy that anywhere and run it.
- Or open `WaveSync.sln` in Visual Studio / Rider and press Run.

Then use **Settings → Start with Windows** if you want it at login.

## How it decides what to change

Wave Link adds its own virtual devices to Windows. If Windows' default is one of those, system audio goes through the Wave Link mixer, and what you actually *hear* is Wave Link's output. WaveSync handles this with **Settings → Change Windows default**:

| Mode | What happens when you pick a device |
|---|---|
| **Auto** (default) | Wave Link's output always switches. The Windows default switches only if it's currently a physical device. If Windows is routed through Wave Link, it stays there so you keep the mixer. |
| **Always** | Both switch, even if that takes Windows off the Wave Link virtual device (bypasses the mixer). |
| **Never** | Only Wave Link switches. |

In Auto, if you pick something Wave Link can't play (or Wave Link isn't running), the Windows default switches instead, so the click always does something.

**Send Windows audio through Wave Link** in the menu puts Windows back on a Wave Link virtual device.

At startup, or when Wave Link (re)starts: if Windows is on a physical device, Wave Link is set to match it. Otherwise WaveSync adopts Wave Link's current output.

## Settings file

`%APPDATA%\WaveSync\config.json` (Settings → Open settings file; then **Reload settings file**).

```jsonc
{
  "windowsDefaultMode": "Auto",          // Auto | Always | Never
  "setCommunicationsDevice": true,       // also set the "Default Communications Device" (Discord, Teams...)
  "mirrorWindowsToWaveLink": true,       // follow changes made in Windows
  "mirrorWaveLinkToWindows": true,       // follow changes made in Wave Link
  "showNotifications": true,
  "waveLinkPort": 0,                     // 0 = auto-discover
  "waveLinkPollMs": 3000,                // safety-net re-check; 0 = rely on events only
  "virtualDeviceNamePatterns": ["Wave Link", "Elgato Virtual"],
  "hiddenDevices": ["NVIDIA", "Realtek Digital"],
  "cycleDevices": ["Headphones", "Speakers"],   // empty = cycle through everything visible
  "hotkeys": [
    { "keys": "Ctrl+Alt+F12", "action": "cycle" },
    { "keys": "Ctrl+Alt+F1",  "device": "Headphones" },
    { "keys": "Ctrl+Alt+F2",  "device": "Speakers" }
  ],
  "deviceOverrides": [
    { "windows": "Headphones (Elgato Wave:3)", "waveLink": "Wave:3 Headphones" }
  ]
}
```

Device names in `hotkeys`, `cycleDevices`, `hiddenDevices` and `deviceOverrides` can be the full name or just part of it.

## If a device shows "(Windows only)" or "(Wave Link only)"

Windows and Wave Link use different IDs and sometimes different names, so WaveSync pairs them by name. If a pair is missed:

1. **Settings → Show device diagnostics** lists every device on both sides, the pairing it chose, and each Wave Link output's `key`.
2. Add a `deviceOverrides` entry using a name or key from that file, then **Reload settings file**.

If one of Wave Link's own virtual devices shows up as a normal device, add part of its name to `virtualDeviceNamePatterns`.

## Files

- `%APPDATA%\WaveSync\config.json`: settings
- `%APPDATA%\WaveSync\log.txt`: what it did and why (Settings → Open log)
- `%APPDATA%\WaveSync\diagnostics.txt`: written on demand

## Caveats

- **Both APIs are unofficial.** Windows has no public API for setting the default device. WaveSync uses the same `IPolicyConfig` interface as NirCmd, SoundSwitch and AudioDeviceCmdlets, which has worked from Windows 7 to 11. Wave Link's local WebSocket API is the one the official Stream Deck plugin uses, documented by the community. An Elgato update could change it.
- **Wave Link 3.x only.** Wave Link 2 had a different API on port 1824.
- "Wave Link output" here means Wave Link's **main output**, the device the Stream Deck plugin's *Set Output Device* changes (what you hear).

## Code map

| File | Role |
|---|---|
| `Audio/CoreAudioInterop.cs` | COM interface definitions (device enumerator, notifications, `IPolicyConfig`) |
| `Audio/WindowsAudio.cs` | List devices, get/set the default, change notifications |
| `WaveLink/WaveLinkClient.cs` | Port discovery, JSON-RPC over WebSocket, auto-reconnect, notifications + polling |
| `Sync/DeviceMatcher.cs` | Pairs Windows devices with Wave Link outputs |
| `Sync/SyncController.cs` | Single source of truth, echo guard, mode rules |
| `UI/TrayApp.cs` | Tray icon and menu, notifications, settings |
| `UI/HotkeyManager.cs` | Global hotkeys |
