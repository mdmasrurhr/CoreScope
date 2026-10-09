# Publishing CoreScope to the Microsoft Store

## 1. Create your developer account (free for individuals)
1. Go to https://storedeveloper.microsoft.com and choose **Get started** → *Individual developer*.
2. Sign in with your Microsoft account. Verify with a government ID and a selfie. No fee.

## 2. Reserve the name
Partner Center → **Apps and games** → **New product** → **MSIX or PWA app** → reserve **CoreScope**
(if taken, e.g. "CoreScope PC Info"). Then open **Product management → Product identity** and copy:
- Package/Identity/Name, e.g. `12345RakinH.CoreScope`
- Package/Identity/Publisher, e.g. `CN=1A2B3C4D-....`
- Package/Properties/PublisherDisplayName, e.g. `Rakin`

## 3. Build the package
On the PC with the .NET 10 SDK, in the PCInfo folder:
```
powershell -ExecutionPolicy Bypass -File tools\msix\make-msix.ps1 -IdentityName "<Name>" -Publisher "<CN=...>" -PublisherDisplayName "<Display name>"
```
Upload `dist\CoreScope-1.0.0.0-x64.msix`. Microsoft signs it, so nobody sees "unknown publisher".

Optional local test first (admin PowerShell; installs a self-signed test copy on this PC only):
`... make-msix.ps1 -TestSign`

## 4. Store listing (Partner Center → Submission)
- **Pricing:** Free. **Markets:** all.
- **Properties:** Category *Utilities & tools*. Check "This product accesses, collects or transmits personal information"
  → **No** for transmission. Privacy policy URL: the CoreScope privacy policy page.
- **Age ratings:** complete the questionnaire (no user content, no purchases) → 3+/Everyone.
- **Packages:** upload the .msix.
- **Store listing:** description and features below, at least one 1366×768 or larger screenshot (Overview, Upgrade,
  Control, Sensors make a good set).

### Notes for certification (paste this)
> CoreScope is a hardware-information and monitoring utility (similar to CPU-Z / HWiNFO).
> runFullTrust: it is a WPF desktop app.
> allowElevation: the app starts as a normal user. Reading CPU temperatures, voltages, fan control, SMART drive health
> and the TPM requires administrator access, so the user can click "Unlock full sensors", which restarts the app
> elevated after a standard UAC prompt. Nothing is elevated without that click, and the app never installs drivers or
> services. It works fully without elevation, just with fewer sensors.
> The app sends no data anywhere; there is no account, telemetry or network service.
> To test: launch → Overview loads; click "Unlock full sensors" in the yellow bar → accept UAC → CPU temperature appears.

### Short description
See every part of your PC, watch live temperatures and loads, get plain-English advice, and find out exactly which upgrades fit.

### Features (one per line)
- Detailed specs: CPU, memory timings (CAS latency), graphics, storage health, motherboard, every device
- Live sensors with graphs, an always-on-top overlay and CSV recording
- Plain-English health insights, each with the measurement behind it
- Upgrade guide: what's soldered, free slots, maximum RAM and SSD, what to buy
- Benchmark: CPU, memory and disk, with history
- Control center: display, power mode, battery care, Wi-Fi/Bluetooth, fans, startup apps, with one-click undo
- Temperature and battery alerts, tray icon, HTML report export

## 5. Allowed elevation: what to expect
`allowElevation` is a restricted capability, so certification may ask for more detail or take a few extra days.
If Microsoft refuses it, remove the `allowElevation` line from `tools\msix\AppxManifest.template.xml` and resubmit.
The Store version then always runs in limited mode, and the button is replaced by a note.
