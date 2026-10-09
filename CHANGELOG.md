# Changelog

## 1.1.1 — 2026-10-09

**Better**
- **"What this will do" before every fix:** the confirmation now shows the change in plain words and the exact command (for example `netsh advfirewall set allprofiles state on`). Network fixes (RSC, TCP auto-tuning) now ask before changing anything, too.
- **Select across several texts:** press on a text and drag down or up over others to highlight them all; Ctrl+C or right-click → Copy puts them on the clipboard in reading order.
- **Selecting text keeps its look:** text with bold or italic parts (and links) no longer turns plain while you select it.
- **Keep my layout on every page** (Appearance → Window layout): pages with their own look change colours and style but the navigation stays where it is. When the layout does change, it now fades in instead of jumping.
- Release process: every release is now built, tested and published automatically from a version tag.

## 1.1.0 — 2026-10-06

**New**
- **Insights check far more, and can fix things:** about 30 more checks across security (antivirus and its definitions, firewall, User Account Control, disk encryption, Remote Desktop, automatic sign-in), Windows (version support dates, update age, pending restart, System Restore, clock sync), maintenance (temporary files, Recycle Bin, hibernation file, Storage Sense, page file), startup load and display refresh rate. Every check also reports when it passes, so you can see it was looked at.
- **Fix buttons on findings:** CoreScope fixes it itself after asking (clean temporary files, empty the Recycle Bin, turn TRIM or the firewall on, sync the clock, create a restore point, switch power plan, raise the refresh rate, restart), opens the exact Windows settings page or tool, jumps to the right section inside CoreScope (for example the startup list), or shows a step-by-step guide for BIOS/UEFI settings (memory speed profile, virtualization, Secure Boot, TPM, BIOS updates) with a button that restarts straight into BIOS. Results appear on the finding, and the checks re-run afterwards.
- **Run full checkup** on the Insights page, plus automatic re-checks every 10 minutes. The health score no longer collapses because of many small tips.
- **Select and copy any text:** press the mouse on any text to select it (drag, double-click for a word, triple-click for a line, Ctrl+A, Ctrl+C). Text inside buttons, tabs and list rows keeps its click, but right-click any text for **Copy** and **Copy everything on this page**.
- **Appearance page:** make CoreScope yours. System, Light or Dark theme; six skins (Fluent, Aurora, Midnight, Ember, Paper, Terminal) that change colours, glass, glow, font, shape *and* window layout; three window layouts (sidebar, compact rail, top tabs); accent colour (swatches, hex or your Windows accent) with a colour wash; Mica / Acrylic / solid backdrop with a transparency slider; glow; font, text size and corner roundness; compact spacing. Every effect can be limited to the parts you choose (window, sidebar, cards, title bar, buttons, headings).
- **My themes:** save your current look under a name, apply it later, or delete it. Export a theme to a file and import one from a friend (theme files hold appearance settings only and are validated on import).
- **Colour-code pages:** each page can have its own accent colour, automatic or picked by you.
- **A different look for a page:** give any page its own skin or saved theme, layout included. The Appearance page always shows your main look so you can see your edits.
- **Network page:** speed test (download and ping to router and internet), connection details, and findings with one-click fixes for "full signal, slow internet" (RSC, TCP auto-tuning, DNS cache).
- **Wi-Fi analyzer:** nearby networks, how crowded each 2.4 / 5 GHz channel is, and which channel your router should use.
- **Search everything (Ctrl+K):** specs, settings, sensors, devices, insights and upgrade notes.
- **Automatic update check:** CoreScope tells you when a new version is on GitHub and installs it in one click. You can turn this off in Settings.

**Better information**
- **Memory timings table:** tCL, tRCD, tRP, tRAS, tRC, tRFC, tWR, tFAW, tRRD and tCCD, in cycles and nanoseconds, plus running vs JEDEC speed.
- **SSD health per drive:** health left, spare blocks, lifetime reads and writes, power-on hours, errors and an estimate of life left.
- **Slot diagrams:** RAM slots and M.2 slots, showing which are filled, empty or soldered.
- **Graph history:** 1 min, 5 min, 1 hour or 24 hours, with hover to read past values.

**Better insights**
- Health score per area (Performance, Storage, Security, Battery, Network, System). Click an area to filter.
- Dismiss a finding for 7 days or permanently, and bring it back any time.

**No drivers**
- CoreScope no longer uses or suggests the PawnIO kernel driver. CPU load and clock come from Windows' own counters (the same ones Task Manager uses), CPU temperature from the processor's die sensor on AMD APUs or the firmware's thermal zones, and power from Windows' Energy Meter where the PC has one.
- Safer around plugging and unplugging the charger: sensor reading pauses while drivers change power state, and drive health is read every 15 seconds instead of every second.

**Interface and speed**
- The sidebar is grouped into Tools, Hardware and Live.
- Instant start: the last scan shows immediately while a fresh one runs.
- Starts without an administrator prompt. "Unlock full sensors" restarts with full access when you need it.
- The window fits small or highly scaled screens. The installer detects a running CoreScope.

## 1.0.0 — 2026-10-04

First release.

**See everything**
- Overview with live CPU / GPU / RAM / battery / network tiles, a health check and "What's using your PC".
- Spec pages: Processor (CPUID, codename, process node, caches, P/E cores), Memory (per-module part numbers, SPD timings incl. CAS latency, soldered vs socketed), Graphics (VRAM, VBIOS, memory type, PCIe link), Storage (health, life left, lifetime writes, PCIe link, TRIM), Motherboard (BIOS age, Secure Boot, TPM), Devices (network, Bluetooth, USB, audio, cameras… with drivers and problem codes), System.
- Sensors page with min/max, 60-second graphs, filtering and **CSV recording** (Documents\CoreScope).

**Understand it**
- Insights in plain English, each with a "Why?" showing the measurement, rule and source.
- Upgrade guide: RAM, SSD, battery, display, Wi-Fi, CPU and GPU — what's soldered, free slots, maximum capacity, link-speed limits, what to buy, search links and a copyable shopping list.
- Benchmark: CPU single/multi, memory bandwidth and latency, disk sequential and 4K random, with history and verdicts.

**Control it**
- Control Center: resolution / refresh rate (auto-revert), brightness, power mode and plan, CPU boost, screen/sleep timeouts, battery conservation (Lenovo), Wi-Fi / Bluetooth, volume, fan control (Auto / Fixed / Curve) on supported hardware, device enable/disable, startup apps, Windows battery report — every change asks first and can be undone for 15 seconds.

**Live with it**
- Tray icon with live tooltip, always-on-top overlay (click-through, opacity), temperature and low-battery alerts, start with Windows (no UAC prompt), single instance, light/dark theme following Windows.
- Export a self-contained HTML report or copy a text summary.
- `CoreScope.exe --selftest` writes selftest.json for diagnosing any PC.

**Requirements:** 64-bit Windows 10 version 2004 (build 19041) or Windows 11. The release package is self-contained (no .NET install needed).
