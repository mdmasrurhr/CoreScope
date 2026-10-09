# CoreScope roadmap

## 1.2.0: trustworthy, automatic, measurable

1.1.0 added a lot (Appearance, wider Insights with fixes, selectable text). 1.2.0 proves it works on other people's PCs,
makes releases painless, and makes findings useful over time instead of once.

Progress is tracked in the [1.2.0 milestone](../../milestone/1).

### 1. Release pipeline
- [x] GitHub Actions on a `v*` tag: build, run the tests, run `tools\package.ps1`, attach Setup.exe, zip and checksum to the release (CI also runs on every push).
- [ ] winget manifest updated automatically for each release (`Rakin.CoreScope`). The workflow step is written; it starts working once the 1.0.0 package is approved and a `WINGET_TOKEN` secret is added.
- [ ] Code signing, so the installer shows a verified publisher (decision: SignPath Foundation, Azure Trusted Signing, or stay unsigned).
- [x] Issue templates, `SECURITY.md`, `CONTRIBUTING.md`.
- [x] README screenshots.

### 2. Insights that keep working after you close the page
- [ ] Tray notification when a *new* warning or critical finding appears (once per finding; respects dismiss and snooze).
- [ ] Health history: store the score and key readings locally and show a trend.
- [ ] Fix log with Undo, and an automatic restore point before risky fixes.
- [ ] More checks, each with a fix path: drive SMART detail, BitLocker recovery-key backup, driver age per device, stale Defender scan, battery drain, resource-hungry background apps.
- [x] A "what this will do" preview line on every fix that changes something (1.1.1).

### 3. Prove it on other PCs
- [ ] Windows 10 pass (Mica needs build 22621; check the fallback backdrop and every page).
- [ ] Varied hardware: desktop with a discrete GPU, AMD CPU, BitLocker on, non-English Windows. An "Attach diagnostics" button packages `selftest.json` and the log.
- [ ] Visual regression tests: render every skin × light/dark × page and compare with saved images (`tools/ui-snapshots`).
- [ ] Accessibility audit: keyboard-only use, screen-reader names, contrast of every skin.

### 4. Polish and known limits
- [x] Select text across several blocks and keep bold runs in the selection overlay (1.1.1).
- [x] Smooth the layout change when a page has its own look, or let layout stay fixed (1.1.1).
- [ ] Theme sharing: preview thumbnails and a small curated gallery in the repo.
- [ ] Performance budget: measure start-up time, idle CPU and memory, and the GPU cost of glow on cards.

### 5. Reach (stretch, may slip to 1.3)
- [ ] Microsoft Store submission (see `STORE-SUBMISSION.md`).
- [ ] ARM64 build.
- [ ] Portable mode that keeps settings next to the exe.

## Suggested order
1. Release pipeline.
2. Fix log with Undo and restore points.
3. Tray notifications and health history.
4. Windows 10 and varied-hardware testing, visual regression tests, accessibility.
5. Release 1.2.0.

## Risks
- Notifications and history add background work: keep them off the sensor thread and cap storage.
- Every new fix needs a confirmation, a log entry and a test.
- Other hardware can only be tested with help: please send `selftest.json` from your PC in an issue.
