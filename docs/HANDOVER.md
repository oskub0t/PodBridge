# PodBridge handover

State as of **28 Nov 2026, version 0.4.6** (`main`). Read this first when picking the project up.
`CLAUDE.md` has the short version of the working rules.

## What it is

A per-user Windows tray app (C#/.NET 10, WPF) that makes AirPods behave on Windows the way they do
on Apple devices, **without administrator rights**. Built for the owner's locked-down work laptop
(Windows 11 26200, ThinkPad, AirPods Pro 3; a colleague uses AirPods 5). Public, MIT-licensed:
https://github.com/oskub0t/PodBridge

## What works (confirmed on the owner's PC unless marked)

| Feature | How | Status |
|---|---|---|
| Recognise *your* AirPods, ignore others | BLE proximity-pairing adverts, learned model ID + RSSI | confirmed |
| L / R / case battery (10 % steps), last known values with age | advert bytes; `BatteryMemory` persisted to `%LOCALAPPDATA%\PodBridge\battery.json` | confirmed |
| Case-open card + "lid opens, buds rise" animation | lid byte (see below), 60 pre-rendered frames | 0.4.6 not yet tested (buds now rise while the lid opens) |
| Card closes the instant the lid closes | lid byte bit 0x08 | confirmed (0.3.8+) |
| Auto-connect when a bud goes in (or case opens / never) | KS one-shot reconnect via Core Audio | confirmed |
| Sound follows your ears (stays on speakers while in case) | default endpoint switching (`IPolicyConfig`) | confirmed |
| Ear detection pause/resume (~0.5 s) | in-ear bits merged from both pods + system media sessions | confirmed (0.3.11 fix) |
| Teams/Zoom call mode and back to stereo | mic-usage registry (watched with RegNotifyChangeKeyValue) | built; call flow not re-verified recently |
| Laptop mic as everyday default outside calls | `LaptopMicOutsideCalls` | built |
| Silent-mic warning in calls | peak meter on AirPods capture endpoint | built, untested |
| Move to This PC (worn elsewhere + PC plays) | SwitchAdvisor + notification | built, untested |
| Low battery alerts | `LowBatteryMonitor` | built |
| Windows notifications (toasts) | Microsoft.Toolkit.Uwp.Notifications, fallback to own HUD | built; **owner's PC reports notifications "off"** (Windows setting/Focus/policy), so it falls back |
| Install for this user, Start menu, Settings → Apps, uninstall | `Installer.cs` (HKCU only) | installed copy confirmed running |
| Update check (daily, GitHub Releases) + one-click update | `Updater.cs`, `ReleaseInfo` | release flow confirmed for v0.4.1 |
| Apple-only Bluetooth filter | `BluetoothFilter` config, on by default | perf figures look fine (11.6 adverts/s, AirPods every ~172 ms, CPU 0.8 %) |
| Fluent/Mica Settings window, Acrylic cards, dark mode, focus ring, screen-reader labels | WPF Fluent theme + DWM | confirmed visually earlier |

## Architecture

```
src/PodBridge.Core      platform-free logic, all unit-tested (151 tests)
  Ble/       ProximityPairingParser, AirPodsModels, PresenceTracker, LidTracker, AdvertCadence,
             ModelSightings, BatteryMemory
  Features/  EarDetection, EarStateMerger, AutoConnect, LowBatteryMonitor, SwitchAdvisor
  Audio/     EndpointResolver, AudioModeController, MicWatchdog
  Calls/     CallDetector
  Updates/   ReleaseInfo (GitHub release JSON, version compare, daily policy)
  Aap/       AapCodec, AapSession (optional driver protocol, see "Driver" below)
src/PodBridge.App       WPF app
  AppController.cs   orchestrates everything (~1000 lines, should be split, see "Open work")
  Platform/  BleScanner, WindowsAudio (Core Audio COM), MediaSessions, RegistryWatcher,
             Notifications, Installer, Updater (+SingleInstance), Plumbing (config, log, autostart,
             mic usage), WindowsBluetooth, AapDriverTransport
  Ui/        SettingsWindow, StatusPanel (tray), ConnectCard (case card), Hud, BatteryRow/Ring,
             Artwork (frames/still), CardChrome (Acrylic), CardPlacement, Theme/*.xaml
tests/PodBridge.Core.Tests
driver/                 optional KMDF driver (parked, see below)
tools/icon, tools/render3d   app icon and 3D artwork generators
assets/3d/airpods_4.glb CC BY 4.0 model (credit in THIRD-PARTY-NOTICES.md)
release/PodBridge.exe   prebuilt exe, committed on purpose (owner's choice)
```

Runtime files: config `%APPDATA%\PodBridge\config.json`; log, battery memory and update downloads
in `%LOCALAPPDATA%\PodBridge\`; installed copy `%LOCALAPPDATA%\Programs\PodBridge\PodBridge.exe`.
Command-line modes: `--background`, `--uninstall`, `--apply-update <target>`, `--wait-for-previous`,
`--updated`.

## AirPods advertisement findings (from the owner's diagnostics)

Apple manufacturer data, type `0x07`, plain variant only (prefix `0x01`, 25 bytes). The 17-byte
`0x06` variant is encrypted and must be ignored (misreading it saved a bogus model ID once).

- **Model ID** bytes 3–4, always ends in `0x20`: Pro 3 = `0x2720`, AirPods 5 = `0x3620` (both
  confirmed), table in `AirPodsModels.cs` and the README. BTHENUM PID = byte-swapped ID (`2027`).
- **Status byte**: `0x20` = advertiser is the LEFT pod. For a pod OUT of the case: `0x02` = this pod
  in ear, `0x08` = other pod in ear (OpenPods mapping, confirmed). A pod IN the case uses the bits
  differently → `EarStateMerger` lets an in-case/charging pod speak only for itself. Both pods
  advertise alternately.
- **Lid byte** (payload byte 6): `0x08` = lid closed, `0x20` = advertiser is in the case (pods out
  of the case send `0x1x`, ignore for lid), `0x07` = counter of openings (wraps 7→0). One pod often
  reports late; `LidTracker` follows the counter (newer = new opening, older = ignore).
- **Cadence**: ~127–375 ms with lid open, ~750 ms worn, keeps advertising ~4 s after lid close.
- Windows does **not** report a battery level for these AirPods (HFP battery property empty).
- Windows creates an "AAP Server" device node (`BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}_VID&0001004c_PID&2027`) with no driver.

Field captures are kept as unit tests (`FeatureTests.cs`, `LidAndConnectTests.cs`,
`EarStateMergerTests.cs`); add new captures the same way.

## Building, releasing, CI

- Build/test anywhere: `dotnet test`; Windows exe: `dotnet publish src/PodBridge.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out`.
  WPF/WinRT build fine on Linux (`EnableWindowsTargeting`); the app can only run on Windows.
- **Release flow** (works since v0.4.1):
  1. bump `<Version>` in `src/PodBridge.App/PodBridge.App.csproj`, commit exe to `release/`, push;
  2. the owner publishes a Release on github.com with tag `vX.Y.Z` (target `main`);
  3. `.github/workflows/build.yml` (on `release: published`) builds on Windows, tests, and attaches
     `PodBridge.exe`; installed copies get an update notification within a day.
- Latest published Release: **v0.4.1**. 0.4.2–0.4.6 are on `main` only (owner publishes when ready).
- `driver.yml` builds the driver on `windows-2022` (compiles cleanly at /W4 /WX; InfVerif fails to
  load on the runner, so the INF is unchecked). Node 20 deprecation warning on actions (harmless).
- Claude Code cloud sessions: branch pushes work; **tag pushes and release creation are refused
  (HTTP 403)** — that's why the owner publishes Releases. Don't work around it.

## Decisions (and why)

- **No admin, ever, for the main app.** Manifest `asInvoker`, guarded by a test.
- **No test-signing / driver work** for now (owner: "no test mode"). The AAP driver
  (`driver/`, `docs/advanced-features.md`) is parked: written, compiles in CI, never run. The app
  side (`AapSession`, Settings "Advanced features", off by default) stays dormant without it.
- **Exe committed in `release/`** (owner wants it in the repo). Each copy adds ~80 MB to history
  (repo history was ~74 MB after a rewrite; it has grown again). Consider stopping once Releases are
  routine.
- **One 3D artwork for every model** (owner's call): "AirPods 4" by Falah3D, CC BY 4.0, pre-rendered
  with three.js (no runtime 3D). Credit required in-app (Settings → About), README, notices.
- **Commits** as `oskub0t <184001172+oskub0t@users.noreply.github.com>` with
  `Co-Authored-By: Claude …`; no session links.
- Apple-style UX but Windows-native chrome (like Apple's own Windows apps); no SF Pro, no Apple art.

## Open work / next steps

1. **Owner to test 0.4.6**: animation on every lid opening and smoothness (60 frames, pre-decoded,
   starts on render). Then publish v0.4.6. (0.4.6 = 0.4.5 with the buds starting at 0.2 s instead of 0.35 s, so there is no pause between lid and buds.)
2. **Memory**: 427 MB working set reported on 0.4.4. Diagnostics now show private bytes, .NET heap
   and WPF graphics tier; check those before changing anything (candidates: card windows not
   released, WPF software rendering, GC settings).
3. **Notifications show "off"** on the owner's PC; verify in Windows Settings → Notifications.
4. **Split `AppController`** (connection, ears/lid, cards, calls, updates, diagnostics).
5. Ideas not started: presentation clicker (stem presses via media session → arrow keys; needs a
   test that AVRCP presses reach PodBridge), walk-away lock, nearby finder (RSSI meter), battery
   history/time-left, global shortcuts, iPhone media/battery via BLE (AMS).
6. Code signing (SignPath Foundation, free for OSS) to avoid SmartScreen/AppLocker issues.
7. Optional: history clean-up of committed exes; bump GitHub Actions to Node 24 versions.

## Asking the owner for data

The owner tests on Windows and pastes **Settings → Copy Diagnostics**: performance, endpoints,
config, raw adverts, and a timeline of ears/lid/ear-detection actions. Most fixes so far came
from that output; ask for it (or a specific section) rather than guessing. The work laptop blocks
unsigned PowerShell scripts: give paste-in one-liners instead.
