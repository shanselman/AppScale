# Milestone 0 — ground truth (Win32Harness baseline)

Status: **measured**. Every number below comes from a JSON result or JSONL log written by
`Win32Harness.exe` on the machine described under *Environment*. The raw files are in
`artifacts\evidence\native\` (`drift.*`, `synthetic.*`, `monitors-os.*`, `screenshot-*.*`).

## What was tested

- `tests\Win32Harness` is a PerMonitorV2 Win32 app. The embedded manifest declares
  `<dpiAwareness>PerMonitorV2</dpiAwareness>`, `<dpiAware>true/pm</dpiAware>` and Common Controls v6.
- **Controls:** label, edit, button, checkbox, combobox, listbox, status static, diagnostics button and a
  diagnostics edit panel.
- **Other UI:** a menu bar (File/View/Help), a modal dialog from an in-memory `DLGTEMPLATE`, a tooltip, and a
  GDI-drawn ruler/grid region (1 logical inch box plus 1-device-pixel line comb for sharpness comparison).
- **Logging:** one JSON object per event. Each event records:
  - `GetDpiForWindow`, `GetDpiForSystem` and `GetDeviceCaps(LOGPIXELSX/Y)` on the window DC;
  - the window/thread awareness context (`GetWindowDpiAwarenessContext`, `GetAwarenessFromDpiAwarenessContext`);
  - the monitor handle, device name and effective/angular/raw DPI (`GetDpiForMonitor`);
  - window/client rects and `GetTextMetrics`;
  - every `WM_DPICHANGED`, with a genuine/synthetic flag, wParam and the suggested RECT.
- Awareness and scale are logged as **separate objects** (`awareness{}` vs `scale{}`) and are never conflated.
- **Genuine monitor transitions:** `--selftest-monitors` moves the window onto each monitor with
  `SetWindowPos`, pumps messages and records each genuine `WM_DPICHANGED`.
- **Input-accuracy baseline:** for each control's centre, `ChildWindowFromPointEx` (and, informationally,
  `WindowFromPoint`) must return that control. This runs in `--selftest-drift` and `--selftest-synthetic`.

## Environment

- **OS:** Windows 11 build 26340, x64.
- **Toolchain:** VS 18 Community (MSVC 14.51), Windows SDK 10.0.26100, CMake (Visual Studio generator).
- **Process awareness:** PerMonitorV2. `GetDpiForSystem` = 144.

Monitors, as measured by `EnumDisplayMonitors` + `GetDpiForMonitor`:

| Device | Primary | Rect (physical px) | Effective DPI | Scale | Angular / raw DPI | Physical size |
|---|---|---|---|---|---|---|
| `\\.\DISPLAY1` | yes | 3840×2560 @ (0,0) | 144 | 150 % | 162 / 163 | 596×397 mm |
| `\\.\DISPLAY2` | no | 1920×1280 @ (931,2560) | 120 | 125 % | 153 / 215 | 226×141 mm |
| `\\.\DISPLAY3` | no | 3840×2560 @ (-3840,5) | 144 | 150 % | 162 / 163 | 596×397 mm |
| `\\.\DISPLAY4` | no | 3840×2560 @ (3840,0) | 144 | 150 % | 162 / 163 | 596×397 mm |

Only two **real** DPIs exist on this machine: 120 (125 %) and 144 (150 %). No monitor runs at 100 % or 200 %,
and display settings were deliberately not changed. Comparisons at 100 %/200 % therefore come from the
Milestone 1 override, not from real monitors. This is stated plainly rather than papered over.

## Commands

```powershell
scripts\build-native.ps1 -Configuration Release
tests\Win32Harness\Run-SelfTests.ps1            # runs all five self-tests, exit code = failures
# individually:
artifacts\bin\Win32Harness\release\Win32Harness.exe --selftest-monitors --result artifacts\evidence\native\monitors-os.json --log artifacts\evidence\native\monitors-os.jsonl
$env:APPSCALE_DPI=96;  artifacts\bin\Win32Harness\release\Win32Harness.exe --screenshot docs\evidence\screenshots\win32-100.png --result artifacts\evidence\native\screenshot-100.json
$env:APPSCALE_DPI=144; ... --screenshot docs\evidence\screenshots\win32-150.png ...
$env:APPSCALE_DPI=192; ... --screenshot docs\evidence\screenshots\win32-200.png ...
```

## Results — genuine monitor transitions (`--selftest-monitors`, no override)

Result: PASS. 4 monitors, distinct DPIs {120, 144}; expected genuine transitions 2, observed 2,
spurious 0, move failures 0.

| Step | Target | OS DPI before→after | Window after (w×h) | Client after | tmHeight | Genuine WM_DPICHANGED |
|---|---|---|---|---|---|---|
| 0 | DISPLAY1 | 144→144 | 1102×596 | 1080×510 | 25 | 0 |
| 1 | DISPLAY2 | 144→120 | 918×497 | 900×425 | 20 | 1 (wParam 120; suggested 918×497) |
| 2 | DISPLAY3 | 120→144 | 1102×596 | 1080×510 | 25 | 1 (wParam 144) |
| 3 | DISPLAY4 | 144→144 | 1102×596 | 1080×510 | 25 | 0 |

The logical client size is 720×340 at 96 DPI. MulDiv gives 1080×510 at 144 and 900×425 at 120; both were
measured exactly.

## Results — OS state and non-client baseline at OS DPI 144 (from `screenshot-150.json`)

| Quantity | Value |
|---|---|
| GetDpiForWindow / GetDpiForSystem / LOGPIXELSX / LOGPIXELSY / monitor | 144 / 144 / 144 / 144 / 144 |
| Window / client | 1102×596 / 1080×510 |
| UI font | Segoe UI, lfHeight −18, tmHeight 25, tmAveCharWidth 10 |
| Measured caption+frame / `AdjustWindowRectExForDpi(144)` caption+frame | 46 / 45 |
| Measured menu bar / `GetSystemMetricsForDpi(SM_CYMENU,144)` | 29 / 30 |
| Measured listbox vertical scrollbar / `SM_CXVSCROLL@144` | 26 / 26 |

## Results — input accuracy baseline

- `--selftest-drift`: 300 layout steps, `hitTestFailures` = 0 (`ChildWindowFromPointEx` returned the control
  at every control centre).
- `--selftest-synthetic`: also `hitTestFailures` = 0.
- `WindowFromPoint` is screen- and z-order-based, so it is recorded only as *informational*.
  - In one run it returned the parent only for the two `STATIC` controls (label, status): 12 mismatches, as
    expected, because static controls answer `WM_NCHITTEST` with `HTTRANSPARENT`.
  - In the final re-run after the clean rebuild, all 54 probes mismatched. Another top-level window on this
    shared desktop was over the harness. The harness is not topmost by design.
  - `ChildWindowFromPointEx`, the window-relative check, had 0 failures in both runs. This is why it is the
    pass/fail criterion.

## Screenshots

`docs\evidence\screenshots\win32-100.png` (720×340), `win32-150.png` (1080×510) and `win32-200.png`
(1440×680) are client-area captures via `PrintWindow(PW_RENDERFULLCONTENT)` saved through WIC.
- `win32-150.png` is the real OS DPI.
- 100 % and 200 % are effective DPI via the Milestone 1 provider on the same 144-DPI monitor.

GDI text and the 1-device-pixel comb are rasterised at the effective DPI, so they are sharp, not bitmap-stretched.

## What this does NOT prove

- **Missing real DPIs:** no real 100 % or 200 % monitor transition was observed; only 144↔120 exists.
- **Text sharpness:** judged only by capture inspection, not by a perceptual metric.
- **Untested input paths:** touch, pen, IME and UI Automation input accuracy were not tested.
- **Not physically exercised:** maximize/restore across monitors, monitor hot-unplug.

## Decision gate

Baseline established. The harness observes genuine transitions correctly, and layout tracks the OS DPI exactly
(MulDiv of logical values). Awareness and scale are logged separately. Proceed to Milestone 1.

References:
- [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)
- [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)
- [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor)
- [High DPI desktop application development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)
