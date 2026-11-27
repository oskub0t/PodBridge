# PodBridge

A small Windows tray app that makes AirPods behave on a work PC the way they do on a Mac,
**without admin rights**.

| You do | PodBridge does | How (all per-user, no admin) |
|---|---|---|
| Open the case | Notices your AirPods (and ignores your colleague's) | Listens to Apple's Bluetooth LE advertisement (WinRT `BluetoothLEAdvertisementWatcher`), filtered by learned model ID + signal strength |
| | Shows a card with L / R / case battery | Battery comes from the same advertisement, in 10 % steps (that's all Apple broadcasts) |
| | Connects | Same driver call as *Settings → Bluetooth → Connect* (`KSPROPSETID_BtAudio` one-shot reconnect via Core Audio) |
| | Sets the stereo "Headphones" output as default | Core Audio `IPolicyConfig` (what every audio switcher uses) |
| Join a Teams call | Switches every default to the AirPods hands-free output + mic | Reads Windows' own "which app is using the mic" record in HKCU |
| | Warns if the AirPods mic gets no audio for 20 s | Peak meter on the AirPods capture endpoint |
| Leave the call | Restores high-quality stereo and your previous default mic | Same as above |

> **Why not "when Teams starts"?** Teams runs all day. Switching then would leave you in mono
> headset quality permanently. A call is detected when Teams actually opens the microphone.

## Design

The UI follows Apple's design principles: one clear action per screen, battery front and
centre, short labels, 44 pt hit targets, motion that comes from the tray, light and dark mode
that follow Windows. It still behaves like a Windows app (tray icon, positioned above the
taskbar, never steals keyboard focus).

- **Case-open card**: battery rings for Left / Right / Case, one primary button (Connect / Done).
- **Tray panel** (click the icon): status, Music | Call segmented control, iOS-style switches.
- **HUD**: small capsule notices ("Call mode", "Music mode", "AirPods mic is silent").

No Apple artwork or fonts are used: SF Pro is licensed for Apple platforms only, so the text uses
Segoe UI Variable, and the earbud and case glyphs are drawn in code.

## First run

1. **Pair once in Windows** (*Settings → Bluetooth & devices → Add device*, hold the case button
   until the light flashes white). Normal users can usually do this. If it's greyed out, your IT
   policy blocks Bluetooth pairing, and no app can get around that.
2. Copy `PodBridge.exe` to e.g. `%LOCALAPPDATA%\Programs\PodBridge\` and run it.
3. Click the tray icon → **Set Up My AirPods…** → open the case next to the PC → **These Are Mine**.
4. In Teams → *Settings → Devices*: set **Speaker** and **Microphone** to the AirPods
   (the *Headset / Hands-Free* entry) or to the default/communications device. Windows defaults
   can't override a device Teams has pinned explicitly.
5. Turn on **Open at login** in the panel (HKCU Run key, no admin).

Your colleague can use the same exe. Their AirPods (a different model) are ignored on your PC
once you've set yours up, and vice versa.

## "My mic isn't picking up": checklist

PodBridge warns you when this happens, but the usual causes are:

1. **Teams is on the wrong device.** Speaker = "Headphones (AirPods…)" while mic = AirPods.
   Once the mic opens, Bluetooth drops to hands-free mode and the stereo endpoint goes silent.
   Use the *Headset / Hands-Free* entry for both, or "Default".
2. **The AirPods jumped to your iPhone/iPad/Mac.** On the iPhone: *Settings → [AirPods] →
   Connect to This iPhone → When Last Connected to This iPhone* (instead of *Automatically*).
3. **Mic set to one bud.** *Settings → [AirPods] → Microphone → Automatically*.
4. **Something else holds the mic.** "Copy Diagnostics" lists every app using it.

## Settings file

`%APPDATA%\PodBridge\config.json` (log: `%LOCALAPPDATA%\PodBridge\log.txt`)

- `MinRssi` (-70): raise to -60 if a nearby identical model still triggers it
- `CallApps`: add your conferencing apps (substring of the mic-usage key)
- `MusicOutputName` / `CallOutputName` / `MicName`: pin exact endpoint names if auto-detect picks wrong
- `ModelNames`: e.g. `{ "0x2720": "AirPods Pro 3" }` once Diagnostics shows your model ID

## Build

```bash
dotnet test
dotnet publish src/PodBridge.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/win-x64
```

The .NET 10 SDK can be installed per-user on Windows without admin (`dotnet-install.ps1`), and the
solution also builds on Linux (`EnableWindowsTargeting`).

## Status: v0.1, not yet run on real hardware

Built and unit-tested (31 tests: advert parsing, colleague filtering, call debounce, endpoint
selection, mode switching). **Not yet run on Windows with real AirPods.** Things to verify first:

- [ ] AirPods Pro 3 model ID (learn mode stores whatever it is; Diagnostics shows it)
- [ ] Left/right orientation and battery mapping on Pro 3 (inherited from older models)
- [ ] Endpoint naming on your Windows build (Win11 may merge Headphones/Headset into one; handled, but check)
- [ ] One-shot reconnect while the AirPods are attached to your iPhone
- [ ] Silent-mic heuristic (exact-zero peak = nobody recording from the AirPods mic)
- [ ] Unsigned exe allowed by your company's AppLocker / SmartScreen

## Layout

```
src/PodBridge.Core     platform-free logic, unit-tested (parser, filter, call detector, mode controller)
src/PodBridge.App      WPF tray app: Platform/ (BLE, Core Audio COM, registry), Ui/ (cards, theme)
tests/                 xUnit
```

## Possible next steps

- Real acrylic blur behind cards (DWM backdrop) on Windows 11
- Low-battery HUD at 20 % / 10 %
- Ear-detection pause/play (the advert has in-ear bits)
- Signed MSIX for easier corporate install
