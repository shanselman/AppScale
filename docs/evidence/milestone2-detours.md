# Milestone 2 — native API virtualization research (Detours, explicit opt-in)

Status: **measured, EXPERIMENTAL**. This document covers the *explicit, owned-harness* mode
(`--selftest-detours`). For injection into arbitrary processes, see
[milestone2-injection.md](milestone2-injection.md). Raw data: `artifacts\evidence\native\detours-192.json` and
`detours-192.jsonl`.

## What was built

`src\AppScale.Native\AppScale.Native.dll` hooks APIs with Microsoft Detours v4.0.1.
- Detours comes from FetchContent, pinned to the tag and compiled from source as a static library. No binaries
  are vendored. The license is in `src\AppScale.Native\THIRD-PARTY-NOTICES.md`.
- **Process-local only:** no global hooks, no `SetWindowsHookEx` across processes, no persistence.
- **When the harness loads it:** only if `APPSCALE_EXPERIMENTAL_DETOURS=1` **and** `APPSCALE_DPI` is valid.
  The harness then calls `LoadLibraryW` and the exported `AppScaleNative_Initialize` *before creating any
  window*.

Each hook is separately toggleable through a bitmask:

| Bit | API | Rule when enabled |
|---|---|---|
| 0x01 | `GetDpiForWindow` | Effective DPI only for HWNDs whose owning thread is the harness UI thread; otherwise call the trampoline. An invalid HWND keeps the real result and `GetLastError`. |
| 0x02 | `GetDpiForSystem` | Effective DPI on the owner thread. |
| 0x04 | `GetDeviceCaps` | Only `LOGPIXELSX/Y` on screen or window DCs; every other index passes through. |
| 0x08 | `GetDpiForMonitor` (shcore) | `MDT_EFFECTIVE_DPI` only; the HRESULT is preserved. |
| 0x10/0x20/0x40 | `GetSystemDpiForProcess`, `GetSystemMetrics`, `SystemParametersInfoW` | Added for injection mode; not installed in this explicit test. |

Every hook:
- calls the Detours trampoline, never the patched export, with a thread-local re-entrancy guard;
- counts calls, overrides and each pass-through reason;
- attributes the caller module via `_ReturnAddress()` and
  `GetModuleHandleExW(FROM_ADDRESS|UNCHANGED_REFCOUNT)`.

`AppScaleNative_GetStatsJson` exports the counters as JSON.

## Command

```powershell
$env:APPSCALE_DPI=192; $env:APPSCALE_EXPERIMENTAL_DETOURS=1
artifacts\bin\Win32Harness\release\Win32Harness.exe --selftest-detours --result artifacts\evidence\native\detours-192.json --log artifacts\evidence\native\detours-192.jsonl
# (also run by tests\Win32Harness\Run-SelfTests.ps1)
```

Environment: Windows 11 build 26340 x64. The harness sits on DISPLAY1 at 144 DPI (OS/system DPI 144); the
effective DPI is 192. See [milestone0-baseline.md](milestone0-baseline.md) for the monitor list.

## Method

The harness contains a **naive code path** that behaves like an unmodified PMv2 app:
1. It queries `GetDpiForWindow`, `GetDpiForSystem`, `GetDeviceCaps(LOGPIXELSX/Y)` and `GetDpiForMonitor`
   directly.
2. It computes the layout of the 9 controls from the answers.

This runs once per phase, with hooks enabled cumulatively. The test checks whether the naive layout equals the
effective-DPI layout, and records the real values next to the app-visible ones.

## Results — hook phases (installMask 0x0F, effective 192, OS 144): PASS (0 semantic failures)

| Phase | Enabled mask | Naive GetDpiForWindow / ForSystem / LOGPIXELSX / ForMonitor | Real (all four) | Naive layout matches effective |
|---|---|---|---|---|
| none | 0x0 | 144 / 144 / 144 / 144 | 144 | 0 / 9 controls |
| GetDpiForWindow | 0x1 | **192** / 144 / 144 / 144 | 144 | **9 / 9** |
| +GetDpiForSystem | 0x3 | 192 / **192** / 144 / 144 | 144 | 9 / 9 |
| +GetDeviceCaps | 0x7 | 192 / 192 / **192** / 144 | 144 | 9 / 9 |
| +GetDpiForMonitor | 0xF | 192 / 192 / 192 / **192** | 144 | 9 / 9 |

**Necessity finding.** For a PMv2 app that lays out from `GetDpiForWindow`, that **one hook is sufficient**
for its own layout. Whether the other hooks are necessary depends on which API the app reads:
- in injection mode, Electron uses `GetDpiForMonitor`;
- WPF uses `GetDpiForWindow`, `GetDpiForMonitor` and `GetSystemMetrics`;
- see [milestone2-injection.md](milestone2-injection.md).

The extra hooks are therefore kept, but each is individually toggleable.

## Results — isolation and ABI

| Check | Hooked result | Real result |
|---|---|---|
| `GetDpiForWindow(invalid HWND)` | 0, `GetLastError` 87 | 0, 87 |
| `GetDpiForWindow(desktop window)` | 144 | 144 |
| `GetDpiForWindow(Shell_TrayWnd)` (another process) | 144 | 144 |
| `GetDeviceCaps(HORZRES)` (non-DPI index) | 3840 | 3840 |
| Worker thread: `GetDpiForWindow(harness window)` | 192 | — |
| Worker thread: `GetDpiForSystem` | 144 (not owner thread) | 144 |
| Worker thread: `GetDpiForWindow(its own window)` | 144 | 144 |

Final call statistics for the explicit run (calls / overridden, with caller modules):

| API | Calls / overridden | Callers |
|---|---|---|
| `GetDpiForWindow` | 10 / 5 | Win32Harness.exe 10. Pass-throughs: not-owned 3, error 1, not-per-monitor 1. |
| `GetDpiForSystem` | 340 / 272 (an earlier run: 356 / 288) | uxtheme.dll 278, USER32.dll 60, Win32Harness.exe 2. uxtheme's count varies between runs. |
| `GetDeviceCaps` | 149 / 145 | uxtheme.dll 145, Win32Harness.exe 2, gdi32full.dll 2. 186 further calls were other indices, passed through. |
| `GetDpiForMonitor` | 1 / 1 | Win32Harness.exe 1 |

In this explicit mode system-module callers are **also** overridden (`includeSystemCallers: true`), so
uxtheme and USER32 internally see 192. In injection mode, system callers pass through by default (see the
injection doc).

## Results — OS-owned UI with all four hooks enabled (key falsification finding)

| Element | Measured | Expected at OS DPI 144 | Expected at effective 192 | Follows effective? |
|---|---|---|---|---|
| Caption: `GetSystemMetrics(SM_CYCAPTION)` | 45 | `ForDpi(144)` = 34 | `ForDpi(192)` = 45 | API value yes (see note); drawn caption **no** |
| Non-client top incl. menu (measured) | 75 | — | — | **no** (identical to unhooked run) |
| Menu bar height (measured) | 29 | `SM_CYMENU@144` = 30 | 39 | **no** |
| Listbox vertical scrollbar (measured) | 26 | 26 | 34 | **no** |
| Fresh tooltip font height | −18 | −18 | −24 | **no** |
| `MessageBoxW` (system dialog) | awareness *System*, app-visible `GetDpiForWindow` 144, 639×260, font −17 | — | — | **no** |
| Template dialog, uncompensated (9 pt) | 722×681, font −18, `GetDpiForWindow` app-visible 192 / real 144 | — | — | **no** — the dialog manager scales by real DPI |
| Template dialog, compensated (12 pt = 9×192/144) | 932×856, font −24 | — | — | yes, but only because the app changed the template |

Note on the caption row: `GetSystemMetrics` itself was **not** hooked in this run (installMask 0x0F), yet it
returned 45. Hypothesis, consistent with the counters but not proven: USER32 obtains the DPI internally through
the hooked `GetDpiForSystem` (USER32.dll made 60 calls to it). Either way, the **drawn** caption and menu stayed
at OS size.

**Conclusion.** API virtualization makes the app *believe* it runs at 192, and the app's own client-area layout
follows. Everything the OS draws or lays out from its internal per-window DPI stays at 144:
- non-client caption and frame, menu bar, scrollbars;
- tooltips and the common-control internal metrics;
- system dialogs and `DialogBox` template scaling.

This is exactly the "internal framework and OS state divergence" the specification calls a possible
fundamental blocker. It is **real and reproducible**. It is cosmetic for the non-client area, but it makes
geometry incoherent: an app that computes non-client sizes from the hooked metrics gets values that do not
match what the OS draws. The harness avoids this only by measuring the real client rect in a correction loop.

## What this does NOT prove

- **Scope:** only the owned harness was tested here; there was no third-party framework in this explicit
  mode.
- **Input:** hit-testing under hooks was not separately measured beyond the Milestone 1 checks.
- **Calls before init:** calls made *before* `AppScaleNative_Initialize` (static initialisers, earlier
  framework DPI caches) are not covered. The harness initialises before creating any window.
- **No real OS DPI change:** the harness runs at a real 144, so no live OS DPI change was exercised while
  hooked (a monitor move while hooked was not run in this mode).

## Decision gate

Hooking one API is sufficient for owned client-area layout, and it works. It **cannot** reconcile OS-owned
non-client rendering, menus, tooltips or system dialogs, and adding more hooks did not change any measured
OS-drawn element. Following the spec rule "do not keep adding hooks indefinitely":
- the hook set is capped at the seven listed APIs;
- the backend stays experimental and per-app opt-in;
- non-client/menu divergence is documented as a known limitation, not something more hooks will fix.

References:
- [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)
- [GetDpiForSystem](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforsystem)
- [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor)
- [GetDeviceCaps](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdevicecaps)
- [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi)
- [Detours wiki](https://github.com/microsoft/Detours/wiki)
