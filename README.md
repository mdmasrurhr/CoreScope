# CoreScope

**See every part of your Windows PC, watch it live, and find out exactly what you can upgrade.**

CoreScope is a free hardware-information and monitoring app for Windows 10 and 11, in the spirit of CPU-Z and HWiNFO
but written for normal people: everything is explained in plain English.

## Download

**[⬇ Download the latest version](../../releases/latest)**: get `CoreScope-x.y.z-Setup.exe` from the newest release and run it.

Or install from a terminal:
```
winget install Rakin.CoreScope
```

> **"Windows protected your PC"?** CoreScope isn't code-signed yet (signing certificates cost money or need a company).
> Click **More info → Run anyway**. It's shown only once.

## What it does

- **Specs:** processor (codename, cores, caches), memory (part numbers, speed, **CAS latency and timings**,
  soldered vs. slots), graphics, storage (health, life left, PCIe speed), motherboard, BIOS, TPM and every device.
- **Live sensors:** load, clocks, temperatures, fans and power, with graphs, an always-on-top overlay and CSV recording.
- **Insights:** plain-English health checks, each showing the measurement and rule behind it.
- **Upgrade guide:** what's soldered, free RAM and M.2 slots, maximum capacity, what to buy, plus a shopping list.
- **Benchmark:** CPU, memory and disk, with your history.
- **Control center:** resolution and refresh rate, brightness, power mode, battery conservation, Wi-Fi and Bluetooth,
  fans, devices and startup apps. Every change can be undone for 15 seconds.
- Tray icon, temperature and battery alerts, and HTML report export.

CoreScope starts as a normal app. Click **Unlock full sensors** to restart it with administrator rights. This adds CPU
temperature, fan control, drive health and the remaining controls.

## Requirements

64-bit Windows 10 version 2004 or later, or Windows 11. Intel or AMD. Nothing else to install.
Optional: the free [PawnIO](https://pawnio.eu) driver for CPU temperature and power on recent Windows versions.

## Privacy

Everything stays on your PC. There is no account, no telemetry and no network service.

## Problems?

Open an [issue](../../issues). To help diagnose a PC, run `CoreScope.exe --selftest` from the install folder
(`%LocalAppData%\Programs\CoreScope`). Attach the `selftest.json` and `corescope.log` files it writes.
It never changes any settings.
