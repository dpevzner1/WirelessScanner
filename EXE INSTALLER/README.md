# WirelessScanner 1.5.0 — Installer

WirelessScanner is a Wi-Fi signal analysis, site-survey and **signal saturation test** tool for Windows 10 (2004+) / 11.

## Install
1. Download **every file in this folder** into one local folder:
   `WirelessScanner.Setup.exe`, `WirelessScanner.Setup.exe.config`, `WirelessScanner.Setup.Core.dll`,
   `WirelessScanner.payload.json` and all `WirelessScanner##.bin` parts (5 parts).
2. Run `WirelessScanner.Setup.exe` (it asks for administrator rights).
3. Choose the folder (default `C:\Program Files\WirelessScanner`) and shortcuts, then **Install**.

No prerequisites: the installer uses .NET Framework 4.8 (built into Windows 10/11) and the application is self-contained.
The setup verifies the reassembled package (size 84,909,742 bytes, SHA-256 `71d6ccaf6019cadb61a8d88b0e69274f4ba31f7d42b75fae75cacf821e7dedcf`) before installing.

## Update, repair, modify, uninstall
- **Update / repair / downgrade:** run this setup again; it detects the installed version and offers the right action
  (older versions require confirmation).
- **Modify shortcuts / uninstall:** *Settings → Apps → WirelessScanner* (Modify or Uninstall), or run setup again.
- **Your data** (sessions, saturation tests, logs, API key) lives in `C:\ProgramData\WirelessScanner` and is kept on
  update and uninstall unless you tick *Also permanently delete all WirelessScanner data*.

## Optional: LAN responder (two-way Wi-Fi tests)
`WirelessScanner.Responder.zip` (SHA-256 `98a59d05a65baa4d31a0e87b5ce985ab78472783cd03a2bf489198bbe49fc3a8`) is NOT part of the app. Unzip it on a **wired** machine on the same
network (Windows, Linux or Raspberry Pi with the .NET 10 runtime) and run `dotnet WirelessScanner.Responder.dll`. It prints a
pairing key; enter its address and key in WirelessScanner: *SETTINGS → Test servers*. Allow TCP/UDP 5410 for the local network only.

Repository: https://github.com/dpevzner1/WirelessScanner — full documentation in `docs/USER_GUIDE.md`.
