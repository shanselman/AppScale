# Milestone 1 — cooperative DPI experiment (Win32Harness)

Status: **measured, PASS**. The raw files are in `artifacts\evidence\native\`: `drift.*`, `synthetic.*`,
`monitors-192.*`, `screenshot-*.json` and `probe.json`.

Environment: Windows 11 build 26340 x64, 4 monitors.
- Real DPIs are 144 (DISPLAY1/3/4) and 120 (DISPLAY2). See [milestone0-baseline.md](milestone0-baseline.md).
- OS/system DPI is 144.

## What was implemented

- **`DpiProvider`** (`tests\Win32Harness\Harness.h/.cpp`). All layout code asks the provider; none of it calls
  `GetDpiForWindow` directly.
  - `effectiveDpi = override ?: osDpi`.
  - The override comes from `APPSCALE_DPI` at startup, the protocol `SetDpi` message, the View → Scale menu, or
    a synthetic `WM_DPICHANGED`.
- **Layout.** `MulDiv(logical, effectiveDpi, 96)` is always applied to the **original logical** rectangles,
  never to the previous scaled values.
- **Fonts.** Fonts are recreated with `CreateFontIndirectW` from
  `SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, effectiveDpi)` (message font).
- **Window size.** The window is sized with `AdjustWindowRectExForDpi(client, style, menu, exStyle,
  effectiveDpi)`. A correction loop then makes the *actual* client equal the logical client. It is needed
  because the OS still draws the caption and menu at the OS DPI (see below).
- **`GetSystemMetricsForDpi`** is evaluated at both the OS and the effective DPI and logged next to
  `GetSystemMetrics`.
- **Cooperative protocol v1** (ADR 0002):
  - `SetPropW(hwnd, "AppScale.Cooperative.v1", 1)` on the top-level window.
  - Registered messages `AppScale.SetDpi.v1` (wParam 72..240 or 0 = reset; returns 1/0) and
    `AppScale.GetDpi.v1` (returns `MAKELONG(effective, os)`).
  - The startup environment variable is `APPSCALE_DPI`.
  - A genuine `WM_DPICHANGED` while an override is active keeps the absolute effective DPI and honours the
    suggested position.
- **Synthetic `WM_DPICHANGED`.** It is sent only by the harness to its own window, with a valid local `RECT*`,
  and is flagged `synthetic:true` in the log. No pointer ever crosses a process boundary.

## Commands

```powershell
scripts\build-native.ps1 -Configuration Release
tests\Win32Harness\Run-SelfTests.ps1          # drift, synthetic, monitors-os, monitors-192, detours-192
# or individually
artifacts\bin\Win32Harness\release\Win32Harness.exe --selftest-drift     --result artifacts\evidence\native\drift.json     --log artifacts\evidence\native\drift.jsonl
artifacts\bin\Win32Harness\release\Win32Harness.exe --selftest-synthetic --result artifacts\evidence\native\synthetic.json --log artifacts\evidence\native\synthetic.jsonl
$env:APPSCALE_DPI=192; artifacts\bin\Win32Harness\release\Win32Harness.exe --selftest-monitors --result artifacts\evidence\native\monitors-192.json --log artifacts\evidence\native\monitors-192.jsonl
# protocol probe against a running harness (mimics Core's CooperativeBackend: SendMessageTimeout 2000 ms)
tests\Win32Harness\Invoke-ProtocolProbe.ps1 -ProcessId <pid> -Sequence 144,192,96,0,300
```

## Results — drift (`--selftest-drift`): PASS

The test ran 50 rounds × {72, 96, 120, 144, 192, 240} = 300 steps, starting at OS DPI 144. At every step it
checked:
- each of the 9 control rects against the expected MulDiv of the logical rect;
- client size;
- the protocol `GetDpi`;
- a per-round reference snapshot;
- hit-testing.

| Check | Failures |
|---|---|
| control rect ≠ MulDiv(logical) | 0 |
| client size | 0 |
| protocol GetDpi | 0 |
| round-reference mismatch (drift) | 0 |
| return-to-start mismatch | 0 |
| hit-test (`ChildWindowFromPointEx` at control centre) | 0 |
| `WindowFromPoint` mismatch (informational, z-order dependent) | 12 in one run (STATICs only, HTTRANSPARENT); 54/54 in the final re-run, when another window covered the harness |

The end layout is **byte-identical** to the start (180-byte snapshot).

Per-DPI layout (identical in every round):

| Effective DPI | Scale | Window | Client | Font tmHeight / aveCharWidth | Combobox height (system-controlled) |
|---|---|---|---|---|---|
| 72 | 75 % | 562×341 | 540×255 | 12 / 5 | 20 |
| 96 | 100 % | 742×426 | 720×340 | 15 / 6 | 23 |
| 120 | 125 % | 922×511 | 900×425 | 20 / 8 | 28 |
| 144 | 150 % | 1102×596 | 1080×510 | 25 / 10 | 33 |
| 192 | 200 % | 1462×766 | 1440×680 | 32 / 13 | 40 |
| 240 | 250 % | 1822×936 | 1800×850 | 41 / 16 | 49 |

The client sizes are exactly `MulDiv(720×340, dpi, 96)`. The window minus client is **constant at 22×86**
for every effective DPI. That is the OS-owned non-client area, which stays at OS DPI 144.

## Results — synthetic vs genuine `WM_DPICHANGED` (`--selftest-synthetic`): PASS

The test sends a synthetic `WM_DPICHANGED` with wParam 192 and a valid suggested RECT of 1462×766 at (48,48).

| Quantity | Before | After |
|---|---|---|
| `GetDpiForWindow` | 144 | 144 |
| `GetDpiForSystem` | 144 | 144 |
| `GetDeviceCaps(LOGPIXELSX/Y)` | 144/144 | 144/144 |
| Layout | 1080×510 client, tm 25 | 1440×680 client, tm 32 (MulDiv-exact, 0 failures) |
| Window rect | — | 1462×766 at (48,48) = suggested (honoured) |
| Protocol `GetDpi` | — | effective 192, os 144 |
| Hit-test failures | — | 0 |
| Restore → byte-identical layout | — | yes |

Handler return was 0 and the event was logged as `synthetic:true`.

**Finding.** A synthetic message changes nothing in OS DPI state. The documented invariant "after
`WM_DPICHANGED`, `GetDpiForWindow` equals wParam" is **broken** for synthetic messages
(`docInvariantWindowDpiEqualsLastWmDpiChangedBroken: true`). For comparison, in the genuine transitions of
`--selftest-monitors`, `GetDpiForWindow` already equalled wParam (120) inside the handler, and the OS
rescaled the non-client area itself. Any code inside the app that re-queries `GetDpiForWindow` after a
synthetic message gets the OS value. This is why Milestone 2 investigates hooking.

## Results — override across real monitor transitions (`--selftest-monitors`, `APPSCALE_DPI=192`): PASS

| Step | Target (OS DPI) | Effective | Window | Client | tmHeight | Genuine WM_DPICHANGED |
|---|---|---|---|---|---|---|
| 0 | DISPLAY1 (144) | 192 | 1462×766 | 1440×680 | 32 | 0 |
| 1 | DISPLAY2 (120) | 192 | 1458×752 | 1440×680 | 32 | 1 |
| 2 | DISPLAY3 (144) | 192 | 1462×766 | 1440×680 | 32 | 1 |
| 3 | DISPLAY4 (144) | 192 | 1462×766 | 1440×680 | 32 | 0 |

With the override active, a genuine change keeps the absolute effective DPI: the client stays 1440×680 and
tmHeight stays 32. Only the window size changes (1458×752 vs 1462×766), because the OS-owned non-client area
is smaller at 120 DPI.

Compare without the override (`monitors-os`): DISPLAY2 gave 918×497 window, 900×425 client, tm 20.

## Results — cooperative protocol probe (out-of-process, as Core sends it)

| SetDpi wParam | SetDpi result | GetDpi effective / os |
|---|---|---|
| 144 | 1 | 144 / 144 |
| 192 | 1 | 192 / 144 |
| 96 | 1 | 96 / 144 |
| 0 (reset) | 1 | 144 / 144 |
| 300 (invalid) | 0 | 144 / 144 (unchanged) |

## Screenshots (client area, `PrintWindow(PW_RENDERFULLCONTENT)` → WIC PNG)

These are on the 144-DPI DISPLAY1, with the effective DPI set by `APPSCALE_DPI`. All three exited 0.

| File | Effective DPI | Client | Window | tmHeight |
|---|---|---|---|---|
| `screenshots/win32-100.png` | 96 | 720×340 | 742×426 | 15 |
| `screenshots/win32-150.png` | 144 (= OS) | 1080×510 | 1102×596 | 25 |
| `screenshots/win32-200.png` | 192 | 1440×680 | 1462×766 | 32 |

Logged non-client divergence:
- At effective 96, `AdjustWindowRectExForDpi` predicts caption+frame 31 and menu 20. The measured values are
  46 and 29, which are the OS-DPI values.
- At effective 192, the predictions are 58 and 39; the measured values are again 46 and 29.
- The vertical scrollbar was measured at 26 in every run (effective-DPI predictions: 17 at 96, 34 at 192).

## What this does NOT prove

- **Non-client UI:** the OS-drawn caption, menu bar, scrollbars, combobox drop height and system dialogs
  do **not** follow the effective DPI. The cooperative path cannot change them. Only the client area is scaled.
- **One app only:** this is one owned Win32 app. It says nothing about WinForms, WPF, Qt, Electron, WinUI or
  MFC.
- **Input:** hit-testing was verified only by `ChildWindowFromPointEx` at control centres. Real mouse, touch,
  IME and UI Automation were not tested.
- **Monitors:** real transitions were only 144↔120.

## Decision gate

The cooperative proof of concept **passes**. Repeated scaling has no cumulative drift, synthetic and genuine
notifications are correctly distinguished, and the protocol works out-of-process. The remaining incoherence is
the OS-owned non-client area, which stays at OS DPI. It is visible but does not break input in the client
area. Proceed to Milestone 2 research.

References:
- [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)
- [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)
- [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi)
- [AdjustWindowRectExForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-adjustwindowrectexfordpi)
- [SystemParametersInfoForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfofordpi)
- [ADR 0002](../adr/0002-cooperative-protocol.md)
