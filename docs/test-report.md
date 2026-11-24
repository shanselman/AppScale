# Test report

**Run:** 2026-11-23, Windows 11 build 26340 x64, .NET SDK 10.0.404, MSVC 14.51 (VS 18), on 4 mixed-DPI monitors
(144/120/144/144). Every number here was measured; nothing is a target. Global display settings were never changed.

## Automated

Reproduce with `scripts\build.ps1 -Integration`. A clean native and managed build plus all tests took about 103 s.

| Suite | Result | What it covers |
|---|---|---|
| `AppScale.Core.Tests` (xUnit) | **59 / 59 pass** | Absolute DPI for 75/100/125/150/175/200/250 %, out-of-range rejection, relative zoom, Win32 `MulDiv` semantics (rounding, sign, ÷0, 64-bit intermediate), scaling from logical values (no drift) versus chained scaling (demonstrated drift), point conversion, letterboxed preview→source mapping. Settings: safe defaults (opted out, profiles disabled, Detours off), round-trip, upsert by executable key, case-insensitive identity, atomic save, invalid scale not persisted, corrupt file preserved and safe defaults used, export/delete/delete-all, stable camelCase JSON. Backend selection: native preferred, no silent visual fallback, visual only when allowed, experimental never auto-selected, unknown preferred id fails, empty list fails closed. Safety: elevated, access-denied, denylisted, %WINDIR% and self are excluded. Controller: opt-in, emergency disable, invalid scale, exclusion before any backend runs, reset-all, visual feature flag. |
| `AppScale.IntegrationTests` | **22 / 22 pass** | Real processes. Each cooperative harness (Win32, WinForms, WPF) is driven through all 6 required scales: apply, verify, reset, OS DPI unchanged. Startup override via `APPSCALE_DPI`. 100 live changes with no window or client drift. An unrelated second instance is untouched. Opt-out and emergency disable fail closed. **Injection into stock (non-cooperative) Win32, WinForms and WPF harnesses: auto-selects `inject`, effective 192 DPI, the client area grows by about 192/144, and reset returns to within 2 px.** **ProfileWatcher auto-applies an enabled profile (175 % → 168 DPI) when the app starts, once per process.** A disabled profile is never injected. The five Win32 self-test modes run as tests. |
| Win32 self-tests (`tests\Win32Harness\Run-SelfTests.ps1`) | **5 / 5 pass** | drift, synthetic vs genuine `WM_DPICHANGED`, real monitor transitions at OS DPI and with a 192 override, and Detours hook phases. |
| UI `--smoke` | **pass** (433 ms) | Starts, lists 14 windows (6 excluded), backends `[cooperative, inject, visual-preview]`, safe reset on exit. |
| UI `--selftest-apply` | **45 pass, 1 skip** | The full UI flow against a WinForms harness, including Remember → watcher → `inject` at 192 DPI. The skip is emergency disable while a window AppScale doesn't own exists ([evidence](evidence/milestone4-5-ui.md)). |

## Manual and exploratory

These are the third-party results summarized in [compatibility.md](compatibility.md):
- **Native at 200 %:** VS Code (Electron) and ScreenToGif (WPF).
- **No change:** 7-Zip (system-aware) and VLC (Qt 5, PMv1, live).
- **Correctly refused:** Audacity (unaware).

## Continuous integration

GitHub Actions (`windows-latest`) builds native and managed code and gates on the unit tests. The integration tests
**skip themselves there, with a stated reason**, because hosted runners are a poor fit:

- They run **elevated**. AppScale refuses elevated targets by design, and launched harnesses inherit elevation.
- They have a **1024×768** display.

The first CI run, before the skip was added, showed both effects: `FAILED (Elevated)` refusals, and harness self-test
failures where Windows clamped the windows at 192–240 DPI.

**Finding: on small displays, upscaling can push controls off-screen.** On a 1024×768 screen, Windows clamped the
WinForms harness client area to 1028 px at 200 % and 250 %, and the ListView was no longer reachable by hit-testing.
AppScale does **not** yet warn when the requested scale won't fit the work area. That is a prerequisite before any
interactive release.

## Not measured

The following are **not validated**. AppScale must not be described as an accessibility solution until they are:
- CPU/GPU overhead and latency.
- Hit-testing accuracy after injection on third-party apps.
- IME, touch, drag/drop, UI Automation and Narrator.
- Monitor disconnect with an override active.
- Crash of the controller while an override is active. The override stays until the target restarts or is reset, because the DLL lives in the target.

See the [manual test checklist](manual-test-checklist.md).
