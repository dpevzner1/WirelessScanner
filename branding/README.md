# WirelessScanner branding

One master design, generated into every size by `python tools/make_icons.py`. With `--master path.png` any square PNG becomes the source, so a new design reaches every target in one step.

| File | Used by |
| --- | --- |
| `wirelessscanner-icon-master.png` | Source (1024 px). Amber Wi-Fi arcs over the three spectrum identity colours (DR-0010) |
| `windows/WirelessScanner.ico` | 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px. Copied to `WirelessScanner.Setup/icon.ico` (setup) and `WirelessScanner.Presentation/Assets/` (app; embedding on hold, see below) |
| `png/icon-*.png` | 16–1024 px for docs, web and stores |
| `android/mipmap-*/ic_launcher.png`, `ic_launcher_round.png`, `ic_launcher_foreground.png` | Android launcher icons (mdpi 48 → xxxhdpi 192 dp). Adaptive foreground has the artwork inside the 66 % safe zone |
| `android/values/ic_launcher_background.xml` | Adaptive-icon background colour `#0B0705` |
| `android/playstore-icon.png` | 512 px store listing |
| `legacy/setup-icon-before-2026-10-16-NEXUSTerminal.ico` | The icon setup carried before 1.3: a "NEXUS Terminal" logo from another project, kept only for reference |

At 32 px and below a simplified drawing (two arcs, no spectrum) keeps the icon legible.

**On hold: the icon inside the app executable.** On the development PC, Malwarebytes real-time protection quarantines `WirelessScanner.Presentation.dll` whenever the icon is embedded (`MachineLearning/Anomalous.93%`, a heuristic false positive). Re-enable `ApplicationIcon` in `WirelessScanner.Presentation.csproj` after the owner decides on:
- an exclusion for the development folder;
- code signing and/or a false-positive report for distribution.

Windows then shows the icon in Explorer, Start, the taskbar and Apps & features: `DisplayIcon` and the shortcuts already point at `WirelessScanner.Presentation.exe,0`.
