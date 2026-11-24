# Milestones 4 & 5 — Controller UI, background watcher and DWM visual magnification (evidence)

Lane: controller UI (`src\AppScale.UI`). Machine: Windows 11 build 26340 x64, .NET SDK 10.0.404, 4 monitors at 144 DPI (150 %).
The UI uses `AppScale.Core` unchanged and talks to it only through `ScaleController` and `ProfileWatcher`.

## Product flow (phase 2: "pick an app, AppScale lies to it")

AppScale is a tray app. When the user is opted in, it runs a background **watcher** (Core `ProfileWatcher`). The main flow is:

1. **Pick an app.** Either select a running window in the picker, or use **Executable…** for an app that is not running yet.
2. **Choose a scale** between 75 and 250 %.
3. Pick one of:
   - **Apply now.** Uses normal backend selection: `cooperative` for protocol-aware apps, `inject` for ordinary apps. Visual magnification is never chosen automatically.
   - **Remember (auto-apply on start)…** Asks for explicit per-app consent: *"AppScale will load its DPI adapter into &lt;exe&gt; each time it starts…"*. It then saves an **enabled** profile with native backends only. From then on the watcher applies the scale to every new (and currently running) instance of that executable.
   - **Launch with scale N%.** For apps that cache DPI at startup. Runs `AppScale.Injector.exe --launch "<exe>" --dpi N`, where the path comes from `InjectionBackend.FindInjector()`.

Every result appears in the **Applied** tab. Each row has the time, source (manual / profile / launch / auto (on start)), exe, pid, backend (`inject` / `cooperative` / `visual-preview`), effective DPI vs OS DPI, and the failure reason if it failed. Automatic applies also raise a tray balloon. The tray tooltip and the first (disabled) tray menu item show **"Watching N apps"**. **Emergency disable** stops the watcher and resets everything.

## What is implemented

| Area | Implementation |
| --- | --- |
| App shell | WPF `WinExe` (`AppScale.UI.exe`) with a PerMonitorV2 `app.manifest`, running `asInvoker` (no elevation). Project reference to Core; no extra NuGet packages. |
| Tray | WinForms `NotifyIcon` with: status item ("Watching N apps") / Open / Reset all / Emergency disable / Exit. The tooltip reads "AppScale — Watching N apps". Balloons appear for automatic applies, including failures. Closing the window hides it to the tray. Exit, session end and process exit stop the watcher and call `ResetAll()` on both controllers. Single instance. |
| Background watcher | `Services\BackgroundWatcher` owns a **separate** `ScaleController` (backends: `CooperativeBackend`, `InjectionBackend`; native only, no visual) and a `ProfileWatcher`, ticked on a worker task about every 750 ms under a lock. It runs only while opted in, not emergency-disabled, and with at least one enabled profile. It restarts whenever settings or profiles change. Each `Applied` event is enriched with the backend id (from `controller.Applied`) and with `GetStatus` (effective vs OS DPI), then marshalled to the UI. |
| Consent | First run (`OptedIn == false`) shows only the consent panel. It explains that ordinary apps get AppScale's DPI adapter loaded **only into the app you picked**, that Remember means it is loaded each time that app starts, and the "never" list: no display changes, no elevation, no telemetry, nothing system-wide, never silently bitmap, never shell/security/elevated processes. Apply is refused with `OptInRequired` until **Enable AppScale**. Further per-app confirmations: Apply now via `inject` shows a blue notice; Remember and enabling a profile checkbox each require a confirmation dialog. **Turn AppScale off** stops the watcher, resets everything and opts out. |
| Window picker | `WindowInspector.EnumerateTopLevelWindows()` with Refresh. Columns: exe name + full path, title, window DPI, monitor DPI, awareness, cooperative, and status/exclusion (`SafetyPolicy.Check`). Excluded rows are greyed out and cannot be selected. |
| Scale selector | Slider 75–250 in steps of 5, plus presets 75/100/125/150/200/250. **Absolute DPI** (96 × scale) and **Relative zoom** (requested DPI ÷ actual DPI) are labelled separately. |
| Status | Target identity, actual DPI, requested scale → DPI, effective backend id and capabilities (native rendering / live change / input / visual-only), restart indicator, **Compatibility unknown** warning for non-cooperative targets (mentions that inject may still work). |
| Apply | `Apply(target, percent, allowVisualFallback:false)`. On `NoBackend` (for example, injector missing or Injection flag off), a separate **Use visual magnification instead** button calls `Apply(..., true)`. The UI never silently falls back to bitmap. Every `ScaleResult` goes to the log and the Applied tab. |
| Launch | **Launch with scale N%** (injector `--launch`; parses the injector's JSON `pid`; refuses `%windir%` executables; 30 s timeout). **Cooperative launch N%** uses `CooperativeLauncher` (APPSCALE_DPI, restart-required indicator). The Experimental Detours flag affects only the owned Win32 harness. |
| Remembered apps (profiles) | Listed with Enabled checkbox, Auto-apply column, Apply profile, Delete, Delete all (confirmation → `ResetAll` + `SettingsStore.DeleteAll`) and Export to JSON. "Save selected window + scale" still saves **disabled**. Remember saves **enabled** after confirmation. Visual magnification is never auto-applied. |
| Feature flags | VisualPreview, **Injection** (with the injector path shown) and ExperimentalDetours (labelled "experimental research"). |
| Visual preview host (M4) | `Preview\DwmPreviewHost` implements `IVisualPreviewHost`. `Preview\PreviewWindow` calls `DwmRegisterThumbnail` / `DwmQueryThumbnailSourceSize` / `DwmUpdateThumbnailProperties` / `DwmUnregisterThumbnail`. |

### DWM thumbnail details
- The destination is source size × zoom in physical pixels, clamped (aspect preserved) to the work area of the preview's monitor. The banner shows the zoom actually displayed.
- The destination rect is the host element's position × the preview window's own DPI (`VisualTreeHelper.GetDpi`), and it is re-laid out on `DpiChanged`.
- Target state changes:
  - Minimized: a message is shown.
  - Closed: the thumbnail is unregistered and the preview closes.
  - Resized: relayout.
  - DWM HRESULT failures: shown as `0x…`.
- The preview is read-only: no input forwarding, and AppScale never reads or saves pixels.

### Automation switches
- `--smoke`: builds the window and view models, enumerates windows, creates and disposes the tray icon, then exits 0.
- `--screenshot <png> [--tab N] [--watch]`: renders the main window via `RenderTargetBitmap`. Non-cooperative titles and paths are redacted. `--watch` runs the background watcher and waits up to 6 s for an automatic apply before capturing. Tabs: 0 Scale, 1 Profiles, 2 Applied, 3 Settings.
- `--selftest-apply <harness.exe>`: end-to-end test through the view-model code path, in a temporary `APPSCALE_HOME` that is deleted afterwards. It uses `AutoConfirm` for dialogs, has a 240 s watchdog, and kills every process it started, including injector-launched ones.
- `--out <file>`: writes console output to a file. `--dummy-target`: an internal non-cooperative window.

## Commands and results

```powershell
dotnet build src\AppScale.UI -c Release
#   Build succeeded. 0 Warning(s) 0 Error(s)

$env:APPSCALE_HOME = "D:\github\AppScale\artifacts\uitest-smoke"
Start-Process artifacts\bin\AppScale.UI\release\AppScale.UI.exe -ArgumentList "--smoke","--out","artifacts\uitest-smoke.txt" -Wait -PassThru
#   exit 0
#   SMOKE OK: ... optedIn=False windows=13 cooperative=1 excluded=4 backends=[cooperative, inject, visual-preview] elapsed=514ms

Remove-Item env:APPSCALE_HOME
Start-Process artifacts\bin\AppScale.UI\release\AppScale.UI.exe -ArgumentList "--selftest-apply","artifacts\bin\WinFormsHarness\release\WinFormsHarness.exe","--out","artifacts\uitest-selftest.txt" -Wait -PassThru
#   exit 0, 45 PASS, 1 SKIP, no harness processes left behind
```

The injector was present at `artifacts\bin\Win32Harness\release\AppScale.Injector.exe`, so the inject section ran. When the injector is missing, that section logs `SKIP inject path: AppScale.Injector.exe not found …` instead. Selftest output (pids elided):

```
PASS  fresh settings start opted-out (consent panel shown)
PASS  cooperative harness window found (pid …)
PASS  harness listed in picker as cooperative and not excluded
PASS  200% → absolute 192 DPI; relative zoom ×1.33 vs actual 144 DPI
PASS  Apply before opt-in refused: FAILED (OptInRequired): AppScale is not enabled. Opt in first (UI: Enable, CLI: appscale optin).
PASS  Enable persists OptedIn=true
PASS  Apply 200% native: OK: Effective DPI 192 (200%), OS DPI 144.
PASS  GetStatus after apply: effective=192 os=144 'Override active'
PASS  status panel backend: cooperative — Cooperative (native re-render) (active) | Native rendering ✓ · Live change ✓ · Input ✓ · Visual-only ✗
PASS  preview DWM thumbnail registered (zoom requested ×1.33, shown ×1.33)
PASS  preview destination 1557×696px = source 1168×522px × 1.333 (preview DPI 144)
PASS  preview labelled: "AppScale — visual magnification ×1.33 (bitmap, read-only)"
PASS  preview closed
PASS  Reset: OK: Reset to OS DPI 144.; status effective=144 os=144
SKIP  emergency-disable test: 1 other cooperative window(s) open (not ours to reset)
PASS  Launch at 125%: OK: Launched WinFormsHarness.exe (pid …) with APPSCALE_DPI=120. ... | indicator: Restart required — launched at 120 DPI (only cooperative apps honour APPSCALE_DPI)
PASS  launched harness reports effective 120 DPI (OS 144)
PASS  Save profile stores it disabled by default
PASS  disabled profile cannot be applied
PASS  watcher running while opted in: 'Watching 0 apps'
PASS  enabling auto-apply requires confirmation (declined → stays off)
PASS  profile deleted
PASS  Remember (confirmed) saves the app
PASS  Remember saved an ENABLED profile at 200% (native only)
PASS  status shows 'Watching 1 app'
PASS  new WinFormsHarness.exe instance auto-applied on start: auto (on start) WinFormsHarness.exe pid … via cooperative: 192 DPI (OS 144) — Applied: Effective DPI 192 (200%), OS DPI 144.
PASS  harness confirms effective 192 DPI (OS 144)
PASS  after delete: 'Watching 0 apps'
injector: D:\github\AppScale\artifacts\bin\Win32Harness\release\AppScale.Injector.exe
PASS  non-cooperative harness window found (pid …)
PASS  backend selection picks inject: inject — Injected DPI virtualization (native re-render) | consent: Apply now will load AppScale's DPI adapter into WinFormsHarness.exe (this process only, until it exits). The app is told its monitor is 192 DPI and re-renders natively.
PASS  Remember non-cooperative app (consent: adapter loaded each time it starts)
PASS  running instance auto-applied: auto (on start) WinFormsHarness.exe pid … via inject: 192 DPI (OS 144) — Applied: Effective DPI 192 (200%), OS DPI 144.
PASS  newly started --no-cooperative instance auto-applied via inject: auto (on start) WinFormsHarness.exe pid … via inject: 192 DPI (OS 144) — Applied: Effective DPI 192 (200%), OS DPI 144.
PASS  injected adapter reports effective 192 DPI vs OS 144
PASS  reset restores OS DPI (effective 144, OS 144)
PASS  Launch with scale 125% via injector: OK: Started WinFormsHarness.exe with the DPI adapter at 120 DPI. …"pid":…,"dpi":120,…,"ack":1,"hr":0}
PASS  injector reported launched pid …
PASS  non-cooperative dummy target found
PASS  Compatibility unknown warning shown for non-cooperative target
PASS  native Apply fails with NoBackend, no silent bitmap fallback, explicit button offered: FAILED (NoBackend): No native backend supports this window. Visual magnification is available only if you explicitly choose it.
PASS  explicit visual magnification: OK: Visual magnification ×1.33 (requested 192 DPI ÷ actual 144 DPI). Not native rendering; read-only.
PASS  minimized target handled (preview shows message, no crash)
PASS  target closed → preview unregistered and closed
PASS  reset after target closed: OK: Preview closed.
PASS  opt-out persisted and watcher stopped ('Not watching (AppScale is off)')
PASS  Applied list has 10 row(s)
SELFTEST PASSED
```

Notes:
- The emergency-disable step is skipped whenever another cooperative window that we don't own is open, because Core's `EmergencyDisable` resets every cooperative window system-wide. It passed in the phase-1 run, when no foreign cooperative window existed.
- During the dummy (visual fallback) section the selftest turns the Injection flag off, so `NoBackend` is reachable. Otherwise, with the injector present, ordinary windows get `inject`. It restores the flag afterwards.

## Screenshots

```powershell
$env:APPSCALE_HOME = "...\artifacts\uitest-consent"   # fresh → consent
AppScale.UI.exe --screenshot docs\evidence\screenshots\ui-consent.png
$env:APPSCALE_HOME = "...\artifacts\uitest-home"      # optedIn, enabled profile WinFormsHarness@200%, disabled WpfHarness@150%
# with WinFormsHarness.exe --no-cooperative running:
AppScale.UI.exe --screenshot docs\evidence\screenshots\ui-main.png     --tab 0 --watch
AppScale.UI.exe --screenshot docs\evidence\screenshots\ui-profiles.png --tab 1 --watch
AppScale.UI.exe --screenshot docs\evidence\screenshots\ui-applied.png  --tab 2 --watch   # fresh harness instance
AppScale.UI.exe --screenshot docs\evidence\screenshots\ui-settings.png --tab 3 --watch
```

- `screenshots/ui-consent.png`: first-run consent, with the adapter-injection explanation and the never-list.
- `screenshots/ui-main.png`: "● Watching 1 app" pill, picker, scale (192 DPI absolute / ×1.33 relative), status with backend and capabilities, and the Apply now / Remember / Preview / Reset actions. The launch card shows Launch with scale (inject) plus Cooperative launch, with the injector path.
- `screenshots/ui-profiles.png`: Remembered apps with Enabled / Auto-apply columns.
- `screenshots/ui-applied.png`: an automatic apply row for a `--no-cooperative` WinFormsHarness: `auto (on start)`, backend **inject**, **192 DPI (OS 144)**, Applied.
- `screenshots/ui-settings.png`: opt-out, emergency disable, feature flags (including Injection) and privacy text.

## Limitations (honest)
- **Injection** depends on lane-native's `AppScale.Injector.exe` and `AppScale.Native.dll`. The UI only invokes them; it cannot make an app that ignores DPI change messages re-layout. Some apps only pick up DPI at startup, which is what **Launch with scale** is for. Elevated, protected, shell and security processes are excluded by `SafetyPolicy`.
- Once an injected app's adapter exposes the cooperative protocol, later applies in another AppScale session report backend `cooperative` for that same process. This is expected, because the adapter speaks the protocol.
- The watcher runs only while the AppScale tray process runs. It is not a service or a startup task, and it does not register itself to start with Windows.
- Visual-preview profiles saved in phase 1 are not applied by the watcher, whose controller is native-only. ProfileWatcher will retry them up to 3× per pid and they fail with NoBackend (see Core requests).
- The visual fallback is a **DWM thumbnail = bitmap scaling** and is **read-only, with no input forwarding**. Windows Graphics Capture, the WGC/DirectComposition prototype and the IddCx virtual monitor are **not done**.
- Tray interactions (hide-to-tray, menu, balloons) are exercised through code paths, not by an automated click test. The tray icon is the generic application icon.
- The relative zoom uses the OS-reported window DPI, which may differ from what an already-overridden app renders at.
