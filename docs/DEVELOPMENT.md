# CoreScope

Hardware information, live sensors and plain-English insights for Windows PCs.
Think CPU-Z + HWiNFO + a friendly technician, in one Fluent-styled app. Version 1.0.0.

## Install (end users)

1. Unzip `CoreScope-1.0.0-win-x64.zip` anywhere.
2. Double-click **`Install.cmd`**. It copies CoreScope to `%LocalAppData%\Programs\CoreScope`, adds Start menu and
   desktop shortcuts and an entry in *Settings → Apps* (uninstall from there, or run `Uninstall.cmd`).
   No .NET install is needed — the package is self-contained.
3. No drivers needed: CPU temperature, clocks and power come from Windows itself and your graphics driver.

CoreScope starts as a normal app. "Unlock full sensors" restarts it with administrator rights when you want drive health (SMART), the TPM and the few controls that need it.

**Supported:** 64-bit Windows 10 2004+ and Windows 11, Intel or AMD CPUs, NVIDIA/AMD/Intel graphics, laptops and
desktops. Features that need specific hardware hide themselves when it isn't present (battery care on
non-Lenovo laptops, fan control on machines whose fans aren't software-controllable, etc.).

**Something wrong on another PC?** Run `CoreScope.exe --selftest` from the install folder. It never changes any
setting; it writes `selftest.json` (and `corescope.log`) describing what worked — send those two files.

## Build from source

1. Install the **.NET 10 SDK**: `winget install Microsoft.DotNet.SDK.10`.
2. Double-click **`build.bat`** — compiles into `app\` and launches it.
3. To make the shareable package: `powershell -ExecutionPolicy Bypass -File tools\package.ps1`
   → `dist\CoreScope-<version>-win-x64.zip`.

## What's inside

See [CHANGELOG.md](CHANGELOG.md) for the full feature list. Pages: Overview · Insights · Upgrade · Control ·
Benchmark · Processor · Memory · Graphics · Storage · Motherboard · Devices · System · Sensors · Appearance · Settings.

## Insights and fixes

`Core/Hardware/InsightEngine*.cs` evaluates findings (hardware plus the health checks in `HealthCollector`), and attaches fix buttons in one place
(`AttachFixes`). Fixes are defined in `Core/Fixes/`: `FixCatalog` (buttons and BIOS guides), `FixRunner` (the actions CoreScope performs, behind
confirmations and an administrator check), `FixCoordinator` (progress, results, re-check). Add a check by adding a finding with a title and a
matching entry in `FixesFor`; a unit test fails if a warning or critical finding has no way forward.

## Appearance

Settings are in the **Appearance** page (themes, skins, layouts, accent colour, glass, glow, fonts, saved themes, per-page looks).
Theme files (`*.corescope-theme.json`) can be exported and imported from there. Code: `Core/Theming/` (catalog, colours,
file format, per-page looks), `Platform/ThemeManager.cs` (turns settings into live resources), `Platform/WindowBackdrop.cs`.

## Project layout

```
src/CoreScope/
  Core/Hardware/    data layer: CpuId, SpecCollector (WMI/registry), SensorHub (LibreHardwareMonitor), InsightEngine
  Core/ViewModels/  MVVM view models (no WPF dependency)
  Core/Theming/     skins, colour maths, theme files, saved themes and per-page looks
  Platform/         Windows-specific code: tray, overlay, theme engine, window backdrop
  Controls/         SparkChart graph control, converters
  Views/            XAML pages
```

## Troubleshooting

- `build.log` (next to build.bat) has compiler output.
- `app\corescope.log` records every collection step and error; `app\sensors.txt` lists every sensor found.

Sensor engine: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## Selecting and copying text

All text can be selected with the mouse (`Platform/TextInteraction.cs`): a transparent read-only text box is laid over the text under the
pointer. Text inside buttons and list rows is click-only; right-click it for Copy / Copy everything on this page.

## Tests

`dotnet test tests/CoreScope.Tests` runs the unit tests (formatting, insights, theming).
