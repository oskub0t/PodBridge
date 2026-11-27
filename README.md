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
| Take an AirPod out / put it back | Pauses / resumes whatever was playing (Automatic Ear Detection) | In-ear flags in the advertisement + Windows' system media controls; resumes only what it paused |
| Wear them on your phone, PC starts playing | "Move to This PC" card, or switches automatically | Like Apple's *Connect to This Mac*: Automatically / When Asked / Never |
| Battery hits 20 % / 10 % | Low battery alert | Once per threshold; charging re-arms it |

### What isn't possible without admin (and why)

Noise-control switching, Conversation Awareness, Adaptive Audio, spatial audio, exact 1 % battery,
renaming, hearing features and the Pro 3 heart rate all go through Apple's private protocol over
a Bluetooth L2CAP channel. Windows only lets a **kernel driver** open L2CAP channels, and drivers
need admin. The AirPods' own controls still work on Windows by themselves: press for play/pause,
press twice/three times for skip/back, press and hold to switch Noise Cancellation/Transparency,
swipe the stem for volume. The Settings window explains this rather than pretending.

> **Why not "when Teams starts"?** Teams runs all day. Switching then would leave you in mono
> headset quality permanently. A call is detected when Teams actually opens the microphone.

## Design: an Apple app on Windows

Apple's own Windows apps (Apple Music, Apple TV, Apple Devices) don't imitate macOS. They use
Windows' own materials and controls, and Apple's style shows in the content. PodBridge does the same:

- **Settings window** (open the app, or double-click the tray icon): laid out like the AirPods
  pane in macOS System Settings (illustration, battery, grouped plain-language settings,
  "On your AirPods" explainer, About), built on WPF's Windows 11 Fluent theme: native title bar,
  Mica background, native combo boxes, buttons and scroll bars.
- **Case-open card & tray panel**: real Windows 11 Acrylic (the Quick Settings material) with
  system rounded corners and shadow on 22H2+; drawn translucent cards on Windows 10.
  Battery rings, one primary action, never steals keyboard focus.
- **HUD**: capsule notices for call/music mode, low battery, silent mic.
- Follows light/dark mode live. Text uses Segoe UI Variable (SF Pro is licensed for Apple
  platforms only); the earbud and case art is drawn in code, not Apple's artwork.

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

Built and unit-tested (47 tests: advert parsing, colleague filtering, call debounce, endpoint
selection, mode switching, ear detection, low battery, switch policy). **Not yet run on Windows with real AirPods.** Things to verify first:

- [ ] AirPods Pro 3 model ID (learn mode stores whatever it is; Diagnostics shows it)
- [ ] Left/right orientation and battery mapping on Pro 3 (inherited from older models)
- [ ] Endpoint naming on your Windows build (Win11 may merge Headphones/Headset into one; handled, but check)
- [ ] One-shot reconnect while the AirPods are attached to your iPhone
- [ ] Silent-mic heuristic (exact-zero peak = nobody recording from the AirPods mic)
- [ ] Unsigned exe allowed by your company's AppLocker / SmartScreen
- [ ] How often AirPods advertise while in your ears (sets ear-detection latency; expect 1–3 s)
- [ ] AirPods keep advertising while connected to the iPhone (needed for "Move to This PC")
- [ ] Acrylic cards render correctly on your Windows 11 build (turn off "Translucent cards" if not)

## Layout

```
src/PodBridge.Core     platform-free logic, unit-tested (parser, filter, call detector, mode controller)
src/PodBridge.App      WPF tray app: Platform/ (BLE, Core Audio COM, registry), Ui/ (cards, theme)
tests/                 xUnit
```

## Possible next steps

- Battery fallback from Windows' own HFP battery report when adverts are sparse
- Signed MSIX for easier corporate install
