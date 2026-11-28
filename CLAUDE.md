# PodBridge: notes for Claude Code

Start with `docs/HANDOVER.md` (status, AirPods protocol findings, release flow, open work).

## Rules

- The app must never need administrator rights: manifest stays `asInvoker` (a test checks it).
  No driver / test-signing work unless the owner asks again.
- Logic goes in `src/PodBridge.Core` with unit tests; Windows-only code in `src/PodBridge.App`.
  Turn every field capture from the owner's diagnostics into a test.
- Build and test: `dotnet test`. The WPF app builds on Linux but only runs on Windows: say so
  when something is untested on hardware.
- Release: bump `<Version>` in `src/PodBridge.App/PodBridge.App.csproj`, commit, push. CI builds
  the exe (uncompressed single file; compression cost ~100+ MB of RAM) and uploads it as a run
  artifact for testing. The owner publishes the GitHub Release (tag pushes are refused from cloud
  sessions); CI then attaches `PodBridge.exe` (needed by ≤0.4.7 updaters) and `PodBridge.zip`.
  Don't commit exes again (owner agreed in 0.4.8; the uncompressed exe is over GitHub's 100 MB limit).
- No WinForms: the tray icon is Shell_NotifyIcon in `Ui/TrayIcon.cs`, dialogs are WPF.
- Commit as `oskub0t <184001172+oskub0t@users.noreply.github.com>`; end messages with the
  `Co-Authored-By` line; no session links.
- 3D artwork comes from `assets/3d/airpods_4.glb` (CC BY 4.0, Falah3D) via `tools/render3d`; keep
  the credit in Settings → About, README and THIRD-PARTY-NOTICES.md.
- When unsure what the AirPods or Windows actually do, ask the owner for *Copy Diagnostics*.
