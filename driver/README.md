# PodBridge AAP driver (optional, experimental)

**You don't need this.** PodBridge works fully without it, and `PodBridge.exe` never asks for
admin. This optional kernel driver unlocks noise control, Conversation Awareness, exact battery
and instant ear detection, by opening the AirPods' Apple Accessory Protocol (AAP) channel, which
Windows only allows from a kernel driver. Background: [docs/advanced-features.md](../docs/advanced-features.md).

> **Status: written, never compiled or run.** Expect compile errors on the first build, and
> don't install it on a PC you depend on. Never on a company-managed PC without IT's approval.

## Step 0: does Windows expose the AAP service? (no admin, safe on any PC)

```powershell
powershell -ExecutionPolicy Bypass -File .\driver\check-aap-service.ps1
```

**If scripts are blocked** ("is not digitally signed", common on company PCs where policy
requires signed scripts): don't try to bypass the policy. Paste the same read-only check
directly into PowerShell instead; typed commands aren't affected:

```powershell
Get-PnpDevice | Where-Object { $_.InstanceId -like 'BTHENUM\*' -and $_.InstanceId -match '_VID&0001004C' } | ForEach-Object { [pscustomobject]@{ Name = $_.FriendlyName; Status = $_.Status; AapService = ($_.InstanceId -match '74ec2172'); HardwareIds = ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds).Data -join '; ') } } | Format-List
```

Look for an entry with `AapService : True`.

- **"the AAP service node exists"**: good. It's named **"AAP Server"**. If your model's hardware
  id isn't in `podbridgeaap.inf` yet, add it.
  *Confirmed so far:* AirPods Pro 3 → `BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}_VID&0001004c_PID&2027`.
- **"no AAP service node"**: Windows doesn't create a device node for that service, so this
  driver can't attach as written. Report it; the fallback is a different attach strategy (e.g. an
  upper filter on the AirPods' main Bluetooth device node).

## Building (Windows, Visual Studio 2022 + WDK)

1. Install Visual Studio 2022 with *Desktop development with C++*, then the **Windows Driver Kit**
   (WDK) and its Visual Studio extension, from Microsoft's WDK download page.
2. Open `driver/PodBridgeAap/PodBridgeAap.vcxproj`, pick **Release | x64**, Build.
   If the project won't load, create *Kernel Mode Driver, Empty (KMDF)* named `PodBridgeAap`
   and add `podbridgeaap.c`, `.h` and `.inf` to it.
3. Output: `podbridgeaap.sys`, `podbridgeaap.inf`, `podbridgeaap.cat` (test-signed).

## Installing on a TEST PC (admin)

Use a PC you can afford to crash, ideally not your main one.

```bat
bcdedit /set testsigning on
```
Reboot (a "Test Mode" watermark appears). Then, in an admin prompt in the build output folder:
```bat
pnputil /add-driver podbridgeaap.inf /install
```
Turn the AirPods off and on (or re-pair), then in PodBridge: *Settings → Advanced features →
Use advanced features*.

**Removing it:**
```bat
pnputil /enum-drivers            (find the oemNN.inf for "PodBridge AAP driver")
pnputil /delete-driver oemNN.inf /uninstall
bcdedit /set testsigning off
```

## Debugging

- Driver messages go to the kernel debug output (prefix `PodBridgeAap:`). Read them with WinDbg
  attached, or with Sysinternals DebugView (*Capture Kernel*; enable the IHV filter:
  `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Debug Print Filter" /v IHVDRIVER /t REG_DWORD /d 0xf`).
- Turn on Driver Verifier for `podbridgeaap.sys` while testing: `verifier /standard /driver podbridgeaap.sys`.
- On the app side, *Copy Diagnostics* shows whether the driver was found and the session state.

## First-run checklist

- [ ] Compiles without warnings (warnings are errors)
- [ ] Check script finds the AAP node; INF hardware ID updated
- [ ] Driver loads on the AAP node (Device Manager shows "PodBridge AirPods control channel")
- [ ] PodBridge opens it as a normal user; debug output shows "AAP channel open"
- [ ] Battery and ear packets arrive (Diagnostics shows exact battery)
- [ ] Noise control changes from Settings take effect
- [ ] Turning Bluetooth off mid-session: no crash, PodBridge falls back
- [ ] All of the above with Driver Verifier on

Things marked `VERIFY` in the source are assumptions to check on the first run.

## Releasing (later)

Test-signed drivers only load in test mode. A driver anyone can install needs Microsoft
**attestation signing** via Partner Center, which requires an EV code-signing certificate.
