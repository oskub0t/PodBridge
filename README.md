# PodBridge

A small Windows tray app that makes AirPods behave on a Windows PC more like they do on a Mac,
**without admin rights**. Built for locked-down work laptops.

**[Download PodBridge.exe](https://github.com/oskub0t/PodBridge/raw/main/release/PodBridge.exe)**:
one self-contained file, no installer, no .NET needed, Windows 10 (2004+) or 11, x64.

| You do | PodBridge does | How (all per-user, no admin) |
|---|---|---|
| Open the case | Notices your AirPods (and ignores other people's) | Apple's Bluetooth LE advertisement (WinRT `BluetoothLEAdvertisementWatcher`), filtered by learned model ID + signal strength |
| | Shows a card with left / right / case battery | Same advertisement, in 10 % steps (that's all Apple broadcasts in the clear) |
| Close the case | Keeps showing the last known levels, dimmed and with their age ("Case · 2 h ago") | Remembered per part in `%LOCALAPPDATA%\PodBridge\battery.json`, like the iOS Batteries widget |
| | Connects | Same driver call as *Settings → Bluetooth → Connect* (`KSPROPSETID_BtAudio` one-shot reconnect via Core Audio) |
| | Sets the stereo output as default, keeps the PC's own mic as the everyday mic | Core Audio `IPolicyConfig` (what every audio switcher uses) |
| Join a Teams call | Switches every default to the AirPods mic and hands-free output | Windows' own "which app is using the mic" record in HKCU |
| | Warns if the AirPods mic gets no audio for 20 s | Peak meter on the AirPods capture endpoint |
| Leave the call | Restores high-quality stereo | Same as above |
| Take an AirPod out / put it back | Pauses / resumes what was playing (Automatic Ear Detection) | In-ear flags in the advertisement + Windows' system media controls; resumes only what it paused |
| Wear them with your phone, PC starts playing | "Move to This PC" card, or switches automatically | Like Apple's *Connect to This Mac*: Automatically / When Asked / Never |
| Battery hits 20 % / 10 % | Low battery alert | Once per threshold; charging re-arms it |

> **Why not "when Teams starts"?** Teams runs all day. Switching then would leave you in mono
> headset quality permanently. A call is detected when Teams actually opens the microphone.

### Optional: advanced features (require administrator, off by default)

Noise-control switching, Conversation Awareness, exact 1 % battery and instant ear detection go
through Apple's private protocol over a Bluetooth L2CAP channel. Windows only lets a **kernel
driver** open those channels, and installing a driver needs admin. PodBridge has an **optional**
path for this. You install a separate driver (with a UAC prompt), then turn it on in
*Settings → Advanced features*. Without the driver, which is the default, everything above works
as normal and `PodBridge.exe` never asks for elevation.

**The driver isn't available yet.** The app side is built; the driver needs a Windows kernel
developer. See [docs/advanced-features.md](docs/advanced-features.md) for the design, risks and
the contract a driver has to meet. Spatial audio, hearing features and heart rate aren't possible
even with a driver.

Without the driver, the AirPods' own controls still work on Windows: press for play/pause, press
twice/three times for skip/back, press and hold to switch Noise Cancellation/Transparency, swipe
the stem for volume.

## First run

1. **Pair once in Windows** (*Settings → Bluetooth & devices → Add device*, hold the case button
   until the light flashes white). Normal users can usually do this. If it's greyed out, your IT
   policy blocks Bluetooth pairing, and no app can get around that.
2. Copy `PodBridge.exe` to e.g. `%LOCALAPPDATA%\Programs\PodBridge\` and run it. The exe is not
   code-signed yet, so SmartScreen may ask you to confirm (*More info → Run anyway*); some company
   policies (AppLocker/WDAC) block unsigned apps completely.
3. Put the AirPods in the case and **open it next to the PC**. When the card asks, choose
   **These Are Mine**. (Setup only offers an open case close by, so a colleague's pair being worn
   nearby isn't offered.)
4. In Teams → *Settings → Devices*: set **Speaker** and **Microphone** to the AirPods or to the
   default device. Windows defaults can't override a device Teams has pinned explicitly.
5. Turn on **Open at login** in Settings (HKCU Run key, no admin).

## "My mic isn't picking up": checklist

PodBridge warns you when this happens, but the usual causes are:

1. **Teams is on the wrong device.** Speaker = "Headphones (AirPods…)" while the mic is a different
   AirPods entry or another device. Use the AirPods for both, or "Default".
2. **The AirPods jumped to your iPhone/iPad/Mac.** On the iPhone: *Settings → [AirPods] →
   Connect to This iPhone → When Last Connected to This iPhone*.
3. **Mic set to one bud.** *Settings → [AirPods] → Microphone → Automatically*.
4. **Something else holds the mic.** "Copy Diagnostics" lists every app using it.

## Design

Apple's own Windows apps (Apple Music, Apple TV, Apple Devices) don't imitate macOS. They use
Windows' materials and controls, and Apple's style shows in the content. PodBridge does the same:

- **Settings window** (open the app, or double-click the tray icon): laid out like the AirPods
  pane in macOS System Settings, built on WPF's Windows 11 Fluent theme (native title bar, Mica).
- **Case-open card & tray panel**: Windows 11 Acrylic with system corners and shadow on 22H2+;
  drawn translucent cards on Windows 10. One primary action, never steals keyboard focus.
- **HUD**: small notices for call/music mode, low battery, silent mic.
- Follows light/dark mode live; keyboard focus ring and screen-reader labels throughout.
- Segoe UI Variable text (SF Pro is licensed for Apple platforms only); the earbud and case art
  is drawn in code, not Apple artwork.

## Privacy

PodBridge makes **no network connections**. Everything stays on your PC:
settings in `%APPDATA%\PodBridge\config.json`, log and last known battery levels in
`%LOCALAPPDATA%\PodBridge\`.

*Copy Diagnostics* contains your Bluetooth device names (often your first name, e.g.
"Alex's AirPods Pro"), your audio devices, and raw Bluetooth advertisement bytes from devices
nearby. **Read it and remove anything private before pasting it into a public issue.**

## Settings file

- `MinRssi` (-70): raise to -60 if a nearby identical model still triggers it
- `CallApps`: add your conferencing apps (substring of the mic-usage key)
- `LaptopMicOutsideCalls` (true): keep the PC's mic as the everyday default
- `MusicOutputName` / `CallOutputName` / `MicName`: pin exact endpoint names if auto-detect picks wrong
- `ModelNames`: e.g. `{ "0x2720": "AirPods Pro 3" }`

## Known AirPods advertisement formats

Field captures show two type-`0x07` variants: the plain `0x01` variant (25 bytes, model ID and
battery in the clear) and an encrypted `0x06` variant (17 bytes) that must be ignored. Genuine
model IDs all end in `0x20`. See `tests/PodBridge.Core.Tests/FeatureTests.cs` for captured samples.

| Model ID | Model |
|---|---|
| `0x0E20` / `0x1420` / `0x2420` | AirPods Pro / Pro 2 / Pro 2 (USB-C) |
| `0x0F20` / `0x1320` | AirPods 2nd / 3rd gen |
| `0x1920` / `0x1B20` | AirPods 4 / 4 ANC (less certain) |
| `0x2720` | seen in the field, probably AirPods Pro 3 (unconfirmed) |
| `0x3620` | seen in the field, unknown |

Contributions of confirmed IDs are welcome (open the case, *Copy Diagnostics*, look for the
strongest `0x…20` entry).

## Build

```bash
dotnet test
dotnet publish src/PodBridge.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/win-x64
```

The .NET 10 SDK can be installed per-user on Windows without admin (`dotnet-install.ps1`); the
solution also builds on Linux/macOS (`EnableWindowsTargeting`). GitHub Actions builds and tests
every push; pushing a `v*` tag publishes the exe to a GitHub Release. The exe in `release/` is
updated for releases only, because every committed copy stays in the git history.

```
src/PodBridge.Core     platform-free logic, unit-tested (parser, filter, calls, audio modes, features, AAP protocol)
src/PodBridge.App      WPF tray app: Platform/ (BLE, Core Audio COM, registry, media), Ui/ (windows, theme)
tests/                 xUnit, including captured real-world advertisements
docs/                  advanced-features.md: optional admin-only driver design
```

## Status

Early but working: it connects, switches audio and reads advertisements on Windows 11. Still to
confirm with real hardware:

- [ ] Left/right/case battery and in-ear flags on AirPods Pro 3 (`0x2720`)
- [ ] Ear-detection latency (how often AirPods advertise while in ears)
- [ ] "Move to This PC" while the AirPods are connected to an iPhone
- [ ] Silent-mic warning (exact-zero peak means nobody is recording from the AirPods mic)
- [ ] Acrylic cards on more Windows 11 builds (turn off *Translucent cards* if they look wrong)

## License

[MIT](LICENSE).

PodBridge is an independent project and is not affiliated with, endorsed by, or sponsored by
Apple Inc. AirPods, iPhone and Mac are trademarks of Apple Inc., registered in the U.S. and
other countries.
