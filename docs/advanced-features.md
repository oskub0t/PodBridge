# Advanced features (optional, requires administrator)

PodBridge works fully **without** administrator rights. That is the default and always will be:
`PodBridge.exe` runs "as invoker" and never asks for elevation.

Some AirPods features can only be reached through Apple's private control protocol, and on Windows
that needs a kernel driver. This document describes the optional **PodBridge AAP driver**: what it
would unlock, how users opt in, and the contract between the app and the driver.

> **Status:** the app side is built and unit-tested. **The driver is written but has never been
> compiled or run** ([`driver/`](../driver/README.md)). Building it needs Windows and the WDK.

## What it unlocks

| Feature | Without driver (default) | With driver |
|---|---|---|
| Battery | 10 % steps, from advertisements | Exact 1 %, pushed by the AirPods |
| Ear detection | 1–2 s delay, from advertisements | Instant |
| Noise control (Off / Transparency / Adaptive / Noise Cancellation) | Press and hold the stem | Switch from Settings |
| Conversation Awareness | Not available | Toggle from Settings |
| Spatial audio, hearing features, heart rate | Not available | Still not available (see below) |

Spatial audio rendering happens on Apple devices, not in the AirPods. Hearing features reportedly
require the host to identify as an Apple device. Heart-rate access is unknown. None of these are
planned.

## How users opt in (two separate, explicit steps)

1. **Install the driver**: a separate download and installer, never bundled into `PodBridge.exe`.
   Windows shows a UAC prompt. The installer states plainly that it installs a kernel driver.
2. **Turn it on** in *Settings → Advanced features → Use advanced features*. Off by default, even
   with the driver installed.

The Settings section is labelled **"Requires administrator · Off by default"** and explains:

- PodBridge works fully without it.
- Don't install it on a company-managed PC without IT's approval.
- How to remove it: *Settings → Apps*, or the driver's uninstaller.

If the driver is missing, disabled, or its channel fails, the app silently uses the no-admin
features. Nothing in the default path depends on it.

## Risks (also shown to users)

- **Kernel code:** a driver bug crashes the whole PC (blue screen), not just the app.
- **Signing:** 64-bit Windows only loads Microsoft-signed drivers. Without that, the driver needs
  *test-signing mode* (Secure Boot off, "Test Mode" watermark). That's unsuitable for most PCs and
  impossible on managed ones.
- **Undocumented protocol:** Apple can change or block it with any firmware update.

## Architecture

```
AirPods ──L2CAP PSM 0x1001 (AAP)──► PodBridge AAP driver (KMDF, kernel)
                                         │  device interface {14937a53-ba6b-4b1f-ba4b-2e3ebe1c57cb}
                                         ▼  ReadFile / WriteFile, one AAP packet each
                                    PodBridge.exe (user mode, no admin)
                                         └─ AapDriverTransport → AapSession → UI / ear detection / battery
```

- `src/PodBridge.Core/Aap/AapCodec.cs`: packet encoding/decoding (unit-tested)
- `src/PodBridge.Core/Aap/AapSession.cs`: session start-up, state, commands (unit-tested with a fake transport)
- `src/PodBridge.App/Platform/AapDriverTransport.cs`: finds and opens the driver's interface

## Driver contract

The app relies on exactly this. The reference implementation is in [`driver/`](../driver/README.md).

1. **Binding.** The driver attaches to the device node Windows creates for the AirPods' AAP
   service (UUID `74ec2172-0bad-4d01-8f77-997b2be0722a`). `driver/check-aap-service.ps1` shows
   whether that node exists and its exact hardware ID.
2. **Interface.** It registers device interface `{14937a53-ba6b-4b1f-ba4b-2e3ebe1c57cb}`, openable
   by the interactive user without admin (SDDL in the INF).
3. **Channel follows the handle.** `CreateFile` opens the L2CAP channel to PSM `0x1001` and fails if
   the AirPods can't be reached. `CloseHandle` closes it. Only one handle at a time; a second open
   fails with a sharing violation.
4. **I/O.** `WriteFile` sends one AAP packet as one L2CAP SDU. `ReadFile` completes with one
   received SDU (one AAP packet). The app always keeps one read pending.
5. **Disconnect.** When the AirPods drop the channel, pending and new I/O fail; the app closes the
   handle and retries later.
6. **No protocol logic in the kernel.** The driver moves bytes; parsing and commands stay in the
   app, where they're testable and safe to change.

## Protocol (as implemented in `AapCodec`)

From public reverse-engineering notes (the LibrePods project's documentation; no code copied, as
that project is GPL-licensed). **Unverified on Windows.** All packets start with
`04 00 04 00 <opcode> 00`, except the handshake.

| Packet | Bytes |
|---|---|
| Handshake (first) | `00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00` |
| Set feature flags | `04 00 04 00 4D 00 FF 00 00 00 00 00 00 00` |
| Request notifications | `04 00 04 00 0F 00 FF FF FF FF` |
| Set noise control | `04 00 04 00 09 00 0D <mode> 00 00 00`, mode `01` off, `02` ANC, `03` transparency, `04` adaptive |
| Set Conversation Awareness | `04 00 04 00 09 00 28 <01 on / 02 off> 00 00 00` |
| Battery (in) | `04 00 04 00 04 00 <n>` + n × `<component 02 R/04 L/08 case> 01 <level> <01 charging/02 discharging/04 disconnected> 01` |
| Ear state (in) | `04 00 04 00 06 00 <primary> <secondary>`, `00` in ear, `01` out, `02` in case |
| Speech level (in) | `04 00 04 00 4B 00 02 00 01 <level>` |

## Building the driver

See [`driver/README.md`](../driver/README.md): pre-check script, build, test-signing, install and
removal, debugging, and the first-run checklist.

## Test plan for the first driver build

- [ ] Interface appears when AirPods connect, disappears when they disconnect
- [ ] App opens it as a normal user (no admin)
- [ ] Handshake + notifications produce battery and ear packets within a few seconds
- [ ] Noise control changes from Settings are confirmed by the AirPods (mode packet echoed)
- [ ] Unplugging/turning off Bluetooth mid-session: no crash, app falls back cleanly
- [ ] Driver Verifier enabled during all of the above
